using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace CodexTrayStatus
{
    internal sealed class UsageHistoryView : Control
    {
        private readonly bool compact;
        private List<DailyUsage> values;
        private int range = 7;
        private readonly Color muted = Color.FromArgb(139, 151, 170);
        private readonly Color accent = Color.FromArgb(119, 211, 190);

        internal UsageHistoryView(bool compactMode)
        {
            compact = compactMode;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(14, 17, 22);
            AccessibleName = compact ? "近七天 Token 趋势" : "每日 Token 和估算费用明细";
        }

        internal void Apply(List<DailyUsage> days, int count)
        {
            range = count;
            values = days == null ? null : days.FindAll(delegate(DailyUsage day) {
                return day.Date >= DateTime.Today.AddDays(1 - count) && day.Date <= DateTime.Today;
            });
            if (values != null) values.Sort(delegate(DailyUsage a, DailyUsage b) { return a.Date.CompareTo(b.Date); });
            Invalidate();
        }

        private void TextAt(Graphics g, string text, float size, Color color, float x, float y, bool bold)
        {
            using (Font font = Typography.Ui(size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (Brush brush = new SolidBrush(color)) g.DrawString(text, font, brush, x, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.ScaleTransform(DeviceDpi / 96f, DeviceDpi / 96f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int chartTop = compact ? 38 : 105;
            int chartHeight = compact ? 68 : 100;
            using (GraphicsPath path = DashboardForm.RoundedPath(new Rectangle(0, 0, 429, compact ? 137 : 243), 14))
            using (Brush fill = new SolidBrush(Color.FromArgb(23, 29, 38))) g.FillPath(fill, path);
            if (values == null)
            {
                TextAt(g, "每日用量", 13, muted, 16, 15, true);
                TextAt(g, "等待统计数据…", 14, muted, 16, 65, false);
                return;
            }
            long total = 0;
            decimal cost = 0;
            double peak = 1;
            foreach (DailyUsage day in values) { total += day.TotalTokens; cost += day.EstimatedCost; peak = Math.Max(peak, day.TotalTokens); }
            if (compact)
            {
                TextAt(g, "近 7 天  /  TOKEN 趋势", 11, muted, 16, 12, true);
                TextAt(g, TaskbarOverlay.FormatTokens(total), 14, accent, 325, 10, true);
            }
            else
            {
                TextAt(g, "累计 Token", 12, muted, 16, 14, false);
                TextAt(g, "估算费用 · USD", 12, muted, 240, 14, false);
                TextAt(g, TaskbarOverlay.FormatTokens(total), 29, Color.WhiteSmoke, 14, 36, true);
                TextAt(g, "$" + cost.ToString("0.00", CultureInfo.InvariantCulture), 29, accent, 238, 36, true);
                TextAt(g, "每日 Token", 11, muted, 16, 82, false);
                TextAt(g, "峰值 " + TaskbarOverlay.FormatTokens((long)(peak == 1 ? 0 : peak)), 11, muted, 282, 82, false);
            }
            using (Pen grid = new Pen(Color.FromArgb(43, 53, 67)))
                for (int i = 0; i < 3; i++) g.DrawLine(grid, 16, chartTop + i * chartHeight / 2, 414, chartTop + i * chartHeight / 2);
            float step = 398f / range;
            for (int i = 0; i < range; i++)
            {
                DateTime date = DateTime.Today.AddDays(i + 1 - range);
                DailyUsage day = values.Find(delegate(DailyUsage item) { return item.Date == date; });
                float height = day == null ? 0 : (float)(day.TotalTokens / peak * (chartHeight - 5));
                if (height > 0)
                {
                    RectangleF bar = new RectangleF(16 + i * step + 3, chartTop + chartHeight - height, step - 6, height);
                    using (Brush brush = new LinearGradientBrush(bar, i == range - 1 ? accent : Color.FromArgb(98, 145, 230), Color.FromArgb(50, 76, 113), 90f)) g.FillRectangle(brush, bar);
                }
            }
            TextAt(g, DateTime.Today.AddDays(1 - range).ToString("MM/dd"), 10, muted, 16, chartTop + chartHeight + 7, false);
            TextAt(g, "今天", 10, accent, 387, chartTop + chartHeight + 7, false);
            if (compact) return;
            TextAt(g, "日期", 11, muted, 12, 260, true);
            TextAt(g, "Token / 输入 · 输出", 11, muted, 134, 260, true);
            TextAt(g, "估算费用", 11, muted, 341, 260, true);
            for (int i = 0; i < range; i++)
            {
                DateTime date = DateTime.Today.AddDays(-i);
                DailyUsage day = values.Find(delegate(DailyUsage item) { return item.Date == date; });
                int y = 285 + i * 42;
                if (i % 2 == 0) using (Brush row = new SolidBrush(Color.FromArgb(23, 29, 38))) g.FillRectangle(row, 0, y - 3, 430, 42);
                TextAt(g, date.ToString("MM/dd") + (i == 0 ? "  今天" : "  " + date.ToString("ddd")), 12, Color.WhiteSmoke, 12, y + 6, false);
                TextAt(g, day == null ? "0" : day.TotalTokens.ToString("N0", CultureInfo.InvariantCulture), 13, Color.WhiteSmoke, 134, y, true);
                TextAt(g, day == null ? "0 / 0" : day.Input.ToString("N0", CultureInfo.InvariantCulture) + " / " + day.Output.ToString("N0", CultureInfo.InvariantCulture), 10, muted, 134, y + 19, false);
                TextAt(g, "$" + (day == null ? 0m : day.EstimatedCost).ToString("0.0000", CultureInfo.InvariantCulture), 12, accent, 341, y + 6, true);
            }
        }
    }
}
