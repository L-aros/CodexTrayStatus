using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodexTrayStatus
{
    internal static class NativeMethods
    {
        internal const int WM_MOUSEACTIVATE = 0x0021;
        internal const int WM_LBUTTONUP = 0x0202;
        internal const int MA_NOACTIVATE = 3;
        internal const int WS_EX_LAYERED = 0x00080000;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const byte AC_SRC_OVER = 0x00;
        internal const byte AC_SRC_ALPHA = 0x01;
        internal const int ULW_ALPHA = 0x00000002;
        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private const uint ABM_GETSTATE = 0x00000004;
        private const int ABS_AUTOHIDE = 0x00000001;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
        private const int QUNS_PRESENTATION_MODE = 4;
        private const int SW_HIDE = 0;
        private const uint GW_HWNDPREV = 3;
        private static readonly IntPtr HGDI_ERROR = new IntPtr(-1);
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref PointNative destination,
            ref SizeNative size, IntPtr sourceDc, ref PointNative source, int colorKey, ref BlendFunction blend, int flags);

        [DllImport("user32.dll")]
        internal static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr awarenessContext);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int awareness);

        [DllImport("shell32.dll")]
        private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        [DllImport("user32.dll")]
        internal static extern bool DestroyIcon(IntPtr icon);

        internal static void EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            try
            {
                // S_OK means the call set PM awareness. E_ACCESSDENIED normally means
                // the manifest already selected an awareness mode, so no legacy call is needed.
                int result = SetProcessDpiAwareness(2);
                if (result == 0 || result == unchecked((int)0x80070005)) return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            try { SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { }
        }

        internal static bool TryGetTaskbarLayout(out Rectangle taskbar, out Rectangle notification)
        {
            IntPtr ignored;
            return TryGetTaskbarLayout(out taskbar, out notification, out ignored);
        }

        internal static bool TryGetTaskbarLayout(out Rectangle taskbar, out Rectangle notification, out IntPtr taskbarWindow)
        {
            taskbar = Rectangle.Empty;
            notification = Rectangle.Empty;
            taskbarWindow = FindWindow("Shell_TrayWnd", null);
            if (taskbarWindow == IntPtr.Zero || !IsWindowVisible(taskbarWindow)) return false;

            IntPtr notificationWindow = FindWindowEx(taskbarWindow, IntPtr.Zero, "TrayNotifyWnd", null);
            Rect taskbarRect;
            Rect notificationRect;
            if (notificationWindow == IntPtr.Zero || !IsWindowVisible(notificationWindow) ||
                !GetWindowRect(taskbarWindow, out taskbarRect) || !GetWindowRect(notificationWindow, out notificationRect))
                return false;

            taskbar = Rectangle.FromLTRB(taskbarRect.Left, taskbarRect.Top, taskbarRect.Right, taskbarRect.Bottom);
            notification = Rectangle.FromLTRB(notificationRect.Left, notificationRect.Top, notificationRect.Right, notificationRect.Bottom);

            // The compact two-line renderer is intentionally limited to the horizontal
            // taskbar containing the notification area. A vertical or secondary taskbar
            // is safer to leave untouched than to cover a large portion of the desktop.
            if (taskbar.Width <= taskbar.Height || taskbar.Width <= 0 || taskbar.Height <= 0 || notification.Width <= 0)
                return false;
            if (notification.Left < taskbar.Left || notification.Right > taskbar.Right ||
                notification.Bottom <= taskbar.Top || notification.Top >= taskbar.Bottom)
                return false;
            return true;
        }

        internal static bool IsTaskbarAutoHidden(Rectangle taskbar, IntPtr taskbarWindow)
        {
            AppBarData data = new AppBarData();
            data.cbSize = Marshal.SizeOf(typeof(AppBarData));
            UIntPtr state;
            try { state = SHAppBarMessage(ABM_GETSTATE, ref data); }
            catch { return false; }
            if ((((long)state.ToUInt64()) & ABS_AUTOHIDE) == 0) return false;

            Rectangle monitor;
            if (!TryGetMonitorBounds(taskbarWindow, out monitor)) return true;
            Rectangle visible = Rectangle.Intersect(taskbar, monitor);
            // During the auto-hide animation Explorer leaves a one or two pixel reveal
            // strip. Wait until the full bar is back before placing our window on it.
            return visible.Width <= 0 || visible.Height + 2 < taskbar.Height;
        }

        internal static bool IsForegroundFullScreen(IntPtr taskbarWindow, IntPtr overlayWindow)
        {
            int notificationState;
            try
            {
                if (SHQueryUserNotificationState(out notificationState) == 0 &&
                    (notificationState == QUNS_RUNNING_D3D_FULL_SCREEN || notificationState == QUNS_PRESENTATION_MODE))
                    return true;
            }
            catch { }

            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == taskbarWindow || foreground == overlayWindow ||
                !IsWindowVisible(foreground)) return false;

            // Borderless full-screen windows commonly do not set the shell's D3D
            // notification state. Compare against the monitor containing our taskbar.
            Rectangle monitor;
            Rect window;
            if (!TryGetMonitorBounds(taskbarWindow, out monitor) || !GetWindowRect(foreground, out window))
                return false;
            return window.Left <= monitor.Left + 2 && window.Top <= monitor.Top + 2 &&
                window.Right >= monitor.Right - 2 && window.Bottom >= monitor.Bottom - 2;
        }

        internal static bool ApplyLayeredBitmap(IntPtr window, Bitmap bitmap, Point location)
        {
            if (window == IntPtr.Zero || bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0) return false;

            IntPtr screenDc = IntPtr.Zero;
            IntPtr memoryDc = IntPtr.Zero;
            IntPtr bitmapHandle = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;
            try
            {
                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return false;
                memoryDc = CreateCompatibleDC(screenDc);
                if (memoryDc == IntPtr.Zero) return false;
                bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
                if (bitmapHandle == IntPtr.Zero) return false;
                previous = SelectObject(memoryDc, bitmapHandle);
                if (previous == IntPtr.Zero || previous == HGDI_ERROR) return false;

                PointNative destination = new PointNative(location.X, location.Y);
                PointNative source = new PointNative(0, 0);
                SizeNative size = new SizeNative(bitmap.Width, bitmap.Height);
                BlendFunction blend = new BlendFunction();
                blend.BlendOp = AC_SRC_OVER;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AC_SRC_ALPHA;
                return UpdateLayeredWindow(window, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA);
            }
            catch (ExternalException)
            {
                return false;
            }
            finally
            {
                if (previous != IntPtr.Zero && previous != HGDI_ERROR && memoryDc != IntPtr.Zero)
                    SelectObject(memoryDc, previous);
                if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
                if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        internal static void HideWindow(IntPtr window)
        {
            if (window != IntPtr.Zero) ShowWindow(window, SW_HIDE);
        }

        internal static bool IsWindowShown(IntPtr window)
        {
            return window != IntPtr.Zero && IsWindowVisible(window);
        }

        internal static bool IsWindowBehind(IntPtr window, IntPtr possibleCover)
        {
            if (window == IntPtr.Zero || possibleCover == IntPtr.Zero || window == possibleCover) return false;
            IntPtr current = GetWindow(window, GW_HWNDPREV);
            int guard = 0;
            while (current != IntPtr.Zero && guard++ < 4096)
            {
                if (current == possibleCover) return true;
                current = GetWindow(current, GW_HWNDPREV);
            }
            return false;
        }

        private static bool TryGetMonitorBounds(IntPtr window, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            IntPtr monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;
            MonitorInfo info = new MonitorInfo();
            info.cbSize = Marshal.SizeOf(typeof(MonitorInfo));
            if (!GetMonitorInfo(monitor, ref info)) return false;
            bounds = Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int cbSize;
            public Rect rcMonitor;
            public Rect rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AppBarData
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public Rect rc;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PointNative
        {
            public int X, Y;
            public PointNative(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SizeNative
        {
            public int Width, Height;
            public SizeNative(int width, int height) { Width = width; Height = height; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }
    }
}
