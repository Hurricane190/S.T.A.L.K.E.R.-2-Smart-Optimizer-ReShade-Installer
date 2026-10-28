using ZoneTune.Core;
using ZoneTune.Core.Models;
using ZoneTune.Core.Services;

namespace ZoneTune.Cli;

public static class SandboxTest
{
    public static async Task<int> RunAsync(AppSettings settings)
    {
        Console.WriteLine("ReShade sandbox install test (does not touch the real game)");
        var real = new GameLocator().Locate(settings.GamePathOverride, settings.GamePathOverride);
        if (!real.IsValid)
        {
            Console.Error.WriteLine("Real game not found; cannot copy the executable for a realistic sandbox.");
            return 2;
        }

        var root = Path.Combine(Path.GetTempPath(), "ZoneTune", "SandboxGame");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        var bin = Path.Combine(root, "Stalker2", "Binaries", "Win64");
        var paks = Path.Combine(root, "Stalker2", "Content", "Paks");
        var config = Path.Combine(root, "Saved", "Config", "Windows");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(paks);
        Directory.CreateDirectory(config);
        File.Copy(real.ShippingExePath, Path.Combine(bin, "Stalker2-Win64-Shipping.exe"));

        var game = new GameInstall
        {
            Root = root,
            ShippingExePath = Path.Combine(bin, "Stalker2-Win64-Shipping.exe"),
            BinariesDir = bin,
            PaksDir = paks,
            ModsDir = Path.Combine(paks, "~mods"),
            DisabledModsDir = Path.Combine(paks, "~mods.disabled"),
            ConfigDir = config,
            LogsDir = Path.Combine(root, "Saved", "Logs"),
            AppDataDir = Path.Combine(root, "Saved"),
            IsValid = true,
            Source = GameSource.Manual
        };

        var hw = new HardwareService().Detect(root);
        var preset = new CustomPresetBuilder().Build(hw, new CustomPresetOptions
        {
            Mode = ReShadeMode.High,
            Name = "Sandbox Custom High",
            UseLevels = true,
            UseColourfulness = true,
            UseDeband = true,
            UseSharpen = true,
            UseFilmic = true,
            UseBloom = true
        });
        Console.WriteLine($"Preset: {preset.Name} · {preset.Techniques.Count} effects · {preset.EstimatedCost}");
        var backups = new BackupService(Path.Combine(root, "Backups"));
        var installer = new ReShadeInstaller();

        Console.WriteLine("Target: " + bin);
        var progress = new Progress<OperationProgress>(p => Console.WriteLine($"  [{p.Percent,3}%] {p.Stage}: {p.Detail}"));
        var result = await installer.InstallAsync(game, hw, preset, backups, settings, progress, ignoreRunningGame: true);

        Console.WriteLine($"Success: {result.Success}");
        foreach (var m in result.Messages) Console.WriteLine("  " + m);
        foreach (var w in result.Warnings) Console.WriteLine("  WARN: " + w);
        foreach (var e in result.Errors) Console.WriteLine("  ERROR: " + e);
        Console.WriteLine("Verification: " + result.Verification);

        var ok = result.Success;
        var info = new ReShadeService().Detect(game, hw);
        ok &= info.Installed;
        ok &= File.Exists(Path.Combine(bin, "ReShade.ini"));
        ok &= info.PresetPath.EndsWith(preset.Name + ".ini", StringComparison.OrdinalIgnoreCase) ||
              info.PresetName.Equals(AppPaths.SafeFileName(preset.Name) + ".ini", StringComparison.OrdinalIgnoreCase);
        ok &= info.ShaderReferences.All(s => s.Present);
        Console.WriteLine($"Detected after install: installed={info.Installed}, version={info.Version}, preset={info.PresetName}, missingShaders={info.ShaderReferences.Count(s => !s.Present)}");

        if (ok)
        {
            Console.WriteLine("Removing ReShade with the official uninstaller (sandbox)...");
            var removal = await installer.RemoveReShadeAsync(game, backups, settings,
                new Progress<OperationProgress>(p => Console.WriteLine($"  [{p.Percent,3}%] {p.Stage}: {p.Detail}")), ignoreRunningGame: true);
            foreach (var m in removal.Messages) Console.WriteLine("  " + m);
            foreach (var e in removal.Errors) Console.WriteLine("  ERROR: " + e);
            ok &= removal.Success;
            var removedInfo = new ReShadeService().Detect(game, hw);
            ok &= !removedInfo.Installed;
            Console.WriteLine($"Detected after removal: installed={removedInfo.Installed}");

            if (removal.ChangeSetId.Length > 0)
            {
                Console.WriteLine("Restoring the removal change set...");
                var restore = backups.Restore(removal.ChangeSetId);
                ok &= restore.Success;
                var restoredInfo = new ReShadeService().Detect(game, hw);
                ok &= restoredInfo.Installed;
                Console.WriteLine($"Detected after restoring ReShade: installed={restoredInfo.Installed}");
            }
            else ok = false;
        }

        if (ok)
        {
            Console.WriteLine("Uninstalling the original install change set...");
            var un = installer.Uninstall(game, backups, result.ChangeSetId, ignoreRunningGame: true);
            foreach (var m in un.Messages) Console.WriteLine("  " + m);
            foreach (var e in un.Errors) Console.WriteLine("  ERROR: " + e);
            ok &= un.Success;
            var after = new ReShadeService().Detect(game, hw);
            ok &= !after.Installed;
            ok &= !File.Exists(Path.Combine(bin, "ReShade.ini"));
            ok &= !File.Exists(Path.Combine(bin, AppPaths.SafeFileName(preset.Name) + ".ini"));
            Console.WriteLine($"Detected after uninstall: installed={after.Installed}");
        }

        Console.WriteLine(ok ? "SANDBOX RESULT: PASS" : "SANDBOX RESULT: FAIL");
        Log.Info("ReShade sandbox test result: " + (ok ? "PASS" : "FAIL"));
        try { Directory.Delete(root, recursive: true); } catch (Exception ex) { Console.WriteLine("Sandbox cleanup: " + ex.Message); }
        return ok ? 0 : 1;
    }
}
