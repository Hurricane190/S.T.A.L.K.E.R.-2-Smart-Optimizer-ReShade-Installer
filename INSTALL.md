# Installation

## Portable release (recommended)

1. Download and extract the ZIP or copy the `dist` folder.
2. Verify the files if you downloaded a release:
   ```powershell
   Get-FileHash .\ZoneTune.exe -Algorithm SHA256
   # compare with checksums.sha256
   ```
3. Run `ZoneTune.exe`. It is self-contained; no .NET runtime install is needed.
4. If Windows SmartScreen shows “Windows protected your PC”, choose **More info → Run anyway** only if you trust the source and the hash matches.
5. On first launch, ZoneTune auto-detects the game from Steam library manifests and scans the PC.

The app writes only to:
- `%LOCALAPPDATA%\ZoneTune` (settings, logs, cache, packages, reports, change sets, `ModQuarantine`, `ModComparisons`)
- the game’s `Saved\Config\Windows` folder **only after you press Apply**
- the game’s `Binaries\Win64` folder **only when you install ReShade/presets**
- the selected mod’s own folder **only after you confirm Mod Analyzer → Quarantine Selected** (files are copied and hash-verified before the original is removed from the load path)

## First run

1. Check the Dashboard cards: GPU/CPU/RAM/display/storage.
2. If the game is missing, open **Settings & Backups → Game folder → Browse** and select the folder containing `Stalker2.exe` (typically `...\steamapps\common\S.T.A.L.K.E.R. 2 Heart of Chornobyl`).
3. Review the **Recommended in-game profile**. Press **Preview changes**, then **Apply recommended** (game must be closed).
4. Or use **Optimize** for problem-specific plans (Loud PC / Low FPS / Stuttering).
5. Use **Preset Studio** only if you want post-processing; its top card installs/removes ReShade and its catalog section installs ready-made presets.
6. Use **Mod Analyzer → Scan Mods** to review static mod evidence. Quarantine is manual, confirmed, and reversible; it never deletes a mod permanently.

## Applying settings

- **Exit STALKER 2 first.** The game rewrites `GameUserSettings.ini` on exit and locks ReShade DLLs while running. ZoneTune refuses to write while `Stalker2-Win64-Shipping.exe` is running.
- Every apply creates a change set under your backup root (default `%LOCALAPPDATA%\ZoneTune\Backups`).
- After applying, launch the game and verify the settings. Changing resolution/scalability may require a restart.
- Restore with **Settings & Backups → Restore**; a safety snapshot of the current state is made first.

## ReShade installation

- Closing the game is mandatory.
- The first install downloads the official ReShade 6.8 setup from `reshade.me` (about 4 MB) and only the shader files the preset needs from the official GitHub repository.
- The setup is verified by **SHA-256 + Authenticode signer thumbprint** before it runs.
- The official headless installer is used (`--api dxgi`).
- A change set records `ReShade.ini`, the previous preset, proxy DLLs and every added shader.
- Use **Uninstall & restore** to roll back, or **Remove ReShade** to run the official uninstaller (backup kept).
- HDR: disable in-game HDR for most presets. The UI warns when HDR is active.

## Undo

- **Dashboard → Undo last change** restores the most recent change set (settings or ReShade).
- **Settings & Backups → Restore latest change** does the same and shows every stored point; **Restore** on a row restores that exact point.
- **Preset Studio → Uninstall & restore** rolls back a preset/ReShade install made by ZoneTune.
- **Mod Analyzer → Restore quarantined mods** returns hash-verified files to their original paths.
- **Preset Studio → Undo last preset install** restores the prior preset/ReShade.ini.
- Restores create their own safety snapshot, so an undo can be undone. Close the game first.

## Preset Studio

Open **Preset Studio** to build a custom ReShade preset:

1. Pick **Potato**, **Medium** or **High** (the app marks the recommended one for your GPU/resolution).
2. Toggle effects and adjust strengths. Above-budget effects are flagged.
3. Check the synthetic before/after preview, then **Save .ini**, **Export…**, or **Install this preset**.
4. If you do not like it, use **Undo last preset install** on the same page.

## Uninstalling ZoneTune

ZoneTune is portable. Delete `ZoneTune.exe` to remove the application. Optionally delete `%LOCALAPPDATA%\ZoneTune` to remove settings, logs, reports, packages and backups. **Do not delete change sets before deciding you no longer need to restore the game.**

## Troubleshooting

| Symptom | Fix |
|---|---|
| Game not detected | Use Browse; select the folder containing `Stalker2.exe`. Xbox installs may need the `Content` parent. |
| “Exit STALKER 2” | Close the game and the Zone Kit editor; check Task Manager for `Stalker2-Win64-Shipping.exe`. |
| ReShade install fails signature check | The official file changed or the download was altered. Delete `%LOCALAPPDATA%\ZoneTune\Downloads\ReShade_Setup_6.8.0.exe` and retry. If it still fails, do not run the file. |
| ReShade not visible in game | Check `ReShade.log` in `Binaries\Win64`; confirm only one graphics proxy (usually `dxgi.dll`) is present; press Home. |
| Settings seem ignored | Another config mod (e.g. OptimaxP) may set the same cvars. Check the warning in Optimize and compare files. |
| Apply fails | Open the log (`%LOCALAPPDATA%\ZoneTune\Logs`); the app attempts automatic rollback. Restore the change set if needed. |

See [ERROR_LOGGING.md](ERROR_LOGGING.md) for log locations and reporting.
