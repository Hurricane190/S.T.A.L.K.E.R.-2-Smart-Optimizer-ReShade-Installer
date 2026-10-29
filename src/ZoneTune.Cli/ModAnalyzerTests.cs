using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ZoneTune.Core;
using ZoneTune.Core.Models;
using ZoneTune.Core.Services;

namespace ZoneTune.Cli;

/// <summary>
/// Fixture-based tests for the mod analyzer, quarantine service and manual
/// comparison. All destructive operations run in a temporary directory.
/// </summary>
public static class ModAnalyzerTests
{
    private static readonly List<(string name, bool pass, string detail)> Results = new();

    public static List<(string name, bool pass, string detail)> Run()
    {
        Results.Clear();
        var root = Path.Combine(Path.GetTempPath(), "ZoneTuneTest", "mod-analyzer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var game = BuildFixture(root);
            var report = new ModPerformanceAnalyzer().Analyze(game, new HardwareInfo { VramMb = 8192 });

            Check("Mod analyzer discovers all fixture types", () =>
                report.Mods.Any(m => m.Name == "HotLua") &&
                report.Mods.Any(m => m.Name == "BoringPack") &&
                report.Mods.Any(m => m.Name == "NativeOnly") &&
                report.Mods.Any(m => m.Name == "VortexFolder") &&
                report.ModConflicts != null, $"{report.Mods.Count} entries");

            Check("Hot Lua mod is HIGH with confirmed script evidence", () =>
            {
                var hot = report.Mods.First(m => m.Name == "HotLua");
                return hot.Risk == ModRiskLevel.High &&
                       hot.Confidence is ModConfidence.Likely or ModConfidence.Hypothesis &&
                       hot.Evidence.Any(e => e.Category == ModEvidenceCategory.Script && e.Confidence == ModConfidence.Confirmed &&
                                             e.Detail.Contains("100 ms", StringComparison.OrdinalIgnoreCase));
            }, "50 ms loop + actor query + tick hook");

            Check("Legacy pak contents are indexed (no size-only risk)", () =>
            {
                var boring = report.Mods.First(m => m.Name == "BoringPack");
                return boring.Risk == ModRiskLevel.Low &&
                       boring.AssetCount > 0 &&
                       boring.Evidence.Any(e => e.Title == "File size context" && e.Detail.Contains("not used as evidence"));
            }, "low-risk locres pak + explicit size caveat");

            Check("IoStore UTOC directory index is parsed", () =>
            {
                var io = report.Mods.First(m => m.Name == "IoStorePack");
                return io.ContainersRead == 1 && io.AssetCount == 1 &&
                       io.Evidence.Any(e => e.Title == "Archive contents indexed" && e.Detail.Contains("directory index"));
            }, "version 8 UTOC paths");

            Check("Native-only mod is UNKNOWN, never guessed", () =>
            {
                var native = report.Mods.First(m => m.Name == "NativeOnly");
                return native.Risk == ModRiskLevel.Unknown &&
                       native.Confidence == ModConfidence.Insufficient &&
                       native.IsNative;
            }, "ASI has no static cost evidence");

            Check("Corrupt pak is UNKNOWN and does not crash the scan", () =>
            {
                var bad = report.Mods.First(m => m.Name == "Unreadable");
                return bad.Risk == ModRiskLevel.Unknown && bad.ContainersUnreadable > 0;
            }, "corrupt archive handled per container");

            Check("Vortex marker blocks quarantine", () =>
            {
                var vortex = report.Mods.First(m => m.Name == "VortexFolder");
                return vortex.VortexManaged && !vortex.QuarantineEligible &&
                       vortex.QuarantineBlockReason.Contains("Vortex", StringComparison.OrdinalIgnoreCase);
            }, "Vortex-managed entry refused safely");

            Check("UE4SS mods.json disables ColdLua", () =>
            {
                var cold = report.Mods.First(m => m.Name == "ColdLua");
                return !cold.Enabled && !cold.QuarantineEligible;
            }, "state read from mods.json, not mods.txt");

            Check("Version detected from folder name", () =>
                report.Mods.First(m => m.Name.StartsWith("Versioned", StringComparison.Ordinal)).Version == "1.2.3",
                "v1.2.3");

            Check("Mod disable warns when the folder carries a Vortex marker", () =>
            {
                var entry = report.Mods.First(m => m.Name == "VortexDisable").Mod;
                var result = new ModService(Path.Combine(root, "vortex-state.json")).Disable(game, entry);
                return result.Success && result.VortexWarning &&
                       result.Message.Contains("Vortex", StringComparison.OrdinalIgnoreCase);
            }, "per-folder marker detected");

            Check("Ignoring a Medium/High warning persists and can be undone", () =>
            {
                var ignores = new ModIgnoreService(Path.Combine(root, "mod-ignores.json"));
                var hot = report.Mods.First(m => m.Name == "HotLua");
                ignores.Ignore(hot);
                var reloaded = new ModIgnoreService(Path.Combine(root, "mod-ignores.json"));
                var target = new ModPerformanceAnalysis
                {
                    Name = "HotLua", Path = hot.Path, Risk = ModRiskLevel.High, Enabled = true
                };
                reloaded.Apply(new[] { target });
                var hidden = target.Ignored && !target.WarningActive;
                reloaded.Unignore(target);
                var visibleAgain = !target.Ignored && target.WarningActive;
                return hidden && visibleAgain && File.Exists(Path.Combine(root, "mod-ignores.json"));
            }, "persisted path-based ignore + unignore");

            Check("Overlapping asset paths are reported", () =>
                report.Overlaps.Any(o => o.Mods.Contains("OverlapA") && o.Mods.Contains("OverlapB")),
                $"{report.Overlaps.Count} overlap note(s)");

            Check("Repeated scan does not duplicate entries", () =>
            {
                var second = new ModPerformanceAnalyzer().Analyze(game, new HardwareInfo { VramMb = 8192 });
                return second.Mods.Count == report.Mods.Count &&
                       second.Mods.Select(m => m.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == second.Mods.Count;
            }, "stable inventory across scans");

            // ---- quarantine / restore ----
            var quarantineRoot = Path.Combine(root, "quarantine");
            var service = new ModQuarantineService(quarantineRoot);
            var qMod = CreateAnalysis(game, "QuarantineMe");
            var qResult = service.Quarantine(new[] { qMod }, game);
            Check("Quarantine copies, verifies and removes the original", () =>
                qResult.Success &&
                !Directory.Exists(qMod.Path) &&
                service.LoadManifest().Entries.Any(e => e.ModName == "QuarantineMe" && e.Status == "Quarantined" && e.Files.Count >= 2),
                $"{qResult.Entries.FirstOrDefault()?.Files.Count ?? 0} files hash-recorded");

            var restore = service.Restore(qResult.Entries[0].Id, game);
            Check("Restore returns byte-identical files to the original path", () =>
                restore.Success &&
                File.Exists(Path.Combine(qMod.Path, "a.txt")) &&
                File.ReadAllText(Path.Combine(qMod.Path, "a.txt")) == "alpha" &&
                File.ReadAllText(Path.Combine(qMod.Path, "nested", "b.txt")) == "beta" &&
                service.LoadManifest().Entries.Any(e => e.ModName == "QuarantineMe" && e.Status == "Restored"),
                "a.txt + nested/b.txt verified");

            var loosePak = report.Mods.First(m => m.Name == "LoosePak");
            var looseQ = service.Quarantine(new[] { loosePak }, game);
            var looseRestore = service.Restore(looseQ.Entries[0].Id, game);
            Check("Loose pak file is quarantined and restored as a file set", () =>
                looseQ.Success && looseRestore.Success &&
                File.Exists(Path.Combine(game.ModsDir, "LoosePak.pak")) &&
                !File.Exists(Path.Combine(game.ModsDir, "LoosePak.pak.zonetune-restore")),
                "loose .pak round-trip");

            var collisionMod = CreateAnalysis(game, "CollisionMod");
            var collisionQ = service.Quarantine(new[] { collisionMod }, game);
            Directory.CreateDirectory(collisionMod.Path);
            File.WriteAllText(Path.Combine(collisionMod.Path, "occupied.txt"), "do not overwrite");
            var collisionRestore = service.Restore(collisionQ.Entries[0].Id, game);
            Check("Restore refuses to overwrite an existing path", () =>
                !collisionRestore.Success &&
                File.ReadAllText(Path.Combine(collisionMod.Path, "occupied.txt")) == "do not overwrite" &&
                Directory.Exists(collisionQ.Entries[0].QuarantinePath),
                "collision reported; quarantine copy kept");

            var tamperMod = CreateAnalysis(game, "TamperMod");
            var tamperQ = service.Quarantine(new[] { tamperMod }, game);
            var tamperEntry = tamperQ.Entries[0];
            var storedFile = Path.Combine(service.Root, tamperEntry.Files[0].RelativePath);
            File.WriteAllText(storedFile, "tampered");
            var tamperRestore = service.Restore(tamperEntry.Id, game);
            Check("Restore detects a changed quarantine file", () =>
                !tamperRestore.Success && tamperRestore.Errors.Any(e => e.Contains("changed", StringComparison.OrdinalIgnoreCase)),
                "hash mismatch refused");

            var vortexQ = service.Validate(CreateAnalysis(game, "VortexFolder"), game);
            Check("Quarantine service independently refuses Vortex", () =>
                !vortexQ.CanMove && vortexQ.Reason.Contains("Vortex", StringComparison.OrdinalIgnoreCase),
                "defence in depth");

            var outsideFile = Path.Combine(game.Root, "Stalker2.exe");
            File.WriteAllText(outsideFile, "not a mod");
            var outsideQ = service.Validate(new ModPerformanceAnalysis
            {
                Name = "Fake game file",
                Path = outsideFile,
                Enabled = true,
                QuarantineEligible = true
            }, game);
            Check("Quarantine refuses a path outside recognized mod roots", () =>
                !outsideQ.CanMove && outsideQ.Reason.Contains("recognized mod", StringComparison.OrdinalIgnoreCase),
                "game files protected");

            // Simulate a crash after copy but before the original was removed.
            var interrupted = CreateAnalysis(game, "InterruptedMod");
            var fakeRel = Path.Combine("Operations", "fake-op", "payload", "InterruptedMod", "x.txt");
            var fakeStored = Path.Combine(service.Root, fakeRel);
            Directory.CreateDirectory(Path.GetDirectoryName(fakeStored)!);
            File.WriteAllText(fakeStored, "recovered");
            var manifest = new QuarantineManifest
            {
                Root = service.Root,
                Entries =
                {
                    new QuarantineEntry
                    {
                        Id = "fake-id", OperationId = "fake-op", ModName = "InterruptedMod", OriginalPath = interrupted.Path,
                        QuarantinePath = Path.Combine(service.Root, "Operations", "fake-op", "payload"),
                        IsDirectory = true, SizeBytes = 8, Status = "Planned",
                        Items = { new QuarantineItem { OriginalPath = interrupted.Path, StoredRelativePath = Path.Combine("Operations", "fake-op", "payload", "InterruptedMod"), IsDirectory = true } },
                        Files = { new QuarantineFileRecord { RelativePath = fakeRel, SizeBytes = 8, Sha256 = BackupService.HashFile(fakeStored) } }
                    }
                }
            };
            File.WriteAllText(service.ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            Directory.Delete(interrupted.Path, true);
            var recovered = service.LoadManifest();
            Check("Interrupted operation is reconciled, not misreported", () =>
                recovered.Entries.Any(e => e.Id == "fake-id" && e.Status == "Quarantined"),
                "Planned + original missing -> Quarantined");

            // ---- comparison ----
            var comparisons = new ModComparisonService(Path.Combine(root, "comparisons"));
            var beforeCsv = WriteCapture(root, "before.csv", 900, 100);
            var afterCsv = WriteCapture(root, "after.csv", 950, 50);
            var before = comparisons.Import(beforeCsv);
            var after = comparisons.Import(afterCsv);
            var record = comparisons.Compare(before, after, "Fixture comparison", "Same route; test only.", "HotLua");
            Check("Manual comparison stores measured stats and caveats", () =>
                File.Exists(Path.Combine(comparisons.DirectoryPath, record.Id + ".json")) &&
                record.ResultText.Contains("Average FPS", StringComparison.Ordinal) &&
                record.Warnings.Any(w => w.Contains("does not attribute", StringComparison.OrdinalIgnoreCase)) &&
                comparisons.Recent(1).Count == 1,
                $"{record.Before.Frames} -> {record.After.Frames} frames; caveats present");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
        return Results.ToList();
    }

    private static GameInstall BuildFixture(string root)
    {
        var paks = Path.Combine(root, "Stalker2", "Content", "Paks");
        var mods = Path.Combine(paks, "~mods");
        var disabled = Path.Combine(paks, "~mods.disabled");
        var binaries = Path.Combine(root, "Stalker2", "Binaries", "Win64");
        var ue4ss = Path.Combine(binaries, "ue4ss", "Mods");
        var config = Path.Combine(root, "Config");
        var logs = Path.Combine(root, "Logs");
        Directory.CreateDirectory(mods);
        Directory.CreateDirectory(disabled);
        Directory.CreateDirectory(ue4ss);
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(logs);

        var game = new GameInstall
        {
            Root = root,
            PaksDir = paks,
            ModsDir = mods,
            DisabledModsDir = disabled,
            BinariesDir = binaries,
            ConfigDir = config,
            LogsDir = logs,
            AppDataDir = Path.Combine(root, "Saved"),
            IsValid = true,
            Source = GameSource.Manual
        };

        // A normal low-risk pak.
        Directory.CreateDirectory(Path.Combine(mods, "BoringPack"));
        WriteLegacyPak(Path.Combine(mods, "BoringPack", "Boring.pak"), ("en/Game.locres", 64));

        // Overlap pair.
        foreach (var name in new[] { "OverlapA", "OverlapB" })
        {
            Directory.CreateDirectory(Path.Combine(mods, name));
            WriteLegacyPak(Path.Combine(mods, name, name + ".pak"), ("Textures/T_shared_d.uasset", 512));
        }

        // IoStore UTOC fixture (version 8 directory index).
        var io = Path.Combine(mods, "IoStorePack");
        Directory.CreateDirectory(io);
        WriteIoStore(Path.Combine(io, "IoStorePack.utoc"));
        File.WriteAllBytes(Path.Combine(io, "IoStorePack.ucas"), new byte[] { 1, 2, 3, 4 });

        // A loose pak.
        WriteLegacyPak(Path.Combine(mods, "LoosePak.pak"), ("GameLite/ItemPrototypes/Test.cfg", 32));

        // Corrupt pak.
        Directory.CreateDirectory(Path.Combine(mods, "Unreadable"));
        File.WriteAllBytes(Path.Combine(mods, "Unreadable", "bad.pak"), Enumerable.Repeat((byte)0x5A, 256).ToArray());

        // Vortex-managed folder.
        var vortex = Path.Combine(mods, "VortexFolder");
        Directory.CreateDirectory(vortex);
        File.WriteAllText(Path.Combine(vortex, "__folder_managed_by_vortex"), "managed");
        File.WriteAllText(Path.Combine(vortex, "data.txt"), "x");
        var vortexDisable = Path.Combine(mods, "VortexDisable");
        Directory.CreateDirectory(vortexDisable);
        File.WriteAllText(Path.Combine(vortexDisable, "__folder_managed_by_vortex"), "managed");
        File.WriteAllText(Path.Combine(vortexDisable, "data.txt"), "x");

        // Version fixture.
        Directory.CreateDirectory(Path.Combine(mods, "Versioned v1.2.3"));
        File.WriteAllText(Path.Combine(mods, "Versioned v1.2.3", "readme.txt"), "version test");

        // UE4SS hot/cold mods.
        var hot = Path.Combine(ue4ss, "HotLua", "Scripts");
        Directory.CreateDirectory(hot);
        File.WriteAllText(Path.Combine(hot, "main.lua"),
            "RegisterHook('/Script/Engine.PlayerController:Tick', function() end)\n" +
            "LoopAsync(50, function()\n  local a = FindAllOf('Actor')\n  SpawnActor('Actor')\nend)\n");
        var cold = Path.Combine(ue4ss, "ColdLua", "Scripts");
        Directory.CreateDirectory(cold);
        File.WriteAllText(Path.Combine(cold, "main.lua"), "LoopAsync(50, function() FindAllOf('Actor') end)\n");
        File.WriteAllText(Path.Combine(ue4ss, "mods.json"),
            "[{\"mod_name\":\"HotLua\",\"mod_enabled\":true},{\"mod_name\":\"ColdLua\",\"mod_enabled\":false}]");

        // Native-only ASI.
        File.WriteAllBytes(Path.Combine(binaries, "NativeOnly.asi"), Enumerable.Repeat((byte)0x90, 128).ToArray());

        // Quarantine fixture with nested files.
        var q = Path.Combine(mods, "QuarantineMe");
        Directory.CreateDirectory(Path.Combine(q, "nested"));
        File.WriteAllText(Path.Combine(q, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(q, "nested", "b.txt"), "beta");

        return game;
    }

    private static ModPerformanceAnalysis CreateAnalysis(GameInstall game, string name)
    {
        var path = Path.Combine(game.ModsDir, name);
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        if (name == "CollisionMod")
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "a.txt"), "collision");
        }
        if (name == "TamperMod") File.WriteAllText(Path.Combine(path, "a.txt"), "tamper");
        if (name == "InterruptedMod") File.WriteAllText(Path.Combine(path, "x.txt"), "recovered");
        return new ModPerformanceAnalysis
        {
            Name = name,
            Path = path,
            KindText = "Pak mod",
            Enabled = true,
            SizeBytes = 1024,
            QuarantineEligible = true
        };
    }

    private static void WriteIoStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
        var header = new byte[144];
        var magic = Encoding.ASCII.GetBytes("-==--==--==--==-");
        Array.Copy(magic, header, magic.Length);
        header[16] = 8;
        BitConverter.GetBytes(144u).CopyTo(header, 20);
        BitConverter.GetBytes(0u).CopyTo(header, 24); // toc entries
        BitConverter.GetBytes(0u).CopyTo(header, 28); // compressed blocks
        BitConverter.GetBytes(12u).CopyTo(header, 32);
        BitConverter.GetBytes(0u).CopyTo(header, 36); // method names
        BitConverter.GetBytes(32u).CopyTo(header, 40);
        BitConverter.GetBytes(0u).CopyTo(header, 44);
        BitConverter.GetBytes(0u).CopyTo(header, 48); // directory index size (scanned)
        writer.Write(header);

        WriteFString(writer, "../../../Stalker2/Content/");
        writer.Write(2); // directories: root + Textures
        writer.Write(uint.MaxValue);
        writer.Write(1u);
        writer.Write(uint.MaxValue);
        writer.Write(uint.MaxValue);
        writer.Write(0u); // Textures
        writer.Write(uint.MaxValue);
        writer.Write(uint.MaxValue);
        writer.Write(0u); // first file
        writer.Write(1); // files
        writer.Write(1u); // file name -> T_shared.uasset
        writer.Write(uint.MaxValue);
        writer.Write(0u);
        writer.Write(2); // strings
        WriteFString(writer, "Textures");
        WriteFString(writer, "T_shared.uasset");
    }

    private static void WriteLegacyPak(string path, params (string name, long size)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
        writer.Write(Enumerable.Repeat((byte)0, 32).ToArray());
        var indexOffset = fs.Position;
        WriteFString(writer, "../../../");
        writer.Write(entries.Length);
        foreach (var entry in entries)
        {
            WriteFString(writer, entry.name);
            writer.Write(0L);
            writer.Write(entry.size);
            writer.Write(entry.size);
            writer.Write(0u); // compression method
            writer.Write(new byte[20]); // hash
            writer.Write((byte)0); // flags
            writer.Write(0u); // block size
        }
        var indexSize = fs.Position - indexOffset;
        writer.Write(0x5A6F12E1u);
        writer.Write(8);
        writer.Write(indexOffset);
        writer.Write(indexSize);
        writer.Write(new byte[20]);
        writer.Write(new byte[160]);
    }

    private static void WriteFString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string WriteCapture(string root, string name, int fastFrames, int slowFrames)
    {
        var path = Path.Combine(root, name);
        var sb = new StringBuilder("Application,ProcessID,MsBetweenPresents,TimeInSeconds\n");
        var time = 0.0;
        for (var i = 0; i < fastFrames; i++)
        {
            sb.AppendLine($"game,1,{16.6667:0.0000},{time:0.0000}");
            time += 0.0166667;
        }
        for (var i = 0; i < slowFrames; i++)
        {
            sb.AppendLine($"game,1,{33.3333:0.0000},{time:0.0000}");
            time += 0.0333333;
        }
        File.WriteAllText(path, sb.ToString());
        return path;
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
}
