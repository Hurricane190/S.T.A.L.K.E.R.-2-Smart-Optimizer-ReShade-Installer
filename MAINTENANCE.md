# Maintenance, Updates & Troubleshooting

**Project:** ZoneTune — STALKER 2 Smart Optimizer  
**Version:** 1.0.0  
**Repo:** https://github.com/Hurricane190/S.T.A.L.K.E.R.-2-Smart-Optimizer-ReShade-Installer  
**Latest release:** https://github.com/Hurricane190/S.T.A.L.K.E.R.-2-Smart-Optimizer-ReShade-Installer/releases/latest  
**Current release tag:** `v1.0.0`

This file is the operational handbook. Keep it with the source; it covers how to rebuild, release, update, recover, and where to look when something breaks.

---

## 1. Folder layout

After cleanup, the project intentionally contains no `dist/`, `bin/`, or `obj/` folders. Everything below is either source, documentation, or Git history.

```text
Smart Optimizer/
  .git/                     Git history and origin remote (keep)
  .gitattributes
  .gitignore
  README.md                 project overview + screenshots
  INSTALL.md                end-user installation and undo steps
  BUILD.md                  build/test instructions
  MAINTENANCE.md            this file
  ERROR_LOGGING.md          log/crash locations and reporting
  THIRD_PARTY_NOTICES.md
  CHANGELOG.md
  LICENSE
  ZoneTune.slnx
  build.ps1
  docs/
    TEST_RESULTS.md
    screenshots/
  src/
    ZoneTune.Core/          detection, config, mods, ReShade, plans, packages, reports
    ZoneTune/               WPF app (7 tabs)
    ZoneTune.Cli/           scan/report/plan/package/self-test/sandbox tools
```

Regenerable and intentionally deleted:

- `dist/` — publish output (200+ MB, also attached to GitHub Releases)
- `src/*/bin/` and `src/*/obj/` — compiler output
- `*.zip`, crash dumps, test outputs — ignored by `.gitignore`

Do **not** delete `.git/`; it is the update history and remote tracking.

---

## 2. Prerequisites

- Windows 10/11 x64
- .NET SDK **10.0** (`dotnet --list-sdks`)
- PowerShell 5.1+
- Git and GitHub CLI (`gh`) for publishing
- STALKER 2 installed for full end-to-end tests (optional for build)
- Game closed for any write/install test

No NuGet packages are required.

---

## 3. Build and run

From the project folder:

```powershell
# Debug run (WPF UI)
dotnet run --project src\ZoneTune

# Release + self-contained single-file publish
.\build.ps1

# Publish without re-running the test suite
.\build.ps1 -SkipTests

# Framework-dependent publish (smaller, requires .NET 10 Desktop Runtime)
.\build.ps1 -FrameworkDependent -SkipTests
```

Output returns to `dist/`:

- `ZoneTune.exe` — self-contained app
- `zonetune-cli.exe`
- docs, screenshots, `checksums.sha256`

---

## 4. Tests before every release

```powershell
# 42 core/functional tests; does not modify the real game config
dotnet run --project src\ZoneTune.Cli -- --selftest

# Real apply test: hash config -> apply recommended settings -> verify -> restore
# Requires STALKER 2 closed. Leaves the original config byte-identical.
dotnet run --project src\ZoneTune.Cli -- --apply-selftest

# ReShade install/remove/restore in %TEMP%; never touches the real game
dotnet run --project src\ZoneTune.Cli -- --sandbox-reshade
```

Expected current baseline: **64/64** self-tests pass; real apply and ReShade sandbox both PASS.

Diagnostics:

```powershell
dotnet run --project src\ZoneTune.Cli -- --scan
dotnet run --project src\ZoneTune.Cli -- --recommend
dotnet run --project src\ZoneTune.Cli -- --plan --problems=loud,fps,stutter
dotnet run --project src\ZoneTune.Cli -- --package
dotnet run --project src\ZoneTune.Cli -- --report
```

---

## 5. Release / update workflow

1. `git pull origin main` and confirm a clean `git status`.
2. Bump the version in:
   - `src/ZoneTune.Core/AppPaths.cs` (`Version`)
   - `src/ZoneTune/ZoneTune.csproj` (`Version`, `FileVersion`)
3. Update:
   - `CHANGELOG.md`
   - `README.md` if features/limits changed
   - `INSTALL.md` / `ERROR_LOGGING.md` if user steps changed
   - `docs/TEST_RESULTS.md` with fresh evidence
   - screenshots under `docs/screenshots/` if UI changed
4. Run all three test commands above.
5. `.\build.ps1` (includes self-tests).
6. Commit source changes only:
   ```powershell
   git add .
   git commit -m "Release vX.Y.Z: ..."
   git push origin main
   ```
7. Tag and release:
   ```powershell
   git tag vX.Y.Z
   git push origin vX.Y.Z
   gh release create vX.Y.Z "dist\ZoneTune.exe" "dist\zonetune-cli.exe" "dist\checksums.sha256" `
     --title "ZoneTune X.Y.Z" --notes "See CHANGELOG.md"
   ```
8. Verify the release page has all three assets and `checksums.sha256` matches.

**Do not commit `dist/`, `bin/`, `obj/`, test ZIPs or crash dumps.** The self-contained EXE is over GitHub’s 100 MB per-file normal limit, so binaries belong in Releases, not the repository. `.gitignore` already enforces this.

If a release is wrong:

```powershell
gh release delete vX.Y.Z --yes
git push origin :refs/tags/vX.Y.Z
git tag -d vX.Y.Z
# fix, rebuild, retag, release again
```

---

## 6. Where app data lives

| Path | Contents |
|---|---|
| `%LOCALAPPDATA%\ZoneTune\Logs` | `zonetune.log`, rotated logs, `crash-*.log`, `selftest.txt` |
| `%LOCALAPPDATA%\ZoneTune\Backups` | change sets (original files + SHA-256 manifests) |
| `%LOCALAPPDATA%\ZoneTune\ChangeSets` | legacy/alternate change-set metadata |
| `%LOCALAPPDATA%\ZoneTune\Packages` | generated optimization ZIPs |
| `%LOCALAPPDATA%\ZoneTune\Reports` | HTML / Markdown / JSON reports |
| `%LOCALAPPDATA%\ZoneTune\Presets` | presets saved by Preset Studio |
| `%LOCALAPPDATA%\ZoneTune\Downloads` | cached official ReShade setup + shader archives |
| `%LOCALAPPDATA%\ZoneTune\settings.json` | game path, backup root, network policy |
| `%LOCALAPPDATA%\ZoneTune\mod-state.json` | reversible mod move records (only if used) |
| `%LOCALAPPDATA%\Stalker2\Saved\Config\Windows` | the game’s live configs |
| `%LOCALAPPDATA%\Stalker2\Saved\Logs` | game logs and mod conflict reports |

Never delete a change set before you are sure you no longer need to restore the game. The folders are safe to rename (not delete) for a clean reset:

```powershell
Rename-Item "$env:LOCALAPPDATA\ZoneTune" "ZoneTune.old"
```

The app recreates defaults on next launch. Keep `ZoneTune.old` until the game is verified.

---

## 7. Troubleshooting quick reference

### App problems
- **App won’t start:** run `src\ZoneTune\bin\Debug\net10.0-windows\ZoneTune.exe` from a terminal to see errors; check `%LOCALAPPDATA%\ZoneTune\Logs\crash-*.log`.
- **Multiple instances / “already running”:** close all `ZoneTune.exe` processes; the app uses a global mutex.
- **Game not detected:** Settings & Backups → Game folder → Browse; select the folder containing `Stalker2.exe`. Xbox/MSIX installs may need the `Content` parent.
- **“Exit STALKER 2”:** check Task Manager for `Stalker2-Win64-Shipping.exe`; also close the Zone Kit editor.
- **Everything looks reset:** restore the newest change set from Settings & Backups; check `settings.json` was not replaced.

### Settings / config
- **Settings seem ignored:** another config mod (OptimaxP) may set the same cvars; check Optimize warnings, or compare `Engine.ini`.
- **Apply failed:** the app attempts automatic rollback. Open the log; use Settings & Backups → Restore on the relevant change set.
- **Want the original back:** Dashboard → Undo last change, or Settings & Backups → Restore latest. Restores are byte-exact and create a safety snapshot.
- **Package install failed:** extract the ZIP and run `uninstall.ps1`; it restores `backup-<timestamp>` and removes files it created.

### ReShade
- **Preset is not visible in-game:** check `Stalker2\Binaries\Win64\ReShade.log`, press Home, ensure only one graphics proxy (`dxgi.dll`) exists.
- **ReShade removed but game looks wrong:** Preset Studio → Restore removed ReShade / Settings & Backups → Restore.
- **Preset artifact / missing shaders:** Preset Studio will report missing `.fx` files. Delete `%LOCALAPPDATA%\ZoneTune\Downloads\reshade-shaders-*.zip` and reinstall to re-fetch.
- **Signature/hash failure:** the official ReShade setup changed or the download is altered. Delete the cached setup and retry. Do not run a file that fails the pinned SHA-256/thumbprint check.
- **HDR washed out:** disable in-game HDR; ReShade presets generally assume SDR.
- **Preset Studio Deband missing:** it comes from the official slim shader archive; if extraction failed, delete the shader caches above and retry. The sandbox test covers this path.
- **Want to remove a preset you dislike:** Preset Studio → Undo last preset install; Preset Studio → Uninstall & restore; or Remove ReShade. Manual installs outside the app are not tracked.
- **Mod quarantine looks wrong:** Mod Analyzer → Restore quarantined mods. The manifest is `%LOCALAPPDATA%\ZoneTune\ModQuarantine\quarantine-manifest.json`; never delete it manually while entries are active.

### Mods
- Disable moves files to `~mods.disabled`; Enable restores them. Nothing is deleted.
- If Vortex is detected, disable the mod in Vortex too or it may redeploy.
- If `mod-state.json` is lost, ReShade/Mods can still be recovered manually from `~mods.disabled`.

### Measurement
- “No measurement” is expected until you import PresentMon CSV.
- Use the same save/route/settings and at least 60 seconds after warm-up.
- Compare 1% / 0.1% lows and p99 frame time, not just average FPS.

---

## 8. Source map for future changes

| Change | File(s) |
|---|---|
| Version/name/paths | `src/ZoneTune.Core/AppPaths.cs` |
| Hardware/display/HDR detection | `src/ZoneTune.Core/Services/HardwareService.cs` |
| Game/Steam detection | `src/ZoneTune.Core/Services/GameLocator.cs` |
| Config read/write | `src/ZoneTune.Core/Services/ConfigService.cs`, `IniDocument.cs` |
| Problem rules / recommended profile | `src/ZoneTune.Core/Services/OptimizationEngine.cs` |
| Scan findings / log analysis | `src/ZoneTune.Core/Services/ScanService.cs` |
| Mods inventory / reversible moves | `ModScanner.cs`, `ModService.cs` |
| ReShade detection / validation | `ReShadeService.cs` |
| ReShade install/remove | `ReShadeInstaller.cs` |
| Preset catalog / Nexus metadata | `PresetCatalog.cs` |
| Preset Studio generator | `CustomPresetBuilder.cs` |
| Package ZIP + installer scripts | `PackageBuilder.cs` |
| Backups/restore | `BackupService.cs` |
| Reports / PresentMon | `ReportService.cs`, `MeasurementService.cs` |
| UI tabs | `src/ZoneTune/UI/Views/*.xaml(.cs)` |
| ReShade pinned URL/hash/thumbprint | `PresetCatalog.ReShadeSetupUrl`, `HttpService.ReShadeSetupSha256`, `HttpService.ReShadeThumbprint` |

### Safety invariants to preserve

1. Never delete mods; move and record.
2. Never write a game file without a change set.
3. Never apply while `Stalker2-Win64-Shipping.exe` is running.
4. Never claim “verified” without distinguishing structural validation from visual testing.
5. Never promise FPS/noise gains without measurement.
6. Never auto-download author-restricted presets; link to the author page instead.
7. Keep the official ReShade setup pinned/verified before execution.

---

## 9. Current release verification (v1.0.0)

```text
SHA-256
dbd0c5a85e8f1bdc5455e9f427d0ebab1596765c1657bcb5242b384fc715bf38  ZoneTune.exe
422834cb8dd1ee069f9c3114778eb73479ae90ef64574290a109d2e0bbe79f50  zonetune-cli.exe
```

Verify a downloaded release:

```powershell
Get-FileHash .\ZoneTune.exe -Algorithm SHA256
```

If the hash differs, do not run it; download again from the release page.

---

## 10. Pre-release checklist

- [ ] `git status` clean
- [ ] Version bumped in both files
- [ ] CHANGELOG updated
- [ ] 64/64 self-tests pass
- [ ] `--apply-selftest` PASS (original config byte-identical)
- [ ] `--sandbox-reshade` PASS
- [ ] UI smoke test: 7 tabs, Preset Studio, review sheet, preview window
- [ ] `build.ps1` succeeds
- [ ] `dist/checksums.sha256` verified
- [ ] commit and push
- [ ] tag + GitHub release with EXE, CLI, checksums
- [ ] release assets downloadable
