# Test results — 2026-10-28

> **2026-10-29 update:** the application now has a **Mod Performance Analyzer** tab and the ReShade Manager was merged into Preset Studio. The current self-test baseline is **64/64 PASS**, including fixture-based mod discovery, risk classification, quarantine/restore, corruption handling, interrupted-operation recovery and manual comparison tests. The historical 1.0.0 results below are retained for traceability.

## 2026-10-29 — Mod Analyzer / Preset Studio update

Commands executed:

```powershell
dotnet build ZoneTune.slnx -c Release
dotnet run --project src\ZoneTune.Cli -c Release -- --selftest
dotnet run --project src\ZoneTune.Cli -c Release -- --mod-analyze
ZoneTune.exe --ui-smoke
```

Results:

- Release build: **0 warnings, 0 errors**.
- Self-test: **64/64 PASS**.
- Mod Analyzer Ignore/Unignore persistence: **PASS** (path-based record survives a reload; Unignore restores the warning).
- UI smoke: all seven tabs instantiated and refreshed: Dashboard, Optimize, Preset Studio, Installed Mods, Mod Analyzer, Settings & Backups, Diagnostic Reports. Exit code 0; no new crash log.
- Single-file `win-x64` publish to a temporary folder: succeeded; the published executable also passed `--ui-smoke` with exit code 0.
- Full `.\build.ps1` release pipeline: 64/64 self-tests passed, self-contained publish completed.
- `dist\ZoneTune.exe` 134.1 MB, `dist\zonetune-cli.exe` 70.7 MB, version `1.1.1`.
- Nexus package `ZoneTune-1.1.1-Nexus.zip` (56.5 MB, SHA-256 `8596698f496dd57fa5ee2a6b3d33f762fead1cfb90af2672c77d25e59eedc4d4`); extracted package passed checksum verification and `--ui-smoke`.
- Real installation read-only analyzer run: **66 inventory entries, 60 enabled, 65 containers read, 19 unreadable, 2 HIGH / 8 MEDIUM / 14 UNKNOWN, 2 cross-mod overlap notes** in under 1 s.
- The High results were UE4SS Lua mods with source-level high-frequency loops plus actor queries (50 ms). They are reported as strong static hypotheses, not measured FPS losses.
- Overlap examples were detected between two VFX mods (6 shared asset paths) and two SFX mods (2 shared asset paths).
- `--apply-selftest` (live config write/restore) and `--sandbox-reshade` (network download/install) were not rerun in this change because they touch the live config or download binaries. The existing 1.0.0 evidence for those paths is retained; the changed code does not touch them in production.

Fixture tests cover:

- legacy pak indexing, loose pak discovery, IoStore index parsing, corrupt pak handling
- UE4SS `mods.json` state, native-only UNKNOWN, Vortex marker refusal, version detection
- repeat scans without duplicates, overlap detection
- quarantine copy/hash/remove, directory and loose-pak restore round-trips, restore collision refusal, tamper detection, interrupted-operation reconciliation
- persisted Ignore/Unignore decisions for Medium/High warnings
- refusal to move paths outside `~mods` / UE4SS Mods / top-level ASI
- manual PresentMon comparison storage and caveats

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

- all seven tabs instantiated and rendered at the time: Dashboard, Optimize, ReShade Manager, Preset Studio, Installed Mods, Settings & Backups, Diagnostic Reports (ReShade Manager was later merged into Preset Studio)
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
