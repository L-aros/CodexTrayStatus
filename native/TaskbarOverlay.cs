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
        private AppSnapshot snapshot;
        private Rectangle lastTaskbar;
        private Rectangle lastNotification;
        private Rectangle lastNativeBounds;
        private bool bitmapReady;
        private bool nativeVisible;
        private bool started;
        private bool timerDisposed;
        private WebPreferences preferences = new WebPreferences { ShowRemaining = true, Currency = "USD", ExchangeRate = 7m };
        private int missingLayoutSamples;
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
            maintainTimer.Start();
            zOrderTimer.Start();
        }

        internal void ApplySnapshot(AppSnapshot value)
        {
            snapshot = value;
            SafeRender(false);
        }

        internal void ApplyPreferences(WebPreferences value)
        {
            preferences = value;
            SafeRender(false);
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

        private void SafeRender(bool forceRender)
        {
            try { RenderAndPosition(forceRender); }
            catch
            {
                // Preserve the last successfully composed frame. A transient Explorer or
                // GDI failure should not turn a harmless retry into a visible blank flash.
                bitmapReady = false;
            }
        }

        private void MaintainZOrder()
        {
            if (IsDisposed || !IsHandleCreated || !nativeVisible || lastTaskbarWindow == IntPtr.Zero ||
                lastNativeBounds.IsEmpty) return;
            try
            {
                if (NativeMethods.IsForegroundFullScreen(lastTaskbarWindow, Handle))
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
                // The slower layout maintenance pass will recover from shell replacement.
            }
        }

        private void RenderAndPosition(bool forceRender)
        {
            if (IsDisposed || !IsHandleCreated) return;
            Rectangle taskbar;
            Rectangle notification;
            IntPtr taskbarWindow;
            if (!NativeMethods.TryGetTaskbarLayout(out taskbar, out notification, out taskbarWindow))
            {
                // Explorer can briefly rebuild TrayNotifyWnd. Require consecutive misses
                // before hiding so a one-sample gap never flashes the overlay off and on.
                missingLayoutSamples++;
                if (missingLayoutSamples >= 3) HideNative();
                return;
            }
            missingLayoutSamples = 0;
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
                    if (!NativeMethods.ApplyLayeredBitmap(Handle, bitmap, target.Location)) { bitmapReady = false; return; }
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
            if (!nativeVisible && !NativeMethods.IsWindowShown(Handle)) return;
            nativeVisible = false;
            if (!IsDisposed && IsHandleCreated) NativeMethods.HideWindow(Handle);
        }

        internal static string FormatTokens(long value) { return TaskbarPresenter.Tokens(value); }
    }
}
