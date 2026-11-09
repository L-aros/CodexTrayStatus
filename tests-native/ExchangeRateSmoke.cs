using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexTrayStatus
{
    internal static class ExchangeRateSmoke
    {
        private static int checks;
        private const string ValidJson = "{\"date\":\"2026-11-02\",\"base\":\"USD\",\"quote\":\"CNY\",\"rate\":6.6483}";
        private const string ValidXml = "<?xml version=\"1.0\"?><gesmes:Envelope xmlns:gesmes=\"http://www.gesmes.org/xml/2002-08-01\" xmlns=\"http://www.ecb.int/vocabulary/2002-08-01/eurofxref\"><Cube><Cube time=\"2026-11-02\"><Cube currency=\"USD\" rate=\"1.1216\"/><Cube currency=\"CNY\" rate=\"7.4567\"/></Cube></Cube></gesmes:Envelope>";

        private sealed class FakeClock
        {
            internal DateTimeOffset Now = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
            internal DateTimeOffset Read() { return Now; }
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            internal readonly List<string> Urls = new List<string>();
            internal string Primary = ValidJson;
            internal string Backup = ValidXml;
            internal HttpStatusCode PrimaryStatus = HttpStatusCode.OK;
            internal HttpStatusCode BackupStatus = HttpStatusCode.OK;
            internal TaskCompletionSource<HttpResponseMessage> Pending;
            internal bool SensitiveHeaders;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                lock (Urls) { Urls.Add(request.Method + " " + request.RequestUri.AbsoluteUri); }
                SensitiveHeaders |= request.Headers.Contains("Authorization") || request.Headers.Contains("Cookie") || request.Headers.Contains("ChatGPT-Account-Id");
                bool primary = request.RequestUri.AbsoluteUri == ExchangeRateService.Url;
                if (primary && Pending != null) return Pending.Task;
                return Task.FromResult(Response(primary ? PrimaryStatus : BackupStatus, primary ? Primary : Backup));
            }
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body)
        {
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }

        private static void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new Exception(message);
        }

        private static void Reject(Action action, string message)
        {
            bool failed = false;
            try { action(); } catch { failed = true; }
            Check(failed, message);
        }

        private static void ParserTests()
        {
            ExchangeRateDocument value = ExchangeRateService.Parse(ValidJson);
            Check(value.Rate == 6.6483m && value.AsOf == "2026-11-02", "JSON quote and true source date must be preserved");
            Check(value.Source == "ECB / Frankfurter" && value.SourceUrl == "https://frankfurter.dev/", "Provider attribution must be fixed by the selected endpoint");
            Check(ExchangeRateService.ParseEcb(ValidXml).Rate == 6.6483m, "ECB fallback must divide same-day EUR/CNY by EUR/USD");
            Reject(delegate { ExchangeRateService.Parse(ValidJson.Replace("\"USD\"", "\"EUR\"")); }, "Wrong base must fail instead of silently converting incorrectly");
            Reject(delegate { ExchangeRateService.Parse(ValidJson.Replace("\"CNY\"", "\"JPY\"")); }, "Wrong quote must fail");
            Reject(delegate { ExchangeRateService.Parse(ValidJson.Replace("6.6483", "0")); }, "Zero cannot become a valid exchange rate");
            Reject(delegate { ExchangeRateService.Parse(ValidJson.Replace("6.6483", "-7")); }, "Negative exchange rates must fail");
            Reject(delegate { ExchangeRateService.Parse(ValidJson.Replace("6.6483", "\"6.6483\"")); }, "Unexpected strings must fail schema validation");
            Reject(delegate { ExchangeRateService.Parse(ValidJson.Replace("6.6483", "true")); }, "Boolean values cannot become numeric rates");
            Reject(delegate { ExchangeRateService.Parse(ValidJson.Replace("2026-11-02", "2026-02-30")); }, "Impossible source dates must fail");
            Reject(delegate { ExchangeRateService.ParseEcb(ValidXml.Replace("currency=\"USD\"", "currency=\"EUR\"")); }, "ECB response must contain both source currencies");
            Reject(delegate { ExchangeRateService.ParseEcb(ValidXml.Replace("1.1216", "0")); }, "ECB division by zero must fail");
            Reject(delegate { ExchangeRateService.ParseEcb(ValidXml.Replace("</Cube></Cube>", "<Cube currency=\"CNY\" rate=\"8\"/></Cube></Cube>")); }, "Ambiguous ECB rates must fail");
            Reject(delegate { ExchangeRateService.ParseEcb("<!DOCTYPE x [<!ENTITY v '7'>]><Cube time=\"2026-11-02\"><Cube currency=\"USD\" rate=\"1\"/><Cube currency=\"CNY\" rate=\"&v;\"/></Cube>"); }, "XML external/entity declarations must not be processed");
        }

        private static async Task RefreshTests()
        {
            FakeClock clock = new FakeClock();
            FakeHandler handler = new FakeHandler();
            using (HttpClient client = new HttpClient(handler))
            {
                ExchangeRateService service = new ExchangeRateService(client, null, clock.Read);
                Check(!service.Status.Available && service.Status.Rate == 0m, "No remote quote should be invented before a download");
                Check(await service.RefreshAsync(), "First valid quote should report an effective change");
                Check(service.Status.Available && !service.Status.Stale && service.Status.Rate == 6.6483m, "A downloaded quote must be exposed for the conversion consumer");
                Check(service.Status.FetchedAt == clock.Now.ToUnixTimeMilliseconds() && service.Status.AsOf == "2026-11-02", "Fetch time and source date must remain distinct");
                Check(!await service.RefreshAsync() && handler.Urls.Count == 1, "Routine refreshes must respect the hourly interval");
                clock.Now = clock.Now.AddMinutes(2);
                Check(!await service.RefreshAsync(true) && handler.Urls.Count == 2, "Manual refresh must bypass the interval without reporting unchanged quotes as changes");
                Check(service.Status.FetchedAt == clock.Now.ToUnixTimeMilliseconds(), "An unchanged successful quote still records a new successful check");
                handler.Primary = ValidJson.Replace("6.6483", "6.7");
                Check(await service.RefreshAsync(true) && service.Status.Rate == 6.7m, "A changed quote must become available immediately");
                handler.Primary = "malformed";
                Check(await service.RefreshAsync(true) && service.Status.Source == "ECB", "A malformed primary response should use the official ECB fallback");
                Check(handler.Urls[handler.Urls.Count - 1] == "GET " + ExchangeRateService.FallbackUrl, "Fallback requests must use the fixed official endpoint");
                long lastSuccess = service.Status.FetchedAt;
                handler.BackupStatus = HttpStatusCode.ServiceUnavailable;
                Check(!await service.RefreshAsync(true) && service.Status.Available && service.Status.Stale, "Failure of both endpoints must retain the last good quote and mark it stale");
                Check(service.Status.Rate == 6.6483m && service.Status.FetchedAt == lastSuccess, "Failed checks must not alter the rate or successful timestamp");
                int calls = handler.Urls.Count;
                clock.Now = clock.Now.AddMinutes(4);
                Check(!await service.RefreshAsync() && handler.Urls.Count == calls, "Failure retry must be throttled");
                clock.Now = clock.Now.AddMinutes(1);
                handler.Primary = ValidJson;
                Check(await service.RefreshAsync() && !service.Status.Stale, "The service must recover when retry becomes due");
                handler.Primary = ValidJson.Replace("2026-11-02", "2026-11-01");
                Check(!await service.RefreshAsync(true) && service.Status.AsOf == "2026-11-02", "A delayed source must not replace a newer cached reference date");
                handler.Primary = ValidJson.Replace("2026-11-02", "2099-11-02");
                Check(!await service.RefreshAsync(true) && service.Status.AsOf == "2026-11-02", "Future source dates must not become usable quotes");
                Check(!handler.SensitiveHeaders, "Public exchange requests must not carry Codex credentials");
                clock.Now = clock.Now.AddHours(2);
                Check(service.Status.Stale, "An overdue quote must become stale even before the next network attempt");
            }
        }

        private static async Task CoalescingTests()
        {
            FakeClock clock = new FakeClock();
            FakeHandler handler = new FakeHandler { Pending = new TaskCompletionSource<HttpResponseMessage>() };
            using (HttpClient client = new HttpClient(handler))
            {
                ExchangeRateService service = new ExchangeRateService(client, null, clock.Read);
                Task<bool> first = service.RefreshAsync();
                Task<bool> second = service.RefreshAsync(true);
                Check(object.ReferenceEquals(first, second) && handler.Urls.Count == 1, "Concurrent automatic and forced refreshes must share one request");
                handler.Pending.SetResult(Response(HttpStatusCode.OK, ValidJson));
                Check(await first && await second && service.Status.Available, "Both callers must observe the one successful publication");
            }
        }

        private static async Task CacheTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "codex-exchange-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "exchange.json");
            FakeClock clock = new FakeClock();
            FakeHandler handler = new FakeHandler();
            try
            {
                using (HttpClient client = new HttpClient(handler))
                {
                    ExchangeRateService service = new ExchangeRateService(client, path, clock.Read);
                    await service.RefreshAsync();
                    Check(File.Exists(path), "Successful quotes must be cached for offline restart");
                    handler.PrimaryStatus = handler.BackupStatus = HttpStatusCode.ServiceUnavailable;
                    ExchangeRateService restored = new ExchangeRateService(client, path, clock.Read);
                    Check(restored.Status.Available && restored.Status.Rate == 6.6483m && !restored.Status.Stale, "A valid recent cache must be immediately available at restart");
                    int calls = handler.Urls.Count;
                    Check(!await restored.RefreshAsync() && handler.Urls.Count == calls, "Restarting must retain the successful check interval");
                    Check(!await restored.RefreshAsync(true) && restored.Status.Available && restored.Status.Stale, "Offline restart must retain the cached quote");
                    ExchangeRateDocument poisoned = new JavaScriptSerializer().Deserialize<ExchangeRateDocument>(File.ReadAllText(path));
                    poisoned.Rate = 0m;
                    File.WriteAllText(path, new JavaScriptSerializer().Serialize(poisoned));
                    Check(!new ExchangeRateService(client, path, clock.Read).Status.Available, "Invalid cached quotes must not become the effective exchange rate");
                    File.WriteAllText(path, "not-json");
                    Check(!new ExchangeRateService(client, path, clock.Read).Status.Available, "Unreadable cache must leave the consumer's fallback intact");
                    // A cache write failure must not discard a valid in-memory quote.
                    handler.PrimaryStatus = HttpStatusCode.OK;
                    Directory.CreateDirectory(Path.Combine(root, "directory-as-file"));
                    ExchangeRateService readOnly = new ExchangeRateService(client, Path.Combine(root, "directory-as-file"), clock.Read);
                    Check(await readOnly.RefreshAsync() && readOnly.Status.Rate == 6.6483m && readOnly.Status.ErrorCode == "exchange_cache_write_failed", "Cache failures must preserve a successful current quote");
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static int Main()
        {
            try
            {
                ParserTests();
                RefreshTests().GetAwaiter().GetResult();
                CoalescingTests().GetAwaiter().GetResult();
                CacheTests().GetAwaiter().GetResult();
                Console.WriteLine("Exchange-rate smoke tests passed (" + checks + " checks).");
                return 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        }
    }
}
