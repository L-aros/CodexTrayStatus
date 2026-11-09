using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexTrayStatus
{
    internal static class TaskbarWindowEventsSmoke
    {
        private static int checks;
        private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "--synthetic-taskbar-cover")
                {
                    using (Cover cover = new Cover(new IntPtr(long.Parse(args[1]))))
                    {
                        cover.Start();
                        Application.Run();
                    }
                    return 0;
                }
                using (Harness harness = new Harness())
                {
                    harness.Start();
                    Application.Run();
                    if (harness.Error != null) throw harness.Error;
                }
                Console.WriteLine("Taskbar window event smoke passed: " + checks + " checks; cross-process reorder, callback recovery, disposal and unchanged foreground.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Taskbar window event failure after " + checks + " checks: " + error.Message);
                return 1;
            }
        }

        private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint eventTime);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

        private static bool Raise(IntPtr window) { return SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 1 | 2 | 0x10); }
        private static bool Behind(IntPtr window, IntPtr cover)
        {
            IntPtr current = GetWindow(window, 3);
            int guard = 0;
            while (current != IntPtr.Zero && guard++ < 4096)
            {
                if (current == cover) return true;
                current = GetWindow(current, 3);
            }
            return false;
        }

        // Both surfaces are transparent and outside the virtual desktop. The dispatcher
        // is a hidden child Control: creating a top-level Form can activate it briefly.
        private abstract class Surface : IDisposable
        {
            private readonly NativeWindow surface = new NativeWindow();
            private readonly Control dispatcher = new Control();
            internal IntPtr Window { get { return surface.Handle; } }
            internal void Create(string title)
            {
                IntPtr ignored = dispatcher.Handle;
                surface.CreateHandle(new CreateParams {
                    Caption = title, ClassName = "STATIC", X = -30000, Y = -30000, Width = 80, Height = 40,
                    Style = unchecked((int)0x80000000), ExStyle = 0x08000000 | 0x00000080 | 0x00080000 | 0x00000008
                });
                Check(SetLayeredWindowAttributes(Window, 0, 0, 2), "Synthetic surface is fully transparent");
                Check(SetWindowPos(Window, new IntPtr(-1), -30000, -30000, 80, 40, 0x10 | 0x40), "Synthetic surface is shown without activation");
            }
            internal void Post(Action action)
            {
                if (dispatcher.IsDisposed) return;
                try { dispatcher.BeginInvoke(action); }
                catch (InvalidOperationException) { if (!dispatcher.IsDisposed) throw; }
            }
            public void Dispose() { Dispose(true); }
            protected virtual void Dispose(bool disposing)
            {
                if (!disposing) return;
                if (surface.Handle != IntPtr.Zero) surface.DestroyHandle();
                dispatcher.Dispose();
            }
        }

        private sealed class Cover : Surface
        {
            private readonly IntPtr label;
            internal Cover(IntPtr labelWindow) { label = labelWindow; }
            internal void Start()
            {
                Create("TaskbarWindowEventsSmoke cover");
                Console.WriteLine("READY " + Window.ToInt64());
                Task.Run(delegate
                {
                    string command;
                    while ((command = Console.ReadLine()) != null)
                    {
                        string captured = command;
                        Post(delegate
                        {
                            if (captured == "quit") { Application.ExitThread(); return; }
                            string[] parts = captured.Split(' ');
                            if (parts.Length != 2 || parts[0] != "raise") return;
                            bool result = Raise(Window);
                            Console.WriteLine("RAISED " + parts[1] + " " + result + " " + Behind(label, Window));
                        });
                    }
                });
            }
        }

        private sealed class Harness : Surface
        {
            private readonly Stopwatch watch = Stopwatch.StartNew();
            private readonly IntPtr initialForeground = GetForegroundWindow();
            private readonly Timer timer = new Timer();
            private readonly WinEventCallback foregroundCallback;
            private IntPtr foregroundHook, coverWindow;
            private Process coverProcess;
            private TaskbarWindowEvents subscription;
            private int sequence, pending, acknowledged, callbacks, repairs, callbacksAtRequest, repairsAtRequest, probeForegroundEvents;
            private long requestedAt, lastRepairAt, maxRepairDelay;
            private bool disposedPhase, finished;
            internal Exception Error { get; private set; }
            internal Harness()
            {
                foregroundCallback = OnForeground;
                timer.Interval = 10;
                timer.Tick += delegate { Guard(Tick); };
            }
            internal void Start()
            {
                // Observe foreground events independently, including this process, so a
                // brief activation cannot be hidden by identical initial/final handles.
                foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundCallback, 0, 0, 0);
                Check(foregroundHook != IntPtr.Zero, "Foreground observer is registered");
                Create("TaskbarWindowEventsSmoke label");
                Check(GetForegroundWindow() == initialForeground, "Creating the label preserves foreground");
                coverProcess = new Process();
                coverProcess.StartInfo = new ProcessStartInfo(Application.ExecutablePath, "--synthetic-taskbar-cover " + Window.ToInt64()) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                coverProcess.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    string line = e.Data;
                    Post(delegate { Guard(delegate { OnChildLine(line); }); });
                };
                coverProcess.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) Console.Error.WriteLine("Synthetic cover: " + e.Data);
                };
                coverProcess.Start();
                coverProcess.BeginOutputReadLine();
                coverProcess.BeginErrorReadLine();
                timer.Start();
            }
            private void OnChildLine(string line)
            {
                string[] parts = line.Split(' ');
                if (parts[0] == "READY")
                {
                    coverWindow = new IntPtr(long.Parse(parts[1]));
                    Check(Raise(Window), "Label is initially above the synthetic cover");
                    Check(!Behind(Window, coverWindow), "Initial z-order is measurable");
                    Console.WriteLine("Synthetic label=" + Window + " cover=" + coverWindow + " childProcess=" + coverProcess.Id + " foreground=" + initialForeground);
                }
                else if (parts[0] == "RAISED")
                {
                    acknowledged = int.Parse(parts[1]);
                    Check(parts[2] == "True", "Child process raised its own cover window");
                }
            }
            private void OnForeground(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint eventTime)
            {
                uint process;
                GetWindowThreadProcessId(window, out process);
                if (process == Process.GetCurrentProcess().Id ||
                    (coverProcess != null && process == coverProcess.Id)) probeForegroundEvents++;
            }
            private void OnReorder()
            {
                callbacks++;
                Guard(delegate
                {
                    Check(GetForegroundWindow() == initialForeground, "Event callback preserves foreground");
                    if (coverWindow == IntPtr.Zero || !Behind(Window, coverWindow)) return;
                    Check(Raise(Window), "Production event callback raises the synthetic label");
                    Check(!Behind(Window, coverWindow), "Label is above the cover before the event callback returns");
                    repairs++;
                    lastRepairAt = watch.ElapsedMilliseconds;
                });
            }
            private void Tick()
            {
                if (finished) return;
                Check(watch.ElapsedMilliseconds < 15000, "Cross-process event regression completes within its total timeout");
                Check(GetForegroundWindow() == initialForeground, "Synthetic windows preserve the user's foreground");
                if (coverWindow == IntPtr.Zero) return;
                if (pending != 0)
                {
                    Check(watch.ElapsedMilliseconds - requestedAt < 1500, "Each synthetic raise receives an event or observation before timeout");
                    if (acknowledged != pending || watch.ElapsedMilliseconds - requestedAt < 60) return;
                    bool behind = Behind(Window, coverWindow);
                    if (pending <= 3)
                        Check(behind, "Without a hook, the cross-process cover remains above the label");
                    else if (pending <= 13)
                    {
                        if (repairs == repairsAtRequest) return;
                        Check(callbacks > callbacksAtRequest && !behind, "Production hook repairs each automatic cross-process reorder");
                        maxRepairDelay = Math.Max(maxRepairDelay, lastRepairAt - requestedAt);
                    }
                    else
                    {
                        Check(callbacks == callbacksAtRequest, "Disposed subscription receives no callbacks");
                        Check(behind, "After disposal, the synthetic cover remains above the label");
                    }
                    Console.WriteLine("Round=" + pending + " phase=" + (pending <= 3 ? "baseline" : pending <= 13 ? "subscribed" : "disposed") +
                        " behind=" + behind + " callbacks=" + callbacks + " repairs=" + repairs);
                    Check(Raise(Window), "Label is reset above the synthetic cover for the next round");
                    pending = 0;
                }
                if (sequence == 3 && subscription == null)
                {
                    subscription = new TaskbarWindowEvents(OnReorder);
                    Check(subscription.IsActive, "Production window event subscription is active");
                }
                if (sequence == 13 && !disposedPhase)
                {
                    subscription.Dispose();
                    subscription.Dispose();
                    Check(!subscription.IsActive, "Repeated disposal leaves the subscription inactive");
                    disposedPhase = true;
                }
                if (sequence == 16)
                {
                    Check(repairs == 10, "All ten subscribed rounds recover inside the production callback");
                    Check(probeForegroundEvents == 0, "Neither synthetic process became foreground, including transient activation");
                    Console.WriteLine("RESULT baseline=3 subscribedRepairs=" + repairs + "/10 disposed=3 maxRequestToRepairMs=" + maxRepairDelay +
                        " probeForegroundEvents=" + probeForegroundEvents + " initialForeground=" + initialForeground + " finalForeground=" + GetForegroundWindow());
                    Finish();
                    return;
                }
                callbacksAtRequest = callbacks;
                repairsAtRequest = repairs;
                requestedAt = watch.ElapsedMilliseconds;
                pending = ++sequence;
                coverProcess.StandardInput.WriteLine("raise " + pending);
                coverProcess.StandardInput.Flush();
            }
            private void Guard(Action action)
            {
                if (finished) return;
                try { action(); }
                catch (Exception error) { Error = error; Finish(); }
            }
            private void Finish()
            {
                finished = true;
                timer.Stop();
                if (subscription != null) subscription.Dispose();
                if (foregroundHook != IntPtr.Zero) { UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
                if (coverProcess != null && !coverProcess.HasExited)
                {
                    coverProcess.StandardInput.WriteLine("quit");
                    coverProcess.StandardInput.Flush();
                }
                Application.ExitThread();
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (subscription != null) subscription.Dispose();
                    if (foregroundHook != IntPtr.Zero) UnhookWinEvent(foregroundHook);
                    timer.Dispose();
                    if (coverProcess != null)
                    {
                        if (!coverProcess.HasExited && !coverProcess.WaitForExit(2000)) coverProcess.Kill();
                        coverProcess.Dispose();
                    }
                }
                base.Dispose(disposing);
                GC.KeepAlive(foregroundCallback);
            }
        }
    }
}
