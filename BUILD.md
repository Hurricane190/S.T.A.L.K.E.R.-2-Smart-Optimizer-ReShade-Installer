# Building from source

## Prerequisites

- Windows 10/11 x64
- .NET SDK **10.0** (the project targets `net10.0-windows`)
- PowerShell 5.1+ (for `build.ps1`)
- No NuGet packages are required; the app uses only the BCL + WPF.

## Layout

```
src/
  ZoneTune.Core/     services/models: hardware, game, INI, mods, ReShade, packages, reports
  ZoneTune/          WPF desktop app (custom dark shell, seven tabs incl. Mod Analyzer, preview window)
  ZoneTune.Cli/      console harness: scan/report/plan/package/selftest/sandbox
```

## Debug build

```powershell
dotnet build src\ZoneTune\ZoneTune.csproj
dotnet run --project src\ZoneTune
```

## Release build (self-contained, single file)

```powershell
.\build.ps1
```

The script:

1. publishes the WPF app self-contained for `win-x64` with a single-file executable,
2. publishes the CLI,
3. copies documentation and notices,
4. writes `dist\checksums.sha256`.

Output:

```
dist\
  ZoneTune.exe
  zonetune-cli.exe
  README.md / INSTALL.md / ERROR_LOGGING.md / THIRD_PARTY_NOTICES.md
  checksums.sha256
```

## Tests

```powershell
# 64 core/functional tests (read-only against the real install; temporary fixtures for writes)
dotnet run --project src\ZoneTune.Cli -- --selftest

# Read-only static mod performance inventory on the detected install
dotnet run --project src\ZoneTune.Cli -- --mod-analyze

# ReShade end-to-end in a temporary game folder:
# install -> official uninstall -> restore -> final rollback
dotnet run --project src\ZoneTune.Cli -- --sandbox-reshade

# Diagnostics
dotnet run --project src\ZoneTune.Cli -- --scan
dotnet run --project src\ZoneTune.Cli -- --recommend
dotnet run --project src\ZoneTune.Cli -- --plan --problems=loud,fps,stutter
dotnet run --project src\ZoneTune.Cli -- --package
dotnet run --project src\ZoneTune.Cli -- --report
```

The self-test never writes to the real game config. The recommended-profile test copies the config to a temp folder and validates apply + restore. `--sandbox-reshade` copies the game executable to `%TEMP%` and bypasses the running-game guard **only for the sandbox target**; the production UI never bypasses it.

## Design notes

- **No WMI dependency**: CPU/GPU/RAM/display detection uses registry + documented Win32 APIs, keeping startup fast and the app dependency-free.
- **INI edits are line-preserving**: comments, repeated keys, casing and inline comments survive. Writes are atomic (`File.Replace` with fallback).
- **Change sets**: every mutation is recorded with existence, SHA-256 before/after and a backup file; restore deletes only files that did not exist before and restores originals byte-for-byte.
- **ReShade**: setup is hash/signature pinned; shaders are extracted only if missing; existing shaders are never overwritten; external presets are not repackaged.
- **Estimates vs. evidence**: hardware profiles and preset costs are labelled estimates. The report importer computes measured statistics from PresentMon CSVs.
- **Threading**: scans and installs run off the UI thread; progress is reported via `IProgress<OperationProgress>`.

## Extending

- Add an optimization rule in `OptimizationEngine` (Core) and expose it through the plan automatically. Include `Rationale`, `Evidence`, `Risk`, `EstimatedEffect`.
- Add a preset in `PresetCatalog.GetBuiltInPresets` with `RawPreset` built by `BuildBuiltInPreset`; use only shader files from the official repository.
- Add a curated third-party preset with `ThirdParty(...)`; never add a direct download URL without an explicit license.
- Reports are plain HTML/Markdown/JSON in `ReportService`; no external assets.

## Known build caveats

- The ReShade setup URL/hash is pinned to 6.8.0. When intentionally upgrading, update `PresetCatalog.ReShadeSetupUrl`, `HttpService.ReShadeSetupSha256` and the expected thumbprint only after verifying the official release.
- The `X509Certificate.CreateFromSignedFile` API is obsolete-but-functional on Windows; the code suppresses the warning and falls back to the certificate loader.
- WPF single-file publish may extract native libraries to a temp folder at first launch (normal).
