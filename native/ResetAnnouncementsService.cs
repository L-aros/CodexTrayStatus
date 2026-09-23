using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexTrayStatus
{
    internal sealed class ResetAnnouncementsResult
    {
        public bool Available { get; set; }
        public bool Stale { get; set; }
        public long FetchedAt { get; set; }
        public object Status { get; set; }
        public object Recent { get; set; }
    }

    // Public, unauthenticated announcements. Never send Codex credentials to this service.
    internal sealed class ResetAnnouncementsService
    {
        private const string StatusUrl = "https://codex-resets.com/api/v1/status";
        private const string ResetsUrl = "https://codex-resets.com/api/v1/resets?limit=5";
        private readonly HttpClient client;
        private readonly object gate = new object();
        private ResetAnnouncementsResult cached;
        private DateTime nextFetchUtc;
        private Task<ResetAnnouncementsResult> inFlight;

        internal ResetAnnouncementsService() : this(CreateClient()) { }
        internal ResetAnnouncementsService(HttpClient httpClient) { client = httpClient; }

        private static HttpClient CreateClient()
        {
            HttpClient httpClient = new HttpClient(new HttpClientHandler { UseCookies = false });
            httpClient.Timeout = TimeSpan.FromSeconds(8);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CodexTrayStatus/0.4.6");
            return httpClient;
        }

        internal Task<ResetAnnouncementsResult> GetAsync()
        {
            lock (gate)
            {
                if (cached != null && DateTime.UtcNow < nextFetchUtc) return Task.FromResult(cached);
                if (inFlight != null && !inFlight.IsCompleted) return inFlight;
                inFlight = RefreshAsync();
                return inFlight;
            }
        }

        private async Task<ResetAnnouncementsResult> RefreshAsync()
        {
            ResetAnnouncementsResult result;
            bool success = false;
            try
            {
                Task<object> statusTask = FetchData(StatusUrl);
                Task<object> resetsTask = FetchData(ResetsUrl);
                await Task.WhenAll(statusTask, resetsTask).ConfigureAwait(false);
                result = new ResetAnnouncementsResult {
                    Available = true, FetchedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Status = statusTask.Result, Recent = resetsTask.Result
                };
                success = true;
            }
            catch
            {
                ResetAnnouncementsResult old;
                lock (gate) old = cached;
                result = old != null && old.Available
                    ? new ResetAnnouncementsResult { Available = true, Stale = true, FetchedAt = old.FetchedAt, Status = old.Status, Recent = old.Recent }
                    : new ResetAnnouncementsResult { Available = false };
            }
            lock (gate)
            {
                cached = result;
                nextFetchUtc = DateTime.UtcNow.AddMinutes(success ? 15 : 2);
                inFlight = null;
            }
            return result;
        }

        private async Task<object> FetchData(string url)
        {
            using (HttpResponseMessage response = await client.GetAsync(url).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 131072) throw new InvalidOperationException("Response too large");
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (json.Length > 131072) throw new InvalidOperationException("Response too large");
                Dictionary<string, object> document = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                object data;
                if (document == null || !document.TryGetValue("data", out data) || data == null)
                    throw new InvalidOperationException("Missing announcement data");
                return data;
            }
        }
    }
}
