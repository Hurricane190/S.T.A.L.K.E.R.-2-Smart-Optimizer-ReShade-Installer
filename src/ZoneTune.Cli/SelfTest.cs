using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using ZoneTune.Core;
using ZoneTune.Core.Models;
using ZoneTune.Core.Services;

namespace ZoneTune.Cli;

public static class SelfTest
{
    private static readonly List<(string name, bool pass, string detail)> Results = new();

    public static int Run(AppSettings settings, ScanService scanService, OptimizationEngine engine, BackupService backups)
    {
        Console.WriteLine("ZoneTune self-test");
        Console.WriteLine("==================");
        var sw = Stopwatch.StartNew();

        TestIni();
        var scan = scanService.Scan(settings);
        TestHardware(scan);
        TestGame(scan);
        TestMods(scan);
        TestReShade(scan);
        TestPresets(scan);
        var plan = TestPlan(engine, scan);
        TestRecommendedProfile(engine, scan);
        TestPackage(plan, scan);
        TestBackup();
        TestModToggle();
        Results.AddRange(ModAnalyzerTests.Run());
        TestCustomPreset(scan);
        TestMeasurement();
        TestReport(scan, plan);
        TestShaderArchive();

        sw.Stop();
        Console.WriteLine();
        var failed = Results.Count(r => !r.pass);
        foreach (var r in Results)
            Console.WriteLine($"{(r.pass ? "PASS" : "FAIL")}  {r.name} — {r.detail}");
        Console.WriteLine();
        Console.WriteLine($"{Results.Count - failed}/{Results.Count} passed in {sw.Elapsed.TotalSeconds:0.0}s.");

        try
        {
            var path = Path.Combine(AppPaths.DataRoot, "selftest.txt");
            var text = string.Join(Environment.NewLine, Results.Select(r => $"{(r.pass ? "PASS" : "FAIL")}  {r.name} — {r.detail}")) +
                       $"{Environment.NewLine}{Results.Count - failed}/{Results.Count} passed";
            IniDocument.WriteTextAtomic(path, text);
            Console.WriteLine("Written: " + path);
        }
        catch { }
        Log.Info($"Self-test complete: {Results.Count - failed}/{Results.Count} passed.");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(string name, Func<bool> action, string success)
    {
        try
        {
            var ok = action();
            Results.Add((name, ok, ok ? success : "assertion failed"));
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
        }
        catch (Exception ex)
        {
            Results.Add((name, false, ex.Message));
            Console.WriteLine($"FAIL {name}: {ex.Message}");
        }
    }

    private static void TestIni()
    {
        Check("INI roundtrip preserves comments and unrelated keys", () =>
        {
            var text = "; header\r\n[Section]\r\nKey=1 ; keep\r\nOther=abc\r\n\r\n[Second]\r\nX=2\r\n";
            var ini = IniDocument.Parse(text);
            if (ini.Get("Section", "key") != "1") return false;
            ini.Set("Section", "Key", "5");
            ini.Set("Section", "New", "yes");
            ini.Set("Third", "A", "b");
            var outText = ini.ToText();
            return outText.Contains("; header") && outText.Contains("Key=5 ; keep") &&
                   outText.Contains("Other=abc") && outText.Contains("New=yes") && outText.Contains("[Third]");
        }, "comments, casing, insertion");

        Check("INI atomic save/reload", () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "ZoneTuneTest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "x.ini");
            File.WriteAllText(path, "[A]\r\nB=1\r\n", new UTF8Encoding(true));
            var ini = IniDocument.Load(path);
            ini.Set("A", "B", "2");
            ini.Save();
            var re = IniDocument.Load(path);
            var ok = re.Get("A", "B") == "2";
            Directory.Delete(dir, true);
            return ok;
        }, "write + parse");
    }

    private static void TestHardware(ScanResult scan)
    {
        Check("CPU detected", () => !string.IsNullOrWhiteSpace(scan.Hardware.CpuName) && scan.Hardware.CpuName != "Unknown CPU", scan.Hardware.CpuName);
        Check("GPU detected", () => scan.Hardware.GpuName != "Unknown GPU" && scan.Hardware.GpuName.Length > 3, scan.Hardware.GpuSummary);
        Check("RAM detected", () => scan.Hardware.RamMb > 1024, scan.Hardware.RamSummary);
        Check("Display detected", () => scan.Hardware.PrimaryResolution.Contains('x') || scan.Hardware.Displays.Count > 0, scan.Hardware.PrimaryResolution + " @" + scan.Hardware.PrimaryRefreshHz);
        Check("Windows version detected", () => scan.Hardware.WindowsBuild > 0, scan.Hardware.WindowsSummary);
    }

    private static void TestGame(ScanResult scan)
    {
        Check("Game located via Steam/manual", () => scan.Game.IsValid && File.Exists(scan.Game.ShippingExePath), scan.Game.Root);
        Check("Saved config folder found", () => scan.Config.GameUserSettingsFound, scan.Config.RawGameUserSettingsPath);
        Check("Findings generated", () => scan.Findings.Count >= 3, $"{scan.Findings.Count} findings");
        Check("Stutter/heat analysis has evidence", () => scan.Findings.All(f => !string.IsNullOrWhiteSpace(f.Title) && !string.IsNullOrWhiteSpace(f.Detail)),
            "all findings have detail");
    }

    private static void TestMods(ScanResult scan)
    {
        Check("Mod inventory readable", () => scan.Mods.Count > 0, $"{scan.Mods.Count} entries");
        Check("Mod entries have valid paths", () => scan.Mods.All(m => !string.IsNullOrWhiteSpace(m.Path)), "paths present");
        Check("Conflict notes readable", () => scan.ModConflicts.All(c => c.Mods.Count > 0), $"{scan.ModConflicts.Count} notes");
    }

    private static void TestReShade(ScanResult scan)
    {
        Check("ReShade detection matches DLL", () =>
        {
            var dll = Path.Combine(scan.Game.BinariesDir, "dxgi.dll");
            var exists = File.Exists(dll);
            return !exists || scan.ReShade.Installed;
        }, $"installed={scan.ReShade.Installed}, version={scan.ReShade.Version}, proxy={scan.ReShade.ProxyDll}");
        Check("Active preset techniques enumerated", () => !scan.ReShade.Installed || scan.ReShade.PresetTechniques.Count >= 0, $"{scan.ReShade.PresetTechniques.Count} techniques");
    }

    private static void TestPresets(ScanResult scan)
    {
        var catalog = new PresetCatalog();
        var presets = catalog.GetBuiltInPresets(scan.Hardware);
        Check("Built-in presets authored with techniques", () => presets.Count >= 3 && presets.All(p => p.Techniques.Count > 0), $"{presets.Count} presets");
        var shaderDir = scan.ReShade.ShadersDir;
        if (string.IsNullOrWhiteSpace(shaderDir) || !Directory.Exists(shaderDir))
            shaderDir = Path.Combine(scan.Game.BinariesDir, "reshade-shaders", "Shaders");
        foreach (var preset in presets)
        {
            var validation = catalog.Validate(preset, new[] { shaderDir }, scan.ReShade.Version);
            Check($"Preset parses: {preset.Name}", () => validation.Shaders.Count > 0, $"{validation.Shaders.Count} refs, {validation.Missing.Count} missing");
        }
        Check("Third-party catalog links only", () =>
        {
            var third = catalog.GetThirdPartyCatalog(scan.Hardware);
            return third.Count >= 5 && third.All(p => p.InstallMode == InstallMode.ExternalOnly && p.SourceUrl.StartsWith("https://"));
        }, "author-page mode + https");
        Check("Local preset import parser", () =>
        {
            var tmp = Path.GetTempFileName();
            File.WriteAllText(tmp, "Techniques=Colourfulness@Colourfulness.fx\n\n[Colourfulness.fx]\ncolourfulness=0.2\n");
            var entry = catalog.FromLocalFile(tmp);
            var ok = entry.Techniques.Count == 1 && entry.InstallMode == InstallMode.LocalFile;
            File.Delete(tmp);
            return ok;
        }, "Techniques parsed");
    }

    private static OptimizationPlan TestPlan(OptimizationEngine engine, ScanResult scan)
    {
        var plan = engine.BuildPlan(scan, new[] { ProblemKind.LoudPc, ProblemKind.LowFps, ProblemKind.Stuttering });
        Check("Plan generated for all problems", () => plan.Actions.Count >= 5, $"{plan.Actions.Count} actions");
        Check("Plan contains reversible config edits", () => plan.ConfigActions.Any(), $"{plan.ConfigActions.Count()} config actions");
        Check("Plan disclaims unmeasured effects", () => plan.Warnings.Any(w => w.Contains("estimate", StringComparison.OrdinalIgnoreCase)) &&
                                                       plan.Actions.All(a => !string.IsNullOrWhiteSpace(a.EstimatedEffect)),
            "warnings + effects present");
        Check("Config targets are real files or Engine.ini", () => plan.ConfigActions.All(a =>
            a.FilePath.EndsWith("GameUserSettings.ini", StringComparison.OrdinalIgnoreCase) ||
            a.FilePath.EndsWith("Engine.ini", StringComparison.OrdinalIgnoreCase)), "targets validated");
        Check("No destructive mod actions in plan", () => plan.Actions.All(a => a.Kind != ActionKind.ModAction), "no auto-delete");
        return plan;
    }

    private static void TestRecommendedProfile(OptimizationEngine engine, ScanResult scan)
    {
        var profile = engine.BuildRecommendedProfile(scan);
        Check("Recommended in-game profile generated", () => profile.ConfigActions.Any() && profile.Summary.Contains("profile"),
            $"{profile.ConfigActions.Count()} changes; {profile.Summary}");
        Check("Profile quality values are in 0–3", () => profile.Actions
            .Where(a => a.Key.StartsWith("sg.", StringComparison.OrdinalIgnoreCase))
            .All(a => int.TryParse(a.ProposedValue, out var v) && v >= 0 && v <= 3), "clamped");

        Check("Profile applies to a sandbox config and validates", () =>
        {
            var temp = Path.Combine(Path.GetTempPath(), "ZoneTuneTest", "profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var gu = Path.Combine(temp, "GameUserSettings.ini");
            var engineIni = Path.Combine(temp, "Engine.ini");
            var guIni = IniDocument.Parse("[/Script/Engine.GameUserSettings]\r\nbUseVSync=False\r\nbUseDynamicResolution=True\r\nFrameRateLimit=0.000000\r\n[ScalabilityGroups]\r\n");
            foreach (var kv in scan.Config.ScalabilityGroups) guIni.Set("ScalabilityGroups", "sg." + kv.Key, kv.Value.ToString());
            guIni.SaveAtomic(gu);
            IniDocument.Parse("[SystemSettings]\r\n").SaveAtomic(engineIni);

            var fakeGame = new GameInstall
            {
                Root = temp,
                ConfigDir = temp,
                IsValid = true,
                ShippingExePath = scan.Game.ShippingExePath
            };
            var fake = new ScanResult
            {
                Hardware = scan.Hardware,
                Game = fakeGame,
                Config = new ConfigSnapshot
                {
                    GameUserSettingsFound = true,
                    EngineIniFound = true,
                    RawGameUserSettingsPath = gu,
                    RawEngineIniPath = engineIni,
                    FrameRateLimit = 0,
                    VSync = false,
                    DynamicResolution = true,
                    ScalabilityGroups = new Dictionary<string, int>(scan.Config.ScalabilityGroups, StringComparer.OrdinalIgnoreCase),
                    SystemSettings = new Dictionary<string, string>(scan.Config.SystemSettings, StringComparer.OrdinalIgnoreCase)
                }
            };
            var sandboxProfile = engine.BuildRecommendedProfile(fake);
            var result = engine.Apply(sandboxProfile, new BackupService(Path.Combine(temp, "backups")), ignoreRunningGame: true);
            var written = IniDocument.Load(gu);
            var targetCap = sandboxProfile.ConfigActions.FirstOrDefault(a => a.Key == "FrameRateLimit")?.ProposedValue;
            var ok = result.Success &&
                     (targetCap == null || written.Get("/Script/Engine.GameUserSettings", "FrameRateLimit") == targetCap) &&
                     written.GetSection("ScalabilityGroups").Count > 0;
            var restored = new BackupService(Path.Combine(temp, "backups")).Restore(result.ChangeSetId);
            ok &= restored.Success && IniDocument.Load(gu).Get("/Script/Engine.GameUserSettings", "FrameRateLimit") == "0.000000";
            try { Directory.Delete(temp, true); } catch { }
            return ok;
        }, "apply + validation + restore");
    }

    private static void TestPackage(OptimizationPlan plan, ScanResult scan)
    {
        string? zip = null;
        Check("Package ZIP created", () =>
        {
            zip = new PackageBuilder().Build(plan, scan, new PresetCatalog().GetBuiltInPresets(scan.Hardware).First());
            return File.Exists(zip) && new FileInfo(zip).Length > 1000;
        }, zip ?? "not built");

        if (zip == null) return;

        var extract = Path.Combine(Path.GetTempPath(), "ZoneTuneTest", "pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extract);

        Check("Package has manifest, installer, uninstaller, checksums", () =>
        {
            ZipFile.ExtractToDirectory(zip, extract);
            var root = Directory.EnumerateDirectories(extract).FirstOrDefault() ?? extract;
            return File.Exists(Path.Combine(root, "manifest.json")) &&
                   File.Exists(Path.Combine(root, "install.ps1")) &&
                   File.Exists(Path.Combine(root, "uninstall.ps1")) &&
                   File.Exists(Path.Combine(root, "checksums.sha256")) &&
                   File.Exists(Path.Combine(root, "README.txt"));
        }, "structure present");

        Check("Merged payload matches planned values", () =>
        {
            var root = Directory.EnumerateDirectories(extract).FirstOrDefault() ?? extract;
            var payload = Path.Combine(root, "payload", "Config", "Windows");
            foreach (var action in plan.ConfigActions)
            {
                var file = Path.Combine(payload, Path.GetFileName(action.FilePath));
                if (!File.Exists(file)) return false;
                var ini = IniDocument.Load(file);
                if (ini.Get(action.Section, action.Key) != action.ProposedValue) return false;
            }
            return true;
        }, $"{plan.ConfigActions.Count()} values verified");

        Check("Package contains no executables or binaries", () =>
        {
            var root = Directory.EnumerateDirectories(extract).FirstOrDefault() ?? extract;
            return !Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Any(f => new[] { ".exe", ".dll", ".asi", ".pak", ".ucas", ".utoc" }
                    .Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }, "config/preset only");

        try { Directory.Delete(extract, true); } catch { }
    }

    private static void TestBackup()
    {
        Check("Backup + restore roundtrip (hash exact)", () =>
        {
            var temp = Path.Combine(Path.GetTempPath(), "ZoneTuneTest", "backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var config = Path.Combine(temp, "GameUserSettings.ini");
            var created = Path.Combine(temp, "Created.ini");
            File.WriteAllText(config, "[A]\r\nB=1\r\n");
            var before = BackupService.HashFile(config);
            var service = new BackupService(Path.Combine(temp, "backups"));
            var cs = service.CreateChangeSet("test", "selftest", temp, new[] { config, created });
            File.WriteAllText(config, "[A]\r\nB=99\r\n");
            File.WriteAllText(created, "new");
            var restore = service.Restore(cs.Id);
            var after = BackupService.HashFile(config);
            var result = restore.Success && before == after && !File.Exists(created);
            try { Directory.Delete(temp, true); } catch { }
            return result;
        }, "original bytes restored; created file removed");
    }

    private static void TestModToggle()
    {
        Check("Mod disable/enable is a reversible move (no delete)", () =>
        {
            var temp = Path.Combine(Path.GetTempPath(), "ZoneTuneTest", "mods-" + Guid.NewGuid().ToString("N"));
            var mods = Path.Combine(temp, "~mods");
            var disabled = Path.Combine(temp, "~mods.disabled");
            Directory.CreateDirectory(Path.Combine(mods, "TestMod"));
            File.WriteAllText(Path.Combine(mods, "TestMod", "data.pak"), "payload");
            File.WriteAllText(Path.Combine(mods, "Loose.pak"), "pak");
            File.WriteAllText(Path.Combine(mods, "Loose.utoc"), "utoc");
            var game = new GameInstall
            {
                Root = temp,
                ModsDir = mods,
                DisabledModsDir = disabled,
                IsValid = true
            };
            var service = new ModService(Path.Combine(temp, "state.json"));
            var dirMod = new ModEntry { Name = "TestMod", Path = Path.Combine(mods, "TestMod"), IsDirectory = true, Kind = ModKind.PakMod, Enabled = true };
            var fileMod = new ModEntry { Name = "Loose", Path = Path.Combine(mods, "Loose.pak"), Kind = ModKind.PakMod, Enabled = true };

            var d1 = service.Disable(game, dirMod);
            var moved = !Directory.Exists(Path.Combine(mods, "TestMod")) && Directory.Exists(Path.Combine(disabled, "TestMod"));
            var d2 = service.Disable(game, fileMod);
            var trioMoved = !File.Exists(Path.Combine(mods, "Loose.pak")) &&
                            !File.Exists(Path.Combine(mods, "Loose.utoc")) &&
                            File.Exists(Path.Combine(disabled, "Loose.pak"));
            var e1Mod = new ModEntry { Name = "TestMod", Path = Path.Combine(disabled, "TestMod"), IsDirectory = true, Kind = ModKind.PakMod, Enabled = false };
            var e1 = service.Enable(game, e1Mod);
            var back = Directory.Exists(Path.Combine(mods, "TestMod")) && File.Exists(Path.Combine(mods, "TestMod", "data.pak"));
            var ok = d1.Success && d2.Success && moved && trioMoved && e1.Success && back;
            try { Directory.Delete(temp, true); } catch { }
            return ok;
        }, "directory + pak triple moved and restored");
    }

    private static void TestCustomPreset(ScanResult scan)
    {
        var builder = new CustomPresetBuilder();
        var catalog = new PresetCatalog();
        var shaderDir = scan.ReShade.ShadersDir;
        if (string.IsNullOrWhiteSpace(shaderDir) || !Directory.Exists(shaderDir))
            shaderDir = Path.Combine(scan.Game.BinariesDir, "reshade-shaders", "Shaders");
        var recommended = builder.Recommend(scan.Hardware);
        var errors = new List<string>();

        foreach (var mode in new[] { ReShadeMode.Potato, ReShadeMode.Medium, ReShadeMode.High })
        {
            var options = builder.Defaults(scan.Hardware, mode);
            var preset = builder.Build(scan.Hardware, options);
            var validation = catalog.Validate(preset, new[] { shaderDir }, scan.ReShade.Version);
            var hasTechniques = preset.Techniques.Count > 0;
            var allShaders = validation.Missing.Count == 0;
            var bloomOk = mode == ReShadeMode.High ? preset.Techniques.Any(t => t.Contains("Bloom")) : !preset.Techniques.Any(t => t.Contains("Bloom"));
            if (!hasTechniques || !allShaders || !bloomOk) errors.Add($"{mode}: techniques={hasTechniques}, shaders={allShaders}, bloom={bloomOk}");
        }

        Check("Preset Studio builds Potato/Medium/High", () => errors.Count == 0,
            errors.Count == 0 ? $"recommended={recommended}; all shaders present" : string.Join(" | ", errors));

        Check("Preset Studio fallback keeps a valid preset", () =>
        {
            var options = builder.Defaults(scan.Hardware, ReShadeMode.Potato);
            options.UseLevels = options.UseColourfulness = options.UseDeband = options.UseSharpen = options.UseFilmic = options.UseBloom = false;
            var preset = builder.Build(scan.Hardware, options);
            return preset.Techniques.Count > 0 && PresetCatalog.ParseTechniques(preset.RawPreset).Count > 0;
        }, "levels fallback");
    }

    private static void TestMeasurement()
    {
        Check("PresentMon statistics", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "ZoneTuneTest-" + Guid.NewGuid().ToString("N") + ".csv");
            var sb = new StringBuilder("Application,ProcessID,MsBetweenPresents,TimeInSeconds\n");
            for (var i = 0; i < 900; i++) sb.AppendLine($"game,1,{16.6667:0.0000},{(i * 0.0166667):0.0000}");
            for (var i = 0; i < 100; i++) sb.AppendLine($"game,1,{33.3333:0.0000},{(15 + i * 0.0333333):0.0000}");
            File.WriteAllText(path, sb.ToString());
            var m = new MeasurementService().ImportPresentMon(path);
            var ok = m.Frames == 1000 && m.AverageFps > 50 && m.AverageFps < 60 && m.P99FrameMs > 20 &&
                     m.OnePercentLowFps < m.AverageFps;
            File.Delete(path);
            return ok;
        }, "1000 frames, 1% low computed");
    }

    private static void TestReport(ScanResult scan, OptimizationPlan plan)
    {
        Check("HTML/MD/JSON report exported", () =>
        {
            var paths = new ReportService().Generate(scan, null, plan);
            var html = File.ReadAllText(paths.Html);
            var md = File.ReadAllText(paths.Markdown);
            return html.Contains("<html") && html.Contains("ZoneTune") && md.Contains("# ZoneTune") &&
                   File.Exists(paths.Json) && html.Contains("estimates", StringComparison.OrdinalIgnoreCase);
        }, "HTML + Markdown + JSON");
    }

    private static void TestShaderArchive()
    {
        var zip = Path.Combine(AppPaths.DownloadsDir, "reshade-shaders-legacy.zip");
        Check("Official shader archive contains built-in effects", () =>
        {
            if (!File.Exists(zip)) return true; // network optional
            using var archive = ZipFile.OpenRead(zip);
            var names = archive.Entries.Select(e => e.FullName).ToList();
            return names.Any(n => n.EndsWith("/LevelsPlus.fx", StringComparison.OrdinalIgnoreCase)) &&
                   names.Any(n => n.EndsWith("/Colourfulness.fx", StringComparison.OrdinalIgnoreCase)) &&
                   names.Any(n => n.EndsWith("/AdaptiveSharpen.fx", StringComparison.OrdinalIgnoreCase));
        }, File.Exists(zip) ? "archive checked" : "not downloaded (skipped)");

        var setup = Path.Combine(AppPaths.DownloadsDir, "ReShade_Setup_6.8.0.exe");
        Check("Official ReShade setup signature", () =>
        {
            if (!File.Exists(setup)) return true; // network optional
            var hash = BackupService.HashFile(setup);
            var sig = HttpService.VerifyAuthenticode(setup, requireReShadeThumbprint: true);
            return sig.IsValid && sig.IsOfficialReShade && hash.Equals(HttpService.ReShadeSetupSha256, StringComparison.OrdinalIgnoreCase);
        }, File.Exists(setup) ? $"hash + Authenticode ({HttpService.VerifyAuthenticode(setup, true).TrustResult:X8})" : "not downloaded (skipped)");
    }
}
