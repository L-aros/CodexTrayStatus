using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace CodexTrayStatus
{
    internal static class TaskbarDisplaySmoke
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr process, uint flags);
        private static int checks;
        private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        private static void Reject(Action action, string message) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, message); }
        private static WebPreferences Preferences() { return new WebPreferences { RefreshInterval = 60, ShowRemaining = true, Currency = "USD", ExchangeRate = 7m, Reminders = ReminderPreferences.Merge(null, null), TaskbarDisplay = TaskbarDisplayPreferences.Merge(null, null) }; }
        private static RateLimitWindow Window(string key, string label, double remaining, long now) { return new RateLimitWindow { Id = key, WindowKey = key, Label = label, WindowSeconds = 18000, UsedPercent = 100 - remaining, RemainingPercent = remaining, ResetsAt = now + 7200000 }; }
        [STAThread]
        private static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception error) { Console.Error.WriteLine("Taskbar failure after " + checks + " checks: " + error.Message); return 1; }
        }
        private static int Run(string[] args)
        {
            Rectangle bar = new Rectangle(-1920, 1040, 1920, 40);
            Rectangle tray = new Rectangle(-200, 1040, 200, 40);
            IntPtr shell = new IntPtr(42);
            Check(TaskbarSurface.Matches(true, shell, bar, tray, shell, bar, tray), "Stable negative-coordinate monitor is valid");
            Check(!TaskbarSurface.Matches(false, shell, bar, tray, shell, bar, tray), "Missing shell cannot resurrect overlay");
            Check(!TaskbarSurface.Matches(true, IntPtr.Zero, bar, tray, IntPtr.Zero, bar, tray), "Zero shell handle rejected");
            Check(!TaskbarSurface.Matches(true, new IntPtr(43), bar, tray, shell, bar, tray), "Explorer replacement invalidates frame");
            Check(!TaskbarSurface.Matches(true, shell, new Rectangle(-1920, 1078, 1920, 40), tray, shell, bar, tray), "Auto-hide animation invalidates frame");
            Check(!TaskbarSurface.Matches(true, shell, bar, new Rectangle(-300, 1040, 300, 40), shell, bar, tray), "Notification expansion invalidates frame");
            Check(!TaskbarSurface.Matches(true, shell, Rectangle.Empty, Rectangle.Empty, shell, Rectangle.Empty, Rectangle.Empty), "Empty geometry rejected");
            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            string date = DateTime.Today.ToString("yyyy-MM-dd");
            AppSnapshot snapshot = new AppSnapshot {
                QuotaState = new DataState { Source = "official", Scope = "account", AccountKey = "test", ObservedAt = now, TimeBasis = "response_received", IsComplete = true },
                UsageState = new DataState { Source = "local", ObservedAt = now, TimeBasis = "scan_completed", IsComplete = true, CoverageEndDate = date },
                Today = new TodayUsage { TotalTokens = 12345, EstimatedCost = 1.25m },
                Quota = new QuotaResult { Limits = new List<RateLimitWindow> { Window("week", "7d", 8.1, now), Window("five", "5h", 72, now), null } }
            };
            WebPreferences prefs = Preferences();
            using (TaskbarOverlay surface = new TaskbarOverlay())
            {
                surface.ApplyPreferences(prefs);
                surface.Start();
                surface.ApplySnapshot(snapshot);
                Check(surface.IsHandleCreated, "Default visible mode creates the overlay handle");
                surface.Stop();
                surface.Stop();
                Check(surface.IsDisposed, "Repeated stop does not recreate a destroyed window handle");
            }
            Check(prefs.TaskbarDisplay.DisplayMode == "overlay", "Default mode remains visible when Windows hides notification icons");
            Check(TaskbarDisplayPreferences.Load("{\"Version\":1,\"ShowCost\":false}").DisplayMode == "overlay", "Legacy settings migrate to visible mode");
            TaskbarDisplayPreferences extended = TaskbarDisplayPreferences.Merge(prefs.TaskbarDisplay, new TaskbarDisplayPreferences { DisplayMode = "overlay" });
            Check(TaskbarDisplayPreferences.Merge(extended, new TaskbarDisplayPreferences { ShowCost = false }).DisplayMode == "overlay", "Partial settings preserve display mode");
            Reject(() => TaskbarDisplayPreferences.Merge(null, new TaskbarDisplayPreferences { DisplayMode = "invalid" }), "Unknown display mode rejected");
            Reject(() => WebPreferencePatch.Parse("{\"TaskbarDisplay\":{\"DisplayMode\":true}}"), "Display mode must be a string");
            TaskbarPresentation view = TaskbarPresenter.Build(snapshot, prefs, now);
            string initialKey = TaskbarPresenter.VisualKey(view);
            view.CompactSecondary += "changed";
            Check(TaskbarPresenter.VisualKey(view) != initialKey, "Compact detail participates in bitmap cache key");
            view = TaskbarPresenter.Build(snapshot, prefs, now);
            view.CompactPrefix += "changed";
            Check(TaskbarPresenter.VisualKey(view) != initialKey, "Compact prefix participates in bitmap cache key");
            view = TaskbarPresenter.Build(snapshot, prefs, now);
            Check(view.WindowKey == "five" && view.Prefix == "2H · 剩余 " && view.Percent == "72%" && view.Secondary == "今日 12.3K · $1.25", "Default layout text must remain compatible");
            Check(TaskbarPresenter.VisualKey(TaskbarPresenter.Build(snapshot, prefs, now + 1500)) == TaskbarPresenter.VisualKey(TaskbarPresenter.Build(snapshot, prefs, now + 3000)), "Stable formatted countdown must not change the bitmap key");
            Check(TaskbarPresenter.VisualKey(view) != TaskbarPresenter.VisualKey(TaskbarPresenter.Build(snapshot, prefs, now + 1500)), "Crossing an hour changes the bitmap key");
            prefs.TaskbarDisplay.SelectionMode = "minimum";
            Check(TaskbarPresenter.Build(snapshot, prefs, now).WindowKey == "week", "Minimum compares raw remaining");
            prefs.ShowRemaining = false;
            Check(TaskbarPresenter.Build(snapshot, prefs, now).Percent == "92%", "Used display must not reverse minimum selection");
            snapshot.Quota.Limits[1].RemainingPercent = 8.1; snapshot.Quota.Limits[1].UsedPercent = 91.9;
            Check(TaskbarPresenter.Select(snapshot, prefs.TaskbarDisplay, now).WindowKey == "five", "Ties prefer five hours");
            snapshot.Quota.Limits.Reverse();
            Check(TaskbarPresenter.Select(snapshot, prefs.TaskbarDisplay, now).WindowKey == "five", "Input order does not change tie result");
            snapshot.Quota.Limits[1].RemainingPercent = double.NaN;
            Check(TaskbarPresenter.Select(snapshot, prefs.TaskbarDisplay, now).WindowKey == "week", "NaN excluded");
            snapshot.QuotaState.ObservedAt = now - DataFreshness.MaxAgeMilliseconds;
            Check(TaskbarPresenter.Build(snapshot, prefs, now).Percent == "--", "Expired values cannot be minimum candidates");
            prefs.TaskbarDisplay.SelectionMode = "selected"; prefs.TaskbarDisplay.SelectedWindowKey = "week";
            view = TaskbarPresenter.Build(snapshot, prefs, now);
            Check(view.Status == "stale" && view.Accent == "neutral" && view.Prefix.StartsWith("旧值"), "Selected stale values must be marked");
            snapshot.QuotaState.ObservedAt = now; snapshot.QuotaState.AccountKey = null; snapshot.QuotaState.Source = "local";
            Check(TaskbarPresenter.Build(snapshot, prefs, now).Status == "reference", "Unattributed local value is reference");
            prefs.TaskbarDisplay.SelectedWindowKey = "gone";
            Check(TaskbarPresenter.Build(snapshot, prefs, now).Status == "missing", "Missing window never silently substitutes");
            snapshot.Quota.Limits.Add(Window("gone", "7d", 50, now));
            Check(TaskbarPresenter.Build(snapshot, prefs, now).WindowKey == "gone", "Window reappearance restores selection");
            snapshot.Quota.Limits.Add(Window("gone", "7d", 20, now));
            Check(TaskbarPresenter.Build(snapshot, prefs, now).Status == "missing", "Ambiguous identity is not guessed");
            prefs.TaskbarDisplay.SelectedWindowKey = "week"; prefs.TaskbarDisplay.ShowCost = false; prefs.TaskbarDisplay.ShowCountdown = false;
            view = TaskbarPresenter.Build(snapshot, prefs, now);
            Check(!view.Secondary.Contains("$") && view.Prefix.Contains("7D") && !view.Prefix.Contains("2H"), "Independent toggles");
            prefs.TaskbarDisplay.ShowCost = true; prefs.Currency = "CNY"; prefs.ExchangeRate = 7.25m;
            snapshot.Today.UnpricedTokens = 2;
            Check(TaskbarPresenter.Build(snapshot, prefs, now).Secondary.Contains("¥9.06"), "Currency and partial pricing");
            snapshot.UsageState.CoverageEndDate = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
            Check(TaskbarPresenter.Build(snapshot, prefs, now).Secondary == "今日暂无统计", "Yesterday must not be labelled today");
            RateLimitWindow time = Window("time", "5h", 10, now);
            foreach (long left in new long[] { -1, 0, 1, 59999, 60000, 3599999, 3600000, 86400000 }) {
                time.ResetsAt = now + left; string text = TaskbarPresenter.Countdown(time, now);
                Check(!text.Contains("-") && (left > 0 || text == "待更新"), "Countdown clamps at reset");
            }
            time.ResetsAt = long.MaxValue; Check(TaskbarPresenter.Countdown(time, now) == null, "Invalid timestamp");
            time.ResetsAt = null; Check(TaskbarPresenter.Countdown(time, now) == null, "Unknown timestamp");
            Check(TaskbarPresenter.Build(null, Preferences(), now).Percent == "--", "Empty snapshot");

            string saved = null;
            bool fail = false;
            TaskbarSettingsStore store = new TaskbarSettingsStore(() => saved, text => { if (fail) throw new IOException(); saved = text; });
            Check(store.Current.ShowCost == true && store.Current.ShowCountdown == true && store.Current.SelectionMode == "default", "Missing registry uses defaults");
            store.Save(new TaskbarDisplayPreferences { SelectionMode = "selected", SelectedWindowKey = "gone", ShowCost = false });
            TaskbarSettingsStore reload = new TaskbarSettingsStore(() => saved, text => saved = text);
            Check(reload.Current.SelectedWindowKey == "gone" && reload.Current.ShowCost == false, "Restart preserves unavailable selection and false");
            fail = true;
            Reject(() => store.Save(new TaskbarDisplayPreferences { ShowCost = true }), "Write failure propagated");
            Check(store.Current.ShowCost == false && TaskbarDisplayPreferences.Load(saved).ShowCost == false, "Failed write preserves memory and persisted value");
            Check(new TaskbarSettingsStore(() => "broken", text => {}).Error != null, "Corrupt settings show error");
            Reject(() => TaskbarDisplayPreferences.Load("{\"Version\":2}"), "Future version rejected");
            Reject(() => TaskbarDisplayPreferences.Merge(null, new TaskbarDisplayPreferences { SelectionMode = "selected" }), "Empty selection rejected");
            Reject(() => TaskbarDisplayPreferences.Merge(null, new TaskbarDisplayPreferences { SelectedWindowKey = new string('x', 513) }), "Bound key length");
            WebPreferences current = Preferences(); current.Currency = "CNY"; current.ExchangeRate = 7.25m; current.Reminders.Enabled = false;
            WebPreferences merged = WebPreferencePatch.Merge(current, WebPreferencePatch.Parse("{\"TaskbarDisplay\":{\"ShowCost\":false}}"));
            Check(merged.Currency == "CNY" && merged.ExchangeRate == 7.25m && merged.Reminders.Enabled == false && merged.TaskbarDisplay.ShowCost == false, "Taskbar-only patch preserves other preferences");
            merged = WebPreferencePatch.Merge(merged, WebPreferencePatch.Parse("{\"Currency\":\"USD\",\"TaskbarDisplay\":null}"));
            Check(merged.TaskbarDisplay.ShowCost == false, "Missing/null taskbar preserves false");
            Reject(() => WebPreferencePatch.Merge(current, WebPreferencePatch.Parse("{\"RefreshInterval\":1}")), "Invalid base patch rejected");
            Reject(() => WebPreferencePatch.Parse("{\"TaskbarDisplay\":{\"ShowCost\":\"false\"}}"), "Boolean strings rejected");

            string directory = args[0]; Directory.CreateDirectory(directory);
            foreach (int iconSize in new[] { 16, 20, 24, 32, 40, 48, 64 })
            foreach (string percentage in new[] { "0%", "8%", "42%", "99%", "100%" })
            foreach (bool contrast in new[] { false, true })
            {
                TaskbarPresentation iconView = new TaskbarPresentation { Status = "fresh", Percent = percentage, Accent = "green" };
                Check(TrayNumberIcon.Text(iconView) == percentage.TrimEnd('%'), "Fresh icon uses presenter's selected metric");
                using (Bitmap rendered = TrayNumberIcon.Draw(iconView, iconSize, contrast))
                {
                    Check(rendered.Width == iconSize && rendered.Height == iconSize, "Icon rendered at requested DPI size");
                    rendered.Save(Path.Combine(directory, "tray-" + iconSize + "-" + percentage.TrimEnd('%') + "-" + contrast + ".png"));
                }
                using (Icon icon = TrayNumberIcon.Create(iconView, iconSize, contrast)) Check(icon.Handle != IntPtr.Zero, "Owned icon survives temporary bitmap disposal");
            }
            foreach (string status in new[] { "stale", "reference", "unknown", "missing" })
                Check(TrayNumberIcon.Text(new TaskbarPresentation { Status = status, Percent = "72%" }) == "?", "Unqualified icon must not present stale/reference as fresh");
            Check(TrayNumberIcon.Text(new TaskbarPresentation { Status = "fresh", Percent = "NaN%" }) == "?", "Invalid icon metric rejected");
            Check(TrayNumberIcon.Text(new TaskbarPresentation { Status = "fresh", Percent = "101%" }) == "?", "Out of range icon metric rejected");
            Check(TrayNumberIcon.Text(new TaskbarPresentation { Status = "fresh", Percent = "28%" }) == "28", "Used mode is not silently replaced with remaining mode");
            using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
            {
                uint gdiBefore = GetGuiResources(process.Handle, 0), userBefore = GetGuiResources(process.Handle, 1);
                for (int i = 0; i < 200; i++)
                {
                    using (Icon icon = TrayNumberIcon.Create(new TaskbarPresentation { Status = "fresh", Percent = (i % 101) + "%", Accent = "green" }, i % 2 == 0 ? 16 : 32, false))
                        Check(icon.Handle != IntPtr.Zero, "Repeated icon updates own a valid handle");
                }
                uint gdiAfter = GetGuiResources(process.Handle, 0), userAfter = GetGuiResources(process.Handle, 1);
                Check(gdiBefore > 0 && userBefore > 0, "GUI resource counters available");
                Check(gdiAfter <= gdiBefore + 4 && userAfter <= userBefore + 4, "Repeated updates release bitmap, DC and HICON resources");
                Console.WriteLine("Icon resource stress: GDI " + gdiBefore + " -> " + gdiAfter + "; USER " + userBefore + " -> " + userAfter + ".");
            }
            int images = 0;
            foreach (int height in new[] { 24, 32, 40, 48, 60, 72, 84, 96, 120 })
            foreach (int width in new[] { 8, 25, 48, 80, 120, 240, 800 })
            foreach (string status in new[] { "fresh", "stale", "reference", "unknown" })
            {
                view = new TaskbarPresentation { Prefix = "很长的额度窗口名称测试 · 剩余 ", CompactPrefix = status == "stale" ? "旧值 剩余 " : status == "reference" ? "参考 剩余 " : "剩余 ", Percent = "100%", Secondary = "今日 9223372036854M · ¥1234567890123456789.99", CompactSecondary = "9223372036854M", Status = status };
                using (TaskbarLayout layout = TaskbarLayout.Create(view, width, height))
                {
                    if (layout == null) { Check(width < 240 || height < 24, "Enough space must not hide overlay: " + width + "x" + height + " " + status); continue; }
                    Check(layout.Width <= width && layout.Height == height, "Layout stays within available area");
                    foreach (RectangleF rect in new[] { layout.PrefixBounds, layout.PercentBounds, layout.SecondaryBounds })
                        if (rect.Width > 0 && rect.Height > 0) Check(rect.Left >= 0 && rect.Right <= layout.Width && rect.Top >= 0 && rect.Bottom <= height, "Every measured text rectangle fits");
                    using (Bitmap bitmap = layout.Draw("green")) {
                        // Alpha must not touch the bitmap boundary: catch clipped glyphs, not just arithmetic errors.
                        for (int x = 0; x < bitmap.Width; x++) Check(bitmap.GetPixel(x, 0).A == 0 && bitmap.GetPixel(x, bitmap.Height - 1).A == 0, "Vertical glyph clipping");
                        for (int y = 0; y < bitmap.Height; y++) Check(bitmap.GetPixel(0, y).A == 0 && bitmap.GetPixel(bitmap.Width - 1, y).A == 0, "Horizontal glyph clipping");
                        if (status == "fresh" && (width == 120 || width == 800)) { bitmap.Save(Path.Combine(directory, "layout-" + height + "-" + width + ".png")); images++; }
                    }
                }
            }
            Console.WriteLine("Taskbar selection, settings and offscreen layout passed: " + checks + " checks, " + images + " images.");
            return 0;
        }
    }
}
