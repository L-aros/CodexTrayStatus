using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;

namespace CodexTrayStatus
{
    internal static class Typography
    {
        internal static readonly string UiFamily;
        internal static readonly string DisplayFamily;

        static Typography()
        {
            HashSet<string> installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (InstalledFontCollection collection = new InstalledFontCollection())
                    foreach (FontFamily family in collection.Families)
                        installed.Add(family.Name);
            }
            catch
            {
                // Font lookup is best effort. The generic fallback is always available.
            }

            string systemUi = null;
            try { systemUi = SystemFonts.MessageBoxFont.FontFamily.Name; }
            catch { }

            UiFamily = Resolve(installed, systemUi, "Microsoft YaHei UI", "Microsoft YaHei",
                "Noto Sans SC", "Microsoft JhengHei UI", "Segoe UI");
            DisplayFamily = Resolve(installed, "Segoe UI Variable Text Semibold", "Segoe UI Semibold",
                "Segoe UI Variable Display", "Segoe UI", UiFamily);
        }

        internal static Font Ui(float size, FontStyle style, GraphicsUnit unit)
        {
            return new Font(UiFamily, size, style, unit);
        }

        internal static Font Display(float size, FontStyle style, GraphicsUnit unit)
        {
            return new Font(DisplayFamily, size, style, unit);
        }

        private static string Resolve(HashSet<string> installed, params string[] candidates)
        {
            foreach (string candidate in candidates)
                if (!string.IsNullOrWhiteSpace(candidate) && installed.Contains(candidate))
                    return candidate;
            return FontFamily.GenericSansSerif.Name;
        }
    }
}
