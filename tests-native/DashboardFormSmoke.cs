using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace CodexTrayStatus
{
    internal static class DashboardFormSmoke
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception error) { Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message); return 1; }
        }

        private static int Run(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string outputDirectory = args.Length == 0
                ? Path.Combine(Environment.CurrentDirectory, "artifacts", "tests", "dashboard")
                : Path.GetFullPath(args[0]);
            Directory.CreateDirectory(outputDirectory);

            TestSegmentedHeaderInput();
            TestLastGoodSnapshotMerge();
            using (DashboardForm form = new DashboardForm(60, true, false))
            {
                int pageChanges = 0;
                int preferenceChanges = 0;
                int statisticsRequests = 0;
                form.StatisticsRequested += delegate { statisticsRequests++; };
                form.PageChanged += delegate { pageChanges++; };
                form.PreferencesChanged += delegate { preferenceChanges++; };
                form.ApplySnapshot(CreateSnapshot());

                Assert(form.CurrentPage == DashboardPage.Details, "The panel must start on Details.");
                form.SelectPage(DashboardPage.Statistics);
                Assert(form.CurrentPage == DashboardPage.Statistics, "Team page selection failed.");
                form.SelectPage(DashboardPage.Settings);
                Assert(form.CurrentPage == DashboardPage.Settings, "Settings page selection failed.");
                form.SelectPage(DashboardPage.Details);
                Assert(form.CurrentPage == DashboardPage.Details, "Details page selection failed.");
                Assert(pageChanges == 3, "Each real page transition must raise PageChanged exactly once.");

                SegmentedHeader refreshTabs = GetPrivateField<SegmentedHeader>(form, "refreshIntervalTabs");
                refreshTabs.SelectedIndex = 0;
                Assert(form.RefreshIntervalSeconds == 30, "Refresh interval control did not update the setting.");
                SegmentedHeader percentageTabs = GetPrivateField<SegmentedHeader>(form, "percentageModeTabs");
                percentageTabs.SelectedIndex = 1;
                Assert(!form.ShowRemaining, "Percentage mode control did not switch to used percentage.");
                ToggleSwitch autoStart = GetPrivateField<ToggleSwitch>(form, "autoStartToggle");
                autoStart.Checked = true;
                Assert(form.AutoStartEnabled, "Auto-start control did not update its state.");
                Assert(preferenceChanges == 3, "Each settings control must publish one preference change.");

                // Render a neutral default state after exercising the mutable settings controls.
                form.ApplyPreferences(60, true, false);

                form.Location = new Point(-2400, -1800);
                form.Show();
                Application.DoEvents();
                RenderPage(form, DashboardPage.Details, Path.Combine(outputDirectory, "details.png"));
                RenderPage(form, DashboardPage.Statistics, Path.Combine(outputDirectory, "statistics.png"));
                Assert(statisticsRequests == 2, "Selecting statistics must open the local webpage.");
                RenderPage(form, DashboardPage.Settings, Path.Combine(outputDirectory, "settings.png"));
                form.Hide();
            }

            Console.WriteLine("Dashboard navigation smoke tests passed.");
            Console.WriteLine("Rendered pages: " + outputDirectory);
            return 0;
        }

        private static void TestSegmentedHeaderInput()
        {
            using (SegmentedHeader tabs = new SegmentedHeader(new[] { "详情", "统计", "设置" }))
            {
                tabs.Size = new Size(300, 40);
                InvokeProtected(tabs, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 250, 20, 0));
                InvokeProtected(tabs, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 250, 20, 0));
                Assert(tabs.SelectedIndex == 2, "Mouse hit testing did not select the third tab.");
                InvokeProtected(tabs, "OnKeyDown", new KeyEventArgs(Keys.Right));
                Assert(tabs.SelectedIndex == 0, "Right arrow did not wrap to the first tab.");
                InvokeProtected(tabs, "OnKeyDown", new KeyEventArgs(Keys.Left));
                Assert(tabs.SelectedIndex == 2, "Left arrow did not wrap to the last tab.");
                InvokeProtected(tabs, "OnKeyDown", new KeyEventArgs(Keys.Home));
                Assert(tabs.SelectedIndex == 0, "Home did not select the first tab.");
                InvokeProtected(tabs, "OnKeyDown", new KeyEventArgs(Keys.End));
                Assert(tabs.SelectedIndex == 2, "End did not select the last tab.");
            }
        }

        private static void TestLastGoodSnapshotMerge()
        {
            AppSnapshot previous = CreateSnapshot();
            AppSnapshot failed = new AppSnapshot
            {
                Error = "temporary failure",
                RefreshedAt = previous.RefreshedAt + 1000
            };
            MethodInfo merge = typeof(TrayApplicationContext).GetMethod(
                "MergeWithLastGood", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(merge != null, "Last-good snapshot merge helper is missing.");
            AppSnapshot result = (AppSnapshot)merge.Invoke(null, new object[] { previous, failed });
            Assert(object.ReferenceEquals(result.Quota, previous.Quota),
                "A temporary quota failure must preserve the last good quota.");
            Assert(object.ReferenceEquals(result.Today, previous.Today),
                "A temporary usage failure must preserve the last good daily totals.");
            Assert(object.ReferenceEquals(result.Daily, previous.Daily),
                "A temporary usage failure must preserve history.");
            Assert(result.Error == "temporary failure",
                "Preserving metrics must not suppress the current refresh error.");
        }

        private static void RenderPage(DashboardForm form, DashboardPage page, string path)
        {
            form.SelectPage(page);
            Application.DoEvents();
            using (Bitmap bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        private static AppSnapshot CreateSnapshot()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return new AppSnapshot
            {
                RefreshedAt = now,
                Quota = new QuotaResult
                {
                    Source = "official",
                    RefreshedAt = now,
                    Limits = new List<RateLimitWindow>
                    {
                        new RateLimitWindow
                        {
                            Id = "primary",
                            Label = "5h",
                            UsedPercent = 28,
                            RemainingPercent = 72,
                            ResetsAt = now + 2 * 60 * 60 * 1000
                        },
                        new RateLimitWindow
                        {
                            Id = "secondary",
                            Label = "7d",
                            UsedPercent = 43,
                            RemainingPercent = 57,
                            ResetsAt = now + 4 * 24 * 60 * 60 * 1000
                        }
                    }
                },
                Daily = CreateDaily(),
                Today = new TodayUsage
                {
                    Input = 1250000,
                    Output = 184000,
                    TotalTokens = 1434000,
                    EstimatedCost = 2.73m
                }
            };
        }

        private static List<DailyUsage> CreateDaily()
        {
            List<DailyUsage> days = new List<DailyUsage>();
            for (int i = 0; i < 30; i++) days.Add(new DailyUsage { Date = DateTime.Today.AddDays(i - 29), Input = 800000 + (i % 5) * 120000, Output = 184000, TotalTokens = 984000 + (i % 5) * 120000, EstimatedCost = 1.25m + (i % 5) * 0.37m });
            return days;
        }

        private static T GetPrivateField<T>(object instance, string name) where T : class
        {
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            T value = field == null ? null : field.GetValue(instance) as T;
            if (value == null) throw new InvalidOperationException("Missing field: " + name);
            return value;
        }

        private static void InvokeProtected(object instance, string name, EventArgs eventArgs)
        {
            MethodInfo method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Missing method: " + name);
            method.Invoke(instance, new object[] { eventArgs });
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
