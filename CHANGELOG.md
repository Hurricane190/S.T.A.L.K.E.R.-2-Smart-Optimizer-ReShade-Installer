# Changelog

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
