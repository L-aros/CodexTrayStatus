using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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

    private static DailyUsage PriceUsage(string model, string context, string usage)
    {
        return QuotaService.SummarizeDailyJsonl(new[] { context.Replace("gpt-5.4", model), usage })[0];
    }

    private static void CheckPricingPath(string context, string usage)
    {
        Dictionary<string, ModelPrice> previous = PricingCatalog.Snapshot();
        string temporaryRoot = Path.Combine(Path.GetTempPath(), "codex-pricing-quota-" + Guid.NewGuid().ToString("N"));
        string sessions = Path.Combine(temporaryRoot, "sessions");
        string sessionPath = Path.Combine(sessions, "pricing.jsonl");
        try
        {
            PricingCatalog.Replace(null);
            PricingCatalog.ReplaceRemote(new Dictionary<string, RemoteModelPrice> {
                { "gpt-5.4", new RemoteModelPrice { Input = 4m, Cached = 0.4m, Output = 20m } }
            });
            DailyUsage remote = PriceUsage("gpt-5.4", context, usage);
            Check(remote.EstimatedCost == 0.0042m && remote.UnpricedTokens == 0,
                "valid remote rates must override built-in rates in actual usage costs");
            Check(remote.Models[0].InputRate == 4m && remote.Models[0].CachedRate == 0.4m && remote.Models[0].OutputRate == 20m,
                "usage details must expose each effective remote rate");

            PricingCatalog.Set("gpt-5.4", new ModelPrice { Input = 8m, Cached = 0.8m, Output = 40m });
            Check(PriceUsage("gpt-5.4", context, usage).EstimatedCost == 0.0084m,
                "manual override must take priority over a valid remote row");
            PricingCatalog.Reset("gpt-5.4");
            Check(PriceUsage("gpt-5.4", context, usage).EstimatedCost == 0.0042m,
                "clearing a manual override must restore remote prices");

            PricingCatalog.ReplaceRemote(new Dictionary<string, RemoteModelPrice> {
                { "gpt-5.4", new RemoteModelPrice { Input = 4m, Cached = 0.4m } },
                { "future-model", new RemoteModelPrice { Input = 2m, Cached = 0.2m } }
            });
            DailyUsage partial = PriceUsage("gpt-5.4", context, usage);
            Check(partial.EstimatedCost == 0.0037m && partial.Models[0].OutputRate == 15m,
                "missing remote output price must use the built-in output rate rather than zero");
            DailyUsage unknown = PriceUsage("future-model", context, usage);
            Check(unknown.UnpricedTokens == 1100 && unknown.EstimatedCost == 0m,
                "an unknown model with an incomplete remote row must remain unpriced");

            PricingCatalog.ReplaceRemote(new Dictionary<string, RemoteModelPrice> {
                { "future-model", new RemoteModelPrice { Input = 2m, Cached = 0.2m, Output = 4m } },
                { "free-model", new RemoteModelPrice { Input = 0m, Cached = 0m, Output = 0m } },
                { "gpt-5.4", new RemoteModelPrice { Input = 2.5m, Cached = 15m, Output = 0.25m } }
            });
            DailyUsage complete = PriceUsage("future-model", context, usage);
            Check(complete.UnpricedTokens == 0 && complete.EstimatedCost == 0.0015m,
                "a complete remote row must price a model absent from built-ins");
            DailyUsage free = PriceUsage("free-model", context, usage);
            Check(free.UnpricedTokens == 0 && free.EstimatedCost == 0m && free.TotalTokens == 1100,
                "a valid all-zero remote row must count usage without reporting unpriced tokens");
            Check(PriceUsage("gpt-5.4", context, usage).EstimatedCost == 0.002875m,
                "remote cached price above input must be rejected and preserve built-in cost");

            PricingCatalog.ReplaceRemote(null);
            Directory.CreateDirectory(sessions);
            File.WriteAllLines(sessionPath, new[] { context, usage });
            DateTime modified = File.GetLastWriteTimeUtc(sessionPath);
            long length = new FileInfo(sessionPath).Length;
            using (HttpClient client = new HttpClient())
            {
                QuotaService service = new QuotaService(client, temporaryRoot);
                DailyUsage initial = service.FetchDailyUsageAsync().GetAwaiter().GetResult().Find(delegate(DailyUsage day) {
                    return day.TotalTokens > 0;
                });
                Check(initial != null && initial.EstimatedCost == 0.002875m,
                    "initial session scan must use built-in pricing before remote publication");
                PricingCatalog.ReplaceRemote(new Dictionary<string, RemoteModelPrice> {
                    { "gpt-5.4", new RemoteModelPrice { Input = 4m, Cached = 0.4m, Output = 20m } }
                });
                DailyUsage beforeInvalidation = service.FetchDailyUsageAsync().GetAwaiter().GetResult().Find(delegate(DailyUsage day) {
                    return day.TotalTokens > 0;
                });
                Check(beforeInvalidation != null && beforeInvalidation.EstimatedCost == 0.002875m,
                    "unchanged session files must retain the existing usage scan cache until invalidation");
                service.InvalidateUsageCache();
                DailyUsage refreshed = service.FetchDailyUsageAsync().GetAwaiter().GetResult().Find(delegate(DailyUsage day) {
                    return day.TotalTokens > 0;
                });
                Check(refreshed != null && refreshed.EstimatedCost == 0.0042m && refreshed.TotalTokens == 1100,
                    "invalidating scan cache must reprice unchanged session files while preserving token counts");
                Check(File.GetLastWriteTimeUtc(sessionPath) == modified && new FileInfo(sessionPath).Length == length,
                    "repricing must not require or modify session file timestamps or contents");
            }
        }
        finally
        {
            PricingCatalog.Replace(previous);
            PricingCatalog.ReplaceRemote(null);
            if (File.Exists(sessionPath)) File.Delete(sessionPath);
            if (Directory.Exists(sessions)) Directory.Delete(sessions);
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot);
        }
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
        CheckPricingPath(nativeContext, usage);
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
