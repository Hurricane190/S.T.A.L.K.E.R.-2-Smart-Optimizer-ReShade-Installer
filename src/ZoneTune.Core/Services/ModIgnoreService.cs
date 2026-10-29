using System.Text.Json;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Persists user "Ignore" decisions for static Medium/High mod warnings.
///
/// Ignoring only suppresses the warning in the analyzer UI; it never hides the
/// evidence, changes the mod, or weakens quarantine/restore checks. A later
/// Unignore removes the record. Paths are normalized so repeated scans keep the
/// decision stable.
/// </summary>
public sealed class ModIgnoreService
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public sealed class IgnoreRecord
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime IgnoredAt { get; set; } = DateTime.Now;
        public string Note { get; set; } = "";
    }

    public sealed class IgnoreFile
    {
        public int Version { get; set; } = 1;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public List<IgnoreRecord> Records { get; set; } = new();
    }

    public ModIgnoreService(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataRoot, "mod-ignores.json");
    }

    public string FilePath => _path;

    public IReadOnlyList<IgnoreRecord> Load()
    {
        lock (_gate)
        {
            var file = Read();
            return file.Records.OrderByDescending(r => r.IgnoredAt).ToList();
        }
    }

    public bool IsIgnored(string path)
    {
        var key = Normalize(path);
        lock (_gate)
        {
            return Read().Records.Any(r => Normalize(r.Path).Equals(key, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Apply(IEnumerable<ModPerformanceAnalysis> mods)
    {
        var records = Load().ToDictionary(r => Normalize(r.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            if (records.TryGetValue(Normalize(mod.Path), out var record))
            {
                mod.Ignored = true;
                mod.IgnoredAt = record.IgnoredAt;
            }
            else
            {
                mod.Ignored = false;
                mod.IgnoredAt = null;
            }
        }
    }

    public void Ignore(ModPerformanceAnalysis mod, string note = "Ignored by user; keep this mod.")
    {
        if (string.IsNullOrWhiteSpace(mod.Path)) return;
        lock (_gate)
        {
            var file = Read();
            var key = Normalize(mod.Path);
            file.Records.RemoveAll(r => Normalize(r.Path).Equals(key, StringComparison.OrdinalIgnoreCase));
            file.Records.Add(new IgnoreRecord
            {
                Path = mod.Path,
                Name = mod.Name,
                IgnoredAt = DateTime.Now,
                Note = note
            });
            Save(file);
        }
        mod.Ignored = true;
        mod.IgnoredAt = DateTime.Now;
        Log.Info($"Mod warning ignored by user: {mod.Name} ({mod.Path})");
    }

    public void Unignore(string path)
    {
        var key = Normalize(path);
        lock (_gate)
        {
            var file = Read();
            var removed = file.Records.RemoveAll(r => Normalize(r.Path).Equals(key, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) Save(file);
        }
        Log.Info($"Mod warning unignored by user: {path}");
    }

    public void Unignore(ModPerformanceAnalysis mod)
    {
        Unignore(mod.Path);
        mod.Ignored = false;
        mod.IgnoredAt = null;
    }

    private IgnoreFile Read()
    {
        try
        {
            if (!File.Exists(_path)) return new IgnoreFile();
            var file = JsonSerializer.Deserialize<IgnoreFile>(File.ReadAllText(_path)) ?? new IgnoreFile();
            file.Records ??= new List<IgnoreRecord>();
            file.Records.RemoveAll(r => string.IsNullOrWhiteSpace(r.Path));
            return file;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read mod ignore list: " + ex.Message);
            return new IgnoreFile();
        }
    }

    private void Save(IgnoreFile file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            file.UpdatedAt = DateTime.Now;
            IniDocument.WriteTextAtomic(_path, JsonSerializer.Serialize(file, _json));
        }
        catch (Exception ex) { Log.Warn("Could not save mod ignore list: " + ex.Message); }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant(); }
        catch { return (path ?? "").TrimEnd('\\').ToLowerInvariant(); }
    }
}
