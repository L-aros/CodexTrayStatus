using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace CodexTrayStatus
{
    // A small, independent record of confirmed quota readings. Token history never
    // feeds this store: quota percentages have their own reset boundaries.
    internal sealed class QuotaHistoryStore
    {
        internal const long RetentionMilliseconds = 90L * 24L * 60L * 60L * 1000L;
        private const int MaximumPoints = 50000;
        private const int MaximumFileBytes = 16 * 1024 * 1024;
        private readonly string path;
        private readonly object gate = new object();
        private readonly List<QuotaHistoryPoint> points = new List<QuotaHistoryPoint>();
        private readonly HashSet<string> observations = new HashSet<string>(StringComparer.Ordinal);
        private string error;

        internal QuotaHistoryStore(string path)
        {
            this.path = path;
            Load();
        }

        internal string Error { get { lock (gate) return error; } }

        private static string Key(QuotaHistoryPoint point)
        {
            return point.AccountKey.Length.ToString(CultureInfo.InvariantCulture) + ":" + point.AccountKey +
                point.WindowKey.Length.ToString(CultureInfo.InvariantCulture) + ":" + point.WindowKey + ":" + point.CycleEnd.ToString(CultureInfo.InvariantCulture) + ":" + point.ObservationId;
        }

        private static bool Valid(QuotaHistoryPoint point)
        {
            return point != null && !string.IsNullOrEmpty(point.AccountKey) && !string.IsNullOrEmpty(point.WindowKey) &&
                !string.IsNullOrEmpty(point.ObservationId) && point.CycleEnd > 0 && point.ObservedAt > 0 &&
                point.UsedPercent >= 0 && point.UsedPercent <= 100 && point.RemainingPercent >= 0 && point.RemainingPercent <= 100 &&
                !double.IsNaN(point.UsedPercent) && !double.IsInfinity(point.UsedPercent) &&
                !double.IsNaN(point.RemainingPercent) && !double.IsInfinity(point.RemainingPercent) &&
                Math.Abs(point.UsedPercent + point.RemainingPercent - 100) < 0.001 && point.Source == "official";
        }

        private void Load()
        {
            lock (gate)
            {
                try
                {
                    if (!File.Exists(path)) return;
                    FileInfo info = new FileInfo(path);
                    if (info.Length > MaximumFileBytes) throw new IOException("quota_history_too_large");
                    JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = MaximumFileBytes };
                    foreach (string line in File.ReadAllLines(path))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        QuotaHistoryPoint point;
                        try { point = serializer.Deserialize<QuotaHistoryPoint>(line); }
                        catch { continue; } // A torn final line must not erase earlier history.
                        if (!Valid(point) || !observations.Add(Key(point))) continue;
                        points.Add(point);
                    }
                    Prune(DataFreshness.Now, true);
                }
                catch { error = "history_read_failed"; points.Clear(); observations.Clear(); }
            }
        }

        internal void Record(AppSnapshot snapshot, long now)
        {
            if (snapshot == null || snapshot.Quota == null || snapshot.Quota.Limits == null || snapshot.QuotaState == null) return;
            DataState state = snapshot.QuotaState;
            if (state.Source != "official" || state.Delivery != "updated" || !state.IsNewObservation ||
                string.IsNullOrEmpty(state.AccountKey) || string.IsNullOrEmpty(state.ObservationId)) return;
            lock (gate)
            {
                if (error != null) return;
                List<QuotaHistoryPoint> accepted = new List<QuotaHistoryPoint>();
                foreach (RateLimitWindow window in snapshot.Quota.Limits)
                {
                    ValidityResult validity = DataFreshness.EvaluateQuota(window, state, now);
                    if (!validity.IsUsable || !validity.IsNewObservation) continue;
                    QuotaHistoryPoint point = new QuotaHistoryPoint {
                        AccountKey = state.AccountKey, WindowKey = window.WindowKey, CycleEnd = window.ResetsAt.Value,
                        ObservedAt = state.ObservedAt.Value, ObservationId = state.ObservationId,
                        UsedPercent = window.UsedPercent.Value, RemainingPercent = window.RemainingPercent.Value, Source = "official"
                    };
                    if (observations.Add(Key(point))) accepted.Add(point);
                }
                if (accepted.Count == 0) return;
                try
                {
                    points.AddRange(accepted);
                    Prune(now, true);
                    Persist();
                }
                catch
                {
                    foreach (QuotaHistoryPoint point in accepted) { points.Remove(point); observations.Remove(Key(point)); }
                    error = "history_write_failed";
                }
            }
        }

        private void Prune(long now, bool rebuildKeys)
        {
            long minimum = now - RetentionMilliseconds;
            points.RemoveAll(delegate(QuotaHistoryPoint point) { return point.ObservedAt < minimum || point.ObservedAt > now + 60000; });
            points.Sort(delegate(QuotaHistoryPoint left, QuotaHistoryPoint right) { return left.ObservedAt.CompareTo(right.ObservedAt); });
            if (points.Count > MaximumPoints) points.RemoveRange(0, points.Count - MaximumPoints);
            if (rebuildKeys)
            {
                observations.Clear();
                foreach (QuotaHistoryPoint point in points) observations.Add(Key(point));
            }
        }

        private void Persist()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                using (StreamWriter writer = new StreamWriter(temporary, false, new System.Text.UTF8Encoding(false)))
                    foreach (QuotaHistoryPoint point in points) writer.WriteLine(serializer.Serialize(point));
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal QuotaHistoryResult Query(string range, string accountKey, long now)
        {
            long duration = range == "24h" ? 24L * 60L * 60L * 1000L : range == "7d" ? 7L * 24L * 60L * 60L * 1000L :
                range == "30d" ? 30L * 24L * 60L * 60L * 1000L : range == "90d" ? RetentionMilliseconds : 0;
            QuotaHistoryResult result = new QuotaHistoryResult { Range = range, To = now, From = now - duration };
            if (duration == 0) { result.ErrorCode = "invalid_range"; return result; }
            lock (gate)
            {
                result.ErrorCode = error; result.Available = error == null;
                foreach (QuotaHistoryPoint point in points)
                    if (point.ObservedAt >= result.From && point.ObservedAt <= now && (string.IsNullOrEmpty(accountKey) || point.AccountKey == accountKey))
                        result.Points.Add(new QuotaHistoryPoint { WindowKey = point.WindowKey, CycleEnd = point.CycleEnd,
                            ObservedAt = point.ObservedAt, ObservationId = point.ObservationId, UsedPercent = point.UsedPercent,
                            RemainingPercent = point.RemainingPercent, Source = point.Source });
            }
            return result;
        }
    }
}
