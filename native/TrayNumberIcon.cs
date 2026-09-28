using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

namespace CodexTrayStatus
{
    // Uses only the supported NotifyIcon surface. No Explorer child resizing,
    // injected code, undocumented taskbar offsets or cross-process parenting.
    internal static class TrayNumberIcon
    {
        internal static string Text(TaskbarPresentation view)
        {
            // A tiny icon cannot qualify a local/stale value. Show an explicit
            // unknown marker; the menu and page retain the qualified reading.
            if (view == null || view.Status != "fresh") return "?";
            int percent;
            string text = (view.Percent ?? "").TrimEnd('%');
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out percent) && percent >= 0 && percent <= 100 ? text : "?";
        }

        internal static string Key(TaskbarPresentation view, int size, bool contrast)
        {
            return Text(view) + ":" + (view == null ? "neutral" : view.Accent) + ":" + size + ":" + contrast + ":" + SystemColors.WindowText.ToArgb() + ":" + SystemColors.Window.ToArgb();
        }

        internal static Bitmap Draw(TaskbarPresentation view, int size, bool contrast)
        {
            size = Math.Max(16, Math.Min(64, size));
            Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                Color foreground = contrast ? SystemColors.WindowText : Color.White;
                Color background = contrast ? SystemColors.Window : Color.FromArgb(28, 31, 38);
                g.Clear(background); // Explicit opaque contrast on light and dark taskbars.
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                string text = Text(view);
                float fontSize = size * .87f;
                Font font = null;
                try
                {
                    for (;;)
                    {
                        if (font != null) font.Dispose();
                        font = Typography.Ui(fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                        SizeF measured = g.MeasureString(text, font, int.MaxValue, format);
                        if ((measured.Width <= size - 2 && measured.Height <= size - 3) || fontSize <= 6) break;
                        fontSize -= .5f;
                    }
                    SizeF bounds = g.MeasureString(text, font, int.MaxValue, format);
                    using (Brush brush = new SolidBrush(foreground))
                        g.DrawString(text, font, brush, (size - bounds.Width) / 2, (size - 2 - bounds.Height) / 2, format);
                    using (Brush brush = new SolidBrush(contrast ? foreground : TaskbarLayout.Accent(view == null || Text(view) == "?" ? "neutral" : view.Accent)))
                        g.FillRectangle(brush, 1, size - 2, size - 2, 1);
                }
                finally { if (font != null) font.Dispose(); }
            }
            return bitmap;
        }

        internal static Icon Create(TaskbarPresentation view, int size, bool contrast)
        {
            using (Bitmap bitmap = Draw(view, size, contrast))
            {
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon icon = Icon.FromHandle(handle)) return (Icon)icon.Clone(); }
                finally { NativeMethods.DestroyIcon(handle); }
            }
        }
    }
}
