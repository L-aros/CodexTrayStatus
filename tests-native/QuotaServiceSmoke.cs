using System;
using System.Collections.Generic;
using CodexTrayStatus;

internal static class QuotaServiceSmoke
{
    private static int failures;

    private static void Check(bool condition, string message)
    {
        if (condition) return;
        Console.Error.WriteLine("FAIL: " + message);
        failures++;
    }

    private static void Main()
    {
        long now = 1760000000000L;
        string official = "{\"rate_limit\":{" +
            "\"primary_window\":{\"limit_window_seconds\":18000,\"used_percent\":3,\"resets_in_seconds\":600}," +
            "\"secondary_window\":{\"limit_window_seconds\":604800,\"used_percent\":42,\"reset_at\":1760001000}}}";
        List<RateLimitWindow> limits = QuotaService.ParseRateLimits(official, now);
        Check(limits.Count == 2, "official response should expose two windows");
        Check(limits.Count > 0 && limits[0].Label == "5h", "primary window should be labelled 5h");
        Check(limits.Count > 0 && Math.Abs((limits[0].RemainingPercent ?? 0) - 97) < 0.001,
            "remaining percentage should be 100 - used");
        Check(limits.Count > 1 && limits[1].Label == "7d", "secondary window should be labelled 7d");

        string additionalOfficial = "{\"rate_limit\":{\"primary_window\":{\"limit_window_seconds\":18000," +
            "\"used_percent\":25,\"reset_after_seconds\":300}},\"additional_rate_limits\":[{" +
            "\"limit_name\":\"gpt-reserve\",\"rate_limit\":{\"primary_window\":{" +
            "\"limit_window_seconds\":604800,\"used_percent\":0,\"reset_after_seconds\":600}}}]}";
        List<RateLimitWindow> additionalLimits = QuotaService.ParseRateLimits(additionalOfficial, now);
        Check(additionalLimits.Count == 2, "additional official limits should be merged");
        Check(additionalLimits.Count > 1 && additionalLimits[1].Label == "7d" &&
            Math.Abs((additionalLimits[1].RemainingPercent ?? 0) - 100) < 0.001,
            "a 7-day reserve window should populate the weekly card");

        string stamp = DateTimeOffset.Now.ToString("o");
        string context = "{\"type\":\"event_msg\",\"timestamp\":\"" + stamp +
            "\",\"payload\":{\"type\":\"turn_context\",\"model\":\"gpt-5.4\"}}";
        string usage = "{\"type\":\"event_msg\",\"timestamp\":\"" + stamp +
            "\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{" +
            "\"input_tokens\":1000,\"cached_input_tokens\":500,\"output_tokens\":100}}}}";
        TodayUsage today = QuotaService.SummarizeTodayJsonl(new[] { context, usage, usage }, DateTimeOffset.Now);
        Check(today.Input == 1000, "duplicate token events should be counted once");
        Check(today.Output == 100, "output tokens should be aggregated");
        Check(today.TotalTokens == 1100, "total tokens should equal input plus output");
        Check(today.EstimatedCost == 0.002875m, "gpt-5.4 estimate should preserve precision");
        Check(today.CachedInput == 500 && today.UncachedInput == 500, "input must split into disjoint categories");
        Check(today.CachedInputCost == 0.000125m && today.UncachedInputCost == 0.00125m && today.OutputCost == 0.0015m,
            "each token category must use its own rate");

        string yesterdayStamp = DateTimeOffset.Now.AddDays(-1).ToString("o");
        string yesterdayUsage = usage.Replace(stamp, yesterdayStamp);
        string nativeContext = "{\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-5.4\"}}";
        List<DailyUsage> days = QuotaService.SummarizeDailyJsonl(new[] { nativeContext, yesterdayUsage, usage, usage, "{broken" });
        Check(days.Count == 2, "history must aggregate separate local calendar days");
        Check(days[0].Date == DateTime.Today.AddDays(-1) && days[1].Date == DateTime.Today,
            "history must be ordered by local date");
        Check(days[0].TotalTokens == 1100 && days[1].TotalTokens == 1100,
            "history must deduplicate each day without losing yesterday");
        Check(days[0].EstimatedCost == 0.002875m && days[1].EstimatedCost == 0.002875m,
            "top-level turn_context must supply the model for daily prices");
        string secondUsage = usage.Replace(stamp, DateTimeOffset.Now.AddSeconds(1).ToString("o"));
        List<DailyUsage> mixed = QuotaService.SummarizeDailyJsonl(new[] { nativeContext, usage,
            nativeContext.Replace("gpt-5.4", "gpt-6-astra"), secondUsage });
        Check(mixed[0].Models.Count == 2 && mixed[0].Models[1].EstimatedCost == 0.0105m,
            "model switches must retain individual rates and totals");
        Check(mixed[0].EstimatedCost == mixed[0].Models[0].EstimatedCost + mixed[0].Models[1].EstimatedCost,
            "daily cost must reconcile with model cost");
        List<DailyUsage> unknown = QuotaService.SummarizeDailyJsonl(new[] { nativeContext.Replace("gpt-5.4", "future-model"), usage });
        Check(unknown[0].UnpricedTokens == 1100 && unknown[0].EstimatedCost == 0,
            "unknown models must not silently inherit another model's price");
        PricingCatalog.Set("future-model", new ModelPrice { Input = 2m, Cached = 0.2m, Output = 4m });
        List<DailyUsage> custom = QuotaService.SummarizeDailyJsonl(new[] { nativeContext.Replace("gpt-5.4", "future-model"), usage });
        Check(custom[0].UnpricedTokens == 0 && custom[0].EstimatedCost > 0, "local price override must price an otherwise unknown model");
        PricingCatalog.Reset("future-model");
        string cumulativeUsage = usage.Replace("last_token_usage", "total_token_usage");
        List<DailyUsage> repeated = QuotaService.SummarizeDailyJsonl(new[] { nativeContext, cumulativeUsage,
            cumulativeUsage.Replace(stamp, DateTimeOffset.Now.AddSeconds(2).ToString("o")) });
        Check(repeated[0].TotalTokens == 1100, "unchanged cumulative totals at a later timestamp must not be counted again");

        string local = "{\"type\":\"event_msg\",\"timestamp\":\"" + stamp +
            "\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{" +
            "\"primary\":{\"window_minutes\":300,\"used_percent\":12,\"resets_in_seconds\":1800}," +
            "\"secondary\":{\"window_minutes\":10080,\"used_percent\":55,\"resets_in_seconds\":3600}}}}";
        List<RateLimitWindow> localLimits = QuotaService.ParseLocalRateLimits(new[] { local }, now, now);
        Check(localLimits.Count == 2, "local fallback should expose two windows");
        Check(localLimits.Count > 0 && Math.Abs((localLimits[0].RemainingPercent ?? 0) - 88) < 0.001,
            "local fallback should convert used to remaining");

        if (failures == 0) Console.WriteLine("Native quota smoke tests passed.");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }
}
