using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace CodexTrayStatus
{
    public sealed class TaskbarDisplayPreferences
    {
        public int? Version { get; set; }
        public string SelectionMode { get; set; }
        public string SelectedWindowKey { get; set; }
        public bool? ShowCost { get; set; }
        public bool? ShowCountdown { get; set; }

        public static TaskbarDisplayPreferences Merge(TaskbarDisplayPreferences current, TaskbarDisplayPreferences patch)
        {
            current = current ?? new TaskbarDisplayPreferences(); patch = patch ?? new TaskbarDisplayPreferences();
            TaskbarDisplayPreferences value = new TaskbarDisplayPreferences {
                Version = patch.Version ?? current.Version ?? 1,
                SelectionMode = patch.SelectionMode ?? current.SelectionMode ?? "default",
                SelectedWindowKey = patch.SelectedWindowKey ?? current.SelectedWindowKey ?? "",
                ShowCost = patch.ShowCost ?? current.ShowCost ?? true,
                ShowCountdown = patch.ShowCountdown ?? current.ShowCountdown ?? true
            };
            if (value.Version != 1 || (value.SelectionMode != "default" && value.SelectionMode != "selected" && value.SelectionMode != "minimum") ||
                value.SelectedWindowKey.Length > 512 || (value.SelectionMode == "selected" && string.IsNullOrWhiteSpace(value.SelectedWindowKey)))
                throw new InvalidOperationException("任务栏设置无效：请选择额度窗口并检查设置版本");
            foreach (char c in value.SelectedWindowKey) if (char.IsControl(c)) throw new InvalidOperationException("额度窗口标识无效");
            return value;
        }

        public static TaskbarDisplayPreferences Load(object raw)
        {
            if (raw == null) return Merge(null, null);
            string text = raw as string;
            if (text == null || text.Length > 4096) throw new InvalidOperationException("任务栏设置无法读取，请重新保存");
            TaskbarDisplayPreferences saved = new JavaScriptSerializer().Deserialize<TaskbarDisplayPreferences>(text);
            if (saved == null || saved.Version != 1) throw new InvalidOperationException("任务栏设置版本无效，请重新保存");
            return Merge(null, saved);
        }
    }

    internal sealed class TaskbarSettingsStore
    {
        private readonly Action<string> write;
        internal TaskbarDisplayPreferences Current { get; private set; }
        internal string Error { get; private set; }
        internal TaskbarSettingsStore(Func<object> read, Action<string> writeValue)
        {
            write = writeValue;
            try { Current = TaskbarDisplayPreferences.Load(read()); }
            catch { Current = TaskbarDisplayPreferences.Merge(null, null); Error = "任务栏设置无法读取，当前使用默认值；请重新保存"; }
        }
        internal void Save(TaskbarDisplayPreferences patch)
        {
            TaskbarDisplayPreferences next = TaskbarDisplayPreferences.Merge(Current, patch);
            write(new JavaScriptSerializer().Serialize(next));
            Current = next; Error = null;
        }
    }

    // Preserve the old DTO while retaining field presence for partial HTTP requests.
    internal static class WebPreferencePatch
    {
        internal static WebPreferences Parse(string json)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> fields = serializer.Deserialize<Dictionary<string, object>>(json);
            if (fields == null) throw new InvalidOperationException("设置值无效");
            WebPreferences value = serializer.Deserialize<WebPreferences>(json);
            object nested;
            if (fields.TryGetValue("TaskbarDisplay", out nested) && nested != null)
            {
                Dictionary<string, object> taskbar = nested as Dictionary<string, object>;
                if (taskbar == null) throw new InvalidOperationException("任务栏设置类型无效");
                foreach (KeyValuePair<string, object> field in taskbar)
                {
                    if (field.Value == null) continue;
                    bool valid = (field.Key == "ShowCost" || field.Key == "ShowCountdown") ? field.Value is bool :
                        field.Key == "Version" ? field.Value is int : (field.Key == "SelectionMode" || field.Key == "SelectedWindowKey") ? field.Value is string : true;
                    if (!valid) throw new InvalidOperationException("任务栏设置类型无效");
                }
            }
            value.SuppliedFields = new HashSet<string>(fields.Keys, StringComparer.Ordinal);
            foreach (string name in new[] { "RefreshInterval", "ShowRemaining", "AutoStart", "Currency", "ExchangeRate" })
                if (fields.ContainsKey(name) && fields[name] == null) throw new InvalidOperationException("设置值不能为 null");
            return value;
        }

        internal static WebPreferences Merge(WebPreferences current, WebPreferences patch)
        {
            Func<string, bool> has = name => patch.SuppliedFields == null || patch.SuppliedFields.Contains(name);
            WebPreferences result = new WebPreferences {
                RefreshInterval = has("RefreshInterval") ? patch.RefreshInterval : current.RefreshInterval,
                ShowRemaining = has("ShowRemaining") ? patch.ShowRemaining : current.ShowRemaining,
                AutoStart = has("AutoStart") ? patch.AutoStart : current.AutoStart,
                Currency = has("Currency") ? patch.Currency : current.Currency,
                ExchangeRate = has("ExchangeRate") ? patch.ExchangeRate : current.ExchangeRate,
                Reminders = ReminderPreferences.Merge(current.Reminders, patch.Reminders),
                TaskbarDisplay = TaskbarDisplayPreferences.Merge(current.TaskbarDisplay, patch.TaskbarDisplay)
            };
            if ((result.RefreshInterval != 30 && result.RefreshInterval != 60 && result.RefreshInterval != 300) ||
                (result.Currency != "USD" && result.Currency != "CNY") || result.ExchangeRate < 0.01m || result.ExchangeRate > 100m)
                throw new InvalidOperationException("设置值无效");
            return result;
        }
    }

    public sealed class TaskbarPresentation
    {
        public string WindowKey { get; set; }
        public string Prefix { get; set; }
        public string CompactPrefix { get; set; }
        public string Percent { get; set; }
        public string Secondary { get; set; }
        public string CompactSecondary { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
        public string Accent { get; set; }
        public long EvaluatedAt { get; set; }
        public long Revision { get; set; }
    }

    internal static class TaskbarPresenter
    {
        internal static string VisualKey(TaskbarPresentation view)
        {
            return view.Prefix + "\u001f" + view.Percent + "\u001f" + view.Secondary + "\u001f" + view.Accent + "\u001f" + view.Status;
        }
        internal static bool ValidPercent(double? value) { return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && value >= 0 && value <= 100; }
        internal static bool ValidPair(RateLimitWindow w) { return w != null && ValidPercent(w.UsedPercent) && ValidPercent(w.RemainingPercent) && Math.Abs(w.UsedPercent.Value + w.RemainingPercent.Value - 100) < 0.001; }
        internal static RateLimitWindow Select(AppSnapshot snapshot, TaskbarDisplayPreferences settings, long now)
        {
            if (snapshot == null || snapshot.Quota == null || snapshot.Quota.Limits == null) return null;
            RateLimitWindow best = null;
            foreach (RateLimitWindow w in snapshot.Quota.Limits)
            {
                if (w == null) continue;
                if (settings.SelectionMode == "selected")
                {
                    if (w.WindowKey == settings.SelectedWindowKey) { if (best != null) return null; best = w; }
                }
                else if (settings.SelectionMode == "minimum")
                {
                    ValidityResult valid = DataFreshness.EvaluateQuota(w, snapshot.QuotaState, now);
                    if (!ValidPair(w) || !valid.IsFresh || snapshot.QuotaState == null || !snapshot.QuotaState.IsComplete) continue;
                    if (best == null || w.RemainingPercent < best.RemainingPercent || (w.RemainingPercent == best.RemainingPercent && Compare(w, best) < 0)) best = w;
                }
                else if (best == null || (!IsFiveHour(best) && IsFiveHour(w))) best = w;
            }
            return best;
        }
        private static bool IsFiveHour(RateLimitWindow w) { return string.Equals(w.Label, "5h", StringComparison.OrdinalIgnoreCase); }
        private static int Compare(RateLimitWindow a, RateLimitWindow b)
        {
            if (IsFiveHour(a) != IsFiveHour(b)) return IsFiveHour(a) ? -1 : 1;
            int compare = string.CompareOrdinal(a.WindowKey, b.WindowKey);
            return compare != 0 ? compare : string.CompareOrdinal(a.Id, b.Id);
        }
        internal static string Tokens(long value)
        {
            if (value >= 1000000) return (value / 1000000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (value >= 1000) return (value / 1000d).ToString(value >= 100000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "K";
            return value.ToString(CultureInfo.InvariantCulture);
        }
        internal static string Countdown(RateLimitWindow window, long now)
        {
            if (!window.ResetsAt.HasValue || window.ResetsAt < 0 || window.ResetsAt > 253402300799999L) return null;
            long left = window.ResetsAt.Value - now;
            if (left <= 0) return "待更新";
            if (left >= 86400000) return (left / 86400000).ToString(CultureInfo.InvariantCulture) + "D";
            if (left >= 3600000) return (left / 3600000).ToString(CultureInfo.InvariantCulture) + "H";
            return Math.Max(1, left / 60000).ToString(CultureInfo.InvariantCulture) + "M";
        }
        internal static TaskbarPresentation Build(AppSnapshot snapshot, WebPreferences preferences, long now)
        {
            TaskbarDisplayPreferences settings = TaskbarDisplayPreferences.Merge(null, preferences.TaskbarDisplay);
            RateLimitWindow w = Select(snapshot, settings, now);
            DataState state = snapshot == null ? null : snapshot.QuotaState;
            ValidityResult validity = DataFreshness.EvaluateQuota(w, state, now);
            bool known = ValidPair(w);
            string status = w == null ? (settings.SelectionMode == "selected" ? "missing" : "unknown") : !known ? "unknown" : !validity.IsFresh ? "stale" : !validity.IsUsable || state.Source != "official" ? "reference" : "fresh";
            string label = w == null ? (status == "missing" ? "所选窗口暂无数据" : "额度未知") : string.IsNullOrEmpty(w.Label) ? "CODEX" : w.Label.ToUpperInvariant();
            // Text is untrusted display data; remove controls and bound work before measuring.
            label = Clean(label);
            string countdown = w != null && settings.ShowCountdown == true ? Countdown(w, now) : null;
            if (countdown != null) label = settings.SelectionMode == "default" ? countdown : label + " " + countdown;
            string marker = status == "stale" ? "旧值 " : status == "reference" ? "参考 " : "";
            string metricLabel = preferences.ShowRemaining ? "剩余 " : "已用 ";
            TaskbarPresentation result = new TaskbarPresentation {
                WindowKey = w == null ? null : w.WindowKey, Status = status,
                Prefix = marker + label + " · " + metricLabel, CompactPrefix = marker + metricLabel,
                Percent = known ? Math.Round(preferences.ShowRemaining ? w.RemainingPercent.Value : w.UsedPercent.Value).ToString("0", CultureInfo.InvariantCulture) + "%" : "--",
                Accent = !known || !validity.IsFresh ? "neutral" : w.RemainingPercent <= 15 ? "red" : w.RemainingPercent <= 40 ? "amber" : "green",
                Detail = status == "missing" ? "所选窗口暂无数据；保留选择，恢复后自动显示" : DataFreshness.Describe(state, validity),
                EvaluatedAt = now, Revision = preferences.Revision
            };
            string date = DateTimeOffset.FromUnixTimeMilliseconds(now).LocalDateTime.ToString("yyyy-MM-dd");
            TodayUsage today = snapshot == null ? null : snapshot.Today;
            if (today == null || (snapshot.UsageState != null && snapshot.UsageState.CoverageEndDate != date)) result.Secondary = result.CompactSecondary = "今日暂无统计";
            else
            {
                ValidityResult usage = DataFreshness.EvaluateUsage(snapshot.UsageState, true, now);
                string prefix = usage.IsFresh ? "" : "旧值 ";
                result.CompactSecondary = prefix + Tokens(today.TotalTokens);
                result.Secondary = prefix + "今日 " + Tokens(today.TotalTokens);
                if (settings.ShowCost == true)
                {
                    string amount;
                    try { amount = (preferences.Currency == "CNY" ? "¥" : "$") + (today.EstimatedCost * (preferences.Currency == "CNY" ? preferences.ExchangeRate : 1m)).ToString("0.00", CultureInfo.InvariantCulture); }
                    catch (OverflowException) { amount = "费用过大"; }
                    result.Secondary += " · " + amount;
                }
            }
            return result;
        }
        private static string Clean(string text)
        {
            System.Text.StringBuilder result = new System.Text.StringBuilder();
            foreach (char c in text) { if (!char.IsControl(c)) result.Append(c); if (result.Length >= 128) break; }
            return result.ToString();
        }
    }
}
