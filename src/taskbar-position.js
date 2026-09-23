const { execFile } = require('node:child_process')

// Electron does not expose the notification-area rectangle. Ask Explorer for its real TrayNotifyWnd
// bounds so the status text can end immediately before it rather than reserving a guessed width.
const POWERSHELL_PROBE = String.raw`
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CodexTrayProbe {
  [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern IntPtr FindWindow(string cls, string name);
  [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string cls, string name);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
'@
$taskbar = [CodexTrayProbe]::FindWindow('Shell_TrayWnd', $null)
$notify = [CodexTrayProbe]::FindWindowEx($taskbar, [IntPtr]::Zero, 'TrayNotifyWnd', $null)
$taskbarRect = New-Object CodexTrayProbe+RECT
$notifyRect = New-Object CodexTrayProbe+RECT
if ($taskbar -ne [IntPtr]::Zero -and $notify -ne [IntPtr]::Zero -and [CodexTrayProbe]::GetWindowRect($taskbar, [ref]$taskbarRect) -and [CodexTrayProbe]::GetWindowRect($notify, [ref]$notifyRect)) {
  @{
    taskbar=@{ left=$taskbarRect.Left; top=$taskbarRect.Top; right=$taskbarRect.Right; bottom=$taskbarRect.Bottom }
    notification=@{ left=$notifyRect.Left; top=$notifyRect.Top; right=$notifyRect.Right; bottom=$notifyRect.Bottom }
  } | ConvertTo-Json -Compress -Depth 3
}`

function validRect(rect) {
  if (!rect || typeof rect !== 'object') return false
  const values = [rect.left, rect.top, rect.right, rect.bottom]
  return values.every(Number.isFinite) && rect.right > rect.left && rect.bottom > rect.top
}

function mapTaskbarLayoutToDisplay(layout, displayBounds) {
  if (!layout || !validRect(layout.taskbar) || !validRect(layout.notification)) return undefined
  const nativeWidth = layout.taskbar.right - layout.taskbar.left
  if (!nativeWidth || !displayBounds || !Number.isFinite(displayBounds.width)) return undefined
  const coordinateRatio = displayBounds.width / nativeWidth
  return {
    notificationLeft: displayBounds.x + Math.round(
      (layout.notification.left - layout.taskbar.left) * coordinateRatio
    ),
    taskbarHeight: Math.max(1, Math.round(
      (layout.taskbar.bottom - layout.taskbar.top) * coordinateRatio
    )),
    isTop: layout.taskbar.top <= 0
  }
}

function readWindowsTaskbarLayout() {
  if (process.platform !== 'win32') return Promise.resolve(undefined)
  return new Promise((resolve) => {
    execFile('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', POWERSHELL_PROBE], {
      windowsHide: true,
      timeout: 3000,
      maxBuffer: 4096
    }, (error, stdout) => {
      if (error || !stdout.trim()) return resolve(undefined)
      try {
        const layout = JSON.parse(stdout)
        resolve(validRect(layout.taskbar) && validRect(layout.notification) ? layout : undefined)
      } catch {
        resolve(undefined)
      }
    })
  })
}

module.exports = { mapTaskbarLayoutToDisplay, readWindowsTaskbarLayout }
