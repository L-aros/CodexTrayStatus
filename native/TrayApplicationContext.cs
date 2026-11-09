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
        private readonly ModelRadarPricingService modelRadarPricing = new ModelRadarPricingService();
        private readonly ExchangeRateService exchangeRates = new ExchangeRateService();
        private readonly ReminderCoordinator reminders;
        private readonly QuotaHistoryStore quotaHistory;
        private ReminderPreferences reminderPreferences;
        private string reminderSettingsError;
        private readonly TaskbarSettingsStore taskbarSettings;
        private AppSnapshot snapshot;
        private int refreshIntervalSeconds;
        private bool showRemaining;
        private bool changingAutoStart;
        private bool autoExchangeRate = true;
        private decimal manualExchangeRate = 7m;
        private bool refreshRatesRequested;
        private int refreshing;
        private int exiting;
        private string statusIconKey;
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
                        manualExchangeRate = rate;
                    autoExchangeRate = Convert.ToString(key.GetValue("AutoExchangeRate", "1"), CultureInfo.InvariantCulture) != "0";
                }
            }
            ApplyEffectiveExchangeRate();
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
            refreshItem.Click += async delegate { await RefreshAsync(false, true); };
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
            notifyIcon.Icon = TrayNumberIcon.Create(null, SystemInformation.SmallIconSize.Width, SystemInformation.HighContrast);
            statusIconKey = TrayNumberIcon.Key(null, SystemInformation.SmallIconSize.Width, SystemInformation.HighContrast);
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
            statistics.PricingStateRequested = delegate { return modelRadarPricing.Status; };
            statistics.ExchangeRateStateRequested = delegate { return exchangeRates.Status; };
            statistics.RatesRefreshRequested = delegate { overlay.BeginInvoke(new Action(async delegate { await RefreshAsync(true, true); })); };
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

        private async Task RefreshAsync(bool silent, bool forceRates = false)
        {
            if (Interlocked.CompareExchange(ref exiting, 0, 0) != 0) return;
            if (forceRates) refreshRatesRequested = true;
            if (Interlocked.CompareExchange(ref refreshing, 1, 0) != 0) return;
            bool refreshMarketRates = refreshRatesRequested;
            refreshRatesRequested = false;
            RefreshState attempt = new RefreshState { AttemptId = Guid.NewGuid().ToString("N"), AttemptedAt = DataFreshness.Now, IsRefreshing = true };
            if (snapshot == null) snapshot = new AppSnapshot();
            snapshot.RefreshState = attempt;
            ApplySnapshotOnUiThread();
            try
            {
                Task<bool> pricingTask = modelRadarPricing.RefreshAsync(refreshMarketRates);
                Task<bool> exchangeTask = exchangeRates.RefreshAsync(refreshMarketRates);
                Task<AppSnapshot> quotaTask = quotaService.RefreshAsync();
                bool pricesChanged = await pricingTask;
                await exchangeTask;
                ApplyEffectiveExchangeRate();
                if (pricesChanged) quotaService.InvalidateUsageCache();
                AppSnapshot value = await quotaTask;
                if (pricesChanged)
                {
                    // Cached session summaries include costs. Refresh them even when
                    // the logs themselves did not change after a new price snapshot.
                    if (value.Daily != null)
                    {
                        value.Daily = await quotaService.FetchDailyUsageAsync();
                        value.Today = value.Daily.Find(delegate(DailyUsage day) { return day.Date == DateTime.Today; }) ?? new TodayUsage();
                    }
                }
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
                if (Interlocked.CompareExchange(ref exiting, 0, 0) == 0)
                {
                    ApplyEffectiveExchangeRate();
                    ApplySnapshotOnUiThread();
                    if (refreshRatesRequested)
                        overlay.BeginInvoke(new Action(async delegate { await RefreshAsync(true); }));
                }
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

            TaskbarPresentation view = TaskbarPresenter.Build(snapshot, CurrentWebPreferences(), DataFreshness.Now);
            string nextQuotaText = view.Prefix + view.Percent + " · " + view.Detail;
            if (!string.Equals(quotaLine.Text, nextQuotaText, StringComparison.Ordinal)) quotaLine.Text = nextQuotaText;

            string nextUsageText = snapshot == null || snapshot.Today == null
                ? "今日：暂无数据"
                : "今日 " + TaskbarOverlay.FormatTokens(snapshot.Today.TotalTokens) + " · " + CurrencyDisplay.Format(snapshot.Today.EstimatedCost);
            if (!string.Equals(usageLine.Text, nextUsageText, StringComparison.Ordinal))
                usageLine.Text = nextUsageText;

            int iconSize = SystemInformation.SmallIconSize.Width;
            bool highContrast = SystemInformation.HighContrast;
            string nextIconKey = TrayNumberIcon.Key(view, iconSize, highContrast);
            if (!statusIconKeyReady || nextIconKey != statusIconKey)
            {
                Icon oldIcon = notifyIcon.Icon;
                notifyIcon.Icon = TrayNumberIcon.Create(view, iconSize, highContrast);
                statusIconKey = nextIconKey;
                statusIconKeyReady = true;
                if (oldIcon != null) oldIcon.Dispose();
            }

            string nextTooltip = BuildTooltip();
            if (!string.Equals(notifyIcon.Text, nextTooltip, StringComparison.Ordinal))
                notifyIcon.Text = nextTooltip;
        }

        private string BuildTooltip()
        {
            TaskbarPresentation view = TaskbarPresenter.Build(snapshot, CurrentWebPreferences(), DataFreshness.Now);
            string text = view.Prefix + view.Percent + "\n" + view.Detail;
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

        private string ApplyWebPreferences(WebPreferences preferences)
        {
            bool saveTaskbar = preferences.TaskbarDisplay != null;
            bool saveReminders = preferences.Reminders != null;
            try { preferences = WebPreferencePatch.Merge(CurrentWebPreferences(), preferences); }
            catch (Exception error) { return error.Message; }
            WebPreferences previous = CurrentWebPreferences();
            decimal previousManualExchangeRate = manualExchangeRate;
            decimal nextManualExchangeRate = preferences.AutoExchangeRate ? manualExchangeRate : preferences.ExchangeRate;
            bool intervalChanged = previous.RefreshInterval != preferences.RefreshInterval || previous.ShowRemaining != preferences.ShowRemaining;
            bool currencyChanged = previous.Currency != preferences.Currency || previous.AutoExchangeRate != preferences.AutoExchangeRate || previousManualExchangeRate != nextManualExchangeRate;
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
                            key.SetValue("ExchangeRate", nextManualExchangeRate.ToString(CultureInfo.InvariantCulture));
                            key.SetValue("AutoExchangeRate", preferences.AutoExchangeRate ? 1 : 0);
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
                                key.SetValue("ExchangeRate", previousManualExchangeRate.ToString(CultureInfo.InvariantCulture));
                                key.SetValue("AutoExchangeRate", previous.AutoExchangeRate ? 1 : 0);
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
            autoExchangeRate = preferences.AutoExchangeRate;
            manualExchangeRate = nextManualExchangeRate;
            ApplyEffectiveExchangeRate();
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
                ExchangeRate = CurrencyDisplay.ExchangeRate, AutoExchangeRate = autoExchangeRate, Reminders = reminderPreferences,
                TaskbarDisplay = taskbarSettings.Current, TaskbarSettingsError = taskbarSettings.Error };
        }

        private void PublishWebPreferences() { statistics.PublishPreferences(CurrentWebPreferences()); }

        private void ApplyEffectiveExchangeRate()
        {
            ExchangeRateState state = exchangeRates.Status;
            decimal rate = autoExchangeRate && state.Available ? state.Rate : manualExchangeRate;
            if (CurrencyDisplay.ExchangeRate == rate) return;
            CurrencyDisplay.ExchangeRate = rate;
            // Cached rates are applied before the overlay/settings store is created.
            if (taskbarSettings == null || overlay == null) return;
            PublishWebPreferences();
            overlay.ApplyPreferences(CurrentWebPreferences());
        }

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
                overlay.BeginInvoke(new Action(async delegate { await RefreshAsync(true); })); // Re-scan prices; token quantities remain derived from logs.
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
