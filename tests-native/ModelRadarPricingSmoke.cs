using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexTrayStatus
{
    internal static class ModelRadarPricingSmoke
    {
        private static int checks;

        private sealed class FakeClock
        {
            internal DateTimeOffset Now = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
            internal DateTimeOffset Read() { return Now; }
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            internal int Calls;
            internal string Payload = Document(Row("gpt-test", "2", "0.2", "10"));
            internal bool Fail;
            internal bool Throw;
            internal bool SensitiveHeaders;
            internal readonly List<string> Urls = new List<string>();
            internal TaskCompletionSource<HttpResponseMessage> Pending;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Interlocked.Increment(ref Calls);
                lock (Urls) { Urls.Add(request.Method + " " + request.RequestUri.AbsoluteUri); }
                SensitiveHeaders |= request.Headers.Contains("Authorization") || request.Headers.Contains("Cookie") ||
                    request.Headers.Contains("ChatGPT-Account-Id");
                if (Throw) throw new HttpRequestException("Simulated offline network");
                if (Pending != null) return Pending.Task;
                return Task.FromResult(Response(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, Payload));
            }
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string json)
        {
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private static void Check(bool condition, string text)
        {
            checks++;
            if (!condition) throw new Exception(text);
        }

        private static string Row(string id, string input, string cached, string output)
        {
            return "\"" + id + "\":{\"id\":\"" + id + "\",\"cost\":{\"input\":" + input +
                ",\"cache_read\":" + cached + ",\"output\":" + output + "}}";
        }

        private static string Document(params string[] rows)
        {
            return "{\"openai\":{\"id\":\"openai\",\"models\":{" + string.Join(",", rows) + "}}}";
        }

        private static void RejectDocument(string json, string reason)
        {
            bool rejected = false;
            try { ModelRadarPricingService.Parse(json); }
            catch { rejected = true; }
            Check(rejected, reason);
        }

        private static void ParseAndValidation()
        {
            string valid = Row("gpt-test", "2", "0.2", "10");
            string rows = Document(valid,
                Row("partial", "3", "null", "null"),
                Row("zero", "0", "0", "0"),
                Row("negative", "-1", "0", "10"),
                Row("nonnumeric", "\"two\"", "0.2", "10"),
                Row("boolean", "true", "0.2", "10"),
                Row("expensive", "100001", "0.2", "10"),
                Row("swapped", "1.75", "14", "0.175"),
                Row("absent", "null", "null", "null"),
                Row("wrong-id", "2", "0.2", "10").Replace("\"id\":\"wrong-id\"", "\"id\":\"different-model\""),
                "\"malformed\":null");
            ModelRadarPricingDocument parsed = ModelRadarPricingService.Parse(rows);
            Check(parsed.Version == 2 && parsed.EffectiveDate == null, "A source without an effective date must not invent one");
            Check(parsed.Models.Count == 3, "Only valid first-party OpenAI rates should survive filtering");
            Check(parsed.Models["gpt-test"].Input == 2m && parsed.Models["gpt-test"].Cached == 0.2m &&
                parsed.Models["gpt-test"].Output == 10m, "Input/cache/output fields must retain their exact meanings");
            Check(parsed.Models["partial"].Input == 3m && !parsed.Models["partial"].Cached.HasValue &&
                !parsed.Models["partial"].Output.HasValue, "Unknown fields must stay null rather than become free rates");
            Check(parsed.Models["zero"].Input == 0m && parsed.Models["zero"].Output == 0m,
                "Explicit numeric zero remains a valid price");
            Check(parsed.RejectedModelCount >= 6, "Malformed or inconsistent rows should be counted");
            Check(!parsed.Models.ContainsKey("swapped"), "A cached price higher than input rejects the whole row");

            ModelRadarPricingDocument ambiguous = ModelRadarPricingService.Parse(Document(valid,
                Row("duplicate", "2", "0.2", "10"), Row("DUPLICATE", "3", "0.3", "15")));
            Check(ambiguous.Models.Count == 1 && !ambiguous.Models.ContainsKey("duplicate"),
                "Duplicate model IDs must not select a rate according to response order");

            RejectDocument(Document(valid).Replace("\"openai\":", "\"other-provider\":"), "Other provider sections must not replace OpenAI pricing");
            RejectDocument(Document(valid).Replace("\"id\":\"openai\"", "\"id\":\"other\""), "A mismatched provider identity must fail");
            RejectDocument("{\"openai\":{\"id\":\"openai\",\"models\":[]}}", "An array cannot replace the model dictionary");
            RejectDocument("{broken", "Malformed JSON must fail");
            RejectDocument("[]", "An array cannot replace the root document");
            RejectDocument(Document(Row("swapped", "1.75", "14", "0.175")),
                "A feed containing only invalid rows must fail instead of replacing usable prices");

            string scoped = Document(valid);
            scoped = scoped.Substring(0, scoped.Length - 1) + ",\"openrouter\":{\"models\":{\"proxy\":{\"id\":\"proxy\",\"cost\":{\"input\":1,\"output\":2,\"cache_read\":0}}}}}";
            Check(ModelRadarPricingService.Parse(scoped).Models.Count == 1,
                "Provider proxy rates outside OpenAI must never enter the catalog");
            ModelRadarPricingDocument officialSample = ModelRadarPricingService.Parse(Document(
                Row("gpt-5.3-codex", "1.75", "0.175", "14"), Row("gpt-6.1-sol", "2", "0.1", "10")));
            Check(officialSample.Models["gpt-5.3-codex"].Cached == 0.175m &&
                officialSample.Models["gpt-6.1-sol"].Output == 10m,
                "Verified Models.dev samples must map input/cache/output in official pricing order");

            string large = Document(valid);
            large = large.Substring(0, large.Length - 1) + ",\"pricingNotes\":\"" + new string('x', 2100000) + "\"}";
            Check(large.Length > 2097152, "Large JSON regression fixture must exceed the serializer default limit");
            Check(ModelRadarPricingService.Parse(large).Models["gpt-test"].Output == 10m,
                "Production-sized JSON larger than 2 MiB must parse successfully");
        }

        private static void RefreshAndRetry()
        {
            PricingCatalog.ReplaceRemote(null);
            FakeClock clock = new FakeClock();
            FakeHandler handler = new FakeHandler();
            using (HttpClient client = new HttpClient(handler))
            {
                ModelRadarPricingService service = new ModelRadarPricingService(client, null, clock.Read);
                Check(service.RefreshAsync().GetAwaiter().GetResult(), "First download must publish a changed catalog");
                Check(service.Status.Available && !service.Status.Stale && service.Status.ModelCount == 1,
                    "Successful refresh must expose an available fresh catalog");
                Check(service.Status.FetchedAt > 0 && service.Status.EffectiveDate == null &&
                    service.Status.Source == "Models.dev · OpenAI" && service.Status.SourceUrl == "https://models.dev/",
                    "Fetch metadata must report the actual source without a fabricated effective date");
                Check(PricingCatalog.ApplyRemote("gpt-test", null).Output == 10m, "Download must publish to the pricing catalog");
                Check(handler.Urls[0] == "GET https://models.dev/api.json" && !handler.SensitiveHeaders,
                    "Price downloads must use the fixed public endpoint without Codex credentials");

                clock.Now = clock.Now.AddHours(6).AddSeconds(-1);
                Check(!service.RefreshAsync().GetAwaiter().GetResult() && handler.Calls == 1, "Fresh prices must be reused for six hours");
                clock.Now = clock.Now.AddSeconds(1);
                handler.Payload = Document(Row("gpt-test", "3", "0.3", "12"));
                Check(service.RefreshAsync().GetAwaiter().GetResult() && handler.Calls == 2, "Six-hour expiry must refresh changed rates");
                Check(PricingCatalog.ApplyRemote("gpt-test", null).Input == 3m, "A changed snapshot must replace previous rates");

                clock.Now = clock.Now.AddHours(6);
                Check(!service.RefreshAsync().GetAwaiter().GetResult() && handler.Calls == 3,
                    "An unchanged successful snapshot must not report a rate change");
                clock.Now = clock.Now.AddHours(6);
                handler.Fail = true;
                Check(!service.RefreshAsync().GetAwaiter().GetResult() && handler.Calls == 4, "HTTP failure must preserve the published snapshot");
                Check(service.Status.Available && service.Status.Stale && !string.IsNullOrEmpty(service.Status.ErrorCode),
                    "Failed refresh must identify stale available prices and its failure");
                Check(PricingCatalog.ApplyRemote("gpt-test", null).Output == 12m, "HTTP failure must keep previous prices");
                clock.Now = clock.Now.AddMinutes(59);
                Check(!service.RefreshAsync().GetAwaiter().GetResult() && handler.Calls == 4, "Failed requests should wait an hour before retrying");
                clock.Now = clock.Now.AddMinutes(1);
                handler.Fail = false;
                handler.Payload = Document(Row("gpt-test", "4", "0.4", "16"));
                Check(service.RefreshAsync().GetAwaiter().GetResult() && handler.Calls == 5, "A one-hour retry must recover updated prices");
                Check(!service.Status.Stale && string.IsNullOrEmpty(service.Status.ErrorCode), "Successful retry must clear stale error state");

                clock.Now = clock.Now.AddHours(6);
                handler.Payload = Document();
                Check(!service.RefreshAsync().GetAwaiter().GetResult() && service.Status.Available,
                    "An empty remote result must not clear usable downloaded prices");
                Check(PricingCatalog.ApplyRemote("gpt-test", null).Output == 16m, "An empty result must retain previous rates");
                clock.Now = clock.Now.AddHours(1);
                handler.Payload = "{broken";
                Check(!service.RefreshAsync().GetAwaiter().GetResult() && service.Status.Stale,
                    "Invalid JSON must become a recoverable refresh error");
                Check(PricingCatalog.ApplyRemote("gpt-test", null).Output == 16m, "Malformed JSON must retain previous rates");
            }
        }

        private static void ConcurrentRequestsAndInitialFailure()
        {
            PricingCatalog.ReplaceRemote(null);
            FakeClock clock = new FakeClock();
            FakeHandler handler = new FakeHandler { Pending = new TaskCompletionSource<HttpResponseMessage>() };
            using (HttpClient client = new HttpClient(handler))
            {
                ModelRadarPricingService service = new ModelRadarPricingService(client, null, clock.Read);
                Task<bool> first = service.RefreshAsync();
                Task<bool> second = service.RefreshAsync(true);
                Check(object.ReferenceEquals(first, second) && handler.Calls == 1 && !first.IsCompleted && !second.IsCompleted,
                    "Concurrent refresh callers must share the active HTTP request");
                handler.Pending.SetResult(Response(HttpStatusCode.OK, handler.Payload));
                Task.WaitAll(first, second);
                Check(handler.Calls == 1 && service.Status.Available, "Both callers must complete from the shared response");
            }

            PricingCatalog.ReplaceRemote(null);
            handler = new FakeHandler { Throw = true };
            using (HttpClient client = new HttpClient(handler))
            {
                ModelRadarPricingService service = new ModelRadarPricingService(client, null, clock.Read);
                Check(!service.RefreshAsync().GetAwaiter().GetResult() && !service.Status.Available,
                    "An initial network error should leave local fallback pricing available to callers");
                Check(!string.IsNullOrEmpty(service.Status.ErrorCode), "An initial network error must have a status code");
                Check(PricingCatalog.ApplyRemote("gpt-test", null) == null, "A failed first download must not invent remote prices");
            }
        }

        private static void ForcedRefresh()
        {
            PricingCatalog.ReplaceRemote(null);
            FakeClock clock = new FakeClock();
            FakeHandler handler = new FakeHandler();
            using (HttpClient client = new HttpClient(handler))
            {
                ModelRadarPricingService service = new ModelRadarPricingService(client, null, clock.Read);
                service.RefreshAsync().GetAwaiter().GetResult();
                handler.Payload = Document(Row("gpt-test", "3", "0.3", "12"));
                Check(!service.RefreshAsync().GetAwaiter().GetResult() && handler.Calls == 1,
                    "Automatic refresh must reuse an unexpired snapshot");
                Check(service.RefreshAsync(true).GetAwaiter().GetResult() && handler.Calls == 2 &&
                    PricingCatalog.ApplyRemote("gpt-test", null).Input == 3m,
                    "Manual refresh must immediately request and apply updated prices");
                handler.Fail = true;
                Check(!service.RefreshAsync(true).GetAwaiter().GetResult() && service.Status.Stale,
                    "Manual refresh failure must retain prices and expose stale status");
                handler.Fail = false;
                Check(!service.RefreshAsync(true).GetAwaiter().GetResult() && handler.Calls == 4 && !service.Status.Stale,
                    "Manual retry must bypass backoff and clear errors even when prices are unchanged");
            }
        }

        private static void DiskCacheAndRecovery()
        {
            string directory = Path.Combine(Path.GetTempPath(), "codex-modelradar-test-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "prices.json");
            Directory.CreateDirectory(directory);
            try
            {
                PricingCatalog.ReplaceRemote(null);
                FakeClock clock = new FakeClock();
                FakeHandler handler = new FakeHandler();
                string huge = handler.Payload;
                handler.Payload = huge.Substring(0, huge.Length - 1) + ",\"pricingNotes\":\"" + new string('x', 2100000) + "\"}";
                using (HttpClient client = new HttpClient(handler))
                {
                    ModelRadarPricingService service = new ModelRadarPricingService(client, path, clock.Read);
                    Check(service.RefreshAsync().GetAwaiter().GetResult(), "A successful network result should populate the disk cache");
                    Check(File.Exists(path) && new FileInfo(path).Length < 100000,
                        "Disk cache should retain normalized prices rather than large irrelevant source notes");
                }

                PricingCatalog.ReplaceRemote(null);
                clock.Now = clock.Now.AddMinutes(1);
                handler = new FakeHandler { Fail = true };
                using (HttpClient client = new HttpClient(handler))
                {
                    ModelRadarPricingService restarted = new ModelRadarPricingService(client, path, clock.Read);
                    Check(restarted.Status.Available && restarted.Status.ModelCount == 1, "Restart must restore cached price metadata");
                    Check(PricingCatalog.ApplyRemote("gpt-test", null).Output == 10m, "Restart must publish cached prices before any network request");
                    Check(!restarted.RefreshAsync().GetAwaiter().GetResult() && handler.Calls == 0,
                        "Restart must respect the persisted cache freshness interval");
                    clock.Now = clock.Now.AddHours(6);
                    Check(!restarted.RefreshAsync().GetAwaiter().GetResult() && handler.Calls == 1 && restarted.Status.Stale,
                        "An expired disk cache should remain usable when offline");
                    Check(PricingCatalog.ApplyRemote("gpt-test", null).Output == 10m, "Offline restart must retain last known prices");
                }

                File.WriteAllText(path, "{broken");
                PricingCatalog.ReplaceRemote(null);
                handler = new FakeHandler();
                using (HttpClient client = new HttpClient(handler))
                {
                    ModelRadarPricingService recovered = new ModelRadarPricingService(client, path, clock.Read);
                    Check(!recovered.Status.Available, "Corrupt disk caches should be ignored without throwing");
                    Check(recovered.RefreshAsync().GetAwaiter().GetResult() && handler.Calls == 1 && recovered.Status.Available,
                        "A corrupt disk cache must not prevent a fresh successful download");
                }
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        private static int LiveSource()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (HttpClient client = new HttpClient(new HttpClientHandler {
                UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            }))
            {
                ModelRadarPricingService service = new ModelRadarPricingService(client, null, null);
                service.RefreshAsync(true).GetAwaiter().GetResult();
                ModelRadarPricingState status = service.Status;
                Check(status.Available && !status.Stale, "Live Models.dev source must download and parse successfully");
                Console.WriteLine("Live source=" + status.Source + " models=" + status.ModelCount +
                    " rejected=" + status.RejectedModelCount + " fetchedAt=" + status.FetchedAt);
                foreach (string id in new string[] { "gpt-5.3-codex", "gpt-5.4", "gpt-6.1-sol", "gpt-6-astra" })
                {
                    ModelPrice price = PricingCatalog.ApplyRemote(id, null);
                    Check(price != null, "Expected Codex model must have complete live pricing: " + id);
                    Console.WriteLine(id + " input=" + price.Input + " cached=" + price.Cached + " output=" + price.Output);
                }
            }
            return 0;
        }

        private static int Main(string[] args)
        {
            try
            {
                // Explicit opt-in keeps the normal suite deterministic and offline.
                if (args.Length == 1 && args[0] == "--live") return LiveSource();
                ParseAndValidation();
                RefreshAndRetry();
                ConcurrentRequestsAndInitialFailure();
                ForcedRefresh();
                DiskCacheAndRecovery();
                Console.WriteLine("Models.dev pricing service tests passed: " + checks + " checks.");
                return 0;
            }
            finally { PricingCatalog.ReplaceRemote(null); }
        }
    }
}
