# Error logging and diagnostics

## Where logs live

```
%LOCALAPPDATA%\ZoneTune\Logs\
  zonetune.log                 current log (rotates at 4 MB)
  zonetune-YYYYMMDD-HHMMSS.log rotated logs
  crash-YYYYMMDD-HHMMSS.log    unhandled exception report
  selftest.txt                 last self-test result
```

Use **Settings & Backups → Open log folder** or the **Logs** link in the footer.

## What is logged

- application start/version/OS/runtime
- hardware, game, config, mod and ReShade detection summaries
- every scan result count (not full personal file lists)
- every backup/change set created, file count and size
- every apply/install step, validation result and rollback
- network downloads: host, file name, size, SHA-256, signature result
- unhandled exceptions with stack trace
- ReShade setup output (headless console)

The app never logs full file contents, save files, credentials or tokens.

## Crash reports

Unhandled dispatcher/AppDomain exceptions are written as `crash-*.log` containing:

- ZoneTune version, timestamp, source (Dispatcher/AppDomain)
- OS/runtime/process path
- full exception and stack trace

The UI also shows a dialog with the log folder. Crash reports are local only; nothing is uploaded.

## Reporting an issue

1. Reproduce the problem and note the time.
2. Collect:
   - `%LOCALAPPDATA%\ZoneTune\Logs\zonetune.log`
   - the newest `crash-*.log` if present
   - `%LOCALAPPDATA%\ZoneTune\selftest.txt`
   - the relevant section of `ReShade.log` if ReShade is involved
3. Include:
   - ZoneTune version (visible in the title/footer)
   - Windows version
   - GPU/CPU/RAM and display resolution/HDR state
   - game source (Steam/Xbox/manual)
   - exact steps and whether the game was running
4. **Review paths and hardware details before sharing.** Reports/logs contain local absolute paths.

## Common log lines

| Line | Meaning |
|---|---|
| `ReShade certificate chain untrusted (0x800B0109); accepting only because the published thumbprint matched` | The published ReShade certificate does not chain to a local root on this PC. The file hash and pinned signer thumbprint matched; this is expected for the official ReShade build. |
| `ReShade setup hash mismatch` | The downloaded setup is not the pinned official 6.8.0 file. Execution is refused. Delete the cached file and retry from reshade.me. |
| `Change set created` | Backup succeeded; the listed files can be restored. |
| `Validation failed for ...` | A written value did not read back correctly; an automatic rollback is attempted. |
| `Scan failed` | The UI catch-all; inspect the stack trace. |
| `Could not parse ReShade.log ... being used by another process` | The game is running and holds the log; close it and rescan. |

## Privacy

No telemetry, analytics, accounts or remote crash upload. Network access is only used for explicitly requested ReShade/setup/shader/preset downloads and public preset search. All network code enforces HTTPS and a host allowlist.
