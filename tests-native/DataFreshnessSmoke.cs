using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CodexTrayStatus;

internal static class DataFreshnessSmoke
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static RateLimitWindow Window(long now) { return new RateLimitWindow { Id = "account.primary_window", WindowKey = "7:account:primary_window", WindowSeconds = 18000, UsedPercent = 25, RemainingPercent = 75, ResetsAt = now + 3600000 }; }
    private static DataState State(long now) { return new DataState { Source = "official", Scope = "account", Delivery = "updated", AccountKey = "test-account", ObservedAt = now, LastSuccessAt = now, LastOfficialSuccessAt = now, TimeBasis = "response_received", IsComplete = true, IsNewObservation = true, ObservationId = "one" }; }
    private static AppSnapshot Snapshot(long now)
    {
        DataState state = State(now);
        return new AppSnapshot { RequestedAccountKey = state.AccountKey, QuotaState = state, Quota = new QuotaResult { State = state, Limits = new List<RateLimitWindow> { Window(now) } }, Today = new TodayUsage { TotalTokens = 42 }, Daily = new List<DailyUsage>(), UsageState = new DataState { Source = "local", Scope = "local_logs", Delivery = "updated", TimeBasis = "scan_completed", ObservedAt = now, LastSuccessAt = now, IsComplete = true, CoverageEndDate = DateTime.Today.ToString("yyyy-MM-dd") } };
    }
    private static void PolicyAndMerge()
    {
        long now = DataFreshness.Now;
        DataState state = State(now);
        RateLimitWindow window = Window(now);
        Check(DataFreshness.EvaluateQuota(window, state, now).IsUsable, "fresh known observation is usable");
        Check(!DataFreshness.EvaluateQuota(window, state, now + 900000).IsFresh, "exact expiry boundary");
        window.ResetsAt = now;
        Check(!DataFreshness.EvaluateQuota(window, state, now).IsUsable && window.RemainingPercent == 75, "reset invalidates without inventing 100 percent");
        window = Window(now); window.UsedPercent = double.NaN;
        Check(!DataFreshness.EvaluateQuota(window, state, now).IsFresh, "invalid value is never fresh");
        window = Window(now); state.ObservedAt = now + 60001;
        Check(!DataFreshness.EvaluateQuota(window, state, now).IsUsable, "future clock rejected");
        state = State(now); state.AccountKey = null;
        Check(!DataFreshness.EvaluateQuota(window, state, now).IsUsable, "unknown account disables automatic decisions");
        state = State(now); window.WindowKey = null;
        Check(!DataFreshness.EvaluateQuota(window, state, now).IsUsable, "unknown window disables automatic decisions");
        AppSnapshot previous = Snapshot(now);
        AppSnapshot failed = new AppSnapshot { RequestedAccountKey = "test-account", QuotaState = DataFreshness.Unavailable(now + 1, "offline"), UsageState = DataFreshness.Unavailable(now + 1, "locked") };
        AppSnapshot merged = DataFreshness.Merge(previous, failed);
        Check(merged.QuotaState.Delivery == "retained" && !merged.QuotaState.IsNewObservation && merged.QuotaState.ObservedAt == now, "failure preserves observation time only");
        Check(object.ReferenceEquals(merged.Quota.State, merged.QuotaState), "one authoritative quota state");
        Check(previous.QuotaState.IsNewObservation && !object.ReferenceEquals(previous.Quota, merged.Quota), "merge does not mutate prior quota");
        Check(merged.UsageState.Delivery == "retained" && merged.Today.TotalTokens == 42, "usage retained independently");
        AppSnapshot switched = DataFreshness.Merge(previous, new AppSnapshot { RequestedAccountKey = "other-account", QuotaState = DataFreshness.Unavailable(now, "offline") });
        Check(switched.Quota == null, "account switch never retains another account");
        AppSnapshot recovered = Snapshot(now + 100); recovered.QuotaState.ObservationId = "two";
        recovered = DataFreshness.Merge(merged, recovered);
        Check(recovered.QuotaState.Delivery == "updated" && recovered.QuotaState.IsNewObservation && recovered.QuotaState.ErrorCode == null, "recovery clears failure");
        previous.UsageState.CoverageEndDate = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
        DataFreshness.Synchronize(previous);
        Check(previous.Today == null && !DataFreshness.EvaluateUsage(previous.UsageState, true, now).IsUsable, "midnight cannot relabel yesterday totals");
        Check(QuotaService.AccountIdentity("account-a") == QuotaService.AccountIdentity("account-a") && QuotaService.AccountIdentity("account-a") != QuotaService.AccountIdentity("account-b") && QuotaService.AccountIdentity(null) == null, "stable isolated identity");
        string pool = "{\"primary_window\":{\"limit_window_seconds\":18000,\"used_percent\":25,\"resets_in_seconds\":3600}}";
        List<RateLimitWindow> pools = QuotaService.ParseRateLimits("{\"rate_limit\":" + pool + ",\"additional_rate_limits\":[{\"limit_name\":\"reserve\",\"rate_limit\":" + pool + "},{\"rate_limit\":" + pool + "}]}", now);
        Check(pools.Count == 3 && pools.FindAll(delegate(RateLimitWindow w) { return w.WindowKey != null; }).Count == 2, "same-label pools remain distinct and unnamed pool identity stays unknown");
    }

    private sealed class Handler : HttpMessageHandler
    {
        public bool Online;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            return Task.FromResult(new HttpResponseMessage(Online ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"rate_limit\":{\"primary_window\":{\"limit_window_seconds\":18000,\"used_percent\":25,\"resets_in_seconds\":3600}}}") });
        }
    }

    private static string LocalEvent(long timestamp, int used)
    {
        string stamp = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToString("o");
        return "{\"type\":\"event_msg\",\"timestamp\":\"" + stamp + "\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"primary\":{\"window_minutes\":300,\"used_percent\":" + used + ",\"resets_in_seconds\":10}}}}";
    }

    private static void Integration()
    {
        string root = Path.Combine(Path.GetTempPath(), "codex-freshness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        try
        {
            // Synthetic credentials only. No live login or session content is read.
            File.WriteAllText(Path.Combine(root, "auth.json"), "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"fake\",\"account_id\":\"synthetic-a\"}}");
            long now = DataFreshness.Now;
            string a = Path.Combine(root, "sessions", "a.jsonl"), b = Path.Combine(root, "sessions", "b.jsonl");
            File.WriteAllText(a, LocalEvent(now - 3600000, 80));
            File.WriteAllText(b, LocalEvent(now - 1800000, 60));
            File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(1));
            Handler handler = new Handler();
            using (HttpClient client = new HttpClient(handler))
            {
                QuotaService service = new QuotaService(client, root);
                AppSnapshot local = service.RefreshAsync().GetAwaiter().GetResult();
                Check(local.QuotaState.ObservedAt == now - 1800000 && local.Quota.Limits[0].RemainingPercent == 40, "select latest event not file mtime, never infer reset");
                Check(local.QuotaState.AccountKey == null && !DataFreshness.EvaluateQuota(local.Quota.Limits[0], local.QuotaState, now).IsUsable, "unattributed stale fallback is display only");
                AppSnapshot repeat = DataFreshness.Merge(local, service.RefreshAsync().GetAwaiter().GetResult());
                Check(!repeat.QuotaState.IsNewObservation && repeat.QuotaState.ObservedAt == local.QuotaState.ObservedAt, "repeated local read is not a new observation");
                handler.Online = true;
                AppSnapshot online = DataFreshness.Merge(repeat, service.RefreshAsync().GetAwaiter().GetResult());
                Check(online.QuotaState.Source == "official" && online.QuotaState.AccountKey == QuotaService.AccountIdentity("synthetic-a") && online.QuotaState.LastOfficialSuccessAt.HasValue, "online recovery binds real request account");
                handler.Online = false;
                AppSnapshot retained = DataFreshness.Merge(online, service.RefreshAsync().GetAwaiter().GetResult());
                Check(retained.QuotaState.Delivery == "retained" && retained.UsageState.Delivery == "updated", "quota failure and successful logs are independent");
                handler.Online = true;
                File.AppendAllText(a, "\n");
                using (FileStream locked = new FileStream(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    AppSnapshot partial = DataFreshness.Merge(retained, service.RefreshAsync().GetAwaiter().GetResult());
                    Check(partial.QuotaState.Delivery == "updated" && partial.UsageState.Delivery == "retained", "online succeeds while locked logs retain totals");
                }
                File.WriteAllText(Path.Combine(root, "auth.json"), "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"fake\",\"account_id\":\"synthetic-b\"}}");
                handler.Online = false;
                AppSnapshot switched = DataFreshness.Merge(online, service.RefreshAsync().GetAwaiter().GetResult());
                Check(switched.QuotaState.AccountKey == null && switched.QuotaState.Source == "local", "switch cannot relabel account A as B");
                File.WriteAllText(a, LocalEvent(now, 20).Replace("\"timestamp\":\"" + DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("o") + "\",", ""));
                File.Delete(b);
                AppSnapshot unknown = service.RefreshAsync().GetAwaiter().GetResult();
                Check(unknown.QuotaState.ObservedAt == null && unknown.QuotaState.TimeBasis == "unknown", "mtime cannot substitute for true observation time");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    public static int Main()
    {
        try { PolicyAndMerge(); Integration(); Console.WriteLine("Data freshness tests passed."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
