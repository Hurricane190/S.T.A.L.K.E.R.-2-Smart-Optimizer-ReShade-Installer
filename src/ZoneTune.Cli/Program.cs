using System.Text;
using ZoneTune.Cli;
using ZoneTune.Core;
using ZoneTune.Core.Models;
using ZoneTune.Core.Services;

Console.OutputEncoding = Encoding.UTF8;
Log.Initialize();

var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
var settingsStore = new SettingsStore();
var settings = settingsStore.Load();
var scanService = new ScanService();
var engine = new OptimizationEngine();
var backups = new BackupService(settings.BackupRoot);

try
{
    if (cliArgs.Length == 0 || cliArgs.Contains("--help") || cliArgs.Contains("-h"))
    {
        PrintHelp();
        return 0;
    }

    if (cliArgs.Contains("--hardware"))
    {
        var hw = new HardwareService().Detect();
        PrintHardware(hw);
        return 0;
    }

    if (cliArgs.Contains("--locate"))
    {
        var game = new GameLocator().Locate(settings.GamePathOverride, settings.GamePathOverride);
        Console.WriteLine($"Valid: {game.IsValid}");
        Console.WriteLine($"Source: {game.Source}");
        Console.WriteLine($"Root: {game.Root}");
        Console.WriteLine($"Shipping exe: {game.ShippingExePath}");
        Console.WriteLine($"Config: {game.ConfigDir}");
        Console.WriteLine($"Mods: {game.ModsDir}");
        return game.IsValid ? 0 : 2;
    }

    if (cliArgs.Contains("--measure"))
    {
        var index = Array.IndexOf(cliArgs, "--measure");
        if (index + 1 >= cliArgs.Length) { Console.Error.WriteLine("--measure requires a CSV path."); return 1; }
        var summary = new MeasurementService().ImportPresentMon(cliArgs[index + 1]);
        PrintMeasurement(summary);
        return 0;
    }

    if (cliArgs.Contains("--mod-analyze"))
    {
        var game = new GameLocator().Locate(settings.GamePathOverride, settings.GamePathOverride);
        if (!game.IsValid)
        {
            Console.Error.WriteLine("Game not found: " + game.ValidationMessage);
            return 2;
        }
        var hardware = new HardwareService().Detect(game.Root);
        var report = new ModPerformanceAnalyzer().Analyze(game, hardware);
        PrintModAnalysis(report);
        return 0;
    }

    if (cliArgs.Contains("--sandbox-reshade"))
    {
        return await SandboxTest.RunAsync(settings);
    }

    if (cliArgs.Contains("--selftest"))
    {
        return SelfTest.Run(settings, scanService, engine, backups);
    }

    if (cliArgs.Contains("--apply-selftest"))
    {
        return ApplyE2ETest.Run(settings, scanService, engine, backups);
    }

    var scan = scanService.Scan(settings);
    if (!scan.Game.IsValid)
    {
        Console.Error.WriteLine("Game not found: " + scan.Game.ValidationMessage);
        Console.Error.WriteLine("Run with --locate or set GamePathOverride in settings.");
        return 2;
    }

    if (cliArgs.Contains("--report"))
    {
        var report = new ReportService().Generate(scan);
        Console.WriteLine("Report: " + report.Html);
        Console.WriteLine("Markdown: " + report.Markdown);
        Console.WriteLine("JSON: " + report.Json);
        return 0;
    }

    if (cliArgs.Contains("--recommend"))
    {
        var profile = engine.BuildRecommendedProfile(scan);
        PrintPlan(profile);
        return 0;
    }

    var problems = ParseProblems(cliArgs);
    if (problems.Count == 0 && cliArgs.Contains("--plan")) problems = new List<ProblemKind> { ProblemKind.LoudPc, ProblemKind.LowFps, ProblemKind.Stuttering };

    if (cliArgs.Contains("--plan") || cliArgs.Contains("--package"))
    {
        if (problems.Count == 0) problems = new List<ProblemKind> { ProblemKind.LoudPc, ProblemKind.LowFps, ProblemKind.Stuttering };
        var plan = engine.BuildPlan(scan, problems);
        PrintPlan(plan);
        if (cliArgs.Contains("--package"))
        {
            var path = new PackageBuilder().Build(plan, scan, new PresetCatalog().GetBuiltInPresets(scan.Hardware).First());
            Console.WriteLine("Package: " + path);
        }
        return 0;
    }

    PrintScan(scan);
    return 0;
}
catch (Exception ex)
{
    Log.Error("CLI failure", ex);
    Console.Error.WriteLine(ex);
    return 1;
}

static List<ProblemKind> ParseProblems(string[] args)
{
    var result = new List<ProblemKind>();
    foreach (var arg in args)
    {
        if (!arg.StartsWith("--problems=", StringComparison.OrdinalIgnoreCase) &&
            !arg.StartsWith("--plan=", StringComparison.OrdinalIgnoreCase)) continue;
        var value = arg[(arg.IndexOf('=') + 1)..];
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("loud", StringComparison.OrdinalIgnoreCase)) result.Add(ProblemKind.LoudPc);
            else if (token.Equals("fps", StringComparison.OrdinalIgnoreCase)) result.Add(ProblemKind.LowFps);
            else if (token.Equals("stutter", StringComparison.OrdinalIgnoreCase)) result.Add(ProblemKind.Stuttering);
        }
    }
    return result;
}

static void PrintHelp()
{
    Console.WriteLine("ZoneTune CLI — diagnostics and package generation");
    Console.WriteLine();
    Console.WriteLine("  --scan                  Hardware, game, config, mods, ReShade, findings");
    Console.WriteLine("  --plan --problems=...   Generate a plan: loud,fps,stutter");
    Console.WriteLine("  --recommend             Hardware-derived in-game settings profile");
    Console.WriteLine("  --package               Build a ZIP package from the generated plan");
    Console.WriteLine("  --report                Export HTML/MD/JSON diagnostic report");
    Console.WriteLine("  --hardware              Hardware only");
    Console.WriteLine("  --locate                Game detection only");
    Console.WriteLine("  --measure <csv>         Import a PresentMon CSV");
    Console.WriteLine("  --mod-analyze           Static mod performance/risk inventory (read-only)");
    Console.WriteLine("  --selftest              Run offline/functional self-tests");
    Console.WriteLine("  --apply-selftest        Apply profile to real config, validate, restore (game closed)");
}

static void PrintHardware(HardwareInfo hw)
{
    Console.WriteLine($"CPU: {hw.CpuSummary} @ {hw.CpuMhz} MHz");
    Console.WriteLine($"GPU: {hw.GpuSummary} ({hw.GpuVendor}, tier {hw.GpuTier}) driver {hw.GpuDriverVersion}");
    Console.WriteLine($"RAM: {hw.RamSummary}");
    Console.WriteLine($"OS: {hw.WindowsSummary} / {hw.SystemModel}");
    Console.WriteLine($"Display: {hw.PrimaryResolution} @ {hw.PrimaryRefreshHz} Hz / HDR {(hw.HdrEnabled ? "on" : hw.AnyHdr ? "capable" : "no")}");
    foreach (var d in hw.Displays)
        Console.WriteLine($"  - {d.MonitorName} ({d.DeviceName}) {d.Summary} bits={d.BitsPerColor} primary={d.IsPrimary}");
    Console.WriteLine($"Storage: {hw.StorageSummary}");
}

static void PrintScan(ScanResult scan)
{
    PrintHardware(scan.Hardware);
    Console.WriteLine();
    Console.WriteLine($"Game: {scan.Game.Root} ({scan.Game.Source})");
    Console.WriteLine($"Config: {scan.Config.ResolutionX}x{scan.Config.ResolutionY}, cap {(scan.Config.FrameRateLimit <= 0 ? "unlimited" : scan.Config.FrameRateLimit.ToString("0"))}, " +
                      $"HDR {scan.Config.HdrConfigured}, OptimaxP {scan.Config.HasOptimaxConfig}");
    Console.WriteLine($"ReShade: installed={scan.ReShade.Installed}, version={scan.ReShade.Version}, proxy={scan.ReShade.ProxyDll}, preset={scan.ReShade.PresetName}, " +
                      $"techniques={scan.ReShade.PresetTechniques.Count}, errors={scan.ReShade.EffectErrors}");
    Console.WriteLine($"Mods: {scan.Mods.Count} entries; conflicts: {scan.ModConflicts.Count}");
    Console.WriteLine();
    foreach (var f in scan.Findings)
        Console.WriteLine($"[{f.Severity}] {f.Title}: {f.Detail}");
}

static void PrintPlan(OptimizationPlan plan)
{
    Console.WriteLine("PLAN " + plan.Id);
    Console.WriteLine(plan.Summary);
    Console.WriteLine();
    foreach (var a in plan.Actions)
    {
        Console.WriteLine($"[{(a.Selected ? "x" : " ")}] {a.Title} ({a.KindText}, {a.RiskText})");
        Console.WriteLine("    " + a.ChangeText);
        Console.WriteLine("    " + a.EstimatedEffect);
    }
    Console.WriteLine();
    foreach (var w in plan.Warnings) Console.WriteLine("WARN: " + w);
}

static void PrintMeasurement(MeasurementSummary m)
{
    Console.WriteLine($"Frames: {m.Frames} / {m.DurationSeconds:0.0}s");
    Console.WriteLine($"Average: {m.AverageFps:0.0} FPS");
    Console.WriteLine($"1% low: {m.OnePercentLowFps:0.0} FPS");
    Console.WriteLine($"0.1% low: {m.PointOnePercentLowFps:0.0} FPS");
    Console.WriteLine($"Median: {m.MedianFrameMs:0.00} ms; p95 {m.P95FrameMs:0.00}; p99 {m.P99FrameMs:0.00}; max {m.MaxFrameMs:0.00}");
    Console.WriteLine($"Stutter index: {m.StutterScore:0.0}");
}

static void PrintModAnalysis(ModPerformanceReport report)
{
    Console.WriteLine($"Game: {report.GameRoot}");
    Console.WriteLine($"Scanned: {report.ScannedAt:u} in {report.Duration.TotalSeconds:0.0}s · " +
                      $"{report.Mods.Count} mods · {report.EnabledCount} enabled · " +
                      $"{report.HighCount} high / {report.MediumCount} medium / {report.UnknownCount} unknown · " +
                      $"{report.ContainersRead} containers read / {report.ContainersUnreadable} unreadable");
    Console.WriteLine("Static evidence only: no FPS loss is claimed without a before/after measurement.");
    Console.WriteLine();
    foreach (var mod in report.Mods.OrderByDescending(m => m.Risk).ThenBy(m => m.Name))
    {
        Console.WriteLine($"[{mod.RiskText,-12}] {mod.Name} {mod.VersionText} ({mod.KindText}, {mod.SizeText}, {(mod.Enabled ? "enabled" : "disabled")})");
        Console.WriteLine($"  confidence: {mod.ConfidenceText} · {mod.ContainerSummary}");
        Console.WriteLine("  " + mod.RiskSummary);
        foreach (var e in mod.Evidence.Where(e => e.Category is ModEvidenceCategory.Script or ModEvidenceCategory.Asset).Take(3))
            Console.WriteLine($"  - [{e.ConfidenceText}] {e.Title}: {e.Detail}");
        foreach (var e in mod.Evidence.Where(e => e.Category == ModEvidenceCategory.Container).Take(2))
            Console.WriteLine($"  - [{e.ConfidenceText}] {e.Title}: {e.Source}");
        if (!mod.QuarantineEligible) Console.WriteLine("  quarantine: blocked · " + mod.QuarantineBlockReason);
    }
    if (report.Overlaps.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("OVERLAPS");
        foreach (var o in report.Overlaps.Take(12))
            Console.WriteLine($"  {string.Join(" + ", o.Mods)} · {o.SharedAssetCount} shared path(s) · e.g. {o.Asset}");
    }
    if (report.Notes.Count > 0)
    {
        Console.WriteLine();
        foreach (var note in report.Notes) Console.WriteLine("NOTE: " + note);
    }
}


