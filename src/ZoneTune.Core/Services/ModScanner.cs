using System.Text.RegularExpressions;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Read-only inventory of pak mods, UE4SS Lua mods, ASI plugins and mod-manager
/// metadata. Disabling is handled by ModService using reversible folder moves.
/// </summary>
public sealed class ModScanner
{
    public (List<ModEntry> Mods, List<ModConflict> Conflicts) Scan(GameInstall game)
    {
        var mods = new List<ModEntry>();
        var conflicts = new List<ModConflict>();
        if (!game.IsValid) return (mods, conflicts);

        ScanPakMods(game, mods, conflicts);
        ScanUe4ss(game, mods);
        ScanAsi(game, mods);
        ScanKnownExtras(game, mods, conflicts);
        ScanConflictLogs(game, conflicts);

        Log.Info($"Mod scan: {mods.Count} entries, {conflicts.Count} conflict notes");
        return (mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList(), conflicts);
    }

    private static void ScanPakMods(GameInstall game, List<ModEntry> mods, List<ModConflict> conflicts)
    {
        if (!Directory.Exists(game.ModsDir)) return;
        var pakNames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in SafeDirectories(game.ModsDir))
        {
            var name = Path.GetFileName(dir);
            var files = SafeFiles(dir, 4);
            var entry = new ModEntry
            {
                Name = name,
                Path = dir,
                Kind = name.StartsWith("~", StringComparison.Ordinal) ? ModKind.MergeOutput : ModKind.PakMod,
                SizeBytes = files.Sum(f => SafeLength(f)),
                Enabled = true,
                IsDirectory = true,
                Notes = $"{files.Length} files"
            };
            entry.HasPak = files.Any(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase));
            entry.HasUtoc = files.Any(f => f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase));
            if (name.Contains("S2MUU", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Merger", StringComparison.OrdinalIgnoreCase))
                entry.Kind = ModKind.MergeOutput;
            mods.Add(entry);
        }

        // Only top-level loose paks are separate components. Recursing here would list
        // every pak inside a mod folder a second time (and inflate the inventory).
        foreach (var file in SafeFiles(game.ModsDir, 0))
        {
            var ext = Path.GetExtension(file);
            if (!ext.Equals(".pak", StringComparison.OrdinalIgnoreCase)) continue;
            var baseName = Path.GetFileNameWithoutExtension(file);
            var utoc = Path.Combine(game.ModsDir, baseName + ".utoc");
            var ucas = Path.Combine(game.ModsDir, baseName + ".ucas");
            var size = SafeLength(file) + SafeLength(utoc) + SafeLength(ucas);
            var mod = new ModEntry
            {
                Name = baseName,
                Path = file,
                Kind = Regex.IsMatch(baseName, "_P$", RegexOptions.IgnoreCase) ? ModKind.PakMod : ModKind.PakMod,
                SizeBytes = size,
                Enabled = true,
                HasPak = true,
                HasUtoc = File.Exists(utoc),
                IsDirectory = false,
                Notes = File.Exists(utoc) ? "IoStore pak" : "Pak mod (no .utoc)"
            };
            mods.Add(mod);
            if (!pakNames.TryGetValue(baseName, out var list)) pakNames[baseName] = list = new();
            list.Add(file);
            if (!mod.HasUtoc)
                mod.Notes += " · pak-only mod";
        }

        foreach (var kv in pakNames.Where(k => k.Value.Count > 1))
        {
            conflicts.Add(new ModConflict
            {
                Title = $"Duplicate pak name: {kv.Key}",
                Detail = "Two files resolve to the same pak name. The game can only mount one; disable one copy.",
                Mods = kv.Value.Select(Path.GetFileName).ToList()!,
                Severity = Severity.Warning,
                Source = "File name scan"
            });
        }

        // The loader mounts ~mods after base paks. A *_P pak is a patch; make that visible
        // rather than treating it as an error.
        foreach (var mod in mods.Where(m => m.Name.EndsWith("_P", StringComparison.OrdinalIgnoreCase)))
            if (string.IsNullOrEmpty(mod.Notes)) mod.Notes = "Patch pak (_P)";

        var disabled = Path.Combine(game.PaksDir, "~mods.disabled");
        if (Directory.Exists(disabled))
        {
            foreach (var dir in SafeDirectories(disabled))
            {
                mods.Add(new ModEntry
                {
                    Name = Path.GetFileName(dir),
                    Path = dir,
                    Kind = ModKind.Other,
                    SizeBytes = SafeFiles(dir, 3).Sum(f => SafeLength(f)),
                    Enabled = false,
                    IsDirectory = true,
                    Notes = "Disabled by ZoneTune (moved out of ~mods)"
                });
            }
            foreach (var file in SafeFiles(disabled, 1).Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
            {
                mods.Add(new ModEntry
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Path = file,
                    Kind = ModKind.PakMod,
                    SizeBytes = SafeLength(file),
                    Enabled = false,
                    IsDirectory = false,
                    Notes = "Disabled by ZoneTune (moved out of ~mods)"
                });
            }
        }
    }

    private static void ScanUe4ss(GameInstall game, List<ModEntry> mods)
    {
        var root = Path.Combine(game.BinariesDir, "ue4ss", "Mods");
        if (!Directory.Exists(root)) return;
        var txtStates = ReadUe4ssModList(Path.Combine(game.BinariesDir, "ue4ss", "mods.txt"));
        var jsonStates = ReadUe4ssJson(Path.Combine(root, "mods.json"));
        var loadOrderStates = ReadUe4ssLoadOrder(root);

        foreach (var dir in SafeDirectories(root))
        {
            var name = Path.GetFileName(dir);
            var files = SafeFiles(dir, 5);
            var hasEnabled = File.Exists(Path.Combine(dir, "enabled.txt"));
            var hasDisabled = File.Exists(Path.Combine(dir, "disabled.txt"));
            var hasDisabledMarker = File.Exists(Path.Combine(dir, "enabled.txt.disabled"));
            var allDisabled = files.Length > 0 &&
                               files.Where(f => f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ||
                                                f.EndsWith(".e2f_disabled", StringComparison.OrdinalIgnoreCase))
                                   .Count() == files.Length;
            var enabled = hasEnabled || (!hasDisabled && !hasDisabledMarker && !allDisabled &&
                (jsonStates.TryGetValue(name, out var jsonState) ? jsonState :
                 loadOrderStates.TryGetValue(name, out var loadState) ? loadState :
                 !txtStates.TryGetValue(name, out var txtState) || txtState));
            var source = hasEnabled ? "enabled.txt" :
                hasDisabled ? "disabled.txt" :
                hasDisabledMarker || allDisabled ? "disabled file markers" :
                jsonStates.ContainsKey(name) ? "mods.json" :
                loadOrderStates.ContainsKey(name) ? "UE4SS load order" :
                txtStates.ContainsKey(name) ? "mods.txt" : "default (no state marker)";
            mods.Add(new ModEntry
            {
                Name = name,
                Path = dir,
                Kind = ModKind.Ue4ssLua,
                SizeBytes = files.Sum(f => SafeLength(f)),
                Enabled = enabled,
                IsDirectory = true,
                Notes = (enabled ? "UE4SS Lua mod" : "Disabled") + $" · state from {source}"
            });
        }
    }

    private static Dictionary<string, bool> ReadUe4ssJson(string path)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var name = item.TryGetProperty("mod_name", out var n) ? n.GetString() :
                           item.TryGetProperty("name", out var n2) ? n2.GetString() : null;
                var enabled = item.TryGetProperty("mod_enabled", out var e) && e.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                    ? e.GetBoolean()
                    : item.TryGetProperty("enabled", out var e2) && e2.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                        ? e2.GetBoolean()
                        : (bool?)null;
                if (!string.IsNullOrWhiteSpace(name) && enabled != null) result[name] = enabled.Value;
            }
        }
        catch { }
        return result;
    }

    private static Dictionary<string, bool> ReadUe4ssLoadOrder(string modsRoot)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(modsRoot, "*_ue4ss_loadOrder.json", SearchOption.TopDirectoryOnly))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    var id = item.TryGetProperty("id", out var i) ? i.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var enabled = !item.TryGetProperty("enabled", out var e) ||
                                  e.ValueKind != System.Text.Json.JsonValueKind.False;
                    result[id] = enabled;
                }
            }
        }
        catch { }
        return result;
    }

    private static Dictionary<string, bool> ReadUe4ssModList(string path)
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
                var parts = trimmed.Split(':', 2);
                if (parts.Length == 2) result[parts[0].Trim()] = parts[1].Trim() == "1";
            }
        }
        catch { }
        return result;
    }

    private static void ScanAsi(GameInstall game, List<ModEntry> mods)
    {
        if (!Directory.Exists(game.BinariesDir)) return;
        foreach (var file in SafeFiles(game.BinariesDir, 0).Where(f => f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            mods.Add(new ModEntry
            {
                Name = name,
                Path = file,
                Kind = ModKind.AsiPlugin,
                SizeBytes = SafeLength(file),
                Enabled = true,
                IsDirectory = false,
                Notes = File.Exists(Path.ChangeExtension(file, ".ini")) ? "ASI plugin · config present" : "ASI plugin"
            });
        }
    }

    private static void ScanKnownExtras(GameInstall game, List<ModEntry> mods, List<ModConflict> conflicts)
    {
        var optimax = Path.Combine(game.ConfigDir, "OptimaxP_Config.ini");
        if (File.Exists(optimax))
        {
            mods.Add(new ModEntry
            {
                Name = "OptimaxP configuration",
                Path = optimax,
                Kind = ModKind.ConfigOverride,
                SizeBytes = SafeLength(optimax),
                Enabled = true,
                Notes = "Third-party config override; can conflict with ZoneTune engine tweaks"
            });
        }

        var mergeTool = Path.Combine(game.Root, "Stalker2SimpleModMerger");
        if (Directory.Exists(mergeTool))
        {
            mods.Add(new ModEntry
            {
                Name = "Stalker2SimpleModMerger",
                Path = mergeTool,
                Kind = ModKind.MergeOutput,
                SizeBytes = 0,
                Enabled = true,
                IsDirectory = true,
                Notes = "Merger tool present; inspect its output before changing pak load order"
            });
        }

        var vortex = SafeFiles(game.BinariesDir, 0).FirstOrDefault(f => f.StartsWith("vortex.deployment.", StringComparison.OrdinalIgnoreCase));
        if (vortex != null)
        {
            mods.Add(new ModEntry
            {
                Name = "Vortex deployment metadata",
                Path = vortex,
                Kind = ModKind.Other,
                SizeBytes = SafeLength(vortex),
                Enabled = true,
                Notes = "Vortex manages file deployment. Disable mods in Vortex to keep its state consistent."
            });
        }

        var loadOrders = SafeFiles(game.Root, 0).Where(f => f.EndsWith("_loadOrder.json", StringComparison.OrdinalIgnoreCase)).ToList();
        if (loadOrders.Count > 0)
        {
            mods.Add(new ModEntry
            {
                Name = "Mod load order files",
                Path = loadOrders[0],
                Kind = ModKind.Other,
                SizeBytes = loadOrders.Sum(SafeLength),
                Enabled = true,
                Notes = $"{loadOrders.Count} load-order file(s) — vortex/load-order metadata"
            });
        }

        var optimaxConflict = mods.FirstOrDefault(m => m.Name == "OptimaxP configuration");
        if (optimaxConflict != null)
        {
            conflicts.Add(new ModConflict
            {
                Title = "Third-party config override detected",
                Detail = "OptimaxP_Config.ini is active alongside the game's own settings. ZoneTune will not overwrite it; " +
                         "Engine.ini tweaks are additive and may be overridden by the mod in some areas.",
                Mods = new List<string> { "OptimaxP configuration" },
                Severity = Severity.Notice,
                Source = "Config scan"
            });
        }
    }

    private static void ScanConflictLogs(GameInstall game, List<ModConflict> conflicts)
    {
        var overview = Path.Combine(game.LogsDir, "Conflicting mods overview.txt");
        if (File.Exists(overview))
        {
            try
            {
                var names = File.ReadLines(overview)
                    .Select(l => l.Trim().TrimStart('1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '.', '\t', ' ').Trim())
                    .Where(l => l.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (names.Count > 0)
                {
                    conflicts.Add(new ModConflict
                    {
                        Title = "Game log reports conflicting mods",
                        Detail = "The game's merger/log output lists paks that modify the same assets. Expect the last-loaded " +
                                 "pak to win and possible visual/quest regressions.",
                        Mods = names,
                        Severity = Severity.Warning,
                        Source = "Conflicting mods overview.txt"
                    });
                }
            }
            catch { }
        }

        var csv = Path.Combine(game.LogsDir, "Mod conflicts.csv");
        if (File.Exists(csv))
        {
            try
            {
                var lines = File.ReadLines(csv).Skip(1).Take(500).ToList();
                if (lines.Count > 0)
                {
                    conflicts.Add(new ModConflict
                    {
                        Title = $"Mod conflict table: {lines.Count}+ overlapping files",
                        Detail = "The game produced a per-file conflict table. The table is read-only evidence; " +
                                 "disable the redundant pak listed under 'Conflicting Mods' and retest.",
                        Mods = lines.Select(l => l.Split(',').ElementAtOrDefault(1)?.Trim('"') ?? "")
                            .Where(s => s.Length > 0).Distinct().Take(20).ToList(),
                        Severity = Severity.Warning,
                        Source = "Mod conflicts.csv"
                    });
                }
            }
            catch { }
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static string[] SafeFiles(string root, int depth)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = depth > 0,
                MaxRecursionDepth = Math.Max(1, depth),
                IgnoreInaccessible = true,
                AttributesToSkip = 0
            }).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
