using System.Text.RegularExpressions;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

public sealed class ScanService
{
    private readonly HardwareService _hardware = new();
    private readonly GameLocator _locator = new();
    private readonly ConfigService _config = new();
    private readonly ModScanner _mods = new();
    private readonly ReShadeService _reshade = new();

    public ScanResult Scan(AppSettings settings, bool includeMods = true, IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        var scan = new ScanResult();
        progress?.Report(new OperationProgress("Locating game", "Steam libraries and known folders", 5));
        scan.Game = _locator.Locate(settings.GamePathOverride, settings.GamePathOverride);
        ct.ThrowIfCancellationRequested();

        progress?.Report(new OperationProgress("Detecting hardware", "CPU, GPU, RAM, displays, HDR", 20));
        scan.Hardware = _hardware.Detect(scan.Game.IsValid ? scan.Game.Root : null);
        ct.ThrowIfCancellationRequested();

        if (!scan.Game.IsValid)
        {
            scan.ScanErrors.Add(scan.Game.ValidationMessage);
            scan.Findings.Add(new DiagnosticFinding
            {
                Id = "game-not-found",
                Severity = Severity.Critical,
                Category = "Game",
                Title = "STALKER 2 installation not found",
                Detail = "Automatic detection did not find Stalker2-Win64-Shipping.exe. Use Settings to choose the game folder.",
                Evidence = scan.Game.ValidationMessage,
                Recommendation = "Select the folder containing Stalker2.exe (usually ...\\steamapps\\common\\S.T.A.L.K.E.R. 2 Heart of Chornobyl).",
                RelatedProblems = { ProblemKind.LowFps, ProblemKind.Stuttering, ProblemKind.LoudPc }
            });
            scan.Findings.AddRange(BuildHardwareFindings(scan));
            return scan;
        }

        progress?.Report(new OperationProgress("Reading configuration", "GameUserSettings.ini, Engine.ini, Scalability.ini", 40));
        scan.Config = _config.Read(scan.Game);
        scan.GameRunning = _config.IsGameRunning();
        ct.ThrowIfCancellationRequested();

        progress?.Report(new OperationProgress("Scanning mods", "Pak mods, UE4SS, ASI, conflicts", 55));
        if (includeMods)
        {
            var (mods, conflicts) = _mods.Scan(scan.Game);
            scan.Mods = mods;
            scan.ModConflicts = conflicts;
        }
        ct.ThrowIfCancellationRequested();

        progress?.Report(new OperationProgress("Auditing ReShade", "Proxy DLL, presets, shaders, log", 75));
        scan.ReShade = _reshade.Detect(scan.Game, scan.Hardware);

        progress?.Report(new OperationProgress("Analysing logs", "Game and ReShade logs", 88));
        scan.Findings.AddRange(BuildFindings(scan));

        progress?.Report(new OperationProgress("Done", "Scan complete", 100));
        Log.Info($"Scan complete: {scan.Findings.Count} findings, {scan.Mods.Count} mod entries");
        return scan;
    }

    private List<DiagnosticFinding> BuildHardwareFindings(ScanResult scan)
    {
        var hw = scan.Hardware;
        return new List<DiagnosticFinding>
        {
            new()
            {
                Id = "hw-summary",
                Severity = Severity.Info,
                Category = "Hardware",
                Title = "Hardware detected",
                Detail = $"{hw.CpuSummary}; {hw.GpuSummary}; {hw.RamSummary}; {hw.WindowsSummary}.",
                Evidence = $"Display: {hw.PrimaryResolution} @ {hw.PrimaryRefreshHz} Hz; HDR {(hw.HdrEnabled ? "enabled" : hw.AnyHdr ? "capable, off" : "not detected")}. " +
                           $"Game drive: {hw.StorageSummary}."
            },
            new()
            {
                Id = "hw-storage",
                Severity = hw.GameDriveTotalBytes > 0 && hw.GameDriveFreeBytes < hw.GameDriveTotalBytes * 0.08
                    ? Severity.Warning : Severity.Info,
                Category = "Storage",
                Title = "Free space on the game drive",
                Detail = hw.StorageSummary,
                Evidence = $"Detected capacity: {hw.GameDriveTotalBytes} bytes.",
                Recommendation = "Keep at least 10–15% free for shader caches, saves and updates."
            }
        };
    }

    private List<DiagnosticFinding> BuildFindings(ScanResult scan)
    {
        var findings = new List<DiagnosticFinding>();
        var hw = scan.Hardware;
        var cfg = scan.Config;
        var rs = scan.ReShade;

        findings.Add(new DiagnosticFinding
        {
            Id = "game-detected",
            Severity = Severity.Info,
            Category = "Game",
            Title = "Game installation detected",
            Detail = $"{scan.Game.Root} ({scan.Game.Source}).",
            Evidence = $"Shipping exe: {scan.Game.ShippingExePath}; config: {scan.Game.ConfigDir}.",
            Recommendation = scan.Game.Source == GameSource.Manual
                ? "Path was selected manually. Re-run detection after moving the install."
                : "Detected through the Steam library manifest."
        });

        findings.Add(new DiagnosticFinding
        {
            Id = "config-state",
            Severity = Severity.Info,
            Category = "Configuration",
            Title = "Current graphics configuration",
            Detail = $"{cfg.ResolutionX}x{cfg.ResolutionY}, {cfg.FullscreenMode}, VSync {(cfg.VSync ? "on" : "off")}, " +
                     $"dynamic resolution {(cfg.DynamicResolution ? "on" : "off")}, frame cap " +
                     $"{(cfg.FrameRateLimit <= 0 ? "unlimited" : $"{cfg.FrameRateLimit:0} FPS")}, HDR {(cfg.HdrConfigured ? "on" : "off")}.",
            Evidence = $"GameUserSettings.ini {(cfg.GameUserSettingsFound ? "found" : "missing")}; Engine.ini {(cfg.EngineIniFound ? "found" : "missing")}; " +
                       $"Scalability.ini {(cfg.ScalabilityIniFound ? "found" : "missing")}.",
            RelatedProblems = { ProblemKind.LowFps, ProblemKind.Stuttering, ProblemKind.LoudPc }
        });

        if (!cfg.GameUserSettingsFound)
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "config-missing",
                Severity = Severity.Warning,
                Category = "Configuration",
                Title = "No GameUserSettings.ini found",
                Detail = "Launch the game once so it creates its settings file before applying an optimization plan.",
                Evidence = $"Expected at {cfg.RawGameUserSettingsPath}."
            });
        }

        if (cfg.FrameRateLimit <= 0 && hw.PrimaryRefreshHz >= 100)
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "uncapped-fps",
                Severity = Severity.Notice,
                Category = "Performance",
                Title = "Unlimited frame rate at a high-refresh display",
                Detail = $"The game is uncapped on a {hw.PrimaryRefreshHz} Hz display. In GPU-bound scenes this pushes the card to its power limit, " +
                         "which is a common source of fan noise and heat. A cap (or VRR with a cap below refresh) is the first thing to test.",
                Evidence = $"FrameRateLimit=0; display {hw.PrimaryResolution}@{hw.PrimaryRefreshHz}Hz.",
                Recommendation = "Select Loud PC or Stuttering in Optimize to generate a frame cap; measure noise and 1% lows before/after.",
                RelatedProblems = { ProblemKind.LoudPc, ProblemKind.Stuttering }
            });
        }

        if (cfg.ScalabilityGroups.Count > 0 && cfg.ScalabilityGroups.Values.All(v => v >= 3) && hw.PrimaryRefreshHz >= 100)
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "epic-settings",
                Severity = Severity.Notice,
                Category = "Performance",
                Title = "All scalability groups are at Epic (3)",
                Detail = "Every visible quality group is maxed. If FPS is below target, shadow, GI and volumetric quality are the highest-value reductions; " +
                         "texture quality can usually stay high on a 12 GB+ card.",
                Evidence = string.Join(", ", cfg.ScalabilityGroups.Select(kv => $"{kv.Key}={kv.Value}")),
                Recommendation = "Use Optimize → Low FPS for reversible one-notch reductions, then measure.",
                RelatedProblems = { ProblemKind.LowFps, ProblemKind.LoudPc }
            });
        }

        if (cfg.DynamicResolution)
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "dynamic-res",
                Severity = Severity.Notice,
                Category = "Performance",
                Title = "Dynamic resolution is enabled",
                Detail = "Dynamic resolution changes internal resolution at runtime. It can help average FPS but may look soft or pump, and it " +
                         "interacts with upscalers. If frame pacing is inconsistent, test with it off.",
                Evidence = "bUseDynamicResolution=True.",
                Recommendation = "Compare a PresentMon capture with it on and off before deciding.",
                RelatedProblems = { ProblemKind.Stuttering, ProblemKind.LowFps }
            });
        }

        if (cfg.HasOptimaxConfig)
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "optimax",
                Severity = Severity.Notice,
                Category = "Configuration",
                Title = "Third-party OptimaxP config is active",
                Detail = "OptimaxP_Config.ini can set the same cvars as ZoneTune's Engine.ini. ZoneTune will not delete it. If a change appears ineffective, " +
                         "check for duplicate keys in both files.",
                Evidence = Path.Combine(scan.Game.ConfigDir, "OptimaxP_Config.ini"),
                RelatedProblems = { ProblemKind.LowFps, ProblemKind.Stuttering }
            });
        }

        if (cfg.S2aeShaderWarmupSkipped)
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "warmup-skipped",
                Severity = Severity.Warning,
                Category = "Performance",
                Title = "S2AE skips startup shader warmup",
                Detail = "Skipping warmup moves shader compilation into gameplay and can cause first-session hitches, especially after a driver update.",
                Evidence = "S2AE.cfg: skipStartupShaderWarmup=true.",
                Recommendation = "Set it to false for a stutter baseline, or accept longer startup but measure compile hitches both ways.",
                RelatedProblems = { ProblemKind.Stuttering }
            });
        }

        // ReShade
        if (rs.Installed)
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "reshade-detected",
                Severity = rs.EffectErrors > 0 ? Severity.Warning : Severity.Info,
                Category = "ReShade",
                Title = $"ReShade {rs.Version} detected",
                Detail = $"Active through {rs.ProxyDll}; preset '{rs.PresetName}' with {rs.PresetTechniques.Count} technique(s); " +
                         $"{rs.ShaderCount} shader files; {rs.PresetCount} preset(s).",
                Evidence = $"{rs.ProxyPath}; log: {rs.LogPath}; errors={rs.EffectErrors}, warnings={rs.EffectWarnings}.",
                RelatedProblems = { ProblemKind.Stuttering, ProblemKind.LowFps }
            });
        }
        else
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "reshade-absent",
                Severity = Severity.Info,
                Category = "ReShade",
                Title = "ReShade is not installed",
                Detail = "Optional. The Preset Studio can install the official ReShade build with backups, but it is not required for performance tuning.",
                Evidence = $"No ReShade module found in {scan.Game.BinariesDir}.",
                Recommendation = "Use the Preset Studio for presets; skip it if you want the lowest overhead."
            });
        }

        if (rs.ProxyConflicts.Count > 0)
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "reshade-proxy-conflict",
                Severity = Severity.Warning,
                Category = "ReShade",
                Title = "Graphics proxy DLL conflict",
                Detail = "More than one graphics wrapper/proxy is present. This can crash at launch or silently bypass ReShade.",
                Evidence = string.Join(" | ", rs.ProxyConflicts),
                Recommendation = "Keep one API proxy (dxgi.dll for DX12). Move other wrappers aside only after making a backup.",
                RelatedProblems = { ProblemKind.Stuttering }
            });
        }

        if (rs.Installed && rs.EffectErrors > 0)
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "reshade-errors",
                Severity = Severity.Warning,
                Category = "ReShade",
                Title = $"{rs.EffectErrors} ReShade effect error(s)",
                Detail = "Shader compile/load errors can make a preset partially active. The log is the source of truth; do not assume a preset is fully working.",
                Evidence = string.Join(" | ", rs.Errors.Take(4)),
                Recommendation = "Open the ReShade overlay and check the effect list for red entries. Switch to a preset whose shaders are present.",
                RelatedProblems = { ProblemKind.Stuttering, ProblemKind.LowFps }
            });
        }

        if (rs.HdrConflict)
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "hdr-reshade",
                Severity = Severity.Warning,
                Category = "Display",
                Title = "HDR is enabled with ReShade active",
                Detail = "ReShade's SDR post-processing chain can wash out or clip HDR output depending on the build and game swapchain. Most community presets assume SDR.",
                Evidence = $"HDR enabled={hw.HdrEnabled}; ReShade {rs.Version}.",
                Recommendation = "Disable in-game HDR while using ReShade, or disable ReShade when playing in HDR.",
                RelatedProblems = { ProblemKind.Stuttering }
            });
        }

        if (rs.ShaderDuplicates.Count > 0)
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "shader-duplicates",
                Severity = Severity.Notice,
                Category = "ReShade",
                Title = "Duplicate shader file names",
                Detail = "Different copies of the same shader name exist. ReShade loads by search path order, which makes behaviour hard to reproduce.",
                Evidence = string.Join(" | ", rs.ShaderDuplicates.Take(6)),
                Recommendation = "Keep one shader repository and remove duplicate folders after backup.",
                RelatedProblems = { ProblemKind.Stuttering }
            });
        }

        if (rs.Installed && rs.ShaderReferences.Any(s => !s.Present))
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "missing-shaders",
                Severity = Severity.Warning,
                Category = "ReShade",
                Title = "Active preset references missing shaders",
                Detail = "At least one technique in the active preset has no matching .fx file in the configured shader paths. ReShade may silently skip it.",
                Evidence = string.Join(", ", rs.ShaderReferences.Where(s => !s.Present).Select(s => s.File).Distinct()),
                Recommendation = "Install the preset's required shader package or choose a preset that validates in the Preset Studio.",
                RelatedProblems = { ProblemKind.Stuttering }
            });
        }

        // Mods
        if (scan.Mods.Count > 0)
        {
            var enabled = scan.Mods.Count(m => m.Enabled);
            findings.Add(new DiagnosticFinding
            {
                Id = "mods-summary",
                Severity = scan.ModConflicts.Any(c => c.Severity >= Severity.Warning) ? Severity.Warning : Severity.Info,
                Category = "Mods",
                Title = $"{enabled} enabled mod component(s)",
                Detail = $"{scan.Mods.Count(m => m.Kind == ModKind.PakMod)} pak entries, {scan.Mods.Count(m => m.Kind == ModKind.Ue4ssLua)} UE4SS mods, " +
                         $"{scan.Mods.Count(m => m.Kind == ModKind.AsiPlugin)} ASI plugins.",
                Evidence = string.Join(", ", scan.Mods.Where(m => m.Enabled).Take(8).Select(m => m.Name)) + (scan.Mods.Count > 8 ? ", …" : ""),
                Recommendation = scan.ModConflicts.Count > 0 ? "Resolve conflict notes before tuning performance." : "Mod list is stored in Installed Mods.",
                RelatedProblems = { ProblemKind.Stuttering }
            });
        }

        foreach (var conflict in scan.ModConflicts)
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "mod-conflict-" + Math.Abs(conflict.Title.GetHashCode()).ToString("x6"),
                Severity = conflict.Severity,
                Category = "Mods",
                Title = conflict.Title,
                Detail = conflict.Detail,
                Evidence = string.Join(", ", conflict.Mods.Take(8)),
                Recommendation = "Disable one conflicting variant (Installed Mods → Disable) and retest. Never delete a mod you may want back.",
                RelatedProblems = { ProblemKind.Stuttering, ProblemKind.LowFps }
            });
        }

        // Hardware / storage
        if (hw.GameDriveTotalBytes > 0 && hw.GameDriveFreeBytes < hw.GameDriveTotalBytes * 0.08)
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "storage-low",
                Severity = Severity.Warning,
                Category = "Storage",
                Title = "Low free space on the game drive",
                Detail = $"Only {AppPaths.FormatBytes(hw.GameDriveFreeBytes)} of {AppPaths.FormatBytes(hw.GameDriveTotalBytes)} is free. " +
                         "SSD write caching, shader caches and patches need headroom.",
                Evidence = $"Drive {hw.GameDriveName}.",
                Recommendation = "Free at least 10–15% and retest stutter.",
                RelatedProblems = { ProblemKind.Stuttering }
            });
        }

        if (hw.VramMb > 0 && hw.VramMb < 10_000 && hw.PrimaryResolution is "3840x2160" or "3440x1440")
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "vram-tight",
                Severity = Severity.Notice,
                Category = "Performance",
                Title = "VRAM is tight for the display resolution",
                Detail = $"{HardwareInfo.FormatGb(hw.VramMb)} VRAM at {hw.PrimaryResolution}. Texture streaming and Lumen compete for the same budget.",
                Evidence = $"{hw.GpuName}; {hw.PrimaryResolution}.",
                Recommendation = "Prefer upscaling from 1440p and avoid adding heavy post-processing.",
                RelatedProblems = { ProblemKind.Stuttering, ProblemKind.LowFps }
            });
        }

        findings.AddRange(AnalyzeLogs(scan));
        if (scan.GameRunning)
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "game-running",
                Severity = Severity.Notice,
                Category = "Game",
                Title = "Game is currently running",
                Detail = "GameUserSettings.ini is rewritten when the game exits. Applying changes now would likely be overwritten or ignored.",
                Evidence = "Stalker2-Win64-Shipping.exe process detected.",
                Recommendation = "Exit the game before applying changes.",
                RelatedProblems = { ProblemKind.LoudPc, ProblemKind.LowFps, ProblemKind.Stuttering }
            });
        }

        return findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Category).ToList();
    }

    private static List<DiagnosticFinding> AnalyzeLogs(ScanResult scan)
    {
        var findings = new List<DiagnosticFinding>();
        var logPath = Path.Combine(scan.Game.LogsDir, "Stalker2.log");
        if (!File.Exists(logPath))
        {
            findings.Add(new DiagnosticFinding
            {
                Id = "game-log-missing",
                Severity = Severity.Info,
                Category = "Logs",
                Title = "No Stalker2.log yet",
                Detail = "Launch the game once to generate a log, then re-scan for launch and crash diagnostics.",
                Evidence = logPath
            });
            return findings;
        }

        try
        {
            var info = new FileInfo(logPath);
            var tailBytes = (int)Math.Min(info.Length, 4 * 1024 * 1024);
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(-tailBytes, SeekOrigin.End);
            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();
            var lines = text.Split('\n');
            var patterns = new (string id, string title, string pattern, Severity severity, ProblemKind problem, RegexOptions options)[]
            {
                ("log-oom", "Out-of-memory / VRAM exhaustion reported", @"Out of video memory|OutOfMemory|Ran out of memory|\bOOM\b", Severity.Critical, ProblemKind.Stuttering, RegexOptions.IgnoreCase),
                ("log-device-removed", "D3D device removed / GPU hang reported", @"DXGI_ERROR_DEVICE_REMOVED|DXGI_ERROR_DEVICE_HUNG|Device Removed|GPU has crashed|Aftermath.*(crash|hang) detected", Severity.Critical, ProblemKind.Stuttering, RegexOptions.IgnoreCase),
                ("log-fatal", "Fatal error in the game log", @"Fatal error:|Unhandled Exception:|Assertion failed", Severity.Critical, ProblemKind.Stuttering, RegexOptions.IgnoreCase),
                ("log-shader", "Shader compilation failures reported", @"Failed to compile|Compilation failed|Shader compile error|ShaderCompile.*failed", Severity.Warning, ProblemKind.Stuttering, RegexOptions.IgnoreCase),
                ("log-streaming", "Texture streaming warnings reported", @"LogStreaming:\s*(Warning|Error)|Texture streaming.*(failed|over budget)|streaming pool.*(exceeded|full)", Severity.Notice, ProblemKind.Stuttering, RegexOptions.IgnoreCase),
                ("log-audio", "Audio device/loop warnings reported", @"LogWwise.*(Error|Warning)|audio device.*(lost|failed|removed)", Severity.Notice, ProblemKind.Stuttering, RegexOptions.IgnoreCase)
            };

            foreach (var p in patterns)
            {
                var matches = lines.Where(l =>
                        Regex.IsMatch(l, p.pattern, p.options) &&
                        !l.Contains("UATHelper", StringComparison.OrdinalIgnoreCase) &&
                        !l.Contains("AutomationTool", StringComparison.OrdinalIgnoreCase) &&
                        !l.Contains("LogConfig: Set CVar", StringComparison.OrdinalIgnoreCase) &&
                        !l.Contains("GFSDK_Aftermath", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matches.Count == 0) continue;
                var sample = matches.LastOrDefault(l => l.Length < 300)?.Trim() ?? "";
                findings.Add(new DiagnosticFinding
                {
                    Id = p.id,
                    Severity = p.severity,
                    Category = "Logs",
                    Title = p.title,
                    Detail = $"{matches.Count} matching line(s) in the last {tailBytes / 1024 / 1024.0:0.#} MB of Stalker2.log.",
                    Evidence = sample.Length > 220 ? sample[..220] : sample,
                    Recommendation = p.id switch
                    {
                        "log-oom" => "Lower texture/streaming pressure, close background GPU apps, and re-check VRAM. ZoneTune's stutter plan includes a conservative pool and VRAM clamp.",
                        "log-device-removed" => "Remove overclocks, verify the driver, and test without ReShade/overlays. A device removal is a crash, not a tuning issue.",
                        "log-fatal" => "Check the newest crash report and the last mods installed. Reproduce on a clean save before changing configs.",
                        "log-shader" => "Reinstall the affected shader/preset, or let shader warmup run. Do not judge performance until compile errors are gone.",
                        _ => "Correlate with a PresentMon capture; log warnings alone are not proof of a frame-time problem."
                    },
                    RelatedProblems = { p.problem }
                });
            }

            var sessionLine = lines.LastOrDefault(l => l.Contains("Log file open", StringComparison.OrdinalIgnoreCase));
            if (sessionLine != null)
            {
                findings.Add(new DiagnosticFinding
                {
                    Id = "log-session",
                    Severity = Severity.Info,
                    Category = "Logs",
                    Title = "Latest game session log",
                    Detail = "Log is read-only; ZoneTune never edits or deletes game logs.",
                    Evidence = sessionLine.Trim().Length > 220 ? sessionLine.Trim()[..220] : sessionLine.Trim()
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not analyse Stalker2.log: {ex.Message}");
        }

        try
        {
            var ue4ssDir = Path.Combine(scan.Game.BinariesDir, "ue4ss");
            if (Directory.Exists(ue4ssDir))
            {
                var dumps = Directory.EnumerateFiles(ue4ssDir, "crash_*.dmp").Count();
                if (dumps > 0)
                {
                    findings.Add(new DiagnosticFinding
                    {
                        Id = "crash-dumps",
                        Severity = Severity.Warning,
                        Category = "Logs",
                        Title = $"{dumps} UE4SS crash dump(s) present",
                        Detail = "Crash dumps indicate earlier crashes in the script/ASI environment. They may be from development or an older mod set.",
                        Evidence = ue4ssDir,
                        Recommendation = "Check timestamps against your current mod set. Do not delete dumps until you have reviewed them.",
                        RelatedProblems = { ProblemKind.Stuttering }
                    });
                }
            }
        }
        catch { }

        return findings;
    }
}
