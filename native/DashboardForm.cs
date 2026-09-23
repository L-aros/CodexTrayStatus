using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;

namespace CodexTrayStatus
{
    internal enum DashboardPage
    {
        Details = 0,
        Statistics = 1,
        Settings = 2
    }

    internal sealed class DashboardForm : Form
    {
        private readonly SegmentedHeader pageTabs;
        private readonly Panel detailsPage;
        private readonly Panel statisticsPage;
        private readonly Panel settingsPage;
        private readonly Label footerMetaLabel;
        private readonly Label totalValue;
        private readonly Label inputValue;
        private readonly Label outputValue;
        private readonly Label costValue;
        private readonly Label errorLabel;
        private readonly Label detailSubtitle;
        private readonly QuotaCard shortCard;
        private readonly QuotaCard longCard;
        private readonly UsageHistoryView miniHistory;
        private readonly SegmentedHeader refreshIntervalTabs;
        private readonly SegmentedHeader percentageModeTabs;
        private readonly ToggleSwitch autoStartToggle;
        private Rectangle lastAnchor;
        private AppSnapshot lastSnapshot;
        private DashboardPage currentPage;
        private int refreshIntervalSeconds;
        private bool showRemaining;
        private bool suppressPreferenceEvents;

        internal event EventHandler RefreshRequested;
        internal event EventHandler PreferencesChanged;
        internal event EventHandler PageChanged;
        internal event EventHandler StatisticsRequested;

        internal DashboardForm()
            : this(60, true, false)
        {
        }

        internal DashboardForm(int initialRefreshIntervalSeconds, bool initialShowRemaining, bool initialAutoStart)
        {
            SuspendLayout();
            Text = "Codex Tray Status";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(14, 17, 22);
            ForeColor = Color.FromArgb(230, 237, 243);
            ClientSize = new Size(480, 660);
            MinimumSize = new Size(420, 520);
            KeyPreview = true;

            pageTabs = new SegmentedHeader(new[] { "详情", "统计", "设置" });
            pageTabs.AccessibleName = "详情面板页面";
            pageTabs.SetBounds(25, 22, 430, 42);
            pageTabs.SelectedIndexChanged += delegate { ActivatePage((DashboardPage)pageTabs.SelectedIndex); };

            Panel pageHost = new Panel();
            // Leave room for a native vertical scrollbar without forcing a horizontal one.
            // The visual content remains aligned to the 430 px grid used by the header/footer.
            pageHost.SetBounds(25, 80, 447, 505);
            pageHost.BackColor = Color.Transparent;

            detailsPage = CreatePage();
            statisticsPage = CreatePage();
            settingsPage = CreatePage();
            pageHost.Controls.Add(detailsPage);
            pageHost.Controls.Add(statisticsPage);
            pageHost.Controls.Add(settingsPage);

            Label detailTitle = MakeLabel("用量概览", 19, FontStyle.Bold, Color.FromArgb(230, 237, 243));
            detailTitle.SetBounds(0, 0, 200, 34);
            detailSubtitle = MakeLabel("CODEX  /  USAGE OVERVIEW", 9, FontStyle.Regular, Color.FromArgb(110, 120, 133));
            detailSubtitle.SetBounds(1, 39, 426, 22);
            detailSubtitle.AutoEllipsis = true;
            detailsPage.Controls.Add(detailTitle);
            detailsPage.Controls.Add(detailSubtitle);

            shortCard = new QuotaCard(true);
            shortCard.SetBounds(0, 64, 203, 128);
            longCard = new QuotaCard(false);
            longCard.SetBounds(214, 64, 216, 128);
            detailsPage.Controls.Add(shortCard);
            detailsPage.Controls.Add(longCard);

            RoundedPanel usage = new RoundedPanel();
            usage.SetBounds(0, 207, 430, 132);
            Label usageTitle = MakeLabel("今日用量", 9.5f, FontStyle.Bold, Color.FromArgb(139, 148, 158));
            usageTitle.SetBounds(15, 13, 160, 22);
            usage.Controls.Add(usageTitle);
            totalValue = AddMetric(usage, "总 Token", 15, 43, 103, Color.FromArgb(230, 237, 243));
            inputValue = AddMetric(usage, "输入", 121, 43, 94, Color.FromArgb(121, 192, 255));
            outputValue = AddMetric(usage, "输出", 218, 43, 94, Color.FromArgb(181, 133, 255));
            costValue = AddMetric(usage, "估算花费", 315, 43, 102, Color.FromArgb(111, 224, 133));
            detailsPage.Controls.Add(usage);

            miniHistory = new UsageHistoryView(true);
            miniHistory.SetBounds(0, 352, 430, 138);
            detailsPage.Controls.Add(miniHistory);
            errorLabel = MakeLabel(string.Empty, 8.5f, FontStyle.Regular, Color.FromArgb(248, 113, 113));
            errorLabel.SetBounds(2, 495, 426, 100);
            errorLabel.Visible = false;
            detailsPage.Controls.Add(errorLabel);

            Label historyTitle = MakeLabel("每日用量", 19, FontStyle.Bold, ForeColor);
            historyTitle.SetBounds(0, 0, 240, 34);
            Label historySubtitle = MakeLabel("在浏览器中查看完整统计", 9, FontStyle.Regular, Color.FromArgb(139, 148, 158));
            historySubtitle.SetBounds(1, 39, 320, 22);
            ActionButton openStatistics = new ActionButton("打开本地统计网页 ↗");
            openStatistics.SetBounds(0, 80, 260, 44);
            openStatistics.Click += delegate { RaiseStatisticsRequested(); };
            Label historyDescription = MakeLabel("缓存 / 未缓存输入 · 模型费用明细\n日期筛选 · 美元 / 人民币切换\n\n网页随托盘程序自动更新。", 11, FontStyle.Regular, Color.FromArgb(139, 148, 158));
            historyDescription.SetBounds(0, 150, 420, 150);
            statisticsPage.Controls.Add(historyTitle);
            statisticsPage.Controls.Add(historySubtitle);
            statisticsPage.Controls.Add(openStatistics);
            statisticsPage.Controls.Add(historyDescription);

            Label settingsTitle = MakeLabel("设置", 21, FontStyle.Bold, Color.FromArgb(230, 237, 243));
            settingsTitle.SetBounds(0, 0, 200, 34);
            Label settingsSubtitle = MakeLabel("更改会立即保存并应用", 9, FontStyle.Regular, Color.FromArgb(110, 120, 133));
            settingsSubtitle.SetBounds(1, 34, 300, 22);
            settingsPage.Controls.Add(settingsTitle);
            settingsPage.Controls.Add(settingsSubtitle);

            RoundedPanel refreshSection = MakeSettingsSection("刷新", 64, 90);
            Label refreshLabel = MakeLabel("刷新间隔", 9, FontStyle.Regular, Color.FromArgb(230, 237, 243));
            refreshLabel.SetBounds(15, 41, 110, 23);
            refreshIntervalTabs = new SegmentedHeader(new[] { "30 秒", "1 分钟", "5 分钟" });
            refreshIntervalTabs.AccessibleName = "刷新间隔";
            refreshIntervalTabs.SetBounds(137, 34, 278, 36);
            refreshIntervalTabs.SelectedIndexChanged += delegate
            {
                int[] values = { 30, 60, 300 };
                refreshIntervalSeconds = values[refreshIntervalTabs.SelectedIndex];
                RaisePreferencesChanged();
            };
            refreshSection.Controls.Add(refreshLabel);
            refreshSection.Controls.Add(refreshIntervalTabs);
            settingsPage.Controls.Add(refreshSection);

            RoundedPanel displaySection = MakeSettingsSection("显示", 164, 90);
            Label percentageLabel = MakeLabel("百分比口径", 9, FontStyle.Regular, Color.FromArgb(230, 237, 243));
            percentageLabel.SetBounds(15, 41, 110, 23);
            percentageModeTabs = new SegmentedHeader(new[] { "剩余", "已使用" });
            percentageModeTabs.AccessibleName = "百分比口径";
            percentageModeTabs.SetBounds(191, 34, 224, 36);
            percentageModeTabs.SelectedIndexChanged += delegate
            {
                showRemaining = percentageModeTabs.SelectedIndex == 0;
                ApplySnapshot(lastSnapshot);
                RaisePreferencesChanged();
            };
            displaySection.Controls.Add(percentageLabel);
            displaySection.Controls.Add(percentageModeTabs);
            settingsPage.Controls.Add(displaySection);

            RoundedPanel generalSection = MakeSettingsSection("通用", 264, 76);
            Label autoStartLabel = MakeLabel("开机自启动", 9, FontStyle.Regular, Color.FromArgb(230, 237, 243));
            autoStartLabel.SetBounds(15, 39, 160, 23);
            autoStartToggle = new ToggleSwitch();
            autoStartToggle.AccessibleName = "开机自启动";
            autoStartToggle.SetBounds(365, 36, 50, 28);
            autoStartToggle.CheckedChanged += delegate { RaisePreferencesChanged(); };
            generalSection.Controls.Add(autoStartLabel);
            generalSection.Controls.Add(autoStartToggle);
            settingsPage.Controls.Add(generalSection);

            RoundedPanel aboutSection = MakeSettingsSection("关于", 350, 76);
            Label versionLabel = MakeLabel("当前版本", 9, FontStyle.Regular, Color.FromArgb(139, 148, 158));
            versionLabel.SetBounds(15, 39, 100, 22);
            string version = Assembly.GetExecutingAssembly().GetName().Version == null
                ? "--" : Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            Label versionValue = MakeLabel("v" + version + "  ·  原生 Windows 版", 9, FontStyle.Bold,
                Color.FromArgb(230, 237, 243));
            versionValue.SetBounds(137, 38, 278, 23);
            aboutSection.Controls.Add(versionLabel);
            aboutSection.Controls.Add(versionValue);
            settingsPage.Controls.Add(aboutSection);

            Panel divider = new Panel();
            divider.BackColor = Color.FromArgb(43, 49, 58);
            divider.SetBounds(25, 596, 430, 1);

            footerMetaLabel = MakeLabel("等待数据 · 尚未刷新", 8.5f, FontStyle.Regular, Color.FromArgb(110, 120, 133));
            footerMetaLabel.SetBounds(25, 612, 265, 22);
            footerMetaLabel.AutoEllipsis = true;

            ActionButton refresh = new ActionButton("↻  刷新");
            refresh.AccessibleName = "立即刷新";
            refresh.SetBounds(304, 605, 70, 34);
            refresh.Click += delegate { RaiseRefreshRequested(); };

            ActionButton collapse = new ActionButton("×  收起");
            collapse.AccessibleName = "收起详情面板";
            collapse.SetBounds(381, 605, 74, 34);
            collapse.Click += delegate { Hide(); };

            Controls.Add(pageTabs);
            Controls.Add(pageHost);
            Controls.Add(divider);
            Controls.Add(footerMetaLabel);
            Controls.Add(refresh);
            Controls.Add(collapse);
            MouseDown += DragWindow;
            KeyDown += HandleFormKeyDown;

            ApplyPreferences(initialRefreshIntervalSeconds, initialShowRemaining, initialAutoStart);
            ActivatePage(DashboardPage.Details);
            ResumeLayout(false);
        }

        internal DashboardPage CurrentPage { get { return currentPage; } }
        internal int RefreshIntervalSeconds { get { return refreshIntervalSeconds; } }
        internal bool ShowRemaining { get { return showRemaining; } }
        internal bool AutoStartEnabled { get { return autoStartToggle.Checked; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= 0x00020000;
                return parameters;
            }
        }

        internal void ApplyPreferences(int intervalSeconds, bool remainingMode, bool autoStart)
        {
            suppressPreferenceEvents = true;
            try
            {
                refreshIntervalSeconds = intervalSeconds <= 30 ? 30 : intervalSeconds >= 300 ? 300 : 60;
                showRemaining = remainingMode;
                refreshIntervalTabs.SelectedIndex = refreshIntervalSeconds == 30 ? 0 : refreshIntervalSeconds == 300 ? 2 : 1;
                percentageModeTabs.SelectedIndex = showRemaining ? 0 : 1;
                autoStartToggle.Checked = autoStart;
                ApplySnapshot(lastSnapshot);
            }
            finally
            {
                suppressPreferenceEvents = false;
            }
        }

        internal void ApplySnapshot(AppSnapshot snapshot)
        {
            lastSnapshot = snapshot;
            RateLimitWindow shortWindow = FindWindow(snapshot, "5h");
            RateLimitWindow longWindow = FindWindow(snapshot, "7d");
            if (shortWindow == null && longWindow == null && HasLimits(snapshot, 1)) shortWindow = snapshot.Quota.Limits[0];
            if (longWindow == null && HasLimits(snapshot, 2)) longWindow = snapshot.Quota.Limits[1];
            shortCard.Apply("5h", shortWindow, showRemaining);
            longCard.Apply("1周", longWindow, showRemaining);

            TodayUsage usage = snapshot == null || (snapshot.UsageState != null && snapshot.UsageState.CoverageEndDate != DateTime.Today.ToString("yyyy-MM-dd")) ? null : snapshot.Today;
            totalValue.Text = usage == null ? "--" : TaskbarOverlay.FormatTokens(usage.TotalTokens);
            inputValue.Text = usage == null ? "--" : TaskbarOverlay.FormatTokens(usage.Input);
            outputValue.Text = usage == null ? "--" : TaskbarOverlay.FormatTokens(usage.Output);
            costValue.Text = usage == null ? "--" : "$" + usage.EstimatedCost.ToString("0.00", CultureInfo.InvariantCulture);

            string refreshText = snapshot == null || snapshot.RefreshedAt <= 0 ? "尚未刷新" : FormatRefreshTime(snapshot.RefreshedAt);
            footerMetaLabel.Text = "刷新尝试 " + (snapshot != null && snapshot.RefreshState != null ? FormatRefreshTime(snapshot.RefreshState.AttemptedAt) : refreshText);
            errorLabel.Text = snapshot == null ? string.Empty :
                (snapshot.Error ?? (snapshot.Quota == null ? string.Empty : snapshot.Quota.Warning ?? string.Empty));
            if (snapshot != null)
            {
                RateLimitWindow window = snapshot.Quota != null && snapshot.Quota.Limits.Count > 0 ? snapshot.Quota.Limits[0] : null;
                errorLabel.Text = "额度：" + DataFreshness.Describe(snapshot.QuotaState, DataFreshness.EvaluateQuota(window, snapshot.QuotaState, DataFreshness.Now)) +
                    "；日志：" + DataFreshness.Describe(snapshot.UsageState, DataFreshness.EvaluateUsage(snapshot.UsageState, snapshot.Daily != null, DataFreshness.Now)) + "\n" + errorLabel.Text;
                detailSubtitle.Text = DataFreshness.Describe(snapshot.QuotaState, DataFreshness.EvaluateQuota(window, snapshot.QuotaState, DataFreshness.Now));
                errorLabel.Text += "\n额度数据：" + StateTime(snapshot.QuotaState == null ? null : snapshot.QuotaState.ObservedAt) +
                    "\n在线成功：" + StateTime(snapshot.QuotaState == null ? null : snapshot.QuotaState.LastOfficialSuccessAt) +
                    "\n日志统计：" + StateTime(snapshot.UsageState == null ? null : snapshot.UsageState.LastSuccessAt);
            }
            errorLabel.Visible = !string.IsNullOrWhiteSpace(errorLabel.Text);
            detailsPage.AutoScrollMinSize = errorLabel.Visible ? new Size(430, 600) : Size.Empty;
            UpdateStatistics();
        }

        internal void SelectPage(DashboardPage page)
        {
            if ((int)page < 0 || (int)page > 2) page = DashboardPage.Details;
            if (pageTabs.SelectedIndex != (int)page) pageTabs.SelectedIndex = (int)page;
            else ActivatePage(page);
        }

        internal void TogglePanel(AppSnapshot snapshot)
        {
            TogglePanel(snapshot, Rectangle.Empty);
        }

        internal void TogglePanel(AppSnapshot snapshot, Rectangle anchor)
        {
            if (Visible)
            {
                Hide();
                return;
            }
            ShowPanel(snapshot, anchor, DashboardPage.Details);
        }

        internal void ShowPanel(AppSnapshot snapshot, Rectangle anchor, DashboardPage page)
        {
            ApplySnapshot(snapshot);
            SelectPage(page);
            if (anchor.IsEmpty)
            {
                Point cursor = Cursor.Position;
                anchor = new Rectangle(cursor.X, cursor.Y, 1, 1);
            }
            lastAnchor = anchor;
            PositionNear(anchor);
            if (!Visible) Show();
            PositionNear(anchor);
            BringToFront();
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            UpdateWindowRegion();
            if (!lastAnchor.IsEmpty) PositionNear(lastAnchor);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (IsHandleCreated) UpdateWindowRegion();
        }

        private static Panel CreatePage()
        {
            Panel page = new Panel();
            page.Dock = DockStyle.Fill;
            page.BackColor = Color.Transparent;
            page.AutoScroll = true;
            return page;
        }

        private static RoundedPanel MakeSettingsSection(string title, int y, int height)
        {
            RoundedPanel panel = new RoundedPanel();
            panel.SetBounds(0, y, 430, height);
            Label heading = MakeLabel(title, 8.5f, FontStyle.Bold, Color.FromArgb(110, 118, 129));
            heading.SetBounds(15, 12, 180, 22);
            panel.Controls.Add(heading);
            return panel;
        }

        private void ActivatePage(DashboardPage page)
        {
            DashboardPage normalized = ((int)page < 0 || (int)page > 2) ? DashboardPage.Details : page;
            bool changed = currentPage != normalized;
            currentPage = normalized;
            detailsPage.Visible = normalized == DashboardPage.Details;
            statisticsPage.Visible = normalized == DashboardPage.Statistics;
            settingsPage.Visible = normalized == DashboardPage.Settings;
            Panel active = normalized == DashboardPage.Details ? detailsPage
                : normalized == DashboardPage.Statistics ? statisticsPage : settingsPage;
            active.AutoScrollPosition = Point.Empty;
            active.BringToFront();
            if (changed)
            {
                EventHandler handler = PageChanged;
                if (handler != null) handler(this, EventArgs.Empty);
                if (normalized == DashboardPage.Statistics) RaiseStatisticsRequested();
            }
        }

        private void UpdateStatistics()
        {
            miniHistory.Apply(lastSnapshot == null ? null : lastSnapshot.Daily, 7);
        }

        private static string StateTime(long? value)
        {
            if (!value.HasValue) return "未知";
            try { return DateTimeOffset.FromUnixTimeMilliseconds(value.Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); }
            catch (ArgumentOutOfRangeException) { return "未知"; }
        }

        private void RaiseStatisticsRequested()
        {
            EventHandler handler = StatisticsRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseRefreshRequested()
        {
            EventHandler handler = RefreshRequested;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaisePreferencesChanged()
        {
            if (suppressPreferenceEvents) return;
            EventHandler handler = PreferencesChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void HandleFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Hide();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D3)
            {
                SelectPage((DashboardPage)((int)e.KeyCode - (int)Keys.D1));
                e.Handled = true;
            }
        }

        private void UpdateWindowRegion()
        {
            if (ClientSize.Width < 4 || ClientSize.Height < 4) return;
            int radius = Math.Max(14, (int)Math.Round(22f * DeviceDpi / 96f));
            using (GraphicsPath path = RoundedPath(ClientRectangle, radius))
            {
                Region previous = Region;
                Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
        }

        private void PositionNear(Rectangle anchor)
        {
            Screen screen = Screen.FromRectangle(anchor);
            Rectangle work = screen.WorkingArea;
            int margin = Math.Max(8, (int)Math.Round(8f * DeviceDpi / 96f));
            int x = anchor.Right - Width;
            int above = anchor.Top - work.Top;
            int below = work.Bottom - anchor.Bottom;
            int y = above >= Height + margin || above >= below ? anchor.Top - Height - margin : anchor.Bottom + margin;
            x = Clamp(x, work.Left + margin, work.Right - Width - margin);
            y = Clamp(y, work.Top + margin, work.Bottom - Height - margin);
            Location = new Point(x, y);
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
        }

        private static Label AddMetric(Control parent, string caption, int x, int y, int width, Color valueColor)
        {
            Label value = MakeLabel("--", 14.5f, FontStyle.Bold, valueColor);
            value.SetBounds(x, y, width, 28);
            value.AutoEllipsis = true;
            Label label = MakeLabel(caption, 8.5f, FontStyle.Regular, Color.FromArgb(110, 118, 129));
            label.Name = "metric-caption-" + x.ToString(CultureInfo.InvariantCulture);
            label.SetBounds(x, y + 33, width, 20);
            parent.Controls.Add(value);
            parent.Controls.Add(label);
            return value;
        }

        private static Label FindMetricCaption(Control parent, int x)
        {
            return parent.Controls["metric-caption-" + x.ToString(CultureInfo.InvariantCulture)] as Label;
        }

        private static Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            return new Label
            {
                Text = text,
                Font = Typography.Ui(size, style, GraphicsUnit.Point),
                ForeColor = color,
                BackColor = Color.Transparent,
                UseMnemonic = false
            };
        }

        private static bool HasLimits(AppSnapshot snapshot, int count)
        {
            return snapshot != null && snapshot.Quota != null && snapshot.Quota.Limits != null && snapshot.Quota.Limits.Count >= count;
        }

        internal static RateLimitWindow FindWindow(AppSnapshot snapshot, string label)
        {
            if (!HasLimits(snapshot, 1)) return null;
            foreach (RateLimitWindow item in snapshot.Quota.Limits)
                if (item != null && string.Equals(item.Label, label, StringComparison.OrdinalIgnoreCase)) return item;
            return null;
        }

        private static string FormatRefreshTime(long milliseconds)
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).LocalDateTime.ToString("M/d HH:mm:ss"); }
            catch { return "刚刚"; }
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (maximum < minimum) return minimum;
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        internal static GraphicsPath RoundedPath(Rectangle rectangle, int radius)
        {
            int maximumRadius = Math.Max(1, Math.Min(rectangle.Width, rectangle.Height) / 2);
            radius = Math.Max(1, Math.Min(radius, maximumRadius));
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter - 1, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter - 1, rectangle.Bottom - diameter - 1, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter - 1, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class SegmentedHeader : Control
    {
        private readonly string[] labels;
        private int selectedIndex;
        private int hoverIndex = -1;
        private int pressedIndex = -1;

        internal event EventHandler SelectedIndexChanged;

        internal SegmentedHeader(string[] values)
        {
            if (values == null || values.Length == 0) throw new ArgumentException("At least one label is required.", "values");
            labels = (string[])values.Clone();
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PageTabList;
        }

        internal int SelectedIndex
        {
            get { return selectedIndex; }
            set
            {
                int normalized = Math.Max(0, Math.Min(labels.Length - 1, value));
                if (selectedIndex == normalized) return;
                selectedIndex = normalized;
                Invalidate();
                EventHandler handler = SelectedIndexChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up)
                SelectedIndex = (selectedIndex + labels.Length - 1) % labels.Length;
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Down)
                SelectedIndex = (selectedIndex + 1) % labels.Length;
            else if (e.KeyCode == Keys.Home)
                SelectedIndex = 0;
            else if (e.KeyCode == Keys.End)
                SelectedIndex = labels.Length - 1;
            else
                return;
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int next = HitTest(e.Location);
            if (hoverIndex == next) return;
            hoverIndex = next;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoverIndex = -1;
            pressedIndex = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Focus();
                pressedIndex = HitTest(e.Location);
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                int released = HitTest(e.Location);
                if (released >= 0 && released == pressedIndex) SelectedIndex = released;
                pressedIndex = -1;
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = DeviceDpi / 96f;
            Rectangle outer = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath outerPath = DashboardForm.RoundedPath(outer, Math.Max(8, (int)(13 * scale))))
            using (Brush outerFill = new SolidBrush(Color.FromArgb(10, 13, 18)))
            using (Pen outerBorder = new Pen(Focused ? Color.FromArgb(80, 130, 166) : Color.FromArgb(43, 49, 58)))
            {
                e.Graphics.FillPath(outerFill, outerPath);
                e.Graphics.DrawPath(outerBorder, outerPath);
            }

            int inset = Math.Max(3, (int)(4 * scale));
            if (hoverIndex >= 0 && hoverIndex != selectedIndex)
            {
                Rectangle hoverBounds = SegmentBounds(hoverIndex, inset);
                using (GraphicsPath hoverPath = DashboardForm.RoundedPath(hoverBounds, Math.Max(6, (int)(9 * scale))))
                using (Brush hoverFill = new SolidBrush(Color.FromArgb(24, 29, 36))) e.Graphics.FillPath(hoverFill, hoverPath);
            }

            Rectangle selected = SegmentBounds(selectedIndex, inset);
            Color selectedFill = pressedIndex == selectedIndex ? Color.FromArgb(37, 61, 82) : Color.FromArgb(28, 46, 62);
            using (GraphicsPath selectedPath = DashboardForm.RoundedPath(selected, Math.Max(6, (int)(9 * scale))))
            using (Brush fill = new SolidBrush(selectedFill))
            using (Pen border = new Pen(Color.FromArgb(72, 112, 143)))
            {
                e.Graphics.FillPath(fill, selectedPath);
                e.Graphics.DrawPath(border, selectedPath);
            }

            float fontSize = Height <= 36 ? 8.3f : 9f;
            using (Font font = Typography.Ui(fontSize, FontStyle.Regular, GraphicsUnit.Point))
            using (Brush active = new SolidBrush(Color.FromArgb(136, 205, 255)))
            using (Brush inactive = new SolidBrush(Color.FromArgb(139, 148, 158)))
            using (Brush hover = new SolidBrush(Color.FromArgb(205, 214, 223)))
            {
                for (int index = 0; index < labels.Length; index++)
                {
                    Rectangle bounds = SegmentBounds(index, 0);
                    DrawCentered(e.Graphics, labels[index], font, index == selectedIndex ? active : index == hoverIndex ? hover : inactive, bounds);
                }
            }
        }

        private int HitTest(Point location)
        {
            if (!ClientRectangle.Contains(location) || Width <= 0) return -1;
            int index = (int)((long)location.X * labels.Length / Math.Max(1, Width));
            return Math.Max(0, Math.Min(labels.Length - 1, index));
        }

        private Rectangle SegmentBounds(int index, int inset)
        {
            int inner = Math.Max(labels.Length, Width - inset * 2);
            int left = inset + inner * index / labels.Length;
            int right = inset + inner * (index + 1) / labels.Length;
            return new Rectangle(left, inset, Math.Max(1, right - left), Math.Max(1, Height - inset * 2));
        }

        private static void DrawCentered(Graphics graphics, string text, Font font, Brush brush, Rectangle bounds)
        {
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                graphics.DrawString(text, font, brush, bounds, format);
            }
        }
    }

    internal sealed class RoundedPanel : Panel
    {
        internal RoundedPanel()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true;
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = DashboardForm.RoundedPath(box, Math.Max(8, (int)(13 * DeviceDpi / 96f))))
            using (Brush fill = new SolidBrush(Color.FromArgb(28, 33, 40)))
            using (Pen border = new Pen(Color.FromArgb(48, 56, 66)))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
        }
    }

    internal sealed class ActionButton : Control
    {
        private bool hovered;
        private bool pressed;

        internal ActionButton(string text)
        {
            Text = text;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint, true);
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { pressed = true; Focus(); Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Color fillColor = !Enabled ? Color.FromArgb(21, 25, 31)
                : pressed ? Color.FromArgb(39, 48, 59) : hovered ? Color.FromArgb(35, 42, 51) : Color.FromArgb(27, 32, 39);
            using (GraphicsPath path = DashboardForm.RoundedPath(box, Math.Max(4, Height / 2)))
            using (Brush fill = new SolidBrush(fillColor))
            using (Pen border = new Pen(Focused ? Color.FromArgb(90, 146, 183) : Color.FromArgb(53, 61, 71)))
            using (Font font = Typography.Ui(8.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (Brush text = new SolidBrush(Enabled ? Color.FromArgb(169, 180, 193) : Color.FromArgb(85, 93, 104)))
            using (StringFormat format = new StringFormat())
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                e.Graphics.DrawString(Text, font, text, box, format);
            }
        }
    }

    internal sealed class ToggleSwitch : Control
    {
        private bool isChecked;
        internal event EventHandler CheckedChanged;

        internal ToggleSwitch()
        {
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor |
                ControlStyles.UserPaint, true);
            BackColor = Color.Transparent;
        }

        internal bool Checked
        {
            get { return isChecked; }
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                Invalidate();
                EventHandler handler = CheckedChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle track = new Rectangle(0, 1, Math.Max(1, Width - 1), Math.Max(1, Height - 3));
            using (GraphicsPath path = DashboardForm.RoundedPath(track, Math.Max(3, track.Height / 2)))
            using (Brush fill = new SolidBrush(Checked ? Color.FromArgb(52, 112, 82) : Color.FromArgb(47, 54, 63)))
            using (Pen border = new Pen(Focused ? Color.FromArgb(121, 192, 255) : Color.FromArgb(66, 75, 86)))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
            int diameter = Math.Max(8, Height - 8);
            int x = Checked ? Width - diameter - 4 : 4;
            using (Brush thumb = new SolidBrush(Checked ? Color.FromArgb(230, 237, 243) : Color.FromArgb(159, 169, 181)))
                e.Graphics.FillEllipse(thumb, x, (Height - diameter) / 2, diameter, diameter);
        }
    }

    internal sealed class QuotaCard : Control
    {
        private readonly bool accentCard;
        private string title = "额度窗口";
        private RateLimitWindow window;
        private bool showRemaining = true;

        internal QuotaCard(bool useAccent)
        {
            accentCard = useAccent;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true;
            BackColor = Color.Transparent;
        }

        internal void Apply(string valueTitle, RateLimitWindow value, bool remainingMode)
        {
            title = valueTitle;
            window = value;
            showRemaining = remainingMode;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = DeviceDpi / 96f;
            Rectangle box = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = DashboardForm.RoundedPath(box, Math.Max(8, (int)(14 * scale))))
            using (Brush fill = new SolidBrush(accentCard ? Color.FromArgb(27, 35, 43) : Color.FromArgb(28, 33, 40)))
            using (Pen border = new Pen(accentCard ? Color.FromArgb(51, 76, 94) : Color.FromArgb(48, 56, 66)))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }

            using (Font titleFont = Typography.Display(9, FontStyle.Bold, GraphicsUnit.Point))
            using (Font percentFont = Typography.Display(21, FontStyle.Bold, GraphicsUnit.Point))
            using (Font detailFont = Typography.Ui(8, FontStyle.Regular, GraphicsUnit.Point))
            using (Brush titleBrush = new SolidBrush(Color.FromArgb(219, 225, 232)))
            using (Brush primaryBrush = new SolidBrush(Accent()))
            using (Brush detailBrush = new SolidBrush(Color.FromArgb(126, 136, 149)))
            using (StringFormat right = new StringFormat())
            {
                float x = 14 * scale;
                e.Graphics.DrawString(title, titleFont, titleBrush, x, 10 * scale);
                right.Alignment = StringAlignment.Far;
                e.Graphics.DrawString(showRemaining ? "剩余" : "已用", detailFont, detailBrush,
                    new RectangleF(0, 11 * scale, Width - 14 * scale, 18 * scale), right);
                double? metric = window == null ? null : showRemaining ? window.RemainingPercent : window.UsedPercent;
                string percent = !metric.HasValue ? "--" : Math.Round(metric.Value).ToString("0", CultureInfo.InvariantCulture) + "%";
                e.Graphics.DrawString(percent, percentFont, primaryBrush, x - 1, 31 * scale);

                Rectangle track = new Rectangle((int)(14 * scale), (int)(72 * scale), Width - (int)(28 * scale), Math.Max(4, (int)(4 * scale)));
                using (GraphicsPath trackPath = DashboardForm.RoundedPath(track, Math.Max(2, track.Height / 2)))
                using (Brush trackBrush = new SolidBrush(Color.FromArgb(55, 62, 72))) e.Graphics.FillPath(trackBrush, trackPath);
                if (metric.HasValue)
                {
                    int progressWidth = Math.Max(track.Height, (int)(track.Width * Math.Max(0, Math.Min(100, metric.Value)) / 100d));
                    progressWidth = Math.Min(track.Width, progressWidth);
                    Rectangle progress = new Rectangle(track.X, track.Y, progressWidth, track.Height);
                    using (GraphicsPath progressPath = DashboardForm.RoundedPath(progress, Math.Max(2, progress.Height / 2)))
                    using (Brush progressBrush = new SolidBrush(Accent())) e.Graphics.FillPath(progressBrush, progressPath);
                }
                e.Graphics.DrawString(FormatResetText(), detailFont, detailBrush, x, 82 * scale);
                e.Graphics.DrawString(FormatDeadline(), detailFont, detailBrush, x, 103 * scale);
            }
        }

        private Color Accent()
        {
            double remaining = window != null && window.RemainingPercent.HasValue ? window.RemainingPercent.Value : 100;
            if (remaining <= 15) return Color.FromArgb(248, 113, 113);
            if (remaining <= 40) return Color.FromArgb(236, 192, 90);
            return accentCard ? Color.FromArgb(111, 224, 133) : Color.FromArgb(121, 192, 255);
        }

        private string FormatResetText()
        {
            DateTimeOffset reset;
            if (!TryGetReset(out reset)) return "重置时间未知";
            TimeSpan left = reset - DateTimeOffset.Now;
            if (left.TotalMinutes <= 0) return "即将重置";
            if (left.TotalDays >= 1) return ((int)left.TotalDays) + "天" + left.Hours + "小时后重置";
            if (left.TotalHours >= 1) return ((int)left.TotalHours) + "小时" + left.Minutes + "分钟后重置";
            return Math.Max(1, left.Minutes) + "分钟后重置";
        }

        private string FormatDeadline()
        {
            DateTimeOffset reset;
            if (!TryGetReset(out reset)) return "到期时间: --";
            DateTime local = reset.LocalDateTime;
            return "到期时间: " + (local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("M/d HH:mm"));
        }

        private bool TryGetReset(out DateTimeOffset reset)
        {
            reset = default(DateTimeOffset);
            if (window == null || !window.ResetsAt.HasValue) return false;
            try { reset = DateTimeOffset.FromUnixTimeMilliseconds(window.ResetsAt.Value); return true; }
            catch { return false; }
        }
    }

}
