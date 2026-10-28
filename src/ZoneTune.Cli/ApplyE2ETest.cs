using ZoneTune.Core;
using ZoneTune.Core.Models;
using ZoneTune.Core.Services;

namespace ZoneTune.Cli;

/// <summary>
/// Real end-to-end test of the one-click in-game settings feature:
/// hash config -> build profile -> apply -> validate -> restore -> hash compare.
/// The game must be closed. The original config is left byte-identical.
/// </summary>
public static class ApplyE2ETest
{
    public static int Run(AppSettings settings, ScanService scanService, OptimizationEngine engine, BackupService backups)
    {
        Console.WriteLine("ZoneTune real apply/restore test (game must be closed)");
        var scan = scanService.Scan(settings, includeMods: false);
        if (!scan.Game.IsValid) { Console.Error.WriteLine("Game not found."); return 2; }
        if (new ConfigService().IsGameRunning())
        {
            Console.Error.WriteLine("STALKER 2 is running. Close it before this test.");
            return 2;
        }

        var gu = scan.Config.RawGameUserSettingsPath;
        var enginePath = scan.Config.RawEngineIniPath;
        var beforeGu = Snapshot(gu);
        var beforeEngine = Snapshot(enginePath);
        var plan = engine.BuildRecommendedProfile(scan);
        var changes = plan.ConfigActions.ToList();
        Console.WriteLine($"Profile: {plan.Summary}");
        Console.WriteLine($"Planned changes: {changes.Count}");
        if (changes.Count == 0)
        {
            Console.WriteLine("Nothing to apply; current config already matches.");
            return 0;
        }

        var result = engine.Apply(plan, backups);
        Console.WriteLine($"Apply success: {result.Success}; change set: {result.ChangeSetId}");
        foreach (var m in result.Messages.Take(12)) Console.WriteLine("  " + m);
        foreach (var e in result.Errors.Take(12)) Console.WriteLine("  ERROR: " + e);
        if (!result.Success) return 1;

        var afterApply = IniDocument.Load(gu);
        var allWritten = true;
        foreach (var action in changes)
        {
            var ini = IniDocument.Load(action.FilePath);
            var actual = ini.Get(action.Section, action.Key);
            var ok = string.Equals(actual?.Trim(), action.ProposedValue.Trim(), StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  {(ok ? "OK " : "BAD")} {action.Key}={actual} (expected {action.ProposedValue})");
            allWritten &= ok;
            if (!ok) break;
        }
        Console.WriteLine($"All values verified in files: {allWritten}");

        var restore = backups.Restore(result.ChangeSetId);
        Console.WriteLine($"Restore success: {restore.Success}");
        foreach (var e in restore.Errors.Take(8)) Console.WriteLine("  ERROR: " + e);
        var afterGu = Snapshot(gu);
        var afterEngine = Snapshot(enginePath);
        var identical = beforeGu == afterGu && beforeEngine == afterEngine;
        Console.WriteLine($"GameUserSettings hash restored: {beforeGu == afterGu}");
        Console.WriteLine($"Engine.ini state restored: {beforeEngine == afterEngine}");

        var pass = allWritten && restore.Success && identical;
        Console.WriteLine(pass ? "REAL APPLY TEST: PASS" : "REAL APPLY TEST: FAIL");
        Log.Info("Real apply test result: " + (pass ? "PASS" : "FAIL"));
        return pass ? 0 : 1;
    }

    private static string Snapshot(string path)
    {
        if (!File.Exists(path)) return "absent";
        return BackupService.HashFile(path);
    }
}
