using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexTrayStatus
{
    internal sealed class ModelRadarPricingState
    {
        public bool Available { get; set; }
        public bool Stale { get; set; }
        public string EffectiveDate { get; set; }
        public long FetchedAt { get; set; }
        public int ModelCount { get; set; }
        public int RejectedModelCount { get; set; }
        public string ErrorCode { get; set; }
        public string Source { get; set; }
        public string SourceUrl { get; set; }
    }

    internal sealed class ModelRadarPricingDocument
    {
        public int Version { get; set; }
        public string EffectiveDate { get; set; }
        public long FetchedAt { get; set; }
        public Dictionary<string, RemoteModelPrice> Models { get; set; }
        public int RejectedModelCount { get; set; }
    }

    // This public price feed uses its own anonymous client, never the Codex client.
    internal sealed class ModelRadarPricingService
    {
        // Keep the legacy type name for the host API, but use the maintained
        // Models.dev catalog for the direct OpenAI provider. The feed's legacy
        // cost fields are explicitly USD per million text tokens.
        internal const string Url = "https://models.dev/api.json";
        internal const string SourceName = "Models.dev · OpenAI";
        internal const string SourceUrl = "https://models.dev/";
        private const int MaximumJsonLength = 16 * 1024 * 1024;
        private readonly HttpClient client;
        private readonly string cachePath;
        private readonly Func<DateTimeOffset> clock;
        private readonly object gate = new object();
        private ModelRadarPricingDocument cached;
        private string error;
        private DateTimeOffset nextFetch;
        private Task<bool> inFlight;

        internal ModelRadarPricingService() : this(CreateClient(), Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexTrayStatus", "models-dev-prices.json"), delegate { return DateTimeOffset.UtcNow; }) { }

        internal ModelRadarPricingService(HttpClient httpClient, string path, Func<DateTimeOffset> now)
        {
            if (httpClient == null) throw new ArgumentNullException("httpClient");
            client = httpClient;
            cachePath = path;
            clock = now ?? delegate { return DateTimeOffset.UtcNow; };
            LoadCache();
        }

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpClient result = new HttpClient(new HttpClientHandler {
                UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });
            result.MaxResponseContentBufferSize = MaximumJsonLength;
            result.DefaultRequestHeaders.UserAgent.ParseAdd("CodexTrayStatus");
            return result;
        }

        internal ModelRadarPricingState Status
        {
            get
            {
                lock (gate)
                {
                    return new ModelRadarPricingState {
                        Available = cached != null,
                        Stale = cached != null && (error == "price_refresh_failed" || clock().ToUnixTimeMilliseconds() - cached.FetchedAt >= TimeSpan.FromHours(6).TotalMilliseconds),
                        EffectiveDate = cached == null ? null : cached.EffectiveDate,
                        FetchedAt = cached == null ? 0 : cached.FetchedAt,
                        ModelCount = cached == null ? 0 : cached.Models.Count,
                        RejectedModelCount = cached == null ? 0 : cached.RejectedModelCount,
                        ErrorCode = error,
                        Source = SourceName,
                        SourceUrl = SourceUrl
                    };
                }
            }
        }

        internal Task<bool> RefreshAsync(bool force = false)
        {
            lock (gate)
            {
                if (inFlight != null && !inFlight.IsCompleted) return inFlight;
                if (!force && clock() < nextFetch) return Task.FromResult(false);
                inFlight = FetchAsync();
                return inFlight;
            }
        }

        private async Task<bool> FetchAsync()
        {
            try
            {
                string json;
                using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                using (HttpResponseMessage response = await client.GetAsync(Url, timeout.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MaximumJsonLength) throw new InvalidDataException("price_feed_too_large");
                    json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                ModelRadarPricingDocument document = Parse(json);
                document.FetchedAt = clock().ToUnixTimeMilliseconds();
                bool changed;
                lock (gate)
                {
                    changed = cached == null || !SamePrices(cached.Models, document.Models);
                    PricingCatalog.ReplaceRemote(document.Models);
                    cached = document;
                    error = null;
                    nextFetch = clock().AddHours(6);
                }
                // A read-only profile still benefits from valid prices for this run.
                try { SaveCache(document); }
                catch { lock (gate) error = "price_cache_write_failed"; }
                return changed;
            }
            catch
            {
                lock (gate)
                {
                    error = "price_refresh_failed";
                    nextFetch = clock().AddHours(1);
                }
                return false;
            }
        }

        internal static ModelRadarPricingDocument Parse(string json)
        {
            Dictionary<string, object> root = Serializer().DeserializeObject(json) as Dictionary<string, object>;
            Dictionary<string, object> provider = root == null ? null : Get(root, "openai") as Dictionary<string, object>;
            if (provider == null || !Equals(Get(provider, "id"), "openai"))
                throw new InvalidDataException("unsupported_price_feed");
            Dictionary<string, object> rows = Get(provider, "models") as Dictionary<string, object>;
            if (rows == null) throw new InvalidDataException("missing_price_models");
            ModelRadarPricingDocument result = new ModelRadarPricingDocument {
                // The upstream API has no catalog-wide pricing effective date.
                // Never substitute model release dates or the download date.
                Version = 2, EffectiveDate = null,
                Models = new Dictionary<string, RemoteModelPrice>(StringComparer.OrdinalIgnoreCase)
            };
            HashSet<string> duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object> item in rows)
            {
                Dictionary<string, object> row = item.Value as Dictionary<string, object>;
                string id = item.Key;
                Dictionary<string, object> costs = row == null ? null : Get(row, "cost") as Dictionary<string, object>;
                decimal? input, cachedInput, output;
                if (string.IsNullOrWhiteSpace(id) || costs == null || !Equals(Get(row, "id"), id) ||
                    !ReadRate(Get(costs, "input"), out input) ||
                    !ReadRate(Get(costs, "cache_read"), out cachedInput) ||
                    !ReadRate(Get(costs, "output"), out output) ||
                    !Valid(new RemoteModelPrice { Input = input, Cached = cachedInput, Output = output }))
                {
                    result.RejectedModelCount++;
                    continue;
                }
                // Duplicate IDs are ambiguous; do not let response order pick a price.
                if (duplicates.Contains(id) || result.Models.ContainsKey(id))
                {
                    if (result.Models.Remove(id)) result.RejectedModelCount++;
                    duplicates.Add(id);
                    result.RejectedModelCount++;
                    continue;
                }
                result.Models.Add(id, new RemoteModelPrice { Input = input, Cached = cachedInput, Output = output });
            }
            if (result.Models.Count == 0) throw new InvalidDataException("no_usable_price_models");
            return result;
        }

        private static object Get(Dictionary<string, object> value, string key)
        {
            object result;
            return value.TryGetValue(key, out result) ? result : null;
        }

        private static bool ReadRate(object raw, out decimal? result)
        {
            result = null;
            if (raw == null) return true;
            if (!(raw is int || raw is long || raw is decimal || raw is double)) return false;
            decimal value;
            if (!decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < 0m || value > 100000m) return false;
            result = value;
            return true;
        }

        private static bool Valid(RemoteModelPrice value)
        {
            return value != null && (value.Input.HasValue || value.Cached.HasValue || value.Output.HasValue) &&
                (!value.Input.HasValue || (value.Input.Value >= 0m && value.Input.Value <= 100000m)) &&
                (!value.Cached.HasValue || (value.Cached.Value >= 0m && value.Cached.Value <= 100000m)) &&
                (!value.Output.HasValue || (value.Output.Value >= 0m && value.Output.Value <= 100000m)) &&
                (!value.Input.HasValue || !value.Cached.HasValue || value.Cached.Value <= value.Input.Value);
        }

        private static bool SamePrices(Dictionary<string, RemoteModelPrice> left, Dictionary<string, RemoteModelPrice> right)
        {
            if (left.Count != right.Count) return false;
            foreach (KeyValuePair<string, RemoteModelPrice> row in left)
            {
                RemoteModelPrice value;
                if (!right.TryGetValue(row.Key, out value) || row.Value.Input != value.Input || row.Value.Cached != value.Cached || row.Value.Output != value.Output) return false;
            }
            return true;
        }

        private static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = MaximumJsonLength };
        }

        private void LoadCache()
        {
            if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath)) return;
            try
            {
                if (new FileInfo(cachePath).Length > MaximumJsonLength) throw new InvalidDataException("price_cache_too_large");
                ModelRadarPricingDocument document = Serializer().Deserialize<ModelRadarPricingDocument>(File.ReadAllText(cachePath));
                if (document == null || document.Version != 2 || document.FetchedAt <= 0 ||
                    document.FetchedAt > clock().AddMinutes(5).ToUnixTimeMilliseconds() ||
                    document.EffectiveDate != null ||
                    document.Models == null || document.Models.Count == 0) throw new InvalidDataException("invalid_price_cache");
                Dictionary<string, RemoteModelPrice> models = new Dictionary<string, RemoteModelPrice>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, RemoteModelPrice> row in document.Models)
                {
                    if (string.IsNullOrWhiteSpace(row.Key) || !Valid(row.Value)) throw new InvalidDataException("invalid_cached_price");
                    models.Add(row.Key, row.Value);
                }
                document.Models = models;
                PricingCatalog.ReplaceRemote(models);
                cached = document;
                nextFetch = DateTimeOffset.FromUnixTimeMilliseconds(document.FetchedAt).AddHours(6);
            }
            catch { error = "price_cache_read_failed"; }
        }

        private void SaveCache(ModelRadarPricingDocument document)
        {
            if (string.IsNullOrEmpty(cachePath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath)));
            string temporary = cachePath + ".tmp";
            try
            {
                File.WriteAllText(temporary, Serializer().Serialize(document));
                if (File.Exists(cachePath)) File.Replace(temporary, cachePath, null);
                else File.Move(temporary, cachePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
