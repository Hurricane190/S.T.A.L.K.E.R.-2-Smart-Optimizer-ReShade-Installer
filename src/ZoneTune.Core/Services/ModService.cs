using System.Text.Json;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Reversible mod enable/disable. Files are moved intact, never deleted; a small
/// state file records the original location so the operation can be undone even
/// after restarting ZoneTune.
/// </summary>
public sealed class ModService
{
    private readonly string _stateFile;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public ModService(string? stateFile = null)
    {
        _stateFile = stateFile ?? Path.Combine(AppPaths.DataRoot, "mod-state.json");
    }

    public sealed class MoveRecord
    {
        public string OriginalPath { get; set; } = "";
        public string CurrentPath { get; set; } = "";
        public string Kind { get; set; } = "";
        public DateTime When { get; set; } = DateTime.Now;
        public string Note { get; set; } = "";
    }

    public List<MoveRecord> LoadState()
    {
        try
        {
            if (!File.Exists(_stateFile)) return new List<MoveRecord>();
            return JsonSerializer.Deserialize<List<MoveRecord>>(File.ReadAllText(_stateFile)) ?? new List<MoveRecord>();
        }
        catch { return new List<MoveRecord>(); }
    }

    private void SaveState(List<MoveRecord> records)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            IniDocument.WriteTextAtomic(_stateFile, JsonSerializer.Serialize(records, _json));
        }
        catch (Exception ex) { Log.Warn("Could not save mod state: " + ex.Message); }
    }

    public (bool Success, string Message, bool VortexWarning) Disable(GameInstall game, ModEntry mod)
    {
        try
        {
            if (new ConfigService().IsGameRunning()) return (false, "Exit STALKER 2 before changing mods; the game holds file handles.", false);
            if (!mod.Enabled) return (true, "Already disabled.", false);
            if (mod.Kind == ModKind.ConfigOverride)
                return (false, "Config overrides are managed as files, not mods. Edit or restore them from Settings & Backups.", false);

            var vortex = VortexManaged(game, mod);
            var state = LoadState();
            var records = new List<MoveRecord>();

            if (mod.Kind == ModKind.AsiPlugin && File.Exists(mod.Path) &&
                mod.Path.EndsWith(".asi", StringComparison.OrdinalIgnoreCase))
            {
                var dest = mod.Path + ".disabled";
                if (File.Exists(dest)) return (false, "A disabled copy already exists: " + dest, vortex);
                File.Move(mod.Path, dest);
                records.Add(new MoveRecord { OriginalPath = mod.Path, CurrentPath = dest, Kind = "asi", Note = "renamed .asi to .asi.disabled" });
            }
            else if (mod.Kind == ModKind.Ue4ssLua && Directory.Exists(mod.Path))
            {
                var disabled = Path.Combine(mod.Path, "disabled.txt");
                var enabled = Path.Combine(mod.Path, "enabled.txt");
                if (!File.Exists(disabled)) File.WriteAllText(disabled, "Disabled by ZoneTune " + DateTime.Now.ToString("u"));
                string? renamed = null;
                if (File.Exists(enabled))
                {
                    renamed = enabled + ".zonetune-off";
                    if (File.Exists(renamed)) File.Delete(renamed);
                    File.Move(enabled, renamed);
                }
                records.Add(new MoveRecord
                {
                    OriginalPath = mod.Path, CurrentPath = mod.Path, Kind = "ue4ss",
                    Note = "created disabled.txt" + (renamed != null ? "; renamed enabled.txt" : "")
                });
            }
            else if (Directory.Exists(mod.Path))
            {
                Directory.CreateDirectory(game.DisabledModsDir);
                var name = Path.GetFileName(mod.Path.TrimEnd('\\'));
                var dest = Unique(Path.Combine(game.DisabledModsDir, name));
                Directory.Move(mod.Path, dest);
                records.Add(new MoveRecord { OriginalPath = mod.Path, CurrentPath = dest, Kind = "directory", Note = "moved out of ~mods" });
            }
            else if (File.Exists(mod.Path))
            {
                Directory.CreateDirectory(game.DisabledModsDir);
                var baseName = Path.GetFileNameWithoutExtension(mod.Path);
                var moved = new List<MoveRecord>();
                if (mod.Path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var ext in new[] { ".pak", ".utoc", ".ucas" })
                    {
                        var src = Path.Combine(Path.GetDirectoryName(mod.Path)!, baseName + ext);
                        if (!File.Exists(src)) continue;
                        var dest = Unique(Path.Combine(game.DisabledModsDir, Path.GetFileName(src)));
                        File.Move(src, dest);
                        moved.Add(new MoveRecord { OriginalPath = src, CurrentPath = dest, Kind = "pak", Note = "moved out of ~mods" });
                    }
                }
                else
                {
                    var dest = Unique(Path.Combine(game.DisabledModsDir, Path.GetFileName(mod.Path)));
                    File.Move(mod.Path, dest);
                    moved.Add(new MoveRecord { OriginalPath = mod.Path, CurrentPath = dest, Kind = "file", Note = "moved out of ~mods" });
                }
                records.AddRange(moved);
            }
            else return (false, "Mod path no longer exists.", vortex);

            state.AddRange(records);
            SaveState(state);
            Log.Info($"Mod disabled: {mod.Name} -> {string.Join(", ", records.Select(r => r.CurrentPath))}");
            var msg = $"Disabled '{mod.Name}'. Files were moved, not deleted.";
            if (vortex) msg += " Vortex may redeploy this file; disable it in Vortex as well to keep deployment metadata consistent.";
            return (true, msg, vortex);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not disable mod {mod.Name}", ex);
            return (false, ex.Message, false);
        }
    }

    public (bool Success, string Message, bool VortexWarning) Enable(GameInstall game, ModEntry mod)
    {
        try
        {
            if (new ConfigService().IsGameRunning()) return (false, "Exit STALKER 2 before changing mods; the game holds file handles.", false);
            if (mod.Enabled) return (true, "Already enabled.", false);
            var vortex = VortexManaged(game, mod);
            var state = LoadState();
            var matched = state.Where(r =>
                string.Equals(r.CurrentPath, mod.Path, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.OriginalPath, mod.Path, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matched.Count > 0)
            {
                foreach (var record in matched)
                {
                    if (record.Kind == "ue4ss" && Directory.Exists(record.OriginalPath))
                    {
                        var disabled = Path.Combine(record.OriginalPath, "disabled.txt");
                        var off = Path.Combine(record.OriginalPath, "enabled.txt.zonetune-off");
                        var enabled = Path.Combine(record.OriginalPath, "enabled.txt");
                        if (File.Exists(off))
                        {
                            if (File.Exists(enabled)) File.Delete(enabled);
                            File.Move(off, enabled);
                        }
                        if (File.Exists(disabled)) File.Delete(disabled);
                    }
                    else if (record.Kind == "asi" && File.Exists(record.CurrentPath))
                    {
                        if (File.Exists(record.OriginalPath)) return (false, "Original .asi path already exists.", vortex);
                        File.Move(record.CurrentPath, record.OriginalPath);
                    }
                    else if (Directory.Exists(record.CurrentPath))
                    {
                        var dest = Unique(record.OriginalPath, allowExisting: false);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        Directory.Move(record.CurrentPath, dest);
                    }
                    else if (File.Exists(record.CurrentPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(record.OriginalPath)!);
                        var dest = Unique(record.OriginalPath, allowExisting: false);
                        File.Move(record.CurrentPath, dest);
                    }
                }
                state.RemoveAll(r => matched.Contains(r));
                SaveState(state);
                Log.Info($"Mod enabled: {mod.Name}");
                return (true, $"Enabled '{mod.Name}'. Files restored to their original location.", vortex);
            }

            // Generic recovery if the state file was lost: move from ~mods.disabled back.
            if (Directory.Exists(game.DisabledModsDir))
            {
                var name = Path.GetFileName(mod.Path.TrimEnd('\\'));
                var source = Path.Combine(game.DisabledModsDir, name);
                if (Directory.Exists(source))
                {
                    Directory.CreateDirectory(game.ModsDir);
                    var dest = Unique(Path.Combine(game.ModsDir, name), allowExisting: false);
                    Directory.Move(source, dest);
                    return (true, $"Moved '{name}' back to ~mods (state file entry was missing).", vortex);
                }
                if (File.Exists(source))
                {
                    Directory.CreateDirectory(game.ModsDir);
                    var dest = Unique(Path.Combine(game.ModsDir, Path.GetFileName(source)), allowExisting: false);
                    File.Move(source, dest);
                    return (true, $"Moved '{Path.GetFileName(source)}' back to ~mods (state file entry was missing).", vortex);
                }
            }
            return (false, "No reversible record found. Move the mod back manually from ~mods.disabled.", vortex);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not enable mod {mod.Name}", ex);
            return (false, ex.Message, false);
        }
    }

    private static bool VortexManaged(GameInstall game, ModEntry? mod = null)
    {
        try
        {
            if (mod != null)
            {
                var dir = Directory.Exists(mod.Path) ? mod.Path : Path.GetDirectoryName(mod.Path) ?? "";
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, "__folder_managed_by_vortex"))) return true;
                if (dir.Length > 0 && Directory.Exists(dir) &&
                    Directory.EnumerateFiles(dir, "*.vortex_backup", SearchOption.TopDirectoryOnly).Any()) return true;
            }
            return Directory.Exists(game.BinariesDir) &&
                   Directory.EnumerateFiles(game.BinariesDir, "vortex.deployment.*", SearchOption.TopDirectoryOnly).Any();
        }
        catch { return false; }
    }

    private static string Unique(string path, bool allowExisting = false)
    {
        if (allowExisting || (!File.Exists(path) && !Directory.Exists(path))) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(dir, $"{name}.{stamp}");
    }
}
