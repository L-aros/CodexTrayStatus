using System;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace CodexTrayStatus
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\CodexTrayStatus.Native.Singleton", out created))
            {
                if (!created) return;
                NativeMethods.EnableDpiAwareness();
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                TrayApplicationContext context = new TrayApplicationContext();
                if (Array.IndexOf(args, "--open") >= 0) context.OpenWeb();
                Application.Run(context);
                GC.KeepAlive(mutex);
            }
        }
    }
}
