# Mod Performance Analyzer

The **Mod Analyzer** tab is a read-only static inspection of installed STALKER 2 mods. It helps you find mods that are *plausible* performance risks, then lets you move selected mods into a verified, reversible quarantine.

It does **not** measure FPS. A `HIGH RISK` label means the files contain strong static indicators (for example, a 50 ms Lua loop that queries actors and registers a tick hook). It is still a hypothesis until you capture a before/after measurement.

## What is inspected

| Content | Method | Reliability |
|---|---|---|
| Legacy `.pak` | Unreal pak footer + index (version 8 tested; other versions fall back to heuristic string extraction) | Exact when the index is readable; UNKNOWN when encrypted/truncated |
| IoStore `.utoc` / `.ucas` pairs | IoStore TOC directory index (mount point, directory/file tables, string table); `.ucas` bulk data is **never** read | Exact when the index is readable |
| Pak sizes | Filesystem and container index metadata | Exact, but size alone is **not** a risk indicator |
| Lua scripts | Source patterns: `RegisterHook`, `LoopAsync`, `ExecuteWithDelay`, `FindAllOf`, `FindFirstOf`, spawn/create calls, `while true`, object notifications | Pattern existence is confirmed; runtime frequency/cost is not |
| Assets | Extension and path categories: textures, VFX/lighting/shader-adjacent, AI/spawn/population-adjacent, audio, data | Confirmed paths; texture resolution/GPU cost is not measured |
| Config overrides | Perf-relevant keys such as `r.ScreenPercentage`, `r.Streaming.PoolSize`, `r.Shadow.*`, `r.Lumen`, `sg.*` | Value is confirmed; effect depends on whether the game loads it |
| Native `.dll` / `.asi` | Presence and size only | Always UNKNOWN for runtime behaviour |
| UE4SS state | `enabled.txt` / `disabled.txt`, `.disabled` markers, `mods.json`, UE4SS load-order JSON, then `mods.txt` | More reliable than `mods.txt` alone |
| Vortex | `__folder_managed_by_vortex`, `.vortex_backup`, `vortex.deployment.*` | Reliable marker; quarantine is blocked |
| Overlaps | Normalized asset paths shared by two mods | Confirmed overlap; not proof of duplicate runtime work |
| Merger output | `~S2MUU`, `Merged`, S2MUU folders | Attribution blocked; source mods must be changed instead |

## Risk levels

- **LOW RISK** — no script or asset pattern associated with runtime cost was found. This is not a guarantee of zero cost.
- **MEDIUM RISK** — one or more potential load points (heavy VFX/texture sets, a scheduled loop, actor queries, relevant config).
- **HIGH RISK** — multiple strong static indicators, such as a high-frequency script loop plus actor queries/spawning and tick hooks.
- **UNKNOWN / INSUFFICIENT EVIDENCE** — encrypted/unreadable archive, native-only mod, merger output, or no inspectable content.

Each row shows:

- what was inspected
- why it was flagged
- possible performance problems (clearly labelled as hypotheses)
- what might improve if disabled (conditional, never a promised FPS gain)
- confidence (`CONFIRMED FACT`, `LIKELY`, `HYPOTHESIS`, `INSUFFICIENT`) and evidence sources
- limitations

### Ignore / Unignore

For MEDIUM and HIGH results, each row has an **Ignore** button. Use it when you deliberately keep a mod and do not want its warning repeated.

- Ignoring suppresses the warning from summary counts and the **Flagged** / risk filters.
- The full evidence, confidence and limitations remain in **Details**.
- The row shows **IGNORED** and offers **Unignore**.
- The choice is stored by normalized mod path in `%LOCALAPPDATA%\ZoneTune\mod-ignores.json`, so it survives rescans and app restarts.
- Ignore never weakens quarantine/restore checks and never edits or disables the mod.

## Quarantine safety

Quarantine is **copy → SHA-256 verify → remove original**. It is never a direct delete.

- A JSON manifest records original path, quarantine path, file hashes, timestamps, status and operation id.
- The original folder structure is preserved.
- Restore verifies hashes and refuses to overwrite an existing path.
- Missing/tampered quarantine files are detected and reported.
- Interrupted operations are reconciled on the next load (for example, copy verified but original still present).
- Vortex-managed entries are refused; disable/purge them in Vortex.
- Config overrides in the game config folder are refused.
- Merger output is refused; change the source mods instead.
- UE4SS mods are moved as a whole folder. `mods.json` / `mods.txt` are **never edited**; UE4SS may log a missing-mod entry until restore.
- ASI plugins are allowed with a warning that other plugins may depend on them.
- Only recognized mod paths are accepted: `~mods`, UE4SS `Mods`, or a top-level `.asi` in `Binaries\Win64`.

The quarantine lives at `%LOCALAPPDATA%\ZoneTune\ModQuarantine` by default. The manifest is human-readable JSON.

## Before/after comparison

Automated in-game benchmarking is not implemented: ZoneTune cannot drive a repeatable route or read game telemetry safely. The honest workflow is:

1. Capture a baseline with PresentMon or CapFrameX using the same save, route, graphics settings, upscaler and warm-up.
2. Quarantine/disable the selected mods.
3. Capture again under the same conditions.
4. Import both CSVs into the Mod Analyzer and press **Compare captures**.

The result reports average FPS, 1%/0.1% lows, median/p95/p99 frame times and the stutter index. It is stored in `%LOCALAPPDATA%\ZoneTune\ModComparisons` and explicitly applies to the tested condition, not to an individual mod.

## CLI

```powershell
dotnet run --project src\ZoneTune.Cli -c Release -- --mod-analyze
```

The CLI prints the same evidence and risk summary without changing any files.

## Tests

`--selftest` runs fixture-based tests for:

- fixture discovery across pak, loose pak, UE4SS, ASI, corrupt archive and Vortex marker
- HIGH/LOW/UNKNOWN classification and explicit evidence
- repeated scans without duplicates
- overlap detection
- quarantine copy/verify/remove, restore round-trip, collision refusal, tamper detection and interrupted-operation recovery
- Vortex refusal in both the analyzer and the service
- manual comparison storage and caveats
- all existing INI, config, ReShade, package, report and mod-toggle tests

## Known limitations

- No FPS, frame-time, VRAM, CPU or GPU measurement from static files.
- Encrypted or truncated archives are reported as UNKNOWN; their contents are not guessed.
- Legacy pak versions other than the tested Unreal layout fall back to heuristic names or UNKNOWN.
- Native plugins cannot be assessed without executing/disassembling them.
- Lua branches may be disabled at runtime; source patterns do not prove execution.
- Asset path categories are heuristics. Texture resolution, triangle counts and draw calls are not parsed.
- Merged output cannot be attributed to a single source mod.
- Vortex must remain the authority for Vortex-managed files.
