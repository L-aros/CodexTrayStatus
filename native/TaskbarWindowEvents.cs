using System;

namespace CodexTrayStatus
{
    // Top-level z-order changes are reported by the desktop's client object,
    // rather than by the taskbar window that moved. Listen without injecting into Explorer.
    internal sealed class TaskbarWindowEvents : IDisposable
    {
        private const uint EventObjectReorder = 0x8004;
        private const uint SkipOwnProcess = 0x0002; // WINEVENT_OUTOFCONTEXT is zero.
        private const int ObjectClient = -4;
        private readonly NativeMethods.WinEventProc callback;
        private readonly Action changed;
        private IntPtr hook;
        private bool dispatching;
        private bool disposed;

        internal TaskbarWindowEvents(Action changed)
        {
            this.changed = changed;
            callback = OnWindowEvent; // Root the managed delegate for the hook's entire lifetime.
            hook = NativeMethods.SetWinEventHook(EventObjectReorder, EventObjectReorder,
                IntPtr.Zero, callback, 0, 0, SkipOwnProcess);
        }

        internal bool IsActive { get { return hook != IntPtr.Zero && !disposed; } }

        private void OnWindowEvent(IntPtr eventHook, uint eventType, IntPtr window,
            int objectId, int childId, uint eventThread, uint eventTime)
        {
            if (disposed || dispatching || eventType != EventObjectReorder ||
                window != NativeMethods.GetDesktopWindow() || objectId != ObjectClient || childId != 0) return;
            dispatching = true;
            try { changed(); }
            catch { /* Never propagate a managed exception through the native callback; the timer retries. */ }
            finally { dispatching = false; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(hook);
                hook = IntPtr.Zero;
            }
            GC.KeepAlive(callback);
        }
    }
}
