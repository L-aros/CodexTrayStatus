using System;
using System.Collections.Generic;

namespace CodexTrayStatus
{
    public sealed class RefreshState
    {
        public string AttemptId { get; set; }
        public long AttemptedAt { get; set; }
        public long? CompletedAt { get; set; }
        public bool IsRefreshing { get; set; }
    }

    public sealed class DataState
    {
        public string Source { get; set; }
        public string Delivery { get; set; }
        public string AccountKey { get; set; }
        public string Scope { get; set; }
        public string ObservationId { get; set; }
        public string TimeBasis { get; set; }
        public long? ObservedAt { get; set; }
        public long? LastSuccessAt { get; set; }
        public long? LastOfficialSuccessAt { get; set; }
        public long? LastAttemptAt { get; set; }
        public string LastAttemptOutcome { get; set; }
        public string ErrorCode { get; set; }
        public bool IsComplete { get; set; }
        public bool IsNewObservation { get; set; }
        public string CoverageStartDate { get; set; }
        public string CoverageEndDate { get; set; }
        public DataState Copy() { return (DataState)MemberwiseClone(); }
    }

    public sealed class ValidityResult
    {
        public bool HasValue { get; set; }
        public bool IsFresh { get; set; }
        public bool IsUsable { get; set; }
        public bool IsNewObservation { get; set; }
        public long? ExpiresAt { get; set; }
        public List<string> ReasonCodes { get; set; }
    }

    public static class DataFreshness
    {
        public const long MaxAgeMilliseconds = 900000;
        public static long Now { get { return (DateTime.UtcNow.Ticks - new DateTime(1970, 1, 1).Ticks) / TimeSpan.TicksPerMillisecond; } }

        private static ValidityResult Evaluate(DataState state, bool hasValue, long now, long? reset)
        {
            ValidityResult result = new ValidityResult { HasValue = hasValue, ReasonCodes = new List<string>() };
            if (!hasValue) result.ReasonCodes.Add("no_value");
            if (state == null || !state.ObservedAt.HasValue || state.TimeBasis == "unknown") result.ReasonCodes.Add("unknown_time");
            else
            {
                long observed = state.ObservedAt.Value;
                if (observed > now + 60000 || observed <= 0) result.ReasonCodes.Add("invalid_time");
                result.ExpiresAt = observed <= long.MaxValue - MaxAgeMilliseconds ? observed + MaxAgeMilliseconds : long.MaxValue;
                if (reset.HasValue) result.ExpiresAt = Math.Min(result.ExpiresAt.Value, reset.Value);
                if (now >= result.ExpiresAt.Value) result.ReasonCodes.Add("expired");
            }
            result.IsFresh = result.ReasonCodes.Count == 0;
            if (state == null || !state.IsComplete) result.ReasonCodes.Add("incomplete");
            result.IsUsable = result.ReasonCodes.Count == 0;
            result.IsNewObservation = state != null && state.IsNewObservation && state.Delivery != "retained";
            return result;
        }

        public static ValidityResult EvaluateQuota(RateLimitWindow window, DataState state, long now)
        {
            ValidityResult result = Evaluate(state, window != null && window.UsedPercent.HasValue, now, window == null ? null : window.ResetsAt);
            if (window != null && window.UsedPercent.HasValue && (double.IsNaN(window.UsedPercent.Value) || double.IsInfinity(window.UsedPercent.Value) || window.UsedPercent < 0 || window.UsedPercent > 100)) { result.ReasonCodes.Add("invalid_value"); result.IsFresh = false; }
            if (state == null || string.IsNullOrEmpty(state.AccountKey)) result.ReasonCodes.Add("unknown_account");
            if (window == null || string.IsNullOrEmpty(window.WindowKey) || !window.WindowSeconds.HasValue || window.WindowSeconds <= 0 || !window.ResetsAt.HasValue) result.ReasonCodes.Add("unknown_window");
            result.IsUsable = result.ReasonCodes.Count == 0;
            return result;
        }

        public static ValidityResult EvaluateUsage(DataState state, bool hasValue, long now)
        {
            ValidityResult result = Evaluate(state, hasValue, now, null);
            string date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(now).ToLocalTime().ToString("yyyy-MM-dd");
            if (state == null || state.CoverageEndDate != date) result.ReasonCodes.Add("date_not_covered");
            result.IsUsable = result.ReasonCodes.Count == 0;
            return result;
        }

        public static DataState Unavailable(long attemptedAt, string error)
        {
            return new DataState { Source = "none", Delivery = "unavailable", LastAttemptAt = attemptedAt, LastAttemptOutcome = "failed", ErrorCode = error, TimeBasis = "unknown" };
        }

        private static DataState Retain(DataState previous, DataState failed)
        {
            DataState state = previous == null ? Unavailable(failed == null ? Now : failed.LastAttemptAt ?? Now, "unknown_metadata") : previous.Copy();
            state.Delivery = "retained"; state.IsNewObservation = false;
            state.LastAttemptAt = failed == null ? null : failed.LastAttemptAt;
            state.LastAttemptOutcome = failed == null ? "failed" : failed.LastAttemptOutcome;
            state.ErrorCode = failed == null ? "refresh_failed" : failed.ErrorCode;
            return state;
        }

        public static AppSnapshot Merge(AppSnapshot previous, AppSnapshot current)
        {
            if (current == null) return previous;
            if (previous == null) { Synchronize(current); return current; }
            DataState before = previous.QuotaState, next = current.QuotaState;
            bool sameAccount = before != null && next != null && !string.IsNullOrEmpty(before.AccountKey) && before.AccountKey == next.AccountKey;
            bool sameRequest = before != null && !string.IsNullOrEmpty(before.AccountKey) && before.AccountKey == current.RequestedAccountKey;
            bool sameLocal = before != null && next != null && before.Source == "local" && next.Source == "local" && before.AccountKey == null && next.AccountKey == null && current.RequestedAccountKey == previous.RequestedAccountKey;
            bool retainUnknownLocal = before != null && before.Source == "local" && before.AccountKey == null && current.RequestedAccountKey == previous.RequestedAccountKey;
            // Unknown identity must never preserve a previously identified account's values.
            bool legacy = before == null && next == null;
            bool retainQuota = current.Quota == null || (next != null && next.Source == "local" && before != null && before.ObservedAt.HasValue && (!next.ObservedAt.HasValue || next.ObservedAt <= before.ObservedAt));
            if (retainQuota && previous.Quota != null && (sameAccount || sameRequest || retainUnknownLocal || legacy))
            {
                current.QuotaState = Retain(before, next);
                current.Quota = legacy ? previous.Quota : new QuotaResult { Limits = previous.Quota.Limits, RefreshedAt = previous.Quota.RefreshedAt, Source = previous.Quota.Source, Warning = previous.Quota.Warning, State = current.QuotaState };
            }
            else if ((sameAccount || sameRequest || sameLocal) && next != null)
            {
                if (!next.LastOfficialSuccessAt.HasValue) next.LastOfficialSuccessAt = before.LastOfficialSuccessAt;
                if (next.ObservationId == before.ObservationId) next.IsNewObservation = false;
            }
            if (current.Daily == null && previous.Daily != null)
            {
                current.Daily = previous.Daily; current.Today = previous.Today;
                current.UsageState = Retain(previous.UsageState, current.UsageState);
            }
            if (current.Today == null && legacy) current.Today = previous.Today;
            Synchronize(current);
            return current;
        }

        public static void Synchronize(AppSnapshot snapshot)
        {
            if (snapshot.Quota != null) snapshot.Quota.State = snapshot.QuotaState;
            if (snapshot.UsageState != null && !string.IsNullOrEmpty(snapshot.UsageState.CoverageEndDate) && snapshot.UsageState.CoverageEndDate != DateTime.Today.ToString("yyyy-MM-dd")) snapshot.Today = null;
        }

        public static string Describe(DataState state, ValidityResult validity)
        {
            string text = state == null || state.Delivery == "unavailable" ? "暂无数据" : state.Delivery == "retained" ? "保留旧值" : state.Source == "official" ? "在线获取" : state.Scope == "local_logs" ? "统计成功" : "本地回退";
            if (state == null && validity != null && validity.HasValue) text = "来源未知";
            if (validity != null && validity.HasValue && !validity.IsFresh) text += " · 已过期或时间未知";
            if (state != null && (state.Scope == "unattributed_local_quota" || (state.Scope == "account" && state.AccountKey == null))) text += " · 账号未知";
            return text;
        }
    }
}
