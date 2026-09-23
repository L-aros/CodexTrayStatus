using System.Collections.Generic;

namespace CodexTrayStatus
{
    public sealed class WebPreferences
    {
        public int RefreshInterval { get; set; }
        public bool ShowRemaining { get; set; }
        public bool AutoStart { get; set; }
        public string Currency { get; set; }
        public decimal ExchangeRate { get; set; }
        public ReminderPreferences Reminders { get; set; }
        public TaskbarDisplayPreferences TaskbarDisplay { get; set; }
        public long Revision { get; set; }
        public string TaskbarSettingsError { get; set; }
        [System.Web.Script.Serialization.ScriptIgnore]
        public System.Collections.Generic.HashSet<string> SuppliedFields { get; set; }
    }

    internal static class CurrencyDisplay
    {
        internal static string Currency = "USD";
        internal static decimal ExchangeRate = 7m;
        internal static string Format(decimal usd)
        {
            return (Currency == "CNY" ? "¥" : "$") + (usd * (Currency == "CNY" ? ExchangeRate : 1m)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
    public sealed class RateLimitWindow
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public double? UsedPercent { get; set; }
        public double? RemainingPercent { get; set; }
        public long? ResetsAt { get; set; }
        public long? WindowSeconds { get; set; }
        public string WindowKey { get; set; }
    }

    public sealed class QuotaResult
    {
        public QuotaResult()
        {
            Limits = new List<RateLimitWindow>();
        }

        public List<RateLimitWindow> Limits { get; set; }
        public long RefreshedAt { get; set; }
        public string Source { get; set; }
        public string Warning { get; set; }
        public DataState State { get; set; }
    }

    public class TodayUsage
    {
        public long Input { get; set; }
        public long CachedInput { get; set; }
        public long UncachedInput { get { return System.Math.Max(0, Input - CachedInput); } }
        public long Output { get; set; }
        public long TotalTokens { get; set; }
        public decimal EstimatedCost { get; set; }
        public decimal CachedInputCost { get; set; }
        public decimal UncachedInputCost { get; set; }
        public decimal OutputCost { get; set; }
        public long UnpricedTokens { get; set; }
    }

    public sealed class ModelUsage : TodayUsage
    {
        public string Model { get; set; }
        public decimal InputRate { get; set; }
        public decimal CachedRate { get; set; }
        public decimal OutputRate { get; set; }
    }

    public sealed class DailyUsage : TodayUsage
    {
        public DailyUsage() { Models = new List<ModelUsage>(); }
        public System.DateTime Date { get; set; }
        public string DateLabel { get { return Date.ToString("yyyy-MM-dd"); } }
        public List<ModelUsage> Models { get; set; }
    }

    public sealed class AppSnapshot
    {
        public RefreshState RefreshState { get; set; }
        public string RequestedAccountKey { get; set; }
        public DataState QuotaState { get; set; }
        public DataState UsageState { get; set; }
        public QuotaResult Quota { get; set; }
        public TodayUsage Today { get; set; }
        public List<DailyUsage> Daily { get; set; }
        public string Error { get; set; }
        public long RefreshedAt { get; set; }
    }

    public sealed class QuotaHistoryPoint
    {
        public string AccountKey { get; set; }
        public string WindowKey { get; set; }
        public long CycleEnd { get; set; }
        public long ObservedAt { get; set; }
        public string ObservationId { get; set; }
        public double UsedPercent { get; set; }
        public double RemainingPercent { get; set; }
        public string Source { get; set; }
    }

    public sealed class QuotaHistoryResult
    {
        public string Range { get; set; }
        public long From { get; set; }
        public long To { get; set; }
        public bool Available { get; set; }
        public string ErrorCode { get; set; }
        public System.Collections.Generic.List<QuotaHistoryPoint> Points { get; set; }
        public QuotaHistoryResult() { Points = new System.Collections.Generic.List<QuotaHistoryPoint>(); }
    }
}
