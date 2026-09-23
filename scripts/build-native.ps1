[CmdletBinding()]
param(
    [string]$Version = "0.4.2",
    [switch]$SkipInstaller,
    [string]$NativeOutputDirectory = "native"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$projectRoot = Split-Path -Parent $PSScriptRoot
$nativeRoot = Join-Path $projectRoot "native"
$artifactRoot = Join-Path $projectRoot "artifacts"
$nativeOutput = Join-Path $artifactRoot $NativeOutputDirectory
$installerOutput = Join-Path $artifactRoot "installer"
$applicationExe = Join-Path $nativeOutput "CodexTrayStatus.exe"
$installerScript = Join-Path $projectRoot "installer\CodexTrayStatus.nsi"

$frameworkRoots = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319")
)
$frameworkRoot = $frameworkRoots | Where-Object {
    Test-Path (Join-Path $_ "csc.exe")
} | Select-Object -First 1

if (-not $frameworkRoot) {
    throw ".NET Framework 4.x C# compiler was not found. Install or enable .NET Framework 4.8."
}

$release = 0
$releaseKeys = @(
    "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\NET Framework Setup\NDP\v4\Full"
)
foreach ($releaseKey in $releaseKeys) {
    if (Test-Path $releaseKey) {
        $candidate = (Get-ItemProperty -Path $releaseKey -Name Release -ErrorAction SilentlyContinue).Release
        if ($candidate -and ([int]$candidate -gt $release)) {
            $release = [int]$candidate
        }
    }
}
if ($release -lt 528040) {
    throw ".NET Framework 4.8 is required (Release >= 528040); detected Release=$release."
}

$sources = @(Get-ChildItem -Path $nativeRoot -Filter "*.cs" -File -Recurse | Sort-Object FullName)
if ($sources.Count -eq 0) {
    throw "No C# sources were found under $nativeRoot."
}

New-Item -ItemType Directory -Force -Path $nativeOutput, $installerOutput | Out-Null

$referenceNames = @(
    "System.dll",
    "System.Core.dll",
    "System.Drawing.dll",
    "System.Windows.Forms.dll",
    "System.Net.Http.dll",
    "System.Web.Extensions.dll"
)
$compilerArguments = @(
    "/nologo",
    "/target:winexe",
    "/platform:anycpu",
    "/optimize+",
    "/debug-",
    "/langversion:5",
    "/out:$applicationExe"
)
foreach ($referenceName in $referenceNames) {
    $referencePath = Join-Path $frameworkRoot $referenceName
    if (-not (Test-Path $referencePath)) {
        throw "Required .NET Framework assembly was not found: $referencePath"
    }
    $compilerArguments += "/reference:$referencePath"
}

$iconPath = Join-Path $nativeRoot "assets\app.ico"
if (Test-Path $iconPath) {
    $compilerArguments += "/win32icon:$iconPath"
}
$manifestPath = Join-Path $nativeRoot "app.manifest"
if (Test-Path $manifestPath) {
    $compilerArguments += "/win32manifest:$manifestPath"
}
$compilerArguments += @($sources | ForEach-Object { $_.FullName })
Get-ChildItem -LiteralPath (Join-Path $nativeRoot 'Statistics') -File | ForEach-Object {
    $compilerArguments += "/resource:$($_.FullName),Statistics.$($_.Name)"
}

Write-Host "Compiling native application..."
& (Join-Path $frameworkRoot "csc.exe") $compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed with exit code $LASTEXITCODE."
}

$configurationPath = Join-Path $nativeRoot "CodexTrayStatus.exe.config"
if (Test-Path $configurationPath) {
    Copy-Item -LiteralPath $configurationPath -Destination ($applicationExe + ".config") -Force
}

if ($SkipInstaller) {
    Write-Host "Application: $applicationExe"
    return
}

$nsisCandidates = @(
    (Join-Path $projectRoot "artifacts\tools\nsis\makensis.exe"),
    (Join-Path $projectRoot ".builder-cache-v2\nsis-3.0.4.1\nsis-3.0.4.1-1mx3n\Bin\makensis.exe"),
    "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
    "$env:ProgramFiles\NSIS\makensis.exe"
)
$makeNsis = $nsisCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $makeNsis) {
    $nsisCommand = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($nsisCommand) { $makeNsis = $nsisCommand.Source }
}
if (-not $makeNsis) {
    throw "NSIS makensis.exe was not found. Install NSIS or place it under artifacts/tools/nsis."
}
if (-not (Test-Path $installerScript)) {
    throw "The NSIS installer script was not found: $installerScript"
}

$setupExe = Join-Path $installerOutput "CodexTrayStatus-Setup.exe"
Write-Host "Building per-user installer..."
& $makeNsis "/INPUTCHARSET" "UTF8" "/DAPP_SOURCE=$applicationExe" "/DAPP_VERSION=$Version" "/DOUTPUT_FILE=$setupExe" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "NSIS compilation failed with exit code $LASTEXITCODE."
}

Write-Host "Application: $applicationExe"
Write-Host "Installer:   $setupExe"
