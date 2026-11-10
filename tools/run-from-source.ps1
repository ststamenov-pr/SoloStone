# SPDX-License-Identifier: AGPL-3.0-or-later
<#
.SYNOPSIS
Builds SoloStone from this source folder and starts its setup. For people who have the source but no installer.

.DESCRIPTION
Does the steps of docs\BUILDING.md in one go, on Windows 10 or 11:

  1. finds your Hearthstone folder and copies the reference libraries the plugin compiles against into
     references\game (they stay on your machine; .gitignore keeps them out of Git);
  2. downloads the mod loader files named in installer\loader-inputs.json and checks every hash;
  3. builds the launcher, the engine and the plugin, and the setup program;
  4. starts the setup program it just built.

Your Hearthstone folder is only read. Quit Battle.net before the setup runs, as for any SoloStone install.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\run-from-source.ps1

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tools\run-from-source.ps1 -Client "D:\Games\Hearthstone" -NoStart
#>
param(
    # Your Hearthstone folder (the one that holds Hearthstone.exe). Found automatically when left out.
    [string]$Client,
    [string]$Version = '0.1.0-dev',
    # Build only; do not start the setup program.
    [switch]$NoStart,
    # Where downloads and the build log go. Must be outside the source folder.
    [string]$WorkDir = (Join-Path $env:LOCALAPPDATA 'SoloStone-source-build')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

function Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Fail([string]$text) { Write-Host ''; Write-Host "STOPPED: $text" -ForegroundColor Red; exit 1 }

# --- tools -----------------------------------------------------------------------------------------------------
Step 'Checking tools'
$wanted = (Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail "The .NET SDK $wanted is not installed. Get exactly that version from https://dotnet.microsoft.com/download/dotnet and run this again." }
$sdks = (& dotnet --list-sdks) | ForEach-Object { ($_ -split ' ')[0] }
if ($sdks -notcontains $wanted) { Fail "This source needs the .NET SDK $wanted exactly (global.json pins it). Installed: $($sdks -join ', '). Install $wanted and run this again." }
Write-Host ".NET SDK $wanted"

$python = $null
foreach ($candidate in @(@('python'), @('py', '-3'), @('python3'))) {
    $command = Get-Command $candidate[0] -ErrorAction SilentlyContinue
    if (-not $command) { continue }
    try {
        $rest = @($candidate | Select-Object -Skip 1)
        $answer = & $candidate[0] @rest --version 2>&1
        if ($LASTEXITCODE -eq 0 -and "$answer" -match 'Python 3') { $python = $candidate; break }
    } catch { }
}
if (-not $python) { Fail 'Python 3 is not installed (or only the Microsoft Store stub is). Get it from https://www.python.org/downloads/ and tick "Add python.exe to PATH".' }
Write-Host "Python: $($python -join ' ')"
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'Git is not installed. Get it from https://git-scm.com/download/win.' }

# --- the Hearthstone folder ------------------------------------------------------------------------------------
Step 'Finding Hearthstone'
$managedPart = 'Hearthstone_Data\Managed'
if (-not $Client) {
    $guesses = @()
    foreach ($key in @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone',
                       'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone')) {
        try { $value = (Get-ItemProperty -LiteralPath $key -ErrorAction Stop).InstallLocation; if ($value) { $guesses += $value } } catch { }
    }
    $guesses += @((Join-Path ${env:ProgramFiles(x86)} 'Hearthstone'), (Join-Path $env:ProgramFiles 'Hearthstone'))
    foreach ($guess in $guesses) {
        if ($guess -and (Test-Path -LiteralPath (Join-Path $guess "$managedPart\Assembly-CSharp.dll"))) { $Client = $guess; break }
    }
}
if (-not $Client) { Fail 'Could not find Hearthstone. Run this again with -Client "<the folder that holds Hearthstone.exe>".' }
$Client = [IO.Path]::GetFullPath($Client).TrimEnd('\')
$managed = Join-Path $Client $managedPart
if (-not (Test-Path -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll'))) { Fail "$Client does not look like a Hearthstone folder (no $managedPart\Assembly-CSharp.dll)." }
Write-Host "Hearthstone: $Client"

# SoloStone was written against one client version. Another one may not compile; say so before the long build.
$tested = Get-Content -LiteralPath (Join-Path $repo 'installer\tested-client.json') -Raw | ConvertFrom-Json
$testedHash = $tested.files.'Hearthstone_Data/Managed/Assembly-CSharp.dll'
$mine = (Get-FileHash -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll') -Algorithm SHA256).Hash
if ($testedHash -and ($mine -ine $testedHash)) {
    Write-Host "Your Hearthstone is not the version SoloStone was tested with ($($tested.version).$($tested.build))." -ForegroundColor Yellow
    Write-Host 'The build may fail, or the game may warn or refuse at launch. If it fails, the log named at the end says where.' -ForegroundColor Yellow
}

# --- reference libraries ---------------------------------------------------------------------------------------
Step 'Copying reference libraries (they stay on this machine)'
$gameReferences = Join-Path $repo 'references\game'
$bepReferences = Join-Path $repo 'references\bepinex'
New-Item -ItemType Directory -Force -Path $gameReferences, $bepReferences, $WorkDir | Out-Null
$names = @('Assembly-CSharp.dll', 'blizzard.bgsclient.dll', 'blizzard.bgssdk.dll', 'Blizzard.T5.Core.dll', 'Blizzard.T5.JobQueue.dll',
           'Blizzard.T5.MiniJSON.dll', 'Blizzard.T5.ServiceLocator.dll', 'UnityEngine.dll', 'UnityEngine.CoreModule.dll',
           'UnityEngine.IMGUIModule.dll', 'UnityEngine.InputLegacyModule.dll', 'UnityEngine.PhysicsModule.dll',
           'UnityEngine.ScreenCaptureModule.dll')
foreach ($name in $names) {
    $from = Join-Path $managed $name
    if (-not (Test-Path -LiteralPath $from)) { Fail "Your Hearthstone has no $managedPart\$name. This client version is too different from the one SoloStone builds against." }
    Copy-Item -LiteralPath $from -Destination (Join-Path $gameReferences $name) -Force
}
Write-Host "$($names.Count) files -> references\game"

# --- loader files ----------------------------------------------------------------------------------------------
Step 'Fetching the mod loader (Doorstop, BepInEx, Mono libraries; each file hash-checked)'
$loader = Join-Path $WorkDir 'loader'
if (Test-Path -LiteralPath $loader) { Remove-Item -LiteralPath $loader -Recurse -Force }
$pythonRest = @($python | Select-Object -Skip 1)
& $python[0] @pythonRest (Join-Path $repo 'installer\loader_inputs.py') fetch --out $loader --cache (Join-Path $WorkDir 'loader-cache')
if ($LASTEXITCODE -ne 0) { Fail 'Fetching the loader files failed (see the message above). It needs internet access for this one step.' }
foreach ($name in @('BepInEx.dll', '0Harmony.dll')) {
    Copy-Item -LiteralPath (Join-Path $loader "BepInEx\core\$name") -Destination (Join-Path $bepReferences $name) -Force
}

# --- build -----------------------------------------------------------------------------------------------------
Step 'Building the launcher, the engine, the plugin and the setup (several minutes the first time)'
$log = Join-Path $WorkDir 'build.log'
$build = Join-Path $repo 'installer\build.ps1'
& powershell -NoProfile -ExecutionPolicy Bypass -File $build -LoaderRoot $loader -ClientReference $Client -Version $Version 2>&1 |
    Tee-Object -FilePath $log
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host "STOPPED: the build failed. The full output is in:" -ForegroundColor Red
    Write-Host "  $log"
    Write-Host 'Lines with "error" in them say what went wrong. If your Hearthstone is newer than the tested version, that is the likely cause.'
    exit 1
}

$setup = Join-Path $repo "installer\out\SoloStone-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $setup)) { Fail "The build finished but $setup is missing. See $log." }
Step 'Built'
Write-Host $setup
if ($NoStart) { exit 0 }

Write-Host ''
Write-Host 'Quit Battle.net now (right-click its tray icon, Quit), then press Enter to start the SoloStone setup.' -ForegroundColor Yellow
[void](Read-Host)
Start-Process -FilePath $setup
Write-Host 'Setup started. Windows may show "Windows protected your PC": More info, then Run anyway. When setup finishes, start SoloStone from its shortcut.'
