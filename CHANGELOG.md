# Changelog

## 1.1.1 — 2026-10-29

- Mod Analyzer: added a persisted **Ignore / Unignore** action for MEDIUM and HIGH risk mods.
- Ignored warnings are excluded from the summary counts and flagged filters, while the full evidence remains available in **Details**.
- Ignore decisions are stored in `%LOCALAPPDATA%\ZoneTune\mod-ignores.json` and survive rescans/app restarts.
- 64/64 self-tests.

## 1.1.0 — 2026-10-29

- New **Mod Performance Analyzer** tab: static inspection of legacy pak and IoStore `.utoc` indexes, Lua patterns, asset categories, native files, config values and cross-mod asset overlaps.
- Per-mod LOW / MEDIUM / HIGH / UNKNOWN assessment with confirmed facts separated from performance hypotheses and explicit limitations. No FPS loss is claimed from file inspection.
- Verified quarantine/restore service: copy → SHA-256 verify → remove original, JSON manifest, collision refusal, tamper/missing detection, interrupted-operation recovery.
- Vortex-managed entries, config overrides and merger output are blocked from quarantine with explanations. UE4SS folders are moved whole without editing `mods.json`/`mods.txt`.
- Manual PresentMon before/after comparison with stored records and per-set attribution warnings.
- ReShade install/repair/remove and the full preset catalog moved into **Preset Studio**; the separate **ReShade Manager** tab was removed.
- UE4SS enabled state now respects `mods.json`, load-order JSON, `enabled.txt`/`disabled.txt` and `.disabled` markers.
- Mod scanner no longer lists paks nested inside a mod folder as duplicate top-level components.
- 63/63 self-tests including fixture-based analyzer, quarantine and comparison coverage.

## 1.0.0 — 2026-10-28

Initial functional release.

- Hardware detection (CPU/GPU/VRAM/RAM/Windows/storage/display/HDR) without WMI.
- Steam/Xbox/manual STALKER 2 detection with `libraryfolders.vdf` parsing.
- Configuration audit: GameUserSettings, Engine.ini, Scalability.ini, OptimaxP, S2AE warmup, ReShade, logs.
- Problem plans for Loud PC, Low FPS and Stuttering with evidence and risk labels.
- Hardware-derived recommended in-game settings profile with one-click Apply, per-setting review, validation and automatic rollback.
- Preset Studio: Potato / Medium / High custom preset generator with effect toggles, sliders, split before/after previews, save/export/install/undo.
- One-click **Undo last change** on the Dashboard and **Restore latest change** in Settings.
- ReShade performance filters: **High performance · low detail**, Balanced, **Ultra detail · high cost**, Bundled, Author pages, Direct download.
- Reversible change sets with SHA-256 manifests and safety snapshots.
- Self-contained ZIP package builder with merged configs, installer/uninstaller, checksums and README.
- ReShade Manager: detection, hardware/HDR checks, bundled + Nexus + GitHub catalog, performance filters, synthetic preview/compare, shader validation, one-click official install, official removal, uninstall/restore.
- Installed Mods inventory and reversible enable/disable; UE4SS, ASI, Vortex and conflict-log detection.
- Settings & Backups: backup root, retention, network/verification policy, restore/delete.
- Diagnostic Reports: HTML/Markdown/JSON export, PresentMon import, before/after comparison, frame-time graph.
- Global error handling and rotating logs.
- 42/42 self-tests + sandboxed ReShade end-to-end test.
