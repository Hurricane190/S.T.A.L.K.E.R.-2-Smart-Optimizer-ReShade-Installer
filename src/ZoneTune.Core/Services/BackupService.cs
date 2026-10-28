using System.Security.Cryptography;
using System.Text.Json;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Reversible change sets. Every file ZoneTune creates or edits is recorded with its
/// original existence/hash; restoring deletes files that did not exist before and
/// copies originals back byte-for-byte.
/// </summary>
public sealed class BackupService
{
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public string Root { get; }

    public BackupService(string? root = null)
    {
        Root = string.IsNullOrWhiteSpace(root) ? AppPaths.BackupsDir : root;
        Directory.CreateDirectory(Root);
    }

    public ChangeSet CreateChangeSet(string kind, string description, string gameRoot, IEnumerable<string> paths, string note = "")
    {
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{kind}-{Guid.NewGuid().ToString("N")[..6]}";
        var dir = Path.Combine(Root, id);
        var filesDir = Path.Combine(dir, "files");
        Directory.CreateDirectory(filesDir);

        var cs = new ChangeSet
        {
            Id = id,
            Kind = kind,
            Description = description,
            GameRoot = gameRoot,
            Directory = dir,
            CreatedAt = DateTime.Now
        };

        var index = 0;
        foreach (var raw in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(raw);
            var existed = File.Exists(path);
            var item = new BackupItem { Path = path, Existed = existed, Note = note };
            if (existed)
            {
                var fileName = $"{index++:000}_{AppPaths.SafeFileName(Path.GetFileName(path))}";
                var rel = Path.Combine("files", fileName);
                var dest = Path.Combine(dir, rel);
                File.Copy(path, dest, overwrite: true);
                item.BackupRelativePath = rel.Replace('\\', '/');
                item.Sha256Before = HashFile(path);
                cs.SizeBytes += new FileInfo(dest).Length;
            }
            cs.Items.Add(item);
        }

        Save(cs);
        Log.Info($"Change set created: {id} ({cs.Items.Count} items, {cs.SizeText}) — {description}");
        return cs;
    }

    /// <summary>Records a file that did not exist before this transaction created it.</summary>
    public void RecordCreatedFile(ChangeSet cs, string path, string note = "Created by ZoneTune")
    {
        var full = Path.GetFullPath(path);
        var existing = cs.Items.FirstOrDefault(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            cs.Items.Add(new BackupItem
            {
                Path = full,
                Existed = false,
                Sha256After = File.Exists(full) ? HashFile(full) : null,
                Note = note
            });
        }
        else
        {
            existing.Sha256After = File.Exists(full) ? HashFile(full) : null;
        }
        Save(cs);
    }

    public void RecordAfter(ChangeSet cs, string path)
    {
        var full = Path.GetFullPath(path);
        var item = cs.Items.FirstOrDefault(i => string.Equals(i.Path, full, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            cs.Items.Add(new BackupItem
            {
                Path = full,
                Existed = false,
                Sha256After = File.Exists(full) ? HashFile(full) : null,
                Note = "Created by ZoneTune"
            });
        }
        else if (File.Exists(full))
        {
            item.Sha256After = HashFile(full);
        }
        Save(cs);
    }

    public void Save(ChangeSet cs)
    {
        Directory.CreateDirectory(cs.Directory);
        IniDocument.WriteTextAtomic(Path.Combine(cs.Directory, "changeset.json"),
            JsonSerializer.Serialize(cs, _json));
    }

    public IReadOnlyList<ChangeSet> List()
    {
        var list = new List<ChangeSet>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var file = Path.Combine(dir, "changeset.json");
            if (!File.Exists(file)) continue;
            try
            {
                var cs = JsonSerializer.Deserialize<ChangeSet>(File.ReadAllText(file));
                if (cs != null)
                {
                    cs.Directory = dir;
                    if (cs.SizeBytes == 0)
                        cs.SizeBytes = GetDirectorySize(dir);
                    list.Add(cs);
                }
            }
            catch (Exception ex) { Log.Warn($"Unreadable change set {dir}: {ex.Message}"); }
        }
        return list.OrderByDescending(c => c.CreatedAt).ToList();
    }

    public ChangeSet? Load(string id)
    {
        var dir = Path.Combine(Root, id);
        var file = Path.Combine(dir, "changeset.json");
        if (!File.Exists(file)) return null;
        try
        {
            var cs = JsonSerializer.Deserialize<ChangeSet>(File.ReadAllText(file));
            if (cs != null) cs.Directory = dir;
            return cs;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not load change set {id}", ex);
            return null;
        }
    }

    public (bool Success, List<string> Messages, List<string> Errors, string SafetyId) Restore(string id)
    {
        var messages = new List<string>();
        var errors = new List<string>();
        var cs = Load(id);
        if (cs == null) return (false, messages, new List<string> { "Change set not found." }, "");

        // Snapshot the current state before restoring, so a restore is itself reversible.
        var current = cs.Items.Select(i => i.Path).ToList();
        var safety = CreateChangeSet("safety", $"Automatic snapshot before restoring {cs.Id}", cs.GameRoot, current);
        messages.Add($"Safety snapshot: {safety.Id}");

        foreach (var item in cs.Items.AsEnumerable().Reverse())
        {
            try
            {
                if (item.Existed)
                {
                    if (string.IsNullOrEmpty(item.BackupRelativePath)) continue;
                    var backup = Path.Combine(cs.Directory, item.BackupRelativePath.Replace('/', '\\'));
                    if (!File.Exists(backup))
                    {
                        errors.Add($"Missing backup file for {item.Path}");
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
                    File.Copy(backup, item.Path, overwrite: true);
                    if (item.Sha256Before != null && HashFile(item.Path) != item.Sha256Before)
                        errors.Add($"Hash mismatch after restore: {item.Path}");
                    else
                        messages.Add($"Restored {Path.GetFileName(item.Path)}");
                }
                else
                {
                    if (File.Exists(item.Path))
                    {
                        File.Delete(item.Path);
                        messages.Add($"Removed created file {Path.GetFileName(item.Path)}");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(item.Path)}: {ex.Message}");
                Log.Error($"Restore failed for {item.Path}", ex);
            }
        }

        cs.Restored = errors.Count == 0;
        cs.RestoredAt = DateTime.Now;
        cs.Messages.AddRange(messages);
        Save(cs);
        Log.Info($"Restore {id}: {messages.Count} actions, {errors.Count} errors, success={cs.Restored}");
        return (errors.Count == 0, messages, errors, safety.Id);
    }

    public bool Delete(string id)
    {
        var dir = Path.Combine(Root, id);
        if (!Directory.Exists(dir)) return false;
        try
        {
            Directory.Delete(dir, recursive: true);
            Log.Info($"Change set deleted: {id}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not delete change set {id}", ex);
            return false;
        }
    }

    public void Prune(int keep)
    {
        if (keep < 1) keep = 1;
        var sets = List().Where(c => !c.Restored).ToList();
        foreach (var old in sets.Skip(keep))
        {
            if (old.Kind == "safety") continue;
            try { Delete(old.Id); } catch { }
        }
    }

    public static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    public static string HashBytes(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static long GetDirectorySize(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }
}
