using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace CodexTrayStatus
{
    public interface IReminderStateStore
    {
        ReminderState Load();
        void Commit(ReminderState state);
    }

    public sealed class ReminderStateStore : IReminderStateStore
    {
        private readonly string path;
        public ReminderStateStore(string path) { this.path = path; }
        public ReminderState Load()
        {
            // File.Exists hides access failures; only FileNotFound means first install.
            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    if (stream.Length > 4194304) throw new IOException("Reminder state too large");
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string json = reader.ReadToEnd();
                        JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 4194304 };
                        Dictionary<string, object> fields = serializer.Deserialize<Dictionary<string, object>>(json);
                        if (fields == null || !fields.ContainsKey("Version") || !fields.ContainsKey("Windows") || !fields.ContainsKey("BaselineOnly")) throw new IOException("Incomplete reminder state");
                        System.Collections.IEnumerable rows = fields["Windows"] as System.Collections.IEnumerable;
                        if (rows == null || fields["BaselineOnly"] == null) throw new IOException("Incomplete reminder state");
                        string[] required = { "AccountKey", "WindowKey", "CycleEnd", "ObservedAt", "ObservationId", "Remaining", "ConsumedThresholds", "PendingRecoveryCycle", "RecoveryConsumed" };
                        foreach (object raw in rows)
                        {
                            Dictionary<string, object> row = raw as Dictionary<string, object>;
                            foreach (string name in required)
                                if (row == null || !row.ContainsKey(name) || (row[name] == null && name != "PendingRecoveryCycle")) throw new IOException("Incomplete reminder row");
                        }
                        ReminderState state = serializer.Deserialize<ReminderState>(json);
                        Validate(state); return state;
                    }
                }
            }
            catch (FileNotFoundException)
            {
                if (File.Exists(path + ".bak")) throw new IOException("Primary reminder state is missing");
                return new ReminderState();
            }
            catch (DirectoryNotFoundException) { return new ReminderState(); }
        }

        private static void Validate(ReminderState state)
        {
            if (state == null || state.Version != 1 || state.Windows == null) throw new IOException("Invalid reminder state");
            HashSet<string> keys = new HashSet<string>();
            foreach (ReminderWindowState row in state.Windows)
            {
                if (row == null || string.IsNullOrEmpty(row.AccountKey) || string.IsNullOrEmpty(row.WindowKey) ||
                    string.IsNullOrEmpty(row.ObservationId) || row.CycleEnd <= 0 || row.CycleEnd > 253402300799999L || row.ObservedAt <= 0 ||
                    row.ObservedAt > 253402300799999L || row.ConsumedThresholds < 0 || row.ConsumedThresholds > 7 ||
                    double.IsNaN(row.Remaining) || double.IsInfinity(row.Remaining) || row.Remaining < 0 || row.Remaining > 100 ||
                    (row.PendingRecoveryCycle.HasValue && (row.PendingRecoveryCycle <= 0 || row.PendingRecoveryCycle > row.CycleEnd)) ||
                    !keys.Add(row.AccountKey.Length + ":" + row.AccountKey + row.WindowKey)) throw new IOException("Invalid reminder state");
            }
        }

        public void Commit(ReminderState state)
        {
            Validate(state);
            string json = new JavaScriptSerializer { MaxJsonLength = 4194304 }.Serialize(state);
            if (Encoding.UTF8.GetByteCount(json) > 4194304) throw new IOException("Reminder state too large");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json); file.Write(bytes, 0, bytes.Length); file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public interface IReminderNotificationSink { void Show(string title, string message); }

    public sealed class ReminderCoordinator
    {
        private readonly IReminderStateStore store;
        private readonly IReminderNotificationSink sink;
        private ReminderState state;
        private string error;
        private bool blocked;
        public ReminderCoordinator(IReminderStateStore store, IReminderNotificationSink sink)
        {
            this.store = store; this.sink = sink;
            try { state = store.Load(); } catch { error = "state_read_failed"; blocked = true; }
        }
        public ReminderStatus Status(ReminderPreferences preferences, DateTimeOffset now)
        {
            return new ReminderStatus { StorageAvailable = state != null && !blocked, ErrorCode = error,
                Suppression = preferences.Suppression(now), SnoozeUntilUtcMs = preferences.SnoozeUntilUtcMs ?? 0 };
        }
        public void Process(AppSnapshot snapshot, ReminderPreferences preferences, DateTimeOffset now)
        {
            if (state == null || blocked) return;
            ReminderEvaluation evaluation;
            try { evaluation = ReminderEngine.Evaluate(snapshot, preferences, state, now); }
            catch { error = "state_evaluation_failed"; blocked = true; return; }
            if (!evaluation.Changed) return;
            try { store.Commit(evaluation.State); state = evaluation.State; }
            catch { error = "state_write_failed"; blocked = true; return; }
            List<string> messages = new List<string>();
            foreach (ReminderEvent item in evaluation.Events) if (item.Disposition == "ready") messages.Add(item.Text);
            if (messages.Count == 0) return;
            // Consumption is durable before any OS call. Never retry a delivery attempt.
            try { sink.Show("Codex 额度提醒", string.Join("\n", messages.ToArray())); error = null; }
            catch { error = "notification_failed"; }
        }
        public string Rebuild()
        {
            ReminderState clean = new ReminderState { BaselineOnly = true };
            try { store.Commit(clean); state = clean; error = null; blocked = false; return null; }
            catch { error = "state_write_failed"; blocked = true; return "无法重建提醒状态，请检查本地数据目录是否可写"; }
        }
    }
}
