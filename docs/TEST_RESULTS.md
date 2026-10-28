# Test results — 2026-10-28

Environment:

- Windows 11 Home 26H2, build 26300, x64
- AMD Ryzen 5 7600X (6C/12T), 32 GB RAM
- NVIDIA GeForce RTX 4070 Ti SUPER, 16 GB VRAM, driver 32.0.16.2101
- 2560×1440 @ 165 Hz, HDR capable (off during tests)
- STALKER 2 Steam install at `D:\SteamLibrary\steamapps\common\S.T.A.L.K.E.R. 2 Heart of Chornobyl`
- 147 mod components, ReShade 6.8.0.2155 via `dxgi.dll`, UE4SS, ZoneEssentials/TrueLedgeGrab ASI plugins, Vortex metadata

## 1. Core self-test — 42/42 PASS

Command:

```powershell
dotnet run --project src\ZoneTune.Cli -c Release -- --selftest
```

Covered:

- comment/casing-preserving INI round-trip and atomic save
- CPU/GPU/VRAM/RAM/display/Windows detection
- Steam/manual game detection and config/discovery paths
- mod inventory + conflict notes
- ReShade detection, preset techniques, shader references
- bundled preset parse/validation (3 presets)
- third-party catalog is author-page-only (no unauthorized downloads)
- local preset import parser
- problem plan generation, evidence labels, non-destructive actions
- recommended profile generation and 0–3 clamping
- recommended profile apply → validation → hash-exact restore in a sandbox
- package ZIP structure, merged values, installer/uninstaller/checksums, no binaries
- backup/restore hash exactness and removal of created files
- reversible mod disable/enable (directory + pak triple) — no deletion
- Preset Studio Potato/Medium/High generation, shader validation, no-effect fallback
- PresentMon CSV statistics (1000 synthetic frames)
- HTML/Markdown/JSON report export
- official shader archive contents
- official ReShade setup hash + Authenticode (pinned thumbprint)

## 2. Real apply/restore test — PASS

Command:

```powershell
dotnet run --project src\ZoneTune.Cli -- --apply-selftest
```

Result:

- 9 recommended settings applied to the live `GameUserSettings.ini`/`Engine.ini`
- every value read back and verified
- restore succeeded
- `GameUserSettings.ini` SHA-256 identical to the pre-test file
- `Engine.ini` absent again (it did not exist before)
- no residual test settings

This exercises the exact one-click Apply path used by the UI.

## 3. ReShade end-to-end sandbox — PASS

Command:

```powershell
dotnet run --project src\ZoneTune.Cli -- --sandbox-reshade
```

The test copies the game executable to `%TEMP%\ZoneTune\SandboxGame` and never touches the live install.

Verified:

- downloaded the official ReShade 6.8 setup
- pinned SHA-256 + certificate thumbprint verification
- headless install (`--api dxgi`)
- ReShade module detected (6.8.0.2158) via `dxgi.dll`
- only required shaders extracted (custom High preset: 6 technique files + `ReShade.fxh`/`ReShadeUI.fxh`, including `Deband.fx` from the official slim archive)
- preset installed and `ReShade.ini` updated
- structural validation: all referenced shaders present
- official headless uninstall removed the module
- restoring the removal change set brought ReShade back
- uninstalling the original change set removed every created file
- no files left behind in the sandbox

## 4. Package installer/uninstaller — PASS

Built a package with `--package`, extracted it, redirected `LOCALAPPDATA` to a temporary folder, and ran `install.ps1` / `uninstall.ps1`:

- installed merged `GameUserSettings.ini` and new `Engine.ini`
- created `backup-<timestamp>` with manifest
- uninstall restored the original `GameUserSettings.ini` SHA-256
- removed the created `Engine.ini`
- refused-to-run guard is present for a running game

## 5. Preset Studio — PASS

- Three baselines generated: Potato, Medium, High; all referenced shaders present
- Custom High preset installed in the ReShade sandbox: 8 shader files, structural validation passed
- Split before/after thumbnails and full preview render
- Save/export/install/undo controls present

## 6. UI smoke test — PASS

- all seven tabs instantiated and rendered: Dashboard, Optimize, ReShade Manager, Preset Studio, Installed Mods, Settings & Backups, Diagnostic Reports
- preset preview window rendered before/after + comparison
- recommended-settings review sheet rendered rows with current/recommended values
- no new `crash-*.log` entries during navigation
- published self-contained `ZoneTune.exe` launched, scanned the real install and displayed 15 findings

## 7. Release artifact

- `dist\ZoneTune.exe` — self-contained, single-file, 133.8 MB
- `dist\zonetune-cli.exe` — 70.5 MB
- `dist\checksums.sha256` verified after build
- `dist\zonetune-cli.exe --selftest` → 40/40 PASS

## Known untested / limitations

- No visual in-game validation of ReShade presets; compatibility is structural only.
- No automated Nexus Mods download/install (author-controlled distribution); author pages + local import are provided.
- The in-game upscaler selector is recommended, not written by config.
- Frame-rate/noise effects were not benchmarked on this machine; no gain is claimed. PresentMon import is provided for the user’s own measurements.
- Xbox/MSIX layout is supported by the locator/validator but was not tested with an actual Xbox install.
