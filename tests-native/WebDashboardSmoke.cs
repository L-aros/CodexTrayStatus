using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using CodexTrayStatus;

namespace CodexTrayStatus.Tests
{
internal static class WebDashboardSmoke
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private static int Main(string[] args)
    {
        using (StatisticsServer server = new StatisticsServer())
        {
            WebPreferences preferences = new WebPreferences { RefreshInterval = 60, ShowRemaining = true, AutoStart = false, Currency = "USD", ExchangeRate = 7m };
            preferences.Reminders = ReminderPreferences.Merge(null, null);
            server.PublishPreferences(preferences);
            server.SavePreferences = delegate(WebPreferences value) { preferences = WebPreferencePatch.Merge(preferences, value); server.PublishPreferences(preferences); return null; };
            int rebuilds = 0;
            server.RebuildReminders = delegate { rebuilds++; return null; };
            server.QuotaHistoryRequested = delegate(string range, string account) {
                if (range != "24h" && range != "7d" && range != "30d" && range != "90d")
                    return new QuotaHistoryResult { Range = range, ErrorCode = "invalid_range" };
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                return new QuotaHistoryResult {
                    Range = range, Available = true, From = now - 24L * 60L * 60L * 1000L, To = now,
                    Points = new List<QuotaHistoryPoint> {
                        new QuotaHistoryPoint { WindowKey = "fixture-five", CycleEnd = now + 7200000, ObservedAt = now - 3600000, ObservationId = "fixture-a", UsedPercent = 28, RemainingPercent = 72, Source = "official" },
                        new QuotaHistoryPoint { WindowKey = "fixture-five", CycleEnd = now + 7200000, ObservedAt = now - 900000, ObservationId = "fixture-b", UsedPercent = 42, RemainingPercent = 58, Source = "official" },
                        new QuotaHistoryPoint { WindowKey = "fixture-week", CycleEnd = now + 518400000, ObservedAt = now - 900000, ObservationId = "fixture-c", UsedPercent = 37, RemainingPercent = 63, Source = "official" }
                    }
                };
            };
            server.SavePricing = delegate(Dictionary<string, ModelPrice> prices) { PricingCatalog.Replace(prices); return null; };
            server.ResetAnnouncementsRequested = delegate { return Task.FromResult(new ResetAnnouncementsResult { Available = true, Status = new { latest_reset = new { id = "fixture-reset" } }, Recent = new object[0] }); };
            server.PublishReminderStatus(new ReminderStatus { StorageAvailable = true });
            int refreshes = 0;
            server.RefreshRequested = delegate { refreshes++; };
            List<DailyUsage> days = new List<DailyUsage>();
            for (int i = 0; i < 30; i++)
            {
                DateTimeOffset stamp = new DateTimeOffset(DateTime.Today.AddDays(i - 29).AddHours(12));
                string input = (1200000 + i % 5 * 350000).ToString();
                string usage = "{\"type\":\"event_msg\",\"timestamp\":\"" + stamp.ToString("o") + "\",\"payload\":{\"type\":\"token_count\",\"model\":\"gpt-5.6-sol\",\"info\":{\"last_token_usage\":{\"input_tokens\":" + input + ",\"cached_input_tokens\":1000000,\"output_tokens\":65000}}}}";
                string other = usage.Replace("gpt-5.6-sol", "gpt-5.4").Replace("65000", "120000");
                days.AddRange(QuotaService.SummarizeDailyJsonl(new[] { usage, other }));
            }
            server.Publish(new AppSnapshot { Daily = days, Today = days[29], RefreshedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Quota = new QuotaResult { Limits = new List<RateLimitWindow> {
                    new RateLimitWindow { Id = "primary", WindowKey = "fixture-five", Label = "5h", UsedPercent = 28, RemainingPercent = 72, ResetsAt = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds() },
                    new RateLimitWindow { Id = "secondary", WindowKey = "fixture-week", Label = "7d", UsedPercent = 37, RemainingPercent = 63, ResetsAt = DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeMilliseconds() }
                } } });
            server.Start();
            using (WebClient client = new WebClient())
            {
                client.Encoding = Encoding.UTF8;
                Check(client.DownloadString(server.Url).Contains("settings-form"), "The embedded page must include settings");
                Check(client.DownloadString(server.Url + "app.js").Contains("CachedInput"), "The script must be served");
                Check(client.DownloadString(server.Url + "api/usage").Contains("gpt-5.6-sol"), "API must contain model breakdowns");
                Check(client.DownloadString(server.Url + "api/reset-announcements").Contains("fixture-reset"), "Public reset announcements are served separately from account usage");
                client.Headers["Origin"] = new Uri(server.Url).GetLeftPart(UriPartial.Authority);
                client.Headers["Content-Type"] = "application/json";
                string priced = client.UploadString(server.Url + "api/pricing", "POST", "{\"fixture-model\":{\"Input\":2,\"Cached\":0.2,\"Output\":4}}");
                Check(priced.Contains("fixture-model") && client.DownloadString(server.Url + "api/pricing").Contains("fixture-model"), "Local pricing API saves and reads overrides");
                string historyJson = client.DownloadString(server.Url + "api/quota-history?range=7d");
                Check(historyJson.Contains("fixture-five") && !historyJson.Contains("AccountKey"), "Quota history API must return isolated, chartable points");
                bool invalidHistory = false;
                try { client.DownloadString(server.Url + "api/quota-history?range=invalid"); }
                catch (WebException) { invalidHistory = true; }
                Check(invalidHistory, "Unsupported quota history range must be rejected");
                string freshnessJson = client.DownloadString(server.Url + "api/usage");
                Check(freshnessJson.Contains("\"QuotaState\"") && freshnessJson.Contains("\"UsageState\"") && freshnessJson.Contains("\"QuotaValidity\"") && freshnessJson.Contains("unknown_account"), "API must expose explicit unknown validity for legacy snapshots");
                Check(freshnessJson.Contains("\"Quota\":["), "Quota array contract must remain compatible");
                string updated = client.UploadString(server.Url + "api/preferences", "POST", "{\"RefreshInterval\":30,\"ShowRemaining\":false,\"AutoStart\":false,\"Currency\":\"CNY\",\"ExchangeRate\":7.25}");
                Check(updated.Contains("CNY") && preferences.ExchangeRate == 7.25m, "Currency and settings changes must persist");
                Check(preferences.Reminders.Enabled == true, "Old clients preserve reminder defaults");
                client.UploadString(server.Url + "api/preferences", "POST", "{\"TaskbarDisplay\":{\"SelectionMode\":\"selected\",\"SelectedWindowKey\":\"fixture-week\",\"ShowCost\":false}}");
                Check(preferences.Currency == "CNY" && preferences.ExchangeRate == 7.25m && preferences.Reminders.Enabled == true, "Taskbar patch preserves currency and reminders");
                string preview = client.DownloadString(server.Url + "api/taskbar-preview");
                Check(preview.Contains("fixture-week") && !preview.Contains("¥"), "Applied preview selects stable window and hides cost");
                client.UploadString(server.Url + "api/taskbar-preview", "POST", "{\"TaskbarDisplay\":{\"ShowCost\":true}}");
                Check(preferences.TaskbarDisplay.ShowCost == false, "Draft preview does not persist");
                client.UploadString(server.Url + "api/preferences", "POST", "{\"Currency\":\"USD\"}");
                Check(preferences.TaskbarDisplay.ShowCost == false, "Unrelated patch preserves taskbar false");
                string baseSettings = "\"RefreshInterval\":30,\"ShowRemaining\":false,\"AutoStart\":false,\"Currency\":\"CNY\",\"ExchangeRate\":7.25";
                client.UploadString(server.Url + "api/preferences", "POST", "{" + baseSettings + ",\"Reminders\":{\"Enabled\":false,\"Threshold10\":false}}");
                client.UploadString(server.Url + "api/preferences", "POST", "{" + baseSettings + "}");
                Check(preferences.Reminders.Enabled == false && preferences.Reminders.Threshold10 == false && preferences.Reminders.Threshold20 == true, "Old client and nested patches preserve reminders");
                bool invalidReminder = false;
                try { client.UploadString(server.Url + "api/preferences", "POST", "{" + baseSettings + ",\"Reminders\":{\"QuietStartMinutes\":480}}"); }
                catch (WebException) { invalidReminder = true; }
                Check(invalidReminder && preferences.Reminders.QuietStartMinutes == 1380, "Invalid reminder patch leaves current settings intact");
                Check(freshnessJson.Contains("\"ReminderStatus\"") && !freshnessJson.Contains("ConsumedThresholds"), "Publish status without internal dedup state");
                client.UploadString(server.Url + "api/reminders/rebuild", "POST", "{}");
                Check(rebuilds == 1, "Explicit rebuild endpoint");
                client.UploadString(server.Url + "api/refresh", "POST", "{}");
                Check(refreshes == 1, "Refresh endpoint must request a refresh");
                bool rejected = false;
                try { client.UploadString(server.Url + "api/preferences", "POST", "{\"RefreshInterval\":1}"); }
                catch (WebException) { rejected = true; }
                Check(rejected, "Invalid preferences must be rejected");
                client.Headers["Origin"] = "https://example.com";
                rejected = false;
                try { client.UploadString(server.Url + "api/refresh", "POST", "{}"); }
                catch (WebException) { rejected = true; }
                Check(rejected && refreshes == 1, "Cross-origin writes must be rejected");
                rejected = false;
                try { client.UploadString(server.Url + "api/reminders/rebuild", "POST", "{}"); }
                catch (WebException) { rejected = true; }
                Check(rejected && rebuilds == 1, "Cross-origin rebuild rejected");
                rejected = false;
                try { client.DownloadString(new Uri(server.Url).GetLeftPart(UriPartial.Authority) + "/api/usage"); }
                catch (WebException) { rejected = true; }
                Check(rejected, "Requests without the private path must be rejected");
            }
            Console.WriteLine("Web dashboard API tests passed.");
            if (args.Length > 0 && args[0] == "--serve")
            {
                preferences.Currency = "USD"; preferences.Reminders = ReminderPreferences.Merge(null, null); preferences.TaskbarDisplay = TaskbarDisplayPreferences.Merge(null, null); server.PublishPreferences(preferences);
                if (Array.IndexOf(args, "--live") >= 0)
                {
                    AppSnapshot live = new QuotaService().RefreshAsync().GetAwaiter().GetResult();
                    Check(live.Daily != null, "Live daily usage must load successfully");
                    foreach (DailyUsage day in live.Daily)
                    {
                        long tokens = 0; decimal cost = 0;
                        foreach (ModelUsage model in day.Models) { tokens += model.TotalTokens; cost += model.EstimatedCost; }
                        Check(tokens == day.TotalTokens && cost == day.EstimatedCost, "Live daily totals must reconcile with models");
                        Check(day.CachedInput + day.UncachedInput + day.Output == day.TotalTokens, "Live token categories must reconcile");
                    }
                    server.Publish(live);
                    Console.WriteLine("Live log reconciliation passed: " + live.Daily.Count + " days.");
                }
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web-test-url.txt"), server.Url);
                Console.WriteLine(server.Url);
                Console.ReadLine();
            }
        }
        return 0;
    }
}
}
