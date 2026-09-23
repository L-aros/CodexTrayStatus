using System.Windows.Forms;

namespace CodexTrayStatus
{
    internal sealed class ReminderNotificationSink : IReminderNotificationSink
    {
        private readonly NotifyIcon icon;
        internal ReminderNotificationSink(NotifyIcon icon) { this.icon = icon; }
        public void Show(string title, string message)
        {
            // Native balloons have bounded text. Preserve one combined notification.
            if (message.Length > 250) message = message.Substring(0, 230) + "… 点击查看全部额度";
            icon.ShowBalloonTip(6000, title, message, ToolTipIcon.Info);
        }
    }
}
