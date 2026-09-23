using System;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using CodexTrayStatus;

internal static class ReminderServiceSmoke
{
    private static int checks;
    private const long Start = 1789000000000L;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static DateTimeOffset Clock(long time) { return DateTimeOffset.FromUnixTimeMilliseconds(time); }
    private static AppSnapshot Sample(double remaining, long time, long reset, string account = "A", string window = "main")
    {
        return new AppSnapshot { QuotaState = new DataState { AccountKey = account, Scope = "account", Source = "official", Delivery = "updated",
            TimeBasis = "response_received", ObservationId = Guid.NewGuid().ToString("N"), ObservedAt = time, IsNewObservation = true, IsComplete = true },
            Quota = new QuotaResult { Limits = new List<RateLimitWindow> { new RateLimitWindow { Id = window, WindowKey = window, WindowSeconds = 18000,
                Label = "5h", RemainingPercent = remaining, UsedPercent = 100 - remaining, ResetsAt = reset } } } };
    }
    private sealed class Sink : IReminderNotificationSink
    {
        internal readonly List<string> Messages = new List<string>();
        internal bool Fail;
        public void Show(string title, string message) { if (Fail) throw new IOException(); Messages.Add(message); }
    }
    private sealed class MemoryStore : IReminderStateStore
    {
        internal ReminderState State = new ReminderState();
        internal bool Fail;
        internal int Writes;
        public ReminderState Load() { return State; }
        public void Commit(ReminderState state) { if (Fail) throw new IOException(); State = state; Writes++; }
    }
    private static void Main()
    {
        ReminderPreferences prefs = ReminderPreferences.Merge(null, null);
        Check(prefs.Enabled == true && prefs.Threshold20 == true && prefs.Threshold10 == true && prefs.Threshold0 == true && prefs.RecoveryEnabled == true && prefs.QuietHoursEnabled == false, "defaults");
        ReminderPreferences off = ReminderPreferences.Merge(prefs, new ReminderPreferences { Enabled = false });
        Check(ReminderPreferences.Merge(off, null).Enabled == false, "omitted patch preserves disabled");
        Check(ReminderPreferences.Merge(off, new ReminderPreferences { Threshold10 = false }).Threshold20 == true, "nested patch preserves others");
        bool invalid = false;
        try { ReminderPreferences.Merge(prefs, new ReminderPreferences { QuietStartMinutes = 480, QuietEndMinutes = 480 }); } catch { invalid = true; }
        Check(invalid, "reject equal quiet times");
        invalid = false; try { ReminderPreferences.Merge(prefs, new ReminderPreferences { QuietEndMinutes = 1440 }); } catch { invalid = true; }
        Check(invalid, "reject out of range minutes");
        ReminderPreferences quiet = ReminderPreferences.Merge(prefs, new ReminderPreferences { QuietHoursEnabled = true });
        Check(quiet.Suppression(new DateTimeOffset(2026, 9, 10, 23, 0, 0, TimeSpan.FromHours(8))) == "quiet_hours", "quiet inclusive start");
        Check(quiet.Suppression(new DateTimeOffset(2026, 9, 11, 7, 59, 0, TimeSpan.FromHours(8))) == "quiet_hours", "quiet midnight");
        Check(quiet.Suppression(new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.FromHours(8))) == null, "quiet exclusive end");
        quiet = ReminderPreferences.Merge(prefs, new ReminderPreferences { QuietHoursEnabled = true, QuietStartMinutes = 540, QuietEndMinutes = 1020 });
        Check(quiet.Suppression(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero)) == "quiet_hours", "daytime range");

        MemoryStore store = new MemoryStore(); Sink sink = new Sink(); ReminderCoordinator service = new ReminderCoordinator(store, sink);
        long t = Start, reset = Start + 3600000;
        foreach (double remaining in new double[] { 21, 20, 19, 10, 0 }) { service.Process(Sample(remaining, t, reset), prefs, Clock(t)); t++; }
        Check(sink.Messages.Count == 3, "21-20-19-10-0 crosses three thresholds");
        service = new ReminderCoordinator(store, sink);
        service.Process(Sample(0, t, reset), prefs, Clock(t)); t++;
        Check(sink.Messages.Count == 3, "restart dedup");
        foreach (double remaining in new double[] { 5, 19, 21, 19, 22, 23, 19, 22 }) { service.Process(Sample(remaining, t, reset), prefs, Clock(t)); t++; }
        Check(sink.Messages.Count == 4 && sink.Messages[3].Contains("已恢复"), "hysteresis and one recovery per cycle");
        service.Process(Sample(8, t, reset, "B"), prefs, Clock(t)); t++;
        service.Process(Sample(8, t, reset, "A", "reserve"), prefs, Clock(t)); t++;
        service.Process(Sample(0, t, reset, "A"), prefs, Clock(t)); t++;
        Check(sink.Messages.Count == 6, "accounts and same-label pools isolated");
        service.Process(Sample(0, t, reset + 1000), prefs, Clock(t)); t++;
        Check(sink.Messages.Count == 6, "relative reset jitter dedup");
        service.Process(Sample(0, t, reset + 3600000), prefs, Clock(t)); t++;
        Check(sink.Messages.Count == 6, "early changed cycle ambiguous");
        t = reset + 1;
        service.Process(Sample(0, t, reset + 3600000), prefs, Clock(t)); t++;
        Check(sink.Messages.Count == 7 && !sink.Messages[6].Contains("已恢复"), "new cycle still zero no recovery");
        service.Process(Sample(100, t, reset), prefs, Clock(t)); t++;
        Check(sink.Messages.Count == 7, "expired boundary never recovers");
        service.Process(Sample(85, t, reset + 3600000), prefs, Clock(t)); t++;
        Check(sink.Messages.Count == 8 && sink.Messages[7].Contains("已恢复"), "online recovery after boundary");

        store = new MemoryStore(); sink = new Sink(); service = new ReminderCoordinator(store, sink); t = Start;
        foreach (double remaining in new double[] {25, 8, 0.4, 0}) { service.Process(Sample(remaining, t, reset), prefs, Clock(t)); t++; }
        Check(sink.Messages.Count == 2 && sink.Messages[1].Contains("剩余 0%"), "multi threshold skips weaker; nonzero is not exhausted");
        Check(store.State.Windows[0].ConsumedThresholds == 7, "all reached flags consumed");
        AppSnapshot repeated = Sample(0, t, reset);
        service.Process(repeated, prefs, Clock(t));
        int writes = store.Writes;
        service.Process(repeated, prefs, Clock(t + 1));
        Check(store.Writes == writes, "duplicate observation does not write");
        repeated = Sample(100, t - 1, reset);
        service.Process(repeated, prefs, Clock(t));
        Check(store.Writes == writes, "older observation ignored");
        Action<AppSnapshot> rejected = delegate(AppSnapshot sample) {
            service.Process(sample, prefs, Clock(t + 20)); Check(store.Writes == writes && sink.Messages.Count == 2, "invalid data must not change state or notify");
        };
        AppSnapshot bad = Sample(100, t + 1, reset); bad.QuotaState.Source = "local"; rejected(bad);
        bad = Sample(100, t + 1, reset); bad.QuotaState.Delivery = "retained"; rejected(bad);
        bad = Sample(100, t + 1, reset); bad.QuotaState.AccountKey = null; rejected(bad);
        bad = Sample(100, t + 1, reset); bad.QuotaState.IsNewObservation = false; rejected(bad);
        bad = Sample(100, t + 1, reset); bad.QuotaState.ObservationId = null; rejected(bad);
        bad = Sample(100, t + 1, reset); bad.QuotaState.IsComplete = false; rejected(bad);
        bad = Sample(100, t + 1, reset); bad.QuotaState.ObservedAt = t - 900000; rejected(bad);
        bad = Sample(100, t + 1, reset); bad.QuotaState.ObservedAt = t + 60021; rejected(bad);
        bad = Sample(100, t + 1, reset); bad.Quota.Limits[0].WindowKey = null; rejected(bad);
        bad = Sample(100, t + 1, reset); bad.Quota.Limits[0].ResetsAt = null; rejected(bad);
        bad = Sample(100, t + 1, reset); bad.Quota.Limits[0].RemainingPercent = double.NaN; rejected(bad);
        bad = Sample(100, t + 1, reset); bad.Quota.Limits[0].RemainingPercent = 0; rejected(bad);
        bad = Sample(100, t + 1, reset); bad.QuotaState = null; rejected(bad);
        bad = Sample(100, t + 1, reset); bad.Error = "usage_scan_failed";
        service.Process(bad, prefs, Clock(t + 20)); Check(sink.Messages.Count == 3, "usage error does not block valid quota");

        store = new MemoryStore(); sink = new Sink(); service = new ReminderCoordinator(store, sink); t = Start;
        service.Process(Sample(8, t, reset), off, Clock(t)); t++;
        service.Process(Sample(8, t, reset), prefs, Clock(t)); t++;
        Check(sink.Messages.Count == 0, "disabled does not backlog");
        ReminderPreferences snooze = ReminderPreferences.Merge(prefs, new ReminderPreferences { SnoozeUntilUtcMs = t + 100 });
        service.Process(Sample(0, t, reset), snooze, Clock(t)); t++;
        service = new ReminderCoordinator(store, sink);
        service.Process(Sample(0, t + 101, reset), snooze, Clock(t + 101));
        Check(sink.Messages.Count == 0, "snooze restart expires without backlog");
        service.Process(Sample(30, t + 102, reset), off, Clock(t + 102));
        service.Process(Sample(30, t + 103, reset), prefs, Clock(t + 103));
        Check(sink.Messages.Count == 0, "disabled recovery consumed");
        store = new MemoryStore(); sink = new Sink(); service = new ReminderCoordinator(store, sink);
        DateTimeOffset night = new DateTimeOffset(2026, 9, 10, 23, 0, 0, TimeSpan.FromHours(8));
        quiet = ReminderPreferences.Merge(prefs, new ReminderPreferences { QuietHoursEnabled = true });
        long nightReset = night.AddDays(1).ToUnixTimeMilliseconds();
        service.Process(Sample(8, night.ToUnixTimeMilliseconds(), nightReset), quiet, night);
        service = new ReminderCoordinator(store, sink);
        DateTimeOffset morning = night.AddHours(9);
        service.Process(Sample(8, morning.ToUnixTimeMilliseconds(), nightReset), quiet, morning);
        Check(sink.Messages.Count == 0, "quiet night and restart do not backlog at 08:00");
        service.Process(Sample(0, morning.AddMinutes(1).ToUnixTimeMilliseconds(), nightReset), quiet, morning.AddMinutes(1));
        Check(sink.Messages.Count == 1, "later new threshold after quiet hours works");
        ReminderPreferences noRecovery = ReminderPreferences.Merge(prefs, new ReminderPreferences { RecoveryEnabled = false });
        service.Process(Sample(30, morning.AddMinutes(2).ToUnixTimeMilliseconds(), nightReset), noRecovery, morning.AddMinutes(2));
        service.Process(Sample(30, morning.AddMinutes(3).ToUnixTimeMilliseconds(), nightReset), prefs, morning.AddMinutes(3));
        Check(sink.Messages.Count == 1, "reenabling recovery does not backlog");
        store = new MemoryStore(); sink = new Sink(); service = new ReminderCoordinator(store, sink);
        ReminderPreferences only20 = ReminderPreferences.Merge(prefs, new ReminderPreferences { Threshold10 = false, Threshold0 = false });
        service.Process(Sample(0, Start, reset), only20, Clock(Start));
        Check(sink.Messages.Count == 1, "most severe enabled reached threshold");
        service.Process(Sample(0, Start + 1, reset), prefs, Clock(Start + 1));
        Check(sink.Messages.Count == 1, "reenabling thresholds does not backlog");
        store = new MemoryStore(); sink = new Sink(); service = new ReminderCoordinator(store, sink);
        bad = Sample(8, Start, reset); bad.Quota.Limits.Add(Sample(0, Start, reset, "A", "reserve").Quota.Limits[0]);
        service.Process(bad, prefs, Clock(Start)); Check(sink.Messages.Count == 1 && sink.Messages[0].Contains("\n"), "combined windows one notification");

        store = new MemoryStore { Fail = true }; sink = new Sink(); service = new ReminderCoordinator(store, sink);
        service.Process(Sample(0, Start, reset), prefs, Clock(Start));
        Check(sink.Messages.Count == 0 && service.Status(prefs, Clock(Start)).StorageAvailable == false, "commit failure pauses before delivery");
        store.Fail = false;
        service.Process(Sample(0, Start + 1, reset), prefs, Clock(Start + 1));
        Check(store.Writes == 0, "failure requires explicit rebuild");
        Check(service.Rebuild() == null, "rebuild succeeds");
        service.Process(Sample(8, Start + 2, reset), prefs, Clock(Start + 2));
        Check(sink.Messages.Count == 0, "rebuild baselines first sample");
        service.Process(Sample(0, Start + 3, reset), prefs, Clock(Start + 3));
        Check(sink.Messages.Count == 1, "rebuild permits later new thresholds");
        store = new MemoryStore(); sink = new Sink { Fail = true }; service = new ReminderCoordinator(store, sink);
        service.Process(Sample(0, Start, reset), prefs, Clock(Start));
        Check(store.State.Windows[0].ConsumedThresholds == 7 && service.Status(prefs, Clock(Start)).ErrorCode == "notification_failed", "delivery failure durable consumption");
        sink.Fail = false; service = new ReminderCoordinator(store, sink);
        service.Process(Sample(0, Start + 1, reset), prefs, Clock(Start + 1));
        Check(sink.Messages.Count == 0, "restart after failed send never retries");
        store = new MemoryStore();
        ReminderEvaluation evaluated = ReminderEngine.Evaluate(Sample(0, Start, reset), prefs, store.State, Clock(Start));
        store.Commit(evaluated.State); // Crash before calling any sink.
        service = new ReminderCoordinator(store, sink);
        service.Process(Sample(0, Start + 1, reset), prefs, Clock(Start + 1));
        Check(sink.Messages.Count == 0, "crash after commit before send never repeats");

        string folder = Path.Combine(Path.GetTempPath(), "CodexReminderTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "state.json"); ReminderStateStore disk = new ReminderStateStore(path);
            Check(disk.Load().Windows.Count == 0, "first install");
            disk.Commit(evaluated.State); disk.Commit(evaluated.State);
            Check(File.Exists(path + ".bak") && disk.Load().Windows[0].ConsumedThresholds == 7, "atomic replacement and reload");
            File.Delete(path);
            service = new ReminderCoordinator(disk, sink);
            Check(!service.Status(prefs, Clock(Start)).StorageAvailable, "missing primary with backup must not reset dedup");
            disk.Commit(evaluated.State);
            service = new ReminderCoordinator(disk, sink);
            service.Process(Sample(0, Start + 10, reset), prefs, Clock(Start + 10));
            Check(sink.Messages.Count == 0, "real file restart dedup");
            File.WriteAllText(path, "{broken");
            service = new ReminderCoordinator(disk, sink);
            Check(service.Status(prefs, Clock(Start)).StorageAvailable == false, "corrupt file pauses, does not silently load backup");
            Check(service.Rebuild() == null && disk.Load().BaselineOnly, "explicit disk rebuild");
            File.WriteAllText(path, "{\"Version\":99,\"Windows\":[]}");
            service = new ReminderCoordinator(disk, sink);
            Check(!service.Status(prefs, Clock(Start)).StorageAvailable, "future state version pauses");
            File.WriteAllText(path, "{}");
            service = new ReminderCoordinator(disk, sink);
            Check(!service.Status(prefs, Clock(Start)).StorageAvailable, "missing state fields fail closed");
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(evaluated.State).Replace("\"ConsumedThresholds\":7,", ""));
            service = new ReminderCoordinator(disk, sink);
            Check(!service.Status(prefs, Clock(Start)).StorageAvailable, "missing consumption mask cannot cause repeated alerts");
            string settings = new JavaScriptSerializer().Serialize(snooze);
            Check(ReminderPreferences.Merge(null, new JavaScriptSerializer().Deserialize<ReminderPreferences>(settings)).SnoozeUntilUtcMs == snooze.SnoozeUntilUtcMs, "settings serialization keeps pause timestamp");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Reminder tests passed: " + checks + " checks (synthetic data, fake notifications).");
    }
}
