using System;
using System.Collections.Generic;
using System.IO;
using CodexTrayStatus;

internal static class QuotaHistorySmoke
{
    private static int checks;
    private static void Check(bool condition, string text) { checks++; if (!condition) throw new Exception(text); }
    private static AppSnapshot Snapshot(long now, string observation, long cycle, double used, string account, string window)
    {
        DataState state = new DataState { Source = "official", Delivery = "updated", Scope = "account", AccountKey = account,
            ObservationId = observation, ObservedAt = now, TimeBasis = "response_received", IsNewObservation = true, IsComplete = true };
        return new AppSnapshot { QuotaState = state, Quota = new QuotaResult { Limits = new List<RateLimitWindow> {
            new RateLimitWindow { Id = window, WindowKey = window, WindowSeconds = 18000, UsedPercent = used, RemainingPercent = 100-used, ResetsAt = cycle }
        } } };
    }
    public static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "codex-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); string path = Path.Combine(root, "quota-history.jsonl");
        try
        {
            long now = DataFreshness.Now, cycle = now + 3600000;
            QuotaHistoryStore store = new QuotaHistoryStore(path);
            store.Record(Snapshot(now - 4000, "one", cycle, 15, "account-a", "five"), now);
            store.Record(Snapshot(now - 3000, "one", cycle, 15, "account-a", "five"), now);
            store.Record(Snapshot(now - 2000, "two", cycle, 35, "account-a", "five"), now);
            store.Record(Snapshot(now - 1000, "two", cycle, 45, "account-a", "week"), now);
            QuotaHistoryResult week = store.Query("7d", "account-a", now);
            Check(week.Available && week.Points.Count == 3, "deduplicates duplicate observation/window and keeps independent windows");
            Check(week.Points[0].AccountKey == null, "query never exposes account identity");
            Check(week.Points[1].UsedPercent == 35 && week.Points[1].RemainingPercent == 65, "preserves raw percentages");
            Check(store.Query("bad", "account-a", now).ErrorCode == "invalid_range", "rejects unbounded range");
            Check(store.Query("24h", "other", now).Points.Count == 0, "accounts stay isolated");
            store = new QuotaHistoryStore(path);
            Check(store.Query("7d", "account-a", now).Points.Count == 3, "restart reloads history");
            AppSnapshot fallback = Snapshot(now, "three", cycle, 50, "account-a", "five");
            fallback.QuotaState.Source = "local"; store.Record(fallback, now);
            fallback = Snapshot(now, "four", cycle, 50, "account-a", "five"); fallback.QuotaState.IsNewObservation = false; store.Record(fallback, now);
            fallback = Snapshot(now, "five", now - 1, 50, "account-a", "five"); store.Record(fallback, now);
            Check(store.Query("7d", "account-a", now).Points.Count == 3, "local, repeated, and expired observations never enter history");
            File.AppendAllText(path, "{broken\n");
            store = new QuotaHistoryStore(path);
            Check(store.Query("7d", "account-a", now).Points.Count == 3, "torn record keeps valid prior history");
            Console.WriteLine("Quota history tests passed: " + checks + " checks."); return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
