# Mod Performance Analyzer — feasibility report

Date: 2026-10-29  
Project: ZoneTune 1.0.0 (WPF, net10.0-windows)  
Inspected installation: `D:\SteamLibrary\steamapps\common\S.T.A.L.K.E.R. 2 Heart of Chornobyl`

Status: **implemented** as the Mod Analyzer tab; see [MOD_ANALYZER.md](MOD_ANALYZER.md) for usage and the final limitations.

This report was written before implementation. It records what the existing application did, what the installed mod formats actually look like, what can be measured statically, and what cannot.

## 1. Existing architecture

| Area | Implementation |
|---|---|
| UI | WPF, custom dark shell, `UserControl` tabs (seven at the time of this report; ReShade Manager was later merged into Preset Studio and Mod Analyzer added), code-behind views built on `UiKit` |
| Core | `ZoneTune.Core` class library with services and models; no external packages |
| Mod inventory | `ModScanner` (read-only) + `ModService` (reversible enable/disable) |
| Game discovery | `GameLocator` (Steam manifest, Xbox/MSIX layout, manual path) |
| Diagnostics | `ScanService`, `OptimizationEngine`, `ReportService` |
| Measurements | `MeasurementService` imports PresentMon CSV and computes frame-time statistics |

The existing mod inventory already detects:

- loose `.pak` files and pak folders in `Stalker2\Content\Paks\~mods`
- pak/utoc/ucas triples (but does not read their contents)
- UE4SS Lua folders under `Binaries\Win64\ue4ss\Mods`
- ASI plugins in `Binaries\Win64`
- `~mods.disabled` entries
- Vortex/load-order metadata files
- merger output (`~S2MUU`) and OptimaxP config overrides

It does not inspect archive contents, Lua code, asset types, overlaps inside archives, or native plugins. The new analyzer can be added beside this inventory without changing the existing scan or disable/enable flow.

## 2. What is actually installed (evidence)

The live installation contains 43 entries in `~mods`, including:

- 35+ folders, some with `__folder_managed_by_vortex` and `.vortex_backup` markers
- IoStore triples: `*.pak` + `*.utoc` + `*.ucas` (UTOC version 8)
- legacy standalone paks with an Unreal pak footer (version 8)
- `~S2MUU\S2MUU_Merged_9999_P.pak` merger output
- UE4SS mods with `mods.json`, `mods.txt`, `enabled.txt`, `disabled.txt`, `.disabled` DLLs, Lua scripts and native `main.dll` files
- ASI plugins (`ZoneEssentials.asi`, `TrueLedgeGrab.asi`) in `Binaries\Win64`
- Vortex-managed folders that Vortex will redeploy if files are moved externally

Key format findings:

1. **Legacy `.pak`** uses the standard Unreal footer (`0x5A6F12E1`) with an index at the end. The index is readable for normal (unencrypted) paks. The footer may be followed by padding, so the footer must be located by scanning the tail, not by a fixed offset.
2. **IoStore `.utoc`** contains a readable directory index. Its layout is version 8: header, TOC entries, compressed-block table, compression method names, directory index (mount point + directory/file tables + string table) and metadata arrays. The index is not encrypted in the inspected mods. It exposes full asset paths and extensions, which is enough for asset-type and overlap analysis.
3. **`.ucas` payloads** hold the bulk data. They must not be read for routine scanning: a single mod can be ~865 MB. Only the small TOC/index is needed.
4. **Some standalone `.pak` files are encrypted or tightly packed**; the index cannot always be parsed. Such mods must be reported as `UNKNOWN / INSUFFICIENT EVIDENCE`, not guessed at.
5. **UE4SS enable state is not determined by `mods.txt` alone.** In this installation `mods.txt` is empty, while `mods.json` and a UE4SS load-order JSON define actual state. The existing `ModScanner` may mark such mods as enabled when they are not. The analyzer must not repeat that mistake; it should resolve state from `enabled.txt` / `disabled.txt` / `.disabled` extensions first, then `mods.json` / load-order metadata, then `mods.txt`.
6. **Vortex management is reliably detectable** from `__folder_managed_by_vortex` and `.vortex_backup`, even though `vortex.deployment.*` was not present in the inspected `Binaries` folder. The existing Vortex check is therefore incomplete for quarantine purposes.

## 3. What can be analyzed reliably

### Reliable (technical facts)

- container type, count, size and readability
- exact asset paths from readable UTOC/pak indexes
- asset extension and category counts (textures, meshes, audio, VFX/shader, AI/spawn path names)
- uncompressed entry sizes from readable legacy pak indexes
- Lua source: files, code size, hook registrations, loop intervals, actor-enumeration/spawn calls
- native binaries present (but not their runtime behaviour)
- Vortex markers, UE4SS state files, merger outputs
- overlaps: the same normalized asset path appearing in two mods (exact when both indexes are readable)
- sizes relative to the rest of the installed mod set

### Hypotheses only (never presented as measured facts)

- a Lua tick hook or 50 ms loop costs frame time
- many texture assets exceed VRAM on this GPU
- VFX/shader assets increase GPU load or cause PSO hitches
- AI/spawn code increases simulation workload
- a large asset count increases load time or streaming pressure
- overlapping files cause duplicate work or a conflict
- disabling a mod will improve FPS, frame time or stutter

### Not possible from static files

- actual FPS, frame time, 1% lows, stutter
- actual VRAM/RAM usage or CPU/GPU utilization
- how often a Lua hook actually runs, or whether its config disables it at runtime
- runtime effect of native `.dll` / `.asi` code
- visual quality, correctness, or whether an asset is ever loaded
- per-mod attribution when several mods are disabled together
- reliable automated in-game benchmarking (no scripted route/telemetry integration, and game state is not controllable safely)

File size alone is explicitly **not** treated as evidence of poor performance.

## 4. Safe quarantine feasibility

Feasible with these rules:

- copy to a quarantine folder, verify SHA-256 of every file, then remove the original from the load path
- keep a JSON manifest with original path, quarantine path, file list, hashes, timestamps and status
- restore only when the original path is free; never overwrite
- refuse Vortex-managed mods (Vortex would redeploy them and its metadata would become inconsistent)
- refuse config overrides that live in the game config folder
- treat UE4SS specially: move the whole mod folder only, never edit `mods.json` / `mods.txt`; warn that UE4SS may log a missing-mod entry until restored
- treat merger output specially: it is generated from multiple source mods; automatic quarantine is blocked with an explanation
- handle cross-volume moves (game on D:, quarantine in `%LOCALAPPDATA%` on C:)
- check free space before copying
- detect interrupted operations from manifest states and reconcile on next load

## 5. Performance comparison feasibility

Reliable in-game benchmarking is **not** feasible in this application:

- no supported telemetry/automation hook into STALKER 2
- no controlled route/scene driver
- PresentMon may not be installed
- GPU/CPU counters alone do not give FPS or frame pacing

The honest implementation is a manual PresentMon workflow using the existing `MeasurementService`:

1. capture a baseline with the selected mods enabled
2. quarantine/disable the selected mods
3. capture the same route/settings again
4. import both CSVs and compare average FPS, 1%/0.1% lows, median/p95/p99 frame times and stutter index

The result applies to the tested set of mods, not to each mod individually. ZoneTune must state that it did not fabricate or attribute the result.

## 6. Implementation risks and mitigations

| Risk | Mitigation |
|---|---|
| Misidentifying a mod as a performance problem | Separate confirmed evidence from hypotheses; require multiple indicators for HIGH; no FPS claims |
| Moving Vortex files | Detect `__folder_managed_by_vortex` / `.vortex_backup`; block quarantine with instructions |
| Breaking UE4SS load order | Move whole folder only; never rewrite `mods.json`/`mods.txt`; warn |
| Data loss on interruption | Copy → hash verify → manifest state → delete original; restore verifies hashes; no overwrite |
| Reading huge archives | Read only UTOC/legacy index, never `.ucas` bulk; cap TOC size |
| Duplicate scan entries | Deduplicate by normalized path; cache by path + size + timestamp |
| Corrupt/encrypted archives | Catch errors per container; report UNKNOWN; never crash the scan |
| UI freeze on 100+ mods | Run analysis off the UI thread with progress; cap files/Lua bytes |
| Existing features regress | Add new service/view; do not modify existing `ModService`/`OptimizationEngine`/ReShade paths |

## 7. Decision

Implement:

- a new **Mod Performance Analyzer** tab and a core analysis service
- UTOC v8 directory-index reading + legacy pak v8 index reading, with safe fallbacks
- Lua pattern analysis with explicit hypothesis wording
- per-mod risk/confidence/evidence and overlap reporting
- a verified quarantine/restore service with manifest and recovery
- a manual PresentMon before/after comparison workflow
- CLI/self-test coverage using temporary fixtures

Do not implement:

- automated in-game benchmarking
- per-mod FPS attribution
- automatic mod removal without confirmation
- direct moves of Vortex-managed files
- runtime injection or memory scanning
