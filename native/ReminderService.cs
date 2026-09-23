using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace CodexTrayStatus
{
    // Nullable members are patches: omitted values preserve the current setting.
    public sealed class ReminderPreferences
    {
        public int? Version { get; set; }
        public bool? Enabled { get; set; }
        public bool? Threshold20 { get; set; }
        public bool? Threshold10 { get; set; }
        public bool? Threshold0 { get; set; }
        public bool? RecoveryEnabled { get; set; }
        public bool? QuietHoursEnabled { get; set; }
        public int? QuietStartMinutes { get; set; }
        public int? QuietEndMinutes { get; set; }
        public long? SnoozeUntilUtcMs { get; set; }

        public static ReminderPreferences Merge(ReminderPreferences current, ReminderPreferences patch)
        {
            current = current ?? new ReminderPreferences(); patch = patch ?? new ReminderPreferences();
            ReminderPreferences value = new ReminderPreferences {
                Version = patch.Version ?? current.Version ?? 1,
                Enabled = patch.Enabled ?? current.Enabled ?? true,
                Threshold20 = patch.Threshold20 ?? current.Threshold20 ?? true,
                Threshold10 = patch.Threshold10 ?? current.Threshold10 ?? true,
                Threshold0 = patch.Threshold0 ?? current.Threshold0 ?? true,
                RecoveryEnabled = patch.RecoveryEnabled ?? current.RecoveryEnabled ?? true,
                QuietHoursEnabled = patch.QuietHoursEnabled ?? current.QuietHoursEnabled ?? false,
                QuietStartMinutes = patch.QuietStartMinutes ?? current.QuietStartMinutes ?? 1380,
                QuietEndMinutes = patch.QuietEndMinutes ?? current.QuietEndMinutes ?? 480,
                SnoozeUntilUtcMs = patch.SnoozeUntilUtcMs ?? current.SnoozeUntilUtcMs ?? 0
            };
            if (value.Version != 1 || value.QuietStartMinutes < 0 || value.QuietStartMinutes >= 1440 ||
                value.QuietEndMinutes < 0 || value.QuietEndMinutes >= 1440 || value.QuietStartMinutes == value.QuietEndMinutes ||
                value.SnoozeUntilUtcMs < 0 || value.SnoozeUntilUtcMs > 253402300799999L)
                throw new InvalidOperationException("提醒设置无效：请检查免打扰时间和设置版本");
            return value;
        }

        public string Suppression(DateTimeOffset now)
        {
            if (Enabled != true) return "disabled";
            if (SnoozeUntilUtcMs > now.ToUnixTimeMilliseconds()) return "snoozed";
            int minute = now.Hour * 60 + now.Minute;
            if (QuietHoursEnabled == true && (QuietStartMinutes < QuietEndMinutes
                ? minute >= QuietStartMinutes && minute < QuietEndMinutes
                : minute >= QuietStartMinutes || minute < QuietEndMinutes)) return "quiet_hours";
            return null;
        }
    }

    public sealed class ReminderWindowState
    {
        public string AccountKey { get; set; }
        public string WindowKey { get; set; }
        public long CycleEnd { get; set; }
        public long ObservedAt { get; set; }
        public string ObservationId { get; set; }
        public double Remaining { get; set; }
        public int ConsumedThresholds { get; set; }
        public long? PendingRecoveryCycle { get; set; }
        public bool RecoveryConsumed { get; set; }
    }

    public sealed class ReminderState
    {
        public int Version { get; set; }
        public bool BaselineOnly { get; set; }
        public List<ReminderWindowState> Windows { get; set; }
        public ReminderState() { Version = 1; Windows = new List<ReminderWindowState>(); }
    }

    public sealed class ReminderEvent
    {
        public string EventId { get; set; }
        public string Text { get; set; }
        public string Kind { get; set; }
        public string Disposition { get; set; }
    }

    public sealed class ReminderEvaluation
    {
        public ReminderState State { get; set; }
        public List<ReminderEvent> Events { get; set; }
        public bool Changed { get; set; }
    }

    public sealed class ReminderStatus
    {
        public bool StorageAvailable { get; set; }
        public string Suppression { get; set; }
        public long SnoozeUntilUtcMs { get; set; }
        public string ErrorCode { get; set; }
    }

    public static class ReminderEngine
    {
        // Compare with the immutable cycle anchor, never accumulate relative-time jitter.
        public const long CycleToleranceMs = 60000;
        public static ReminderEvaluation Evaluate(AppSnapshot snapshot, ReminderPreferences preferences, ReminderState previous, DateTimeOffset now)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 4194304 };
            ReminderState state = serializer.Deserialize<ReminderState>(serializer.Serialize(previous));
            ReminderEvaluation result = new ReminderEvaluation { State = state, Events = new List<ReminderEvent>() };
            DataState meta = snapshot == null ? null : snapshot.QuotaState;
            if (meta == null || snapshot.Quota == null || snapshot.Quota.Limits == null || meta.Source != "official" ||
                meta.Delivery != "updated" || meta.Scope != "account" || meta.TimeBasis != "response_received" ||
                !meta.IsNewObservation || string.IsNullOrEmpty(meta.ObservationId)) return result;
            foreach (RateLimitWindow window in snapshot.Quota.Limits)
            {
                ValidityResult validity = DataFreshness.EvaluateQuota(window, meta, now.ToUnixTimeMilliseconds());
                if (!validity.IsUsable || !validity.IsNewObservation || !window.RemainingPercent.HasValue || window.ResetsAt > 253402300799999L) continue;
                double remaining = window.RemainingPercent.Value;
                if (double.IsNaN(remaining) || double.IsInfinity(remaining) || remaining < 0 || remaining > 100 ||
                    Math.Abs(remaining + window.UsedPercent.Value - 100) > 0.000001) continue;
                ReminderWindowState row = state.Windows.Find(delegate(ReminderWindowState item) { return item.AccountKey == meta.AccountKey && item.WindowKey == window.WindowKey; });
                bool baseline = row == null && state.BaselineOnly;
                if (row != null)
                {
                    if (meta.ObservedAt <= row.ObservedAt || meta.ObservationId == row.ObservationId) continue;
                    long delta = window.ResetsAt.Value - row.CycleEnd;
                    if (delta < -CycleToleranceMs) continue; // Never reopen a retired cycle.
                    if (delta > CycleToleranceMs)
                    {
                        // Before the known boundary, a changed reset is ambiguous, not a new cycle.
                        if (meta.ObservedAt < row.CycleEnd) continue;
                        row.CycleEnd = window.ResetsAt.Value;
                        row.ConsumedThresholds = 0;
                        row.RecoveryConsumed = false;
                    }
                }
                else
                {
                    row = new ReminderWindowState { AccountKey = meta.AccountKey, WindowKey = window.WindowKey, CycleEnd = window.ResetsAt.Value };
                    state.Windows.Add(row);
                }
                row.ObservedAt = meta.ObservedAt.Value; row.ObservationId = meta.ObservationId; row.Remaining = remaining;
                result.Changed = true;
                string suppression = baseline ? "baseline" : preferences.Suppression(now);
                int[] thresholds = { 20, 10, 0 };
                bool[] enabled = { preferences.Threshold20 == true, preferences.Threshold10 == true, preferences.Threshold0 == true };
                int selected = -1;
                for (int i = 0; i < thresholds.Length; i++)
                    if (remaining <= thresholds[i] && (row.ConsumedThresholds & (1 << i)) == 0)
                    {
                        row.ConsumedThresholds |= 1 << i;
                        if (enabled[i]) selected = i;
                    }
                string label = window.Label == "5h" ? "5 小时额度" : window.Label == "7d" ? "每周额度" : (window.Label ?? "额度");
                // Include the pool identity so equal-duration pools are distinguishable.
                label += " [" + (window.Id ?? window.WindowKey) + "]";
                string percent = remaining > 0 && remaining < 1 ? "低于 1%" : remaining.ToString("0.##", CultureInfo.InvariantCulture) + "%";
                if (selected >= 0)
                    result.Events.Add(new ReminderEvent { EventId = EventKey(row, row.CycleEnd, "threshold" + thresholds[selected]), Kind = "low",
                        Disposition = suppression ?? "ready", Text = label + "剩余 " + percent + "；预计重置 " + DateTimeOffset.FromUnixTimeMilliseconds(window.ResetsAt.Value).ToLocalTime().ToString("MM-dd HH:mm") });
                if (remaining <= 20 && !row.RecoveryConsumed) row.PendingRecoveryCycle = row.CycleEnd;
                if (remaining >= 22 && row.PendingRecoveryCycle.HasValue)
                {
                    result.Events.Add(new ReminderEvent { EventId = EventKey(row, row.PendingRecoveryCycle.Value, "recovery"), Kind = "recovery",
                        Disposition = suppression ?? (preferences.RecoveryEnabled == true ? "ready" : "disabled"), Text = label + "已恢复至 " + percent });
                    row.PendingRecoveryCycle = null; row.RecoveryConsumed = true;
                }
            }
            return result;
        }

        private static string EventKey(ReminderWindowState row, long cycle, string kind)
        {
            return row.AccountKey.Length + ":" + row.AccountKey + row.WindowKey.Length + ":" + row.WindowKey + ":" + cycle.ToString(CultureInfo.InvariantCulture) + ":" + kind;
        }
    }
}
