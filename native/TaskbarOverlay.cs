using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

namespace CodexTrayStatus
{
    internal sealed class TaskbarOverlay : Form
    {
        private readonly Timer maintainTimer;
        private readonly Timer zOrderTimer;
        private TaskbarWindowEvents windowEvents;
        private AppSnapshot snapshot;
        private Rectangle lastTaskbar;
        private Rectangle lastNotification;
        private Rectangle lastNativeBounds;
        private bool bitmapReady;
        private bool nativeVisible;
        private bool started;
        private bool timerDisposed;
        private WebPreferences preferences = new WebPreferences { ShowRemaining = true, Currency = "USD", ExchangeRate = 7m };
        private string lastVisualKey;
        private IntPtr lastTaskbarWindow;

        internal event EventHandler ClickRequested;

        internal TaskbarOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            Size = new Size(1, 1);
            maintainTimer = new Timer();
            maintainTimer.Interval = 1500;
            maintainTimer.Tick += delegate { SafeRender(false); };
            zOrderTimer = new Timer();
            zOrderTimer.Interval = 100;
            zOrderTimer.Tick += delegate { MaintainZOrder(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
                return parameters;
            }
        }

        internal void Start()
        {
            if (started || IsDisposed) return;
            started = true;
            IntPtr ignored = Handle; // Create the native window without displaying a blank first frame.
            SafeRender(true);
            UpdateTimers();
        }

        internal void ApplySnapshot(AppSnapshot value)
        {
            snapshot = value;
            SafeRender(false);
        }

        internal void ApplyPreferences(WebPreferences value)
        {
            preferences = value;
            UpdateTimers();
            SafeRender(false);
        }

        private bool ExtendedDisplay { get { return preferences != null && preferences.TaskbarDisplay != null && preferences.TaskbarDisplay.DisplayMode == "overlay"; } }

        private void UpdateTimers()
        {
            if (timerDisposed) return;
            bool enabled = started && ExtendedDisplay;
            maintainTimer.Enabled = zOrderTimer.Enabled = enabled;
            if (enabled && windowEvents == null) windowEvents = new TaskbarWindowEvents(MaintainZOrder);
            if (!enabled) StopWindowEvents();
        }

        private void StopWindowEvents()
        {
            if (windowEvents == null) return;
            windowEvents.Dispose();
            windowEvents = null;
        }

        internal bool TryGetAnchorBounds(out Rectangle bounds)
        {
            if (nativeVisible && !lastNativeBounds.IsEmpty)
            {
                bounds = lastNativeBounds;
                return true;
            }

            Rectangle taskbar;
            Rectangle notification;
            if (NativeMethods.TryGetTaskbarLayout(out taskbar, out notification))
            {
                bounds = notification;
                return true;
            }
            bounds = Rectangle.Empty;
            return false;
        }

        internal void Stop()
        {
            StopWindowEvents();
            if (!timerDisposed)
            {
                maintainTimer.Stop();
                zOrderTimer.Stop();
                maintainTimer.Dispose();
                zOrderTimer.Dispose();
                timerDisposed = true;
            }
            HideNative();
            if (!IsDisposed) Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) StopWindowEvents();
            if (disposing && !timerDisposed)
            {
                maintainTimer.Dispose();
                zOrderTimer.Dispose();
                timerDisposed = true;
            }
            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WM_MOUSEACTIVATE)
            {
                message.Result = new IntPtr(NativeMethods.MA_NOACTIVATE);
                return;
            }
            if (message.Msg == NativeMethods.WM_LBUTTONUP)
            {
                EventHandler handler = ClickRequested;
                if (handler != null) handler(this, EventArgs.Empty);
                message.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref message);
            if (message.Msg == 0x02E0) SafeRender(true); // WM_DPICHANGED
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            nativeVisible = false;
            bitmapReady = false;
            lastNativeBounds = Rectangle.Empty;
            lastTaskbarWindow = IntPtr.Zero;
            base.OnHandleDestroyed(e);
        }

        private void SafeRender(bool forceRender)
        {
            try { RenderAndPosition(forceRender); }
            catch
            {
                // An old frame may now cover a different shell surface. Retry composition
                // on the next pass, but never preserve an unverified screen position.
                bitmapReady = false;
                HideNative();
            }
        }

        private void MaintainZOrder()
        {
            if (IsDisposed || !IsHandleCreated || !started || !ExtendedDisplay) return;
            if (!nativeVisible || lastTaskbarWindow == IntPtr.Zero || lastNativeBounds.IsEmpty)
            {
                SafeRender(false);
                return;
            }
            try
            {
                Rectangle taskbar;
                Rectangle notification;
                IntPtr taskbarWindow;
                bool found = NativeMethods.TryGetTaskbarLayout(out taskbar, out notification, out taskbarWindow);
                if (!TaskbarSurface.Matches(found, taskbarWindow, taskbar, notification,
                    lastTaskbarWindow, lastTaskbar, lastNotification))
                {
                    // Revalidate and compose against the new geometry in this pass.
                    // Hiding first leaves a blank until the slower render timer fires.
                    SafeRender(false);
                    return;
                }
                if (NativeMethods.IsTaskbarAutoHidden(taskbar, taskbarWindow) ||
                    NativeMethods.IsForegroundFullScreen(taskbarWindow, Handle))
                {
                    HideNative();
                    return;
                }
                if (!NativeMethods.IsWindowShown(Handle))
                {
                    nativeVisible = false;
                    ShowNative(lastNativeBounds, false);
                    return;
                }
                if (NativeMethods.IsWindowBehind(Handle, lastTaskbarWindow))
                    ShowNative(lastNativeBounds, true);
            }
            catch
            {
                HideNative(); // The slower pass revalidates and recovers the shell surface.
            }
        }

        private void RenderAndPosition(bool forceRender)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (!ExtendedDisplay) { HideNative(); return; }
            Rectangle taskbar;
            Rectangle notification;
            IntPtr taskbarWindow;
            if (!NativeMethods.TryGetTaskbarLayout(out taskbar, out notification, out taskbarWindow))
            {
                HideNative();
                return;
            }
            if (NativeMethods.IsTaskbarAutoHidden(taskbar, taskbarWindow) ||
                NativeMethods.IsForegroundFullScreen(taskbarWindow, Handle))
            {
                HideNative();
                return;
            }

            bool shellChanged = lastTaskbarWindow != IntPtr.Zero && taskbarWindow != lastTaskbarWindow;
            bool layoutChanged = taskbar != lastTaskbar || notification != lastNotification || shellChanged;
            lastTaskbar = taskbar;
            lastNotification = notification;
            lastTaskbarWindow = taskbarWindow;

            TaskbarPresentation view = TaskbarPresenter.Build(snapshot, preferences, DataFreshness.Now);
            string visualKey = TaskbarPresenter.VisualKey(view);
            bool visualChanged = !string.Equals(visualKey, lastVisualKey, StringComparison.Ordinal);
            if (!forceRender && !layoutChanged && !visualChanged && bitmapReady && !lastNativeBounds.IsEmpty)
            {
                ShowNative(lastNativeBounds, NativeMethods.IsWindowBehind(Handle, taskbarWindow));
                return;
            }
            using (TaskbarLayout layout = TaskbarLayout.Create(view, notification.Left - taskbar.Left - 8, taskbar.Height))
            {
                if (layout == null) { bitmapReady = false; HideNative(); return; }
                Rectangle target = new Rectangle(notification.Left - layout.Width - 4, taskbar.Top, layout.Width, taskbar.Height);
                using (Bitmap bitmap = layout.Draw(view.Accent))
                {
                    if (!NativeMethods.ApplyLayeredBitmap(Handle, bitmap, target.Location)) { bitmapReady = false; HideNative(); return; }
                }
                lastNativeBounds = target;
                bitmapReady = true;
                lastVisualKey = visualKey;
                ShowNative(target, shellChanged || NativeMethods.IsWindowBehind(Handle, taskbarWindow));
            }
        }

        private void ShowNative(Rectangle target, bool forceZOrder)
        {
            if (IsDisposed || !IsHandleCreated) return;
            bool actuallyVisible = NativeMethods.IsWindowShown(Handle);
            if (!forceZOrder && nativeVisible && actuallyVisible) return;
            uint flags = NativeMethods.SWP_NOACTIVATE;
            if (!actuallyVisible) flags |= NativeMethods.SWP_SHOWWINDOW;
            nativeVisible = NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST,
                target.X, target.Y, target.Width, target.Height,
                flags);
        }

        private void HideNative()
        {
            if (IsDisposed || !IsHandleCreated) { nativeVisible = false; return; }
            if (!nativeVisible && !NativeMethods.IsWindowShown(Handle)) return;
            nativeVisible = false;
            if (!IsDisposed && IsHandleCreated) NativeMethods.HideWindow(Handle);
        }

        internal static string FormatTokens(long value) { return TaskbarPresenter.Tokens(value); }
    }

    // Pure shell identity/geometry gate, shared by runtime maintenance and regression tests.
    internal static class TaskbarSurface
    {
        internal static bool Matches(bool found, IntPtr window, Rectangle bar, Rectangle tray,
            IntPtr previousWindow, Rectangle previousBar, Rectangle previousTray)
        {
            return found && window != IntPtr.Zero && window == previousWindow &&
                bar.Width > bar.Height && bar.Height > 0 && tray.Width > 0 && tray.Height > 0 &&
                bar.Contains(tray) && bar == previousBar && tray == previousTray;
        }
    }
}
