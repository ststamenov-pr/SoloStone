# Building SoloStone

SoloStone is an unofficial offline single-player mod for Hearthstone®. It requires your own legally obtained Hearthstone client. Not affiliated with, endorsed by, or sponsored by Blizzard Entertainment. Hearthstone is a trademark or registered trademark of Blizzard Entertainment, Inc.

This page builds the launcher, the plugin, the engine and the Windows installer from this repository. Run every
command from the repository root.

The repository contains no Hearthstone program files, art or sound. To compile the plugin you need **your own Hearthstone client** as
reference libraries (step 1). Without one you can still build and test the launcher (step 3).

The card and rules test suites are not published yet. What is here are the launcher tests and the build checks below.

## The short way, on Windows

If you only want to build this source and play it, one script does every step below on a Windows 10 or 11 machine
that has Hearthstone installed, the .NET SDK 10.0.402, Python 3 and Git:

```text
powershell -ExecutionPolicy Bypass -File tools\run-from-source.ps1
```

It finds your Hearthstone folder (or takes `-Client "<folder>"`), copies the reference libraries into `references\`,
fetches and hash-checks the loader files, runs `installer\build.ps1` and starts the setup program it built
(`installer\out\SoloStone-Setup-0.1.0-dev.exe`). `-NoStart` stops after the build. The full build output goes to
`%LOCALAPPDATA%\SoloStone-source-build\build.log`. Your Hearthstone folder is only read.

The rest of this page is the same thing step by step.

## What you need

| Need | Detail |
|---|---|
| .NET SDK | **10.0.402**. `global.json` pins it and refuses any other version, so install exactly that one. |
| Python 3 | On your `PATH` as `python3`, for `installer/stage.py` and the loader fetch tool. The engine and plugin builds themselves need only the .NET SDK. |
| Git | `stage.py` records the source revision with it, so build from a Git checkout. |
| Windows 10 or 11 | Only for the last step (the installer). Steps 1 to 4 also run on macOS (tested); Linux is untested. |
| Hearthstone client | The version SoloStone was tested with (named in `installer/tested-client.json`). Its `Hearthstone_Data\Managed` folder is the plugin's reference set. A different client may not compile or may not match our release. |
| Internet access | `stage.py` downloads Doorstop 4.5.0, BepInEx 5.4.23.5 and the Mono class libraries named in `installer/loader-inputs.json` and checks every file against its recorded SHA-256. Nothing is taken from your machine for these. Origins and licences are in [NOTICE](../NOTICE). |
| Inno Setup 6.4.3 | `build.ps1` restores it from NuGet (`Tools.InnoSetup`), or pass your own `ISCC.exe` with `-Iscc`. |

## 1. Reference libraries

Create two folders and fill them. These files are not in the repository. Do not commit them or anything built from them.

`references/game/` takes these files from the client's `Hearthstone_Data\Managed` folder:

```text
Assembly-CSharp.dll              blizzard.bgsclient.dll           blizzard.bgssdk.dll
Blizzard.T5.Core.dll             Blizzard.T5.JobQueue.dll         Blizzard.T5.MiniJSON.dll
Blizzard.T5.ServiceLocator.dll   UnityEngine.dll                  UnityEngine.CoreModule.dll
UnityEngine.IMGUIModule.dll      UnityEngine.InputLegacyModule.dll
UnityEngine.PhysicsModule.dll    UnityEngine.ScreenCaptureModule.dll
```

`references/bepinex/` takes `BepInEx.dll` and `0Harmony.dll`. They are the ones from the BepInEx 5.4.23.5 release
that the launcher ships (hashes in `installer/loader-inputs.json`). Fetch and check them with the same tool the
stage uses:

```text
python3 installer/loader_inputs.py fetch --out /tmp/solostone-loader
mkdir -p references/bepinex
cp /tmp/solostone-loader/BepInEx/core/BepInEx.dll /tmp/solostone-loader/BepInEx/core/0Harmony.dll references/bepinex/
```

## 2. Build the engine and the plugin

```text
dotnet build plugin/OfflineProbe.csproj -c Debug -p:EngineSource=true -p:Installer=true -p:NuGetAudit=false
```

`EngineSource=true` builds the SabberStone-based engine in `engine/` and ships it with the plugin. `Installer=true`
builds the plugin as the installer version, which only starts inside a marked SoloStone copy. We ship the **Debug**
build of the engine because it keeps the exception behaviour the game needs.

## 3. Test the launcher

```text
dotnet run --project installer/SoloStone.Launcher.Tests -c Release
```

Some tests need the reference libraries from step 1 (in `references/`, and the client must be the one named in
`installer/tested-client.json`) and are skipped without them. To make skips fail, as our checks do, run the whole set
with `installer/test-all.sh`. It uses the `dotnet` on your `PATH`, or the one in `DOTNET`:

```text
sh installer/test-all.sh
```

## 4. Stage a release

`stage.py` builds the launcher and the plugin and checks that a release build holds no test hooks. It refuses an
output folder inside your checkout, or one that contains it. Any other folder works, for example
`/tmp/solostone-stage`. It uses the reference folders from step 1; `--references <folder>` points it at another folder
that holds `game/` and `bepinex/`.

```text
python3 installer/stage.py --dotnet "$(command -v dotnet)" --out <stage folder> --version <version>
```

For a build that can check for updates, add the **public** keys and the repository name. These are build inputs
and must never be private keys:

```text
--root-key RW... --signing-key RW... --repo <owner/name> --seq-floor <n>
```

Without them, updates are not configured and everything else works.

For a release build also set `SOLOSTONE_TOOL_DLL` before staging, so the stage runs the Mono check (it makes sure
the plugin only uses things Unity's Mono has). Without it the stage prints "Mono guard skipped". `test-all.sh` from
step 3 builds the file it needs:

```text
export SOLOSTONE_TOOL_DLL="$PWD/installer/out/gate/bin/SoloStone.Launcher.Tests/release/SoloStone.Launcher.Tests.dll"
```

## 5. Finish on Windows: loader files, update zip and installer

Copy the stage folder to a Windows 10 or 11 machine that has a checkout of the same tag. In PowerShell, with the
copied folder as `<stage folder>` (the second line is for the default execution policy, which blocks scripts):

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build.ps1 -Staged <stage folder> -Version <version> `
    -ClientReference <your Hearthstone folder>
```

The stage folder already holds the loader files (`<stage folder>\loader`, fetched and hash-checked in step 4), and
`build.ps1` checks again that the folder holds exactly the files in `installer\loader-inputs.json`. To use a folder
you fetched yourself, add `-LoaderRoot <folder>`.

What it does: adds the loader files, writes `payload.json`, checks that no Hearthstone client file and no stray
program is in the payload (every payload file is compared by hash with `-ClientReference`), builds the update zip
`SoloStone-<version>.zip` and the setup `SoloStone-Setup-<version>.exe`, and writes `build-result.json` into
`installer\out`. A release build ends with `SoloStone.exe check-release`. **Never publish a build that has not
passed it.**

Every download carries `NOTICE`, `LICENSE`, `LICENSE-ADDITIONAL-PERMISSION.md`, `LICENSES\` and a `SOURCE.txt` that says
where this source is (made from `installer/SOURCE.template.txt` and the `--repo` you gave), in its app folder.
`check-release` refuses a build without them.

Without `-Staged`, `build.ps1` builds the launcher, runs the launcher tests and builds the plugin itself. It then
needs the reference folders from step 1, or `-References <folder>` for a folder that holds `game\` and `bepinex\`.

## Reproducing a release

The release notes record the tool versions and the hash of every reference file that built the download. Our
download is byte-identical to yours only if your reference files are byte-identical to those. Older Hearthstone
clients are not available from us, so for most people a rebuild runs the same source but will not match the
published hashes.

## Known gaps

- Tests for cards and rules are not in this repository yet.
- The release hashes cannot be reproduced without the same reference client files.
- Signing a release (the update signature) is done by the maintainer with a private key that is not in any build.
