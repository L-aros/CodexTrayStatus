[CmdletBinding()]
param(
    [ValidateRange(30, 3600)]
    [int]$DurationSeconds = 180,
    [ValidateRange(10, 1000)]
    [int]$SampleMilliseconds = 25,
    [ValidateRange(5, 300)]
    [int]$ProgressSeconds = 30
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class CodexTrayStabilityNative
{
    public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern int GetClassName(IntPtr window, StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out Rect rectangle);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr window, uint command);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static bool IsBehind(IntPtr window, IntPtr possibleCover)
    {
        IntPtr current = GetWindow(window, 3); // GW_HWNDPREV
        int guard = 0;
        while (current != IntPtr.Zero && guard++ < 4096)
        {
            if (current == possibleCover) return true;
            current = GetWindow(current, 3);
        }
        return false;
    }
}
'@

$process = Get-Process -Name CodexTrayStatus -ErrorAction Stop |
    Where-Object { $_.Path -like '*\CodexTrayStatus\CodexTrayStatus.exe' } |
    Select-Object -First 1
if (-not $process) { throw "Installed CodexTrayStatus process was not found." }

[void][CodexTrayStabilityNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
$script:overlay = [IntPtr]::Zero
$script:taskbar = [IntPtr]::Zero
$callback = [CodexTrayStabilityNative+EnumWindowsProc] {
    param($window, $parameter)
    $owner = 0
    [void][CodexTrayStabilityNative]::GetWindowThreadProcessId($window, [ref]$owner)
    $class = New-Object System.Text.StringBuilder 128
    [void][CodexTrayStabilityNative]::GetClassName($window, $class, $class.Capacity)
    if ($class.ToString() -eq 'Shell_TrayWnd') { $script:taskbar = $window }
    if ($owner -eq $process.Id -and [CodexTrayStabilityNative]::IsWindowVisible($window)) {
        $title = New-Object System.Text.StringBuilder 128
        [void][CodexTrayStabilityNative]::GetWindowText($window, $title, $title.Capacity)
        $rectangle = New-Object CodexTrayStabilityNative+Rect
        [void][CodexTrayStabilityNative]::GetWindowRect($window, [ref]$rectangle)
        if ($title.Length -eq 0 -and $rectangle.Right - $rectangle.Left -gt 20) {
            $script:overlay = $window
        }
    }
    return $true
}
[void][CodexTrayStabilityNative]::EnumWindows($callback, [IntPtr]::Zero)
if ($overlay -eq [IntPtr]::Zero -or $taskbar -eq [IntPtr]::Zero) {
    throw "Required windows were not found: overlay=$overlay taskbar=$taskbar"
}

$hiddenSamples = 0
$behindSamples = 0
$blankFrames = 0
$rectangleChanges = 0
$samples = 0
$currentBehindStart = -1L
$longestBehindMilliseconds = 0L
$nextPixelSample = 0L
$nextProgress = [long]$ProgressSeconds * 1000
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$lastRectangle = New-Object CodexTrayStabilityNative+Rect
[void][CodexTrayStabilityNative]::GetWindowRect($overlay, [ref]$lastRectangle)
$lastCpu = $process.CPU

while ($watch.ElapsedMilliseconds -lt $DurationSeconds * 1000L) {
    $process.Refresh()
    if ($process.HasExited -or -not [CodexTrayStabilityNative]::IsWindow($overlay)) {
        throw "CodexTrayStatus exited or destroyed its overlay at $($watch.ElapsedMilliseconds)ms."
    }

    $visible = [CodexTrayStabilityNative]::IsWindowVisible($overlay)
    $behind = [CodexTrayStabilityNative]::IsBehind($overlay, $taskbar)
    if (-not $visible) { $hiddenSamples++ }
    if ($behind) {
        $behindSamples++
        if ($currentBehindStart -lt 0) { $currentBehindStart = $watch.ElapsedMilliseconds }
    }
    elseif ($currentBehindStart -ge 0) {
        $duration = $watch.ElapsedMilliseconds - $currentBehindStart
        $longestBehindMilliseconds = [Math]::Max($longestBehindMilliseconds, $duration)
        $currentBehindStart = -1
    }

    $rectangle = New-Object CodexTrayStabilityNative+Rect
    [void][CodexTrayStabilityNative]::GetWindowRect($overlay, [ref]$rectangle)
    if ($rectangle.Left -ne $lastRectangle.Left -or $rectangle.Top -ne $lastRectangle.Top -or
        $rectangle.Right -ne $lastRectangle.Right -or $rectangle.Bottom -ne $lastRectangle.Bottom) {
        $rectangleChanges++
        $lastRectangle = $rectangle
    }

    if ($watch.ElapsedMilliseconds -ge $nextPixelSample -and $visible -and -not $behind) {
        $width = $rectangle.Right - $rectangle.Left
        $height = $rectangle.Bottom - $rectangle.Top
        if ($width -gt 0 -and $height -gt 0) {
            $bitmap = New-Object System.Drawing.Bitmap($width, $height)
            try {
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.CopyFromScreen($rectangle.Left, $rectangle.Top, 0, 0,
                        (New-Object System.Drawing.Size($width, $height)),
                        [System.Drawing.CopyPixelOperation]::SourceCopy)
                }
                finally { $graphics.Dispose() }

                $textPixels = 0
                for ($y = 3; $y -lt $height - 3; $y += 2) {
                    for ($x = 3; $x -lt $width - 3; $x += 2) {
                        $color = $bitmap.GetPixel($x, $y)
                        $maximum = [Math]::Max($color.R, [Math]::Max($color.G, $color.B))
                        $minimum = [Math]::Min($color.R, [Math]::Min($color.G, $color.B))
                        if (($color.R -gt 125 -and $color.G -gt 125 -and $color.B -gt 125) -or
                            ($maximum - $minimum -gt 50 -and $maximum -gt 130)) {
                            $textPixels++
                        }
                    }
                }
                if ($textPixels -lt 12) { $blankFrames++ }
            }
            finally { $bitmap.Dispose() }
        }
        $nextPixelSample += 1000
    }

    $samples++
    if ($watch.ElapsedMilliseconds -ge $nextProgress) {
        $cpu = $process.CPU
        Write-Output ("progress={0}s samples={1} hidden={2} behind={3} blank={4} rectChanges={5} cpuDelta={6:F2}s ws={7:F1}MiB" -f
            [Math]::Floor($watch.Elapsed.TotalSeconds), $samples, $hiddenSamples, $behindSamples,
            $blankFrames, $rectangleChanges, ($cpu - $lastCpu), ($process.WorkingSet64 / 1MB))
        $lastCpu = $cpu
        $nextProgress += [long]$ProgressSeconds * 1000
    }
    Start-Sleep -Milliseconds $SampleMilliseconds
}

if ($currentBehindStart -ge 0) {
    $longestBehindMilliseconds = [Math]::Max(
        $longestBehindMilliseconds, $watch.ElapsedMilliseconds - $currentBehindStart)
}
$finalBehind = [CodexTrayStabilityNative]::IsBehind($overlay, $taskbar)
$passed = $hiddenSamples -eq 0 -and $blankFrames -eq 0 -and -not $finalBehind -and
    $longestBehindMilliseconds -le 200

Write-Output ("RESULT passed={0} duration={1}s samples={2} hidden={3} behindSamples={4} longestBehindMs={5} blankFrames={6} rectChanges={7} finalBehind={8}" -f
    $passed, $DurationSeconds, $samples, $hiddenSamples, $behindSamples,
    $longestBehindMilliseconds, $blankFrames, $rectangleChanges, $finalBehind)
if (-not $passed) { exit 1 }
