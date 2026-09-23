using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexTrayStatus
{
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private const string PreferencesRegistryPath = @"Software\CodexTrayStatus";
        private readonly QuotaService quotaService;
        private readonly TaskbarOverlay overlay;
        private readonly NotifyIcon notifyIcon;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private readonly System.Windows.Forms.Timer freshnessTimer;
        private readonly ContextMenuStrip contextMenu;
        private readonly ToolStripMenuItem quotaLine;
        private readonly ToolStripMenuItem usageLine;
        private readonly ToolStripMenuItem autoStartItem;
        private readonly StatisticsServer statistics = new StatisticsServer();
        private readonly ResetAnnouncementsService resetAnnouncements = new ResetAnnouncementsService();
        private readonly ReminderCoordinator reminders;
        private readonly QuotaHistoryStore quotaHistory;
        private ReminderPreferences reminderPreferences;
        private string reminderSettingsError;
        private readonly TaskbarSettingsStore taskbarSettings;
        private AppSnapshot snapshot;
        private int refreshIntervalSeconds;
        private bool showRemaining;
        private bool changingAutoStart;
        private int refreshing;
        private int exiting;
        private int statusIconKey;
        private bool statusIconKeyReady;

        internal TrayApplicationContext()
        {
            refreshIntervalSeconds = LoadRefreshIntervalSeconds();
            showRemaining = LoadShowRemaining();
            reminderPreferences = LoadReminderPreferences();
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PreferencesRegistryPath))
            {
                if (key != null)
                {
                    CurrencyDisplay.Currency = Convert.ToString(key.GetValue("Currency", "USD")) == "CNY" ? "CNY" : "USD";
                    decimal rate;
                    if (decimal.TryParse(Convert.ToString(key.GetValue("ExchangeRate", "7")), NumberStyles.Number, CultureInfo.InvariantCulture, out rate) && rate >= 0.01m && rate <= 100m)
                        CurrencyDisplay.ExchangeRate = rate;
                }
            }
            taskbarSettings = new TaskbarSettingsStore(delegate {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PreferencesRegistryPath)) return key == null ? null : key.GetValue("TaskbarDisplayV1");
            }, delegate(string json) {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(PreferencesRegistryPath)) key.SetValue("TaskbarDisplayV1", json);
            });
            LoadPricingOverrides();
            quotaService = new QuotaService();
            overlay = new TaskbarOverlay();
            overlay.ApplyPreferences(CurrentWebPreferences());
            quotaLine = new ToolStripMenuItem("额度：等待刷新") { Enabled = false };
            usageLine = new ToolStripMenuItem("今日：等待统计") { Enabled = false };
            autoStartItem = new ToolStripMenuItem("开机启动") { Checked = IsAutoStartEnabled(), CheckOnClick = true };
            autoStartItem.CheckedChanged += ToggleAutoStart;

            contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add(new ToolStripMenuItem("Codex Tray Status") { Enabled = false });
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(quotaLine);
            contextMenu.Items.Add(usageLine);
            contextMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem detailsItem = new ToolStripMenuItem("详情");
            detailsItem.Click += delegate { OpenDashboard(DashboardPage.Details); };
            contextMenu.Items.Add(detailsItem);
            ToolStripMenuItem teamItem = new ToolStripMenuItem("用量统计");
            teamItem.Click += delegate { OpenDashboard(DashboardPage.Statistics); };
            contextMenu.Items.Add(teamItem);
            ToolStripMenuItem settingsItem = new ToolStripMenuItem("设置");
            settingsItem.Click += delegate { OpenDashboard(DashboardPage.Settings); };
            contextMenu.Items.Add(settingsItem);
            ToolStripMenuItem refreshItem = new ToolStripMenuItem("立即刷新");
            refreshItem.Click += async delegate { await RefreshAsync(false); };
            contextMenu.Items.Add(refreshItem);
            contextMenu.Items.Add(autoStartItem);
            ToolStripMenuItem pause = new ToolStripMenuItem("暂停额度提醒");
            pause.DropDownItems.Add("1 小时", null, delegate { SnoozeReminders(DateTimeOffset.Now.AddHours(1).ToUnixTimeMilliseconds()); });
            pause.DropDownItems.Add("直到明天 08:00", null, delegate { SnoozeReminders(new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(8)).ToUnixTimeMilliseconds()); });
            pause.DropDownItems.Add("取消临时暂停", null, delegate { SnoozeReminders(0); });
            contextMenu.Items.Add(pause);
            contextMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem quitItem = new ToolStripMenuItem("退出");
            quitItem.Click += delegate { ExitThread(); };
            contextMenu.Items.Add(quitItem);

            notifyIcon = new NotifyIcon();
            notifyIcon.Text = "Codex Tray Status";
            notifyIcon.ContextMenuStrip = contextMenu;
            notifyIcon.Icon = CreateStatusIcon(null);
            statusIconKey = GetStatusIconKey(null);
            statusIconKeyReady = true;
            notifyIcon.Visible = true;
            reminders = new ReminderCoordinator(new ReminderStateStore(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTrayStatus", "reminders-state.json")), new ReminderNotificationSink(notifyIcon));
            quotaHistory = new QuotaHistoryStore(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTrayStatus", "quota-history.jsonl"));
            notifyIcon.BalloonTipClicked += delegate { OpenDashboard(DashboardPage.Details); };
            notifyIcon.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleDashboard();
            };

            overlay.ClickRequested += delegate { ToggleDashboard(); };
            overlay.Start();
            statistics.SavePreferences = delegate(WebPreferences preferences) {
                return (string)overlay.Invoke(new Func<string>(delegate { return ApplyWebPreferences(preferences); }));
            };
            statistics.RefreshRequested = delegate { overlay.BeginInvoke(new Action(async delegate { await RefreshAsync(false); })); };
            statistics.RebuildReminders = delegate { return (string)overlay.Invoke(new Func<string>(delegate {
                string error = reminders.Rebuild(); ApplySnapshot(); return error;
            })); };
            statistics.QuotaHistoryRequested = delegate(string range, string account) { return quotaHistory.Query(range, account, DataFreshness.Now); };
            statistics.ResetAnnouncementsRequested = delegate { return resetAnnouncements.GetAsync(); };
            statistics.SavePricing = delegate(System.Collections.Generic.Dictionary<string, ModelPrice> values) { return (string)overlay.Invoke(new Func<string>(delegate { return SavePricingOverrides(values); })); };
            PublishWebPreferences();

            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = refreshIntervalSeconds * 1000;
            refreshTimer.Tick += async delegate { await RefreshAsync(true); };
            refreshTimer.Start();
            freshnessTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            freshnessTimer.Tick += delegate { if (snapshot != null) { DataFreshness.Synchronize(snapshot); ApplySnapshotOnUiThread(); } };
            freshnessTimer.Start();

            // The overlay handle already exists, so this posts the initial refresh to
            // the WinForms message loop without introducing a cross-thread UI race.
            overlay.BeginInvoke(new Action(async delegate { await RefreshAsync(true); }));
        }

        private async Task RefreshAsync(bool silent)
        {
            if (Interlocked.CompareExchange(ref exiting, 0, 0) != 0) return;
            if (Interlocked.CompareExchange(ref refreshing, 1, 0) != 0) return;
            RefreshState attempt = new RefreshState { AttemptId = Guid.NewGuid().ToString("N"), AttemptedAt = DataFreshness.Now, IsRefreshing = true };
            if (snapshot == null) snapshot = new AppSnapshot();
            snapshot.RefreshState = attempt;
            ApplySnapshotOnUiThread();
            try
            {
                AppSnapshot value = await quotaService.RefreshAsync();
                attempt.IsRefreshing = false;
                attempt.CompletedAt = DataFreshness.Now;
                value.RefreshState = attempt;
                if (Interlocked.CompareExchange(ref exiting, 0, 0) != 0) return;
                if (value != null) snapshot = MergeWithLastGood(snapshot, value);
                if (reminderSettingsError == null) reminders.Process(snapshot, reminderPreferences, DateTimeOffset.Now);
                quotaHistory.Record(snapshot, DataFreshness.Now);
                ApplySnapshotOnUiThread();
            }
            catch (Exception error)
            {
                attempt.IsRefreshing = false;
                attempt.CompletedAt = DataFreshness.Now;
                snapshot = DataFreshness.Merge(snapshot, new AppSnapshot { RefreshState = attempt, RefreshedAt = DataFreshness.Now, Error = "刷新暂不可用", QuotaState = DataFreshness.Unavailable(attempt.AttemptedAt, "refresh_failed"), UsageState = DataFreshness.Unavailable(attempt.AttemptedAt, "refresh_failed") });
                ApplySnapshotOnUiThread();
                if (!silent && Interlocked.CompareExchange(ref exiting, 0, 0) == 0 && notifyIcon.Visible)
                    notifyIcon.ShowBalloonTip(4000, "Codex Tray Status", error.Message, ToolTipIcon.Warning);
            }
            finally
            {
                Interlocked.Exchange(ref refreshing, 0);
            }
        }

        private void ApplySnapshotOnUiThread()
        {
            if (Interlocked.CompareExchange(ref exiting, 0, 0) != 0 || overlay.IsDisposed) return;
            if (overlay.InvokeRequired)
            {
                try
                {
                    overlay.BeginInvoke(new Action(delegate
                    {
                        if (Interlocked.CompareExchange(ref exiting, 0, 0) == 0) ApplySnapshot();
                    }));
                }
                catch (InvalidOperationException) { }
                return;
            }
            ApplySnapshot();
        }

        private static AppSnapshot MergeWithLastGood(AppSnapshot previous, AppSnapshot current)
        {
            return DataFreshness.Merge(previous, current);
        }

        private void ApplySnapshot()
        {
            if (Interlocked.CompareExchange(ref exiting, 0, 0) != 0) return;
            overlay.ApplySnapshot(snapshot);
            ReminderStatus reminderStatus = reminders.Status(reminderPreferences, DateTimeOffset.Now);
            if (reminderSettingsError != null) { reminderStatus.ErrorCode = reminderSettingsError; reminderStatus.Suppression = "settings_error"; }
            statistics.PublishReminderStatus(reminderStatus);
            statistics.Publish(snapshot);

            RateLimitWindow primary = FindPrimaryWindow();
            RateLimitWindow iconWindow = primary != null && TaskbarPresenter.ValidPair(primary) && DataFreshness.EvaluateQuota(primary, snapshot.QuotaState, DataFreshness.Now).IsFresh ? primary : null;
            TaskbarPresentation view = TaskbarPresenter.Build(snapshot, CurrentWebPreferences(), DataFreshness.Now);
            string nextQuotaText = view.Prefix + view.Percent + " · " + view.Detail;
            if (!string.Equals(quotaLine.Text, nextQuotaText, StringComparison.Ordinal)) quotaLine.Text = nextQuotaText;

            string nextUsageText = snapshot == null || snapshot.Today == null
                ? "今日：暂无数据"
                : "今日 " + TaskbarOverlay.FormatTokens(snapshot.Today.TotalTokens) + " · " + CurrencyDisplay.Format(snapshot.Today.EstimatedCost);
            if (!string.Equals(usageLine.Text, nextUsageText, StringComparison.Ordinal))
                usageLine.Text = nextUsageText;

            int nextIconKey = GetStatusIconKey(iconWindow);
            if (!statusIconKeyReady || nextIconKey != statusIconKey)
            {
                Icon oldIcon = notifyIcon.Icon;
                notifyIcon.Icon = CreateStatusIcon(iconWindow);
                statusIconKey = nextIconKey;
                statusIconKeyReady = true;
                if (oldIcon != null) oldIcon.Dispose();
            }

            string nextTooltip = BuildTooltip();
            if (!string.Equals(notifyIcon.Text, nextTooltip, StringComparison.Ordinal))
                notifyIcon.Text = nextTooltip;
        }

        private RateLimitWindow FindPrimaryWindow()
        {
            return TaskbarPresenter.Select(snapshot, taskbarSettings.Current, DataFreshness.Now);
        }

        private string BuildTooltip()
        {
            string text = "Codex Tray Status";
            if (snapshot != null) text += "\n" + DataFreshness.Describe(snapshot.QuotaState, DataFreshness.EvaluateQuota(FindPrimaryWindow(), snapshot.QuotaState, DataFreshness.Now));
            if (snapshot != null && snapshot.Quota != null && snapshot.Quota.Limits != null)
                foreach (RateLimitWindow item in snapshot.Quota.Limits)
                    if (item != null)
                    {
                        double? metric = showRemaining ? item.RemainingPercent : item.UsedPercent;
                        if (metric.HasValue)
                            text += "\n" + item.Label + (showRemaining ? " 剩余 " : " 已用 ") +
                                Math.Round(metric.Value).ToString("0", CultureInfo.InvariantCulture) + "%";
                    }
            return text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private void ToggleDashboard()
        {
            if (Interlocked.CompareExchange(ref exiting, 0, 0) != 0) return;
            OpenDashboard(DashboardPage.Details);
        }

        internal void OpenWeb() { OpenDashboard(DashboardPage.Details); }

        private void OpenDashboard(DashboardPage page)
        {
            if (Interlocked.CompareExchange(ref exiting, 0, 0) != 0) return;
            try { statistics.Publish(snapshot); PublishWebPreferences(); statistics.Open(page == DashboardPage.Details ? "overview" : page == DashboardPage.Statistics ? "statistics" : "settings"); }
            catch (Exception error) { MessageBox.Show(error.Message, "无法打开本地网页", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private static Icon CreateStatusIcon(RateLimitWindow window)
        {
            int remaining = GetStatusIconKey(window);
            Color accent = remaining < 0 ? Color.FromArgb(110, 120, 134)
                : remaining <= 15 ? Color.FromArgb(239, 92, 92)
                : remaining <= 40 ? Color.FromArgb(230, 173, 62)
                : Color.FromArgb(83, 201, 126);
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.Clear(Color.Transparent);
                using (Brush background = new SolidBrush(Color.FromArgb(28, 31, 38)))
                    graphics.FillEllipse(background, 2, 2, 28, 28);
                using (Pen ring = new Pen(accent, 3)) graphics.DrawEllipse(ring, 3, 3, 26, 26);
                string text = remaining < 0 ? "–" : remaining.ToString(CultureInfo.InvariantCulture);
                using (Font font = Typography.Display(text.Length >= 3 ? 9 : 11, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Brush brush = new SolidBrush(Color.White))
                {
                    SizeF size = graphics.MeasureString(text, font);
                    graphics.DrawString(text, font, brush, (32 - size.Width) / 2f, (32 - size.Height) / 2f);
                }
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { NativeMethods.DestroyIcon(handle); }
            }
        }

        private static int GetStatusIconKey(RateLimitWindow window)
        {
            return window == null || !window.RemainingPercent.HasValue
                ? -1 : (int)Math.Round(window.RemainingPercent.Value);
        }

        private string ApplyWebPreferences(WebPreferences preferences)
        {
            bool saveTaskbar = preferences.TaskbarDisplay != null;
            bool saveReminders = preferences.Reminders != null;
            try { preferences = WebPreferencePatch.Merge(CurrentWebPreferences(), preferences); }
            catch (Exception error) { return error.Message; }
            WebPreferences previous = CurrentWebPreferences();
            bool intervalChanged = previous.RefreshInterval != preferences.RefreshInterval || previous.ShowRemaining != preferences.ShowRemaining;
            bool currencyChanged = previous.Currency != preferences.Currency || previous.ExchangeRate != preferences.ExchangeRate;
            bool autoStartChanged = previous.AutoStart != preferences.AutoStart;
            try
            {
                if (autoStartChanged) WriteAutoStart(preferences.AutoStart);
                if (intervalChanged) SavePreferences(preferences.RefreshInterval, preferences.ShowRemaining);
                if (currencyChanged || saveReminders)
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(PreferencesRegistryPath))
                    {
                        if (currencyChanged) {
                            key.SetValue("Currency", preferences.Currency);
                            key.SetValue("ExchangeRate", preferences.ExchangeRate.ToString(CultureInfo.InvariantCulture));
                        }
                        if (saveReminders) key.SetValue("RemindersV1", new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(preferences.Reminders));
                    }
                // Last persistence operation: a failure leaves Current untouched.
                if (saveTaskbar) taskbarSettings.Save(preferences.TaskbarDisplay);
            }
            catch (Exception)
            {
                // No in-memory setting or frame has been changed yet.
                bool rollbackFailed = false;
                try { if (autoStartChanged) WriteAutoStart(previous.AutoStart); } catch { rollbackFailed = true; }
                try { if (intervalChanged) SavePreferences(previous.RefreshInterval, previous.ShowRemaining); } catch { rollbackFailed = true; }
                try {
                    if (currencyChanged || saveReminders)
                        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(PreferencesRegistryPath)) {
                            if (currencyChanged) {
                                key.SetValue("Currency", previous.Currency);
                                key.SetValue("ExchangeRate", previous.ExchangeRate.ToString(CultureInfo.InvariantCulture));
                            }
                            if (saveReminders) key.SetValue("RemindersV1", new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(previous.Reminders));
                        }
                } catch { rollbackFailed = true; }
                return rollbackFailed ? "保存失败，部分旧设置无法恢复；当前显示未变，请重新保存" : "设置保存失败，当前设置和显示未变";
            }
            // Persistence succeeded. Never report a display failure as a failed save.
            refreshIntervalSeconds = preferences.RefreshInterval;
            showRemaining = preferences.ShowRemaining;
            reminderPreferences = preferences.Reminders;
            if (saveReminders) reminderSettingsError = null;
            CurrencyDisplay.Currency = preferences.Currency;
            CurrencyDisplay.ExchangeRate = preferences.ExchangeRate;
            changingAutoStart = true;
            autoStartItem.Checked = preferences.AutoStart;
            changingAutoStart = false;
            refreshTimer.Interval = refreshIntervalSeconds * 1000;
            PublishWebPreferences();
            overlay.ApplyPreferences(CurrentWebPreferences()); // SafeRender retries failed bitmap updates.
            try { ApplySnapshot(); } catch { /* The freshness timer retries UI publication. */ }
            return null;
        }

        private WebPreferences CurrentWebPreferences()
        {
            return new WebPreferences { RefreshInterval = refreshIntervalSeconds, ShowRemaining = showRemaining,
                AutoStart = autoStartItem != null && autoStartItem.Checked, Currency = CurrencyDisplay.Currency,
                ExchangeRate = CurrencyDisplay.ExchangeRate, Reminders = reminderPreferences,
                TaskbarDisplay = taskbarSettings.Current, TaskbarSettingsError = taskbarSettings.Error };
        }

        private void PublishWebPreferences() { statistics.PublishPreferences(CurrentWebPreferences()); }

        private void LoadPricingOverrides()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PreferencesRegistryPath))
                {
                    string json = key == null ? null : key.GetValue("PricingOverridesV1") as string;
                    if (string.IsNullOrEmpty(json)) return;
                    PricingCatalog.Replace(new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, ModelPrice>>(json));
                }
            }
            catch { PricingCatalog.Replace(null); }
        }

        private string SavePricingOverrides(System.Collections.Generic.Dictionary<string, ModelPrice> values)
        {
            try
            {
                PricingCatalog.Replace(values);
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(PreferencesRegistryPath))
                    key.SetValue("PricingOverridesV1", new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(PricingCatalog.Snapshot()));
                quotaService.InvalidateUsageCache();
                RefreshAsync(true); // Re-scan prices; token quantities remain derived from logs.
                return null;
            }
            catch (Exception error) { return error.Message; }
        }

        private ReminderPreferences LoadReminderPreferences()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PreferencesRegistryPath))
                {
                    object raw = key == null ? null : key.GetValue("RemindersV1");
                    if (raw == null) return ReminderPreferences.Merge(null, null);
                    string json = raw as string;
                    if (json == null) throw new InvalidOperationException();
                    ReminderPreferences saved = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<ReminderPreferences>(json);
                    if (saved == null || saved.Version != 1) throw new InvalidOperationException();
                    return ReminderPreferences.Merge(null, saved);
                }
            }
            catch { reminderSettingsError = "settings_read_failed"; return ReminderPreferences.Merge(null, new ReminderPreferences { Enabled = false }); }
        }

        private void SnoozeReminders(long until)
        {
            try
            {
                ReminderPreferences next = ReminderPreferences.Merge(reminderPreferences, new ReminderPreferences { SnoozeUntilUtcMs = until });
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(PreferencesRegistryPath))
                    key.SetValue("RemindersV1", new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(next));
                reminderPreferences = next; PublishWebPreferences(); ApplySnapshot();
            }
            catch { reminderSettingsError = "settings_write_failed"; ApplySnapshot(); }
        }

        private static int LoadRefreshIntervalSeconds()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PreferencesRegistryPath))
                {
                    int value;
                    if (key != null && int.TryParse(Convert.ToString(key.GetValue("RefreshIntervalSeconds"),
                        CultureInfo.InvariantCulture), out value) && (value == 30 || value == 60 || value == 300))
                        return value;
                }
            }
            catch { }
            return 60;
        }

        private static bool LoadShowRemaining()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PreferencesRegistryPath))
                {
                    int value;
                    if (key != null && int.TryParse(Convert.ToString(key.GetValue("ShowRemaining"),
                        CultureInfo.InvariantCulture), out value)) return value != 0;
                }
            }
            catch { }
            return true;
        }

        private static void SavePreferences(int intervalSeconds, bool remainingMode)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(PreferencesRegistryPath))
            {
                if (key == null) throw new InvalidOperationException("无法保存应用设置");
                key.SetValue("RefreshIntervalSeconds", intervalSeconds, RegistryValueKind.DWord);
                key.SetValue("ShowRemaining", remainingMode ? 1 : 0, RegistryValueKind.DWord);
            }
        }

        private static bool IsAutoStartEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return key != null && key.GetValue("CodexTrayStatus") != null;
        }

        private static void WriteAutoStart(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (key == null) throw new InvalidOperationException("无法打开开机启动设置");
                if (enabled) key.SetValue("CodexTrayStatus", "\"" + Application.ExecutablePath + "\" --startup");
                else key.DeleteValue("CodexTrayStatus", false);
            }
        }

        private void ToggleAutoStart(object sender, EventArgs e)
        {
            if (changingAutoStart) return;
            bool requested = autoStartItem.Checked;
            try
            {
                WriteAutoStart(requested);
                PublishWebPreferences();
            }
            catch (Exception error)
            {
                changingAutoStart = true;
                autoStartItem.Checked = !requested;
                changingAutoStart = false;
                if (notifyIcon.Visible)
                    notifyIcon.ShowBalloonTip(4000, "Codex Tray Status", error.Message, ToolTipIcon.Warning);
            }
        }

        protected override void ExitThreadCore()
        {
            if (Interlocked.Exchange(ref exiting, 1) != 0) return;
            refreshTimer.Stop();
            refreshTimer.Dispose();
            freshnessTimer.Stop();
            freshnessTimer.Dispose();
            statistics.Dispose();

            Icon finalIcon = notifyIcon.Icon;
            notifyIcon.Visible = false;
            notifyIcon.Icon = null;
            notifyIcon.Dispose();
            if (finalIcon != null) finalIcon.Dispose();
            contextMenu.Dispose();

            overlay.Stop();
            base.ExitThreadCore();
        }
    }
}
