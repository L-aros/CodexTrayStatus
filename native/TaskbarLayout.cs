using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace CodexTrayStatus
{
    // Owns the exact measured fonts and rectangles used for rendering. No window or I/O.
    internal sealed class TaskbarLayout : IDisposable
    {
        internal Font MainFont, PercentFont, DetailFont;
        internal string Prefix, Percent, Secondary;
        internal RectangleF PrefixBounds, PercentBounds, SecondaryBounds;
        internal int Width, Height, Level;
        internal static Color Accent(string accent)
        {
            return accent == "red" ? Color.FromArgb(239, 92, 92) : accent == "amber" ? Color.FromArgb(230, 173, 62) :
                accent == "green" ? Color.FromArgb(83, 201, 126) : Color.FromArgb(150, 160, 174);
        }
        internal static TaskbarLayout Create(TaskbarPresentation view, int availableWidth, int height)
        {
            if (availableWidth < 12 || height < 14) return null;
            TaskbarLayout layout = new TaskbarLayout { Height = height, Percent = view.Percent };
            layout.MainFont = Typography.Ui(Math.Max(10, Math.Min(26, Math.Max(13, height * .265f))), FontStyle.Regular, GraphicsUnit.Pixel);
            layout.PercentFont = Typography.Ui(layout.MainFont.Size, FontStyle.Bold, GraphicsUnit.Pixel);
            layout.DetailFont = Typography.Ui(Math.Max(9.5f, Math.Min(18, height * .18f)), FontStyle.Regular, GraphicsUnit.Pixel);
            using (Bitmap measureBitmap = new Bitmap(1, 1))
            using (Graphics graphics = Graphics.FromImage(measureBitmap))
            using (StringFormat format = StringFormat.GenericTypographic)
            {
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                for (int level = 0; level < 4; level++)
                {
                    layout.Level = level;
                    layout.Prefix = level == 0 ? view.Prefix : view.CompactPrefix;
                    layout.Secondary = level == 0 ? view.Secondary : level == 1 ? view.CompactSecondary : "";
                    // At the smallest size preserve stale/reference semantics, even if that means hiding.
                    if (level == 3) layout.Prefix = view.Status == "stale" ? "旧 " : view.Status == "reference" ? "参考 " : "";
                    float prefix = Measure(graphics, layout.Prefix, layout.MainFont, format);
                    float percent = Measure(graphics, layout.Percent, layout.PercentFont, format);
                    float secondary = Measure(graphics, layout.Secondary, layout.DetailFont, format);
                    float gap = Math.Max(1, (float)Math.Round(height * .025));
                    float mainHeight = Math.Max(layout.MainFont.GetHeight(graphics), layout.PercentFont.GetHeight(graphics));
                    float secondaryHeight = layout.Secondary.Length == 0 ? 0 : layout.DetailFont.GetHeight(graphics);
                    float totalHeight = mainHeight + (secondaryHeight > 0 ? gap + secondaryHeight : 0);
                    int width = (int)Math.Ceiling(Math.Max(prefix + percent, secondary)) + 8;
                    if (width > availableWidth || totalHeight + 4 > height) continue;
                    layout.Width = width;
                    float top = (height - totalHeight) / 2;
                    layout.PrefixBounds = new RectangleF(width - 4 - prefix - percent, top, prefix, mainHeight);
                    layout.PercentBounds = new RectangleF(width - 4 - percent, top, percent, mainHeight);
                    layout.SecondaryBounds = new RectangleF(width - 4 - secondary, top + mainHeight + gap, secondary, secondaryHeight);
                    return layout;
                }
            }
            layout.Dispose(); return null;
        }
        private static float Measure(Graphics graphics, string text, Font font, StringFormat format)
        {
            return text.Length == 0 ? 0 : graphics.MeasureString(text, font, int.MaxValue, format).Width;
        }
        internal Bitmap Draw(string accent)
        {
            Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (StringFormat format = StringFormat.GenericTypographic)
            using (Brush primary = new SolidBrush(Color.FromArgb(235, 238, 242)))
            using (Brush highlight = new SolidBrush(Accent(accent)))
            using (Brush muted = new SolidBrush(Color.FromArgb(170, 181, 190)))
            {
                graphics.Clear(Color.Transparent);
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.TextContrast = 2;
                graphics.DrawString(Prefix, MainFont, primary, PrefixBounds.Location, format);
                graphics.DrawString(Percent, PercentFont, highlight, PercentBounds.Location, format);
                if (Secondary.Length > 0) graphics.DrawString(Secondary, DetailFont, muted, SecondaryBounds.Location, format);
            }
            return bitmap;
        }
        public void Dispose() { MainFont.Dispose(); PercentFont.Dispose(); DetailFont.Dispose(); }
    }
}
