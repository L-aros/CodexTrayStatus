using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;

namespace CodexTrayStatus
{
    internal sealed class ExchangeRateState
    {
        public bool Available { get; set; }
        public bool Stale { get; set; }
        public decimal Rate { get; set; }
        public string AsOf { get; set; }
        public long FetchedAt { get; set; }
        public string Source { get; set; }
        public string SourceUrl { get; set; }
        public string ErrorCode { get; set; }
    }

    internal sealed class ExchangeRateDocument
    {
        public int Version { get; set; }
        public decimal Rate { get; set; }
        public string AsOf { get; set; }
        public long FetchedAt { get; set; }
        public string Source { get; set; }
        public string SourceUrl { get; set; }
    }

    // Both endpoints publish the ECB daily reference rate. The fallback calculates
    // USD/CNY from the same day's EUR/CNY and EUR/USD, never from mixed providers.
    internal sealed class ExchangeRateService
    {
        internal const string Url = "https://api.frankfurter.dev/v2/providers/ecb/rate/USD/CNY";
        internal const string FallbackUrl = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
        private const string FrankfurterSource = "ECB / Frankfurter";
        private const string EcbSource = "ECB";
        private const string FrankfurterPage = "https://frankfurter.dev/";
        private const string EcbPage = "https://www.ecb.europa.eu/stats/policy_and_exchange_rates/euro_reference_exchange_rates/html/index.en.html";
        private const int MaximumLength = 128 * 1024;
        private readonly HttpClient client;
        private readonly string cachePath;
        private readonly Func<DateTimeOffset> clock;
        private readonly object gate = new object();
        private ExchangeRateDocument cached;
        private string error;
        private DateTimeOffset nextFetch;
        private Task<bool> inFlight;

        internal ExchangeRateService() : this(CreateClient(), Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexTrayStatus", "exchange-rate.json"), delegate { return DateTimeOffset.UtcNow; }) { }

        internal ExchangeRateService(HttpClient httpClient, string path, Func<DateTimeOffset> now)
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
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });
            result.MaxResponseContentBufferSize = MaximumLength;
            result.DefaultRequestHeaders.UserAgent.ParseAdd("CodexTrayStatus");
            return result;
        }

        internal ExchangeRateState Status
        {
            get
            {
                lock (gate)
                {
                    return new ExchangeRateState {
                        Available = cached != null,
                        Stale = cached != null && (error == "exchange_refresh_failed" ||
                            clock().ToUnixTimeMilliseconds() - cached.FetchedAt >= TimeSpan.FromHours(1).TotalMilliseconds ||
                            clock().UtcDateTime.Date - DateTime.ParseExact(cached.AsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture) > TimeSpan.FromDays(7)),
                        Rate = cached == null ? 0m : cached.Rate,
                        AsOf = cached == null ? null : cached.AsOf,
                        FetchedAt = cached == null ? 0 : cached.FetchedAt,
                        Source = cached == null ? FrankfurterSource : cached.Source,
                        SourceUrl = cached == null ? FrankfurterPage : cached.SourceUrl,
                        ErrorCode = error
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
                ExchangeRateDocument document = null;
                try
                {
                    document = Parse(await DownloadAsync(Url).ConfigureAwait(false));
                    ValidateReferenceDate(document);
                }
                catch { document = null; }
                if (document == null)
                {
                    document = ParseEcb(await DownloadAsync(FallbackUrl).ConfigureAwait(false));
                    ValidateReferenceDate(document);
                }
                document.FetchedAt = clock().ToUnixTimeMilliseconds();
                bool changed;
                lock (gate)
                {
                    if (cached != null && string.CompareOrdinal(document.AsOf, cached.AsOf) < 0)
                        throw new InvalidDataException("older_exchange_date");
                    changed = cached == null || cached.Rate != document.Rate || cached.AsOf != document.AsOf || cached.Source != document.Source;
                    cached = document;
                    error = null;
                    nextFetch = clock().AddHours(1);
                }
                try { SaveCache(document); }
                catch { lock (gate) error = "exchange_cache_write_failed"; }
                return changed;
            }
            catch
            {
                lock (gate)
                {
                    error = "exchange_refresh_failed";
                    nextFetch = clock().AddMinutes(5);
                }
                return false;
            }
        }

        private void ValidateReferenceDate(ExchangeRateDocument document)
        {
            if (DateTime.ParseExact(document.AsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture) > clock().UtcDateTime.Date.AddDays(1))
                throw new InvalidDataException("future_exchange_date");
            lock (gate)
            {
                if (cached != null && string.CompareOrdinal(document.AsOf, cached.AsOf) < 0)
                    throw new InvalidDataException("older_exchange_date");
            }
        }

        private async Task<string> DownloadAsync(string url)
        {
            using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            using (HttpResponseMessage response = await client.GetAsync(url, timeout.Token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaximumLength) throw new InvalidDataException("exchange_feed_too_large");
                string value = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (value.Length > MaximumLength) throw new InvalidDataException("exchange_feed_too_large");
                return value;
            }
        }

        internal static ExchangeRateDocument Parse(string json)
        {
            Dictionary<string, object> root = Serializer().DeserializeObject(json) as Dictionary<string, object>;
            object raw;
            decimal rate;
            if (root == null || !root.ContainsKey("base") || !Equals(root["base"], "USD") ||
                !root.ContainsKey("quote") || !Equals(root["quote"], "CNY") ||
                !root.TryGetValue("rate", out raw) ||
                !(raw is int || raw is long || raw is decimal || raw is double) ||
                !decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out rate) ||
                !ValidRate(rate) || !root.TryGetValue("date", out raw))
                throw new InvalidDataException("invalid_exchange_feed");
            return Document(rate, raw as string, FrankfurterSource, FrankfurterPage);
        }

        internal static ExchangeRateDocument ParseEcb(string xml)
        {
            string date = null;
            decimal? usd = null, cny = null;
            XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (StringReader input = new StringReader(xml))
            using (XmlReader reader = XmlReader.Create(input, settings))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Cube") continue;
                    string time = reader.GetAttribute("time");
                    if (time != null)
                    {
                        if (date != null) throw new InvalidDataException("ambiguous_exchange_date");
                        date = time;
                    }
                    string currency = reader.GetAttribute("currency");
                    if (currency != "USD" && currency != "CNY") continue;
                    decimal value;
                    if (date == null || !decimal.TryParse(reader.GetAttribute("rate"), NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out value) || value <= 0m)
                        throw new InvalidDataException("invalid_ecb_rate");
                    if (currency == "USD")
                    {
                        if (usd.HasValue) throw new InvalidDataException("duplicate_ecb_rate");
                        usd = value;
                    }
                    else
                    {
                        if (cny.HasValue) throw new InvalidDataException("duplicate_ecb_rate");
                        cny = value;
                    }
                }
            }
            if (!usd.HasValue || !cny.HasValue) throw new InvalidDataException("missing_ecb_rate");
            // Match Frankfurter's four-decimal currency-pair quote precision.
            return Document(decimal.Round(cny.Value / usd.Value, 4, MidpointRounding.AwayFromZero), date, EcbSource, EcbPage);
        }

        private static ExchangeRateDocument Document(decimal rate, string date, string source, string sourceUrl)
        {
            DateTime parsed;
            if (!ValidRate(rate) || !DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                throw new InvalidDataException("invalid_exchange_rate");
            return new ExchangeRateDocument { Version = 1, Rate = rate, AsOf = date, Source = source, SourceUrl = sourceUrl };
        }

        private static bool ValidRate(decimal value) { return value > 0m && value <= 1000m; }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = MaximumLength }; }

        private void LoadCache()
        {
            if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath)) return;
            try
            {
                if (new FileInfo(cachePath).Length > MaximumLength) throw new InvalidDataException("exchange_cache_too_large");
                ExchangeRateDocument value = Serializer().Deserialize<ExchangeRateDocument>(File.ReadAllText(cachePath));
                if (value == null || value.Version != 1 || value.FetchedAt <= 0 ||
                    value.FetchedAt > clock().AddMinutes(5).ToUnixTimeMilliseconds() ||
                    !((value.Source == FrankfurterSource && value.SourceUrl == FrankfurterPage) ||
                      (value.Source == EcbSource && value.SourceUrl == EcbPage))) throw new InvalidDataException("invalid_exchange_cache");
                Document(value.Rate, value.AsOf, value.Source, value.SourceUrl);
                if (DateTime.ParseExact(value.AsOf, "yyyy-MM-dd", CultureInfo.InvariantCulture) > clock().UtcDateTime.Date.AddDays(1))
                    throw new InvalidDataException("future_exchange_date");
                cached = value;
                nextFetch = DateTimeOffset.FromUnixTimeMilliseconds(value.FetchedAt).AddHours(1);
            }
            catch { error = "exchange_cache_read_failed"; }
        }

        private void SaveCache(ExchangeRateDocument document)
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
