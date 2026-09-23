[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$frameworkRoot = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path (Join-Path $frameworkRoot "csc.exe"))) {
    $frameworkRoot = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319"
}

$testOutput = Join-Path $projectRoot "artifacts\tests\QuotaServiceSmoke.exe"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $testOutput) | Out-Null

$arguments = @(
    "/nologo",
    "/target:exe",
    "/platform:anycpu",
    "/optimize+",
    "/langversion:5",
    "/out:$testOutput",
    "/reference:$(Join-Path $frameworkRoot 'System.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Core.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Net.Http.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Web.Extensions.dll')",
    (Join-Path $projectRoot "native\Models.cs"),
    (Join-Path $projectRoot "native\TaskbarDisplay.cs"),
    (Join-Path $projectRoot "native\ReminderService.cs"),
    (Join-Path $projectRoot "native\ReminderStateStore.cs"),
    (Join-Path $projectRoot "native\DataFreshness.cs"),
    (Join-Path $projectRoot "native\QuotaHistoryStore.cs"),
    (Join-Path $projectRoot "native\PricingCatalog.cs"),
    (Join-Path $projectRoot "native\QuotaService.cs"),
    (Join-Path $projectRoot "tests-native\QuotaServiceSmoke.cs")
)

& (Join-Path $frameworkRoot "csc.exe") $arguments
if ($LASTEXITCODE -ne 0) { throw "Native smoke-test compilation failed." }
& $testOutput
if ($LASTEXITCODE -ne 0) { throw "Native smoke tests failed." }

$freshnessOutput = Join-Path $projectRoot 'artifacts\tests\DataFreshnessSmoke.exe'
$freshnessArguments = @("/out:$freshnessOutput") + @($arguments | Where-Object { $_ -notlike '/out:*' -and $_ -notlike '*QuotaServiceSmoke.cs' })
$freshnessArguments += (Join-Path $projectRoot 'tests-native\DataFreshnessSmoke.cs')
& (Join-Path $frameworkRoot 'csc.exe') $freshnessArguments
if ($LASTEXITCODE -ne 0) { throw 'Freshness test compilation failed.' }
& $freshnessOutput
if ($LASTEXITCODE -ne 0) { throw 'Freshness tests failed.' }

$historyOutput = Join-Path $projectRoot 'artifacts\tests\QuotaHistorySmoke.exe'
$historyArguments = @("/out:$historyOutput") + @($arguments | Where-Object { $_ -notlike '/out:*' -and $_ -notlike '*QuotaServiceSmoke.cs' })
$historyArguments += (Join-Path $projectRoot 'tests-native\QuotaHistorySmoke.cs')
& (Join-Path $frameworkRoot 'csc.exe') $historyArguments
if ($LASTEXITCODE -ne 0) { throw 'Quota history test compilation failed.' }
& $historyOutput
if ($LASTEXITCODE -ne 0) { throw 'Quota history tests failed.' }

$reminderOutput = Join-Path $projectRoot 'artifacts\tests\ReminderServiceSmoke.exe'
$reminderArguments = @("/out:$reminderOutput") + @($arguments | Where-Object { $_ -notlike '/out:*' -and $_ -notlike '*QuotaServiceSmoke.cs' })
$reminderArguments += (Join-Path $projectRoot 'tests-native\ReminderServiceSmoke.cs')
& (Join-Path $frameworkRoot 'csc.exe') $reminderArguments
if ($LASTEXITCODE -ne 0) { throw 'Reminder test compilation failed.' }
& $reminderOutput
if ($LASTEXITCODE -ne 0) { throw 'Reminder tests failed.' }

$dashboardTestOutput = Join-Path $projectRoot "artifacts\tests\DashboardFormSmoke.exe"
$dashboardRenderOutput = Join-Path $projectRoot "artifacts\tests\dashboard"
$nativeSources = @(Get-ChildItem -Path (Join-Path $projectRoot "native") -Filter "*.cs" -File -Recurse |
    Sort-Object FullName | ForEach-Object { $_.FullName })
$dashboardArguments = @(
    "/nologo",
    "/target:exe",
    "/main:CodexTrayStatus.DashboardFormSmoke",
    "/platform:anycpu",
    "/optimize+",
    "/langversion:5",
    "/out:$dashboardTestOutput",
    "/reference:$(Join-Path $frameworkRoot 'System.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Core.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Drawing.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Windows.Forms.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Net.Http.dll')",
    "/reference:$(Join-Path $frameworkRoot 'System.Web.Extensions.dll')"
)
$dashboardArguments += $nativeSources
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'native/Statistics') -File | ForEach-Object {
    $dashboardArguments += "/resource:$($_.FullName),Statistics.$($_.Name)"
}
$dashboardArguments += (Join-Path $projectRoot "tests-native\DashboardFormSmoke.cs")

& (Join-Path $frameworkRoot "csc.exe") $dashboardArguments
if ($LASTEXITCODE -ne 0) { throw "Dashboard smoke-test compilation failed." }
& $dashboardTestOutput $dashboardRenderOutput
if ($LASTEXITCODE -ne 0) { throw "Dashboard smoke tests failed." }

$taskbarOutput = Join-Path $projectRoot 'artifacts\tests\TaskbarDisplaySmoke.exe'
$taskbarArguments = @("/out:$taskbarOutput", '/main:CodexTrayStatus.TaskbarDisplaySmoke') + @($dashboardArguments | Where-Object { $_ -notlike '/out:*' -and $_ -notlike '/main:*' -and $_ -notlike '*DashboardFormSmoke.cs' })
$taskbarArguments += (Join-Path $projectRoot 'tests-native\TaskbarDisplaySmoke.cs')
& (Join-Path $frameworkRoot 'csc.exe') $taskbarArguments
if ($LASTEXITCODE -ne 0) { throw 'Taskbar test compilation failed.' }
& $taskbarOutput (Join-Path $projectRoot 'artifacts\tests\taskbar')
if ($LASTEXITCODE -ne 0) { throw 'Taskbar tests failed.' }

$resetOutput = Join-Path $projectRoot 'artifacts\tests\ResetAnnouncementsSmoke.exe'
$resetArguments = @('/out:' + $resetOutput) + @($arguments | Where-Object { $_ -notlike '/out:*' -and $_ -notlike '*QuotaServiceSmoke.cs' })
$resetArguments += (Join-Path $projectRoot 'native\ResetAnnouncementsService.cs'), (Join-Path $projectRoot 'tests-native\ResetAnnouncementsSmoke.cs')
& (Join-Path $frameworkRoot 'csc.exe') $resetArguments
if ($LASTEXITCODE -ne 0) { throw 'Reset announcement test compilation failed.' }
& $resetOutput
if ($LASTEXITCODE -ne 0) { throw 'Reset announcement tests failed.' }

$webOutput = Join-Path $projectRoot 'artifacts\tests\WebDashboardSmoke.exe'
$webArguments = @("/out:$webOutput") + @($arguments | Where-Object { $_ -notlike '/out:*' -and $_ -notlike '*QuotaServiceSmoke.cs' })
$webArguments += (Join-Path $projectRoot 'native\StatisticsServer.cs'), (Join-Path $projectRoot 'native\ResetAnnouncementsService.cs'), (Join-Path $projectRoot 'tests-native\WebDashboardSmoke.cs')
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'native\Statistics') -File | ForEach-Object {
    $webArguments += "/resource:$($_.FullName),Statistics.$($_.Name)"
}
& (Join-Path $frameworkRoot 'csc.exe') $webArguments
if ($LASTEXITCODE -ne 0) { throw 'Web dashboard compilation failed.' }
& $webOutput
if ($LASTEXITCODE -ne 0) { throw 'Web dashboard tests failed.' }

$nodeCommand = Get-Command node -ErrorAction SilentlyContinue
if ($nodeCommand) {
    & $nodeCommand.Source (Join-Path $projectRoot 'tests-native\WebFreshnessSmoke.js')
    if ($LASTEXITCODE -ne 0) { throw 'Web freshness display tests failed.' }
    & $nodeCommand.Source (Join-Path $projectRoot 'tests-native\WebResetAnnouncementsSmoke.js')
    if ($LASTEXITCODE -ne 0) { throw 'Public reset display tests failed.' }
}
else { Write-Host 'Node is unavailable; optional web display tests skipped.' }
