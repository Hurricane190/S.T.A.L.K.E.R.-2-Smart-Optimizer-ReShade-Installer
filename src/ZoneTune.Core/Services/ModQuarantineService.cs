using System.Security.Cryptography;
using System.Text.Json;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Verified, reversible mod quarantine.
///
/// Safety model:
///   - copy to a dedicated quarantine folder, hash every file, verify the copy
///   - only then remove the original from the load path
///   - never overwrite an existing original on restore
///   - keep a manifest with original paths, hashes, timestamps and status
///   - reconcile interrupted operations instead of guessing
///   - refuse Vortex-managed files and config overrides
/// </summary>
public sealed class ModQuarantineService
{
    private readonly string _root;
    private readonly string _manifestPath;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private readonly object _gate = new();

    public ModQuarantineService(string? root = null)
    {
        _root = root ?? Path.Combine(AppPaths.DataRoot, "ModQuarantine");
        _manifestPath = Path.Combine(_root, "quarantine-manifest.json");
    }

    public string Root => _root;
    public string ManifestPath => _manifestPath;

    public QuarantineManifest LoadManifest()
    {
        lock (_gate)
        {
            var manifest = ReadManifest();
            if (Reconcile(manifest)) SaveManifest(manifest);
            return manifest;
        }
    }

    public List<QuarantineEntry> ActiveEntries()
    {
        var manifest = LoadManifest();
        return manifest.Entries
            .Where(e => e.IsActive)
            .OrderByDescending(e => e.CreatedAt)
            .ToList();
    }

    public (bool CanMove, string Reason) Validate(ModPerformanceAnalysis analysis, GameInstall game)
    {
        if (!game.IsValid) return (false, "A valid game folder is required.");
        if (new ConfigService().IsGameRunning()) return (false, "Exit STALKER 2 before moving mods; the game holds file handles.");
        if (!analysis.QuarantineEligible) return (false, analysis.QuarantineBlockReason);
        if (string.IsNullOrWhiteSpace(analysis.Path)) return (false, "The mod path is empty.");
        if (!File.Exists(analysis.Path) && !Directory.Exists(analysis.Path)) return (false, "The mod path no longer exists.");
        if (!IsAllowedModPath(analysis.Path, game))
            return (false, "The path is not a recognized mod location (~mods, UE4SS Mods, or a top-level ASI plugin). " +
                           "ZoneTune will not move game files or metadata.");
        if (analysis.IsConfigOverride)
            return (false, "Config overrides are not moved by the quarantine. Use Settings & Backups.");
        if (IsVortexManaged(analysis.Path, game))
            return (false, "This entry is Vortex-managed. Disable/purge it in Vortex; moving it would desynchronise deployment metadata.");
        if (analysis.IsMergeOutput)
            return (false, "Merger output is generated from multiple source mods; quarantine the source mods instead.");
        if (!analysis.Enabled)
            return (false, "The entry is already disabled.");

        var manifest = LoadManifest();
        if (manifest.Entries.Any(e => e.IsActive &&
                                      string.Equals(e.OriginalPath, analysis.Path, StringComparison.OrdinalIgnoreCase)))
            return (false, "This mod is already in the quarantine manifest.");

        var size = EstimateSize(analysis);
        var free = FreeSpace(_root);
        if (free >= 0 && free < size + 64L * 1024 * 1024)
            return (false, $"Not enough free space in the quarantine drive ({AppPaths.FormatBytes(free)} free; " +
                           $"{AppPaths.FormatBytes(size)} plus headroom is required).");
        return (true, "");
    }

    public QuarantineResult Quarantine(IReadOnlyList<ModPerformanceAnalysis> analyses, GameInstall game,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new QuarantineResult();
        if (analyses.Count == 0)
        {
            result.Message = "No mods were selected.";
            return result;
        }
        if (new ConfigService().IsGameRunning())
        {
            result.Message = "Exit STALKER 2 before moving mods.";
            result.Errors.Add(result.Message);
            return result;
        }

        Directory.CreateDirectory(_root);
        var stagingRoot = Path.Combine(_root, ".staging");
        Directory.CreateDirectory(stagingRoot);

        for (var index = 0; index < analyses.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var analysis = analyses[index];
            progress?.Report(new OperationProgress("Quarantining mods",
                $"{index + 1}/{analyses.Count} · {analysis.Name}", 5 + (int)(90.0 * index / analyses.Count)));

            var (canMove, reason) = Validate(analysis, game);
            if (!canMove)
            {
                result.Errors.Add($"{analysis.Name}: {reason}");
                continue;
            }

            var entry = QuarantineOne(analysis, game, stagingRoot, result, ct);
            if (entry != null)
            {
                result.Entries.Add(entry);
                if (entry.Status == "Quarantined")
                    result.Warnings.AddRange((entry.Note ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
        }

        var ok = result.Entries.Count(e => e.Status == "Quarantined");
        result.Success = ok > 0 && result.Errors.Count == 0;
        result.Message = ok == 0
            ? "No mods were quarantined."
            : $"Quarantined {ok} mod(s). Files were copied, hash-verified and then removed from the load path. " +
              "Use Restore to return them to their original locations.";
        progress?.Report(new OperationProgress("Done", result.Message, 100));
        Log.Info($"Quarantine operation: {ok} mod(s) quarantined, {result.Errors.Count} error(s)");
        return result;
    }

    private QuarantineEntry? QuarantineOne(ModPerformanceAnalysis analysis, GameInstall game, string stagingRoot,
        QuarantineResult result, CancellationToken ct)
    {
        var opId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var payloadRelative = Path.Combine("Operations", opId, "payload");
        var payload = Path.Combine(_root, payloadRelative);
        var staged = Path.Combine(stagingRoot, opId);
        var entry = new QuarantineEntry
        {
            Id = opId,
            OperationId = opId,
            ModName = analysis.Name,
            Kind = analysis.KindText,
            OriginalPath = analysis.Path,
            QuarantinePath = payload,
            IsDirectory = Directory.Exists(analysis.Path),
            SizeBytes = EstimateSize(analysis),
            VortexManaged = analysis.VortexManaged,
            Ue4ss = analysis.IsUe4ss,
            Status = "Planned",
            Note = analysis.QuarantineNote
        };

        var manifest = LoadManifest();
        manifest.Entries.Add(entry);
        SaveManifest(manifest);

        try
        {
            Directory.CreateDirectory(staged);
            var items = new List<QuarantineItem>();
            if (Directory.Exists(analysis.Path))
            {
                var name = Path.GetFileName(analysis.Path.TrimEnd('\\'));
                var dest = Path.Combine(staged, "payload", name);
                CopyDirectory(analysis.Path, dest, ct);
                items.Add(new QuarantineItem
                {
                    OriginalPath = analysis.Path,
                    StoredRelativePath = Path.Combine("Operations", opId, "payload", name),
                    IsDirectory = true
                });
            }
            else
            {
                foreach (var source in ExpandFileSet(analysis.Path))
                {
                    ct.ThrowIfCancellationRequested();
                    var destDir = Path.Combine(staged, "payload", "files");
                    Directory.CreateDirectory(destDir);
                    var dest = Path.Combine(destDir, Path.GetFileName(source));
                    CopyFilePreserve(source, dest);
                    items.Add(new QuarantineItem
                    {
                        OriginalPath = source,
                        StoredRelativePath = Path.Combine("Operations", opId, "payload", "files", Path.GetFileName(source)),
                        IsDirectory = false
                    });
                }
            }

            entry.Items = items;

            // Record and verify every staged file against the source before removing anything.
            entry.Files = new List<QuarantineFileRecord>();
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var stored = item.IsDirectory
                    ? Path.Combine(staged, "payload", Path.GetFileName(item.OriginalPath.TrimEnd('\\')))
                    : Path.Combine(staged, "payload", "files", Path.GetFileName(item.OriginalPath));
                if (item.IsDirectory)
                {
                    foreach (var sourceFile in EnumerateFiles(item.OriginalPath))
                    {
                        var relative = Path.GetRelativePath(item.OriginalPath, sourceFile);
                        var storedFile = Path.Combine(stored, relative);
                        var sourceHash = HashFile(sourceFile);
                        var storedHash = HashFile(storedFile);
                        if (!sourceHash.Equals(storedHash, StringComparison.OrdinalIgnoreCase))
                            throw new IOException($"Verification failed for {relative}: copied bytes differ.");
                        entry.Files.Add(new QuarantineFileRecord
                        {
                            RelativePath = Path.Combine(item.StoredRelativePath, relative),
                            SizeBytes = new FileInfo(storedFile).Length,
                            Sha256 = storedHash,
                            LastWriteUtc = File.GetLastWriteTimeUtc(sourceFile)
                        });
                    }
                }
                else
                {
                    var sourceHash = HashFile(item.OriginalPath);
                    var storedHash = HashFile(stored);
                    if (!sourceHash.Equals(storedHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Verification failed for {Path.GetFileName(stored)}: copied bytes differ.");
                    entry.Files.Add(new QuarantineFileRecord
                    {
                        RelativePath = item.StoredRelativePath,
                        SizeBytes = new FileInfo(stored).Length,
                        Sha256 = storedHash,
                        LastWriteUtc = File.GetLastWriteTimeUtc(item.OriginalPath)
                    });
                }
            }

            // Move the verified payload into its final location.
            Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
            if (Directory.Exists(payload)) Directory.Delete(payload, true);
            Directory.Move(Path.Combine(staged, "payload"), payload);
            entry.Status = "Copied";
            entry.UpdatedAt = DateTime.Now;
            SaveEntry(entry);

            // Only now remove the original. If this fails, both copies remain and the
            // manifest reports COPY VERIFIED, never a false "quarantined" state.
            try
            {
                if (entry.IsDirectory) Directory.Delete(entry.OriginalPath, true);
                else foreach (var item in items.Where(i => !i.IsDirectory)) File.Delete(item.OriginalPath);
            }
            catch (Exception ex)
            {
                entry.Status = "Copied";
                entry.Error = "Original could not be removed: " + ex.Message;
                entry.Note = AppendNote(entry.Note,
                    "A verified quarantine copy exists, but the original is still in place. Close the game/editors and retry, or restore the original path manually.");
                SaveEntry(entry);
                result.Errors.Add($"{analysis.Name}: verified copy created but the original could not be removed ({ex.Message}).");
                return entry;
            }

            entry.Status = "Quarantined";
            entry.UpdatedAt = DateTime.Now;
            entry.Error = "";
            SaveEntry(entry);
            try { Directory.Delete(staged, true); } catch { }
            return entry;
        }
        catch (Exception ex)
        {
            entry.Status = "Failed";
            entry.Error = ex.Message;
            entry.UpdatedAt = DateTime.Now;
            SaveEntry(entry);
            try { if (Directory.Exists(staged)) Directory.Delete(staged, true); } catch { }
            result.Errors.Add($"{analysis.Name}: quarantine failed before the original was changed ({ex.Message}).");
            Log.Error($"Quarantine failed for {analysis.Name}", ex);
            return entry;
        }
    }

    public QuarantineResult Restore(string entryId, GameInstall game, IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new QuarantineResult();
        if (new ConfigService().IsGameRunning())
        {
            result.Message = "Exit STALKER 2 before restoring mods.";
            result.Errors.Add(result.Message);
            return result;
        }

        var manifest = LoadManifest();
        var entry = manifest.Entries.FirstOrDefault(e => e.Id.Equals(entryId, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            result.Message = "Quarantine record not found.";
            result.Errors.Add(result.Message);
            return result;
        }

        var unsafeOriginals = entry.Items.Where(i => !IsAllowedModPath(i.OriginalPath, game)).Select(i => i.OriginalPath).ToList();
        if (unsafeOriginals.Count > 0)
        {
            result.Message = "Restore refused: the manifest contains a path outside the recognized mod locations. Nothing was written.";
            result.Errors.AddRange(unsafeOriginals.Select(p => "Unsafe original path: " + p));
            return result;
        }

        var verification = VerifyStored(entry);
        if (verification.Count > 0)
        {
            result.Message = "The quarantine copy is incomplete or has changed; nothing was restored.";
            result.Errors.AddRange(verification);
            entry.Status = "Missing";
            entry.Error = string.Join(" | ", verification.Take(4));
            entry.UpdatedAt = DateTime.Now;
            SaveEntry(entry);
            return result;
        }

        // An interrupted restore can leave some verified originals in place. Accept
        // those only when every expected file already matches its quarantine hash.
        var alreadyRestored = new List<QuarantineItem>();
        var collisions = new List<string>();
        foreach (var item in entry.Items)
        {
            if (!File.Exists(item.OriginalPath) && !Directory.Exists(item.OriginalPath)) continue;
            if (ItemMatchesStored(entry, item)) alreadyRestored.Add(item);
            else collisions.Add(item.OriginalPath);
        }
        if (collisions.Count > 0)
        {
            result.Message = "Restore refused: an original path already exists with different contents. Nothing was overwritten.";
            result.Errors.AddRange(collisions.Select(p => "Already exists with different contents: " + p));
            return result;
        }

        var free = FreeSpace(Path.GetDirectoryName(entry.OriginalPath) ?? game.Root);
        if (free >= 0 && free < entry.SizeBytes + 64L * 1024 * 1024)
        {
            result.Message = $"Not enough free space at the original location ({AppPaths.FormatBytes(free)} free).";
            result.Errors.Add(result.Message);
            return result;
        }

        progress?.Report(new OperationProgress("Restoring mod", entry.ModName, 10));
        var restored = new List<string>();
        try
        {
            foreach (var item in entry.Items)
            {
                ct.ThrowIfCancellationRequested();
                if (alreadyRestored.Contains(item))
                {
                    restored.Add(item.OriginalPath);
                    continue;
                }
                var stored = Path.Combine(_root, item.StoredRelativePath);
                if (!IsUnder(stored, _root)) throw new IOException("Unsafe quarantine path in manifest: " + item.StoredRelativePath);
                var parent = Path.GetDirectoryName(item.OriginalPath);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                var temp = item.OriginalPath + ".zonetune-restore-" + entry.Id;
                if (File.Exists(temp) || Directory.Exists(temp)) throw new IOException("Temporary restore path already exists: " + temp);

                if (item.IsDirectory)
                {
                    CopyDirectory(stored, temp, ct);
                    VerifyDirectoryHashes(stored, temp, entry, item);
                    Directory.Move(temp, item.OriginalPath);
                }
                else
                {
                    CopyFilePreserve(stored, temp);
                    VerifyFileHash(stored, temp, entry, item);
                    File.Move(temp, item.OriginalPath);
                }
                restored.Add(item.OriginalPath);
            }

            // Every restored file is verified. The duplicate quarantine payload can now be removed.
            var cleanupWarning = "";
            try
            {
                if (IsUnder(entry.QuarantinePath, _root) && Directory.Exists(entry.QuarantinePath))
                    Directory.Delete(entry.QuarantinePath, true);
            }
            catch (Exception ex)
            {
                cleanupWarning = "Quarantine copy could not be removed: " + ex.Message;
            }

            entry.Status = "Restored";
            entry.UpdatedAt = DateTime.Now;
            entry.Error = cleanupWarning;
            entry.Note = AppendNote(entry.Note, "Restored and hash-verified.");
            SaveEntry(entry);
            result.Success = true;
            result.Entries.Add(entry);
            result.Message = $"Restored '{entry.ModName}' to its original location and verified the files.";
            if (!string.IsNullOrWhiteSpace(cleanupWarning)) result.Warnings.Add(cleanupWarning);
            progress?.Report(new OperationProgress("Restored", result.Message, 100));
            Log.Info($"Quarantine restore: {entry.ModName} -> {entry.OriginalPath}");
            return result;
        }
        catch (Exception ex)
        {
            entry.Status = "RestoreIncomplete";
            entry.Error = ex.Message + " Restored before failure: " + string.Join(", ", restored);
            entry.UpdatedAt = DateTime.Now;
            SaveEntry(entry);
            result.Message = "Restore did not complete. The quarantine copy was kept.";
            result.Errors.Add(ex.Message);
            result.Errors.Add("Already restored: " + (restored.Count == 0 ? "none" : string.Join(", ", restored)));
            Log.Error($"Quarantine restore failed for {entry.ModName}", ex);
            return result;
        }
    }

    public QuarantineResult RestoreAll(GameInstall game, IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        var entries = ActiveEntries();
        var result = new QuarantineResult { Success = true };
        for (var i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress("Restoring mods", $"{i + 1}/{entries.Count} · {entries[i].ModName}",
                (int)(100.0 * i / Math.Max(1, entries.Count))));
            var one = Restore(entries[i].Id, game, null, ct);
            if (!one.Success)
            {
                result.Success = false;
                result.Errors.AddRange(one.Errors);
            }
            else result.Entries.Add(entries[i]);
        }
        result.Message = result.Success
            ? $"Restored {result.Entries.Count} mod(s)."
            : "Some mods could not be restored; the remaining quarantine copies were kept.";
        return result;
    }

    private List<string> VerifyStored(QuarantineEntry entry)
    {
        var errors = new List<string>();
        foreach (var file in entry.Files)
        {
            var path = Path.Combine(_root, file.RelativePath);
            if (!IsUnder(path, _root))
            {
                errors.Add("Unsafe quarantine path: " + file.RelativePath);
                continue;
            }
            if (!File.Exists(path))
            {
                errors.Add("Missing quarantine file: " + file.RelativePath);
                continue;
            }
            var info = new FileInfo(path);
            if (info.Length != file.SizeBytes)
                errors.Add("Size changed: " + file.RelativePath);
            else if (!HashFile(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                errors.Add("Hash changed: " + file.RelativePath);
        }
        if (entry.Files.Count == 0)
        {
            var emptyDirectoriesOk = entry.Items.Count > 0 && entry.Items.All(i =>
                i.IsDirectory && Directory.Exists(Path.Combine(_root, i.StoredRelativePath)));
            if (!emptyDirectoriesOk) errors.Add("The manifest has no file hashes for this entry.");
        }
        return errors;
    }

    private static void VerifyDirectoryHashes(string stored, string restored, QuarantineEntry entry, QuarantineItem item)
    {
        foreach (var file in entry.Files)
        {
            var relative = Path.GetRelativePath(item.StoredRelativePath, file.RelativePath);
            if (relative.StartsWith("..", StringComparison.Ordinal)) continue;
            var restoredFile = Path.Combine(restored, relative);
            if (!File.Exists(restoredFile)) throw new IOException("Restored file missing: " + relative);
            if (!HashFile(restoredFile).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Restored file hash mismatch: " + relative);
            try { File.SetLastWriteTimeUtc(restoredFile, file.LastWriteUtc); } catch { }
        }
    }

    private static bool ItemMatchesStored(QuarantineEntry entry, QuarantineItem item)
    {
        try
        {
            if (item.IsDirectory)
            {
                if (!Directory.Exists(item.OriginalPath)) return false;
                foreach (var file in entry.Files)
                {
                    var relative = Path.GetRelativePath(item.StoredRelativePath, file.RelativePath);
                    if (relative.StartsWith("..", StringComparison.Ordinal)) continue;
                    var original = Path.Combine(item.OriginalPath, relative);
                    if (!File.Exists(original)) return false;
                    if (!HashFile(original).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            }

            if (!File.Exists(item.OriginalPath)) return false;
            var record = entry.Files.FirstOrDefault(f =>
                f.RelativePath.Equals(item.StoredRelativePath, StringComparison.OrdinalIgnoreCase));
            return record != null &&
                   HashFile(item.OriginalPath).Equals(record.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void VerifyFileHash(string stored, string restored, QuarantineEntry entry, QuarantineItem item)
    {
        if (!HashFile(restored).Equals(HashFile(stored), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Restored file hash mismatch: " + Path.GetFileName(restored));
        var record = entry.Files.FirstOrDefault(f =>
            f.RelativePath.Equals(item.StoredRelativePath, StringComparison.OrdinalIgnoreCase));
        if (record != null)
        {
            try { File.SetLastWriteTimeUtc(restored, record.LastWriteUtc); } catch { }
        }
    }

    private bool Reconcile(QuarantineManifest manifest)
    {
        var changed = false;
        foreach (var entry in manifest.Entries.Where(e => e.Status is "Planned" or "Copied"))
        {
            var storedExists = entry.Files.Count > 0 && entry.Files.All(f => File.Exists(Path.Combine(_root, f.RelativePath)));
            var originalsExist = entry.Items.Count > 0 &&
                                 entry.Items.All(i => File.Exists(i.OriginalPath) || Directory.Exists(i.OriginalPath));
            if (storedExists && originalsExist)
            {
                if (entry.Status != "Copied" || !(entry.Note ?? "").Contains("Recovered: verified copy", StringComparison.Ordinal))
                {
                    entry.Status = "Copied";
                    entry.Note = AppendNote(entry.Note,
                        "Recovered: verified copy and original both exist. Restore is not needed; close handles and retry quarantine if desired.");
                    changed = true;
                }
            }
            else if (storedExists && !originalsExist)
            {
                if (entry.Status != "Quarantined" || !(entry.Note ?? "").Contains("Recovered: original was removed", StringComparison.Ordinal))
                {
                    entry.Status = "Quarantined";
                    entry.Note = AppendNote(entry.Note, "Recovered: original was removed after the copy was verified.");
                    changed = true;
                }
            }
            else if (!storedExists)
            {
                entry.Status = "Missing";
                entry.Error = "The quarantine payload is missing or incomplete.";
                changed = true;
            }
            entry.UpdatedAt = DateTime.Now;
        }
        foreach (var entry in manifest.Entries.Where(e => e.Status == "Quarantined"))
        {
            if (entry.Files.Count > 0 && !entry.Files.All(f => File.Exists(Path.Combine(_root, f.RelativePath))))
            {
                entry.Status = "Missing";
                entry.Error = "One or more quarantine files are missing.";
                changed = true;
            }
        }
        return changed;
    }

    private QuarantineManifest ReadManifest()
    {
        try
        {
            if (!File.Exists(_manifestPath)) return new QuarantineManifest { Root = _root };
            var text = File.ReadAllText(_manifestPath);
            var manifest = JsonSerializer.Deserialize<QuarantineManifest>(text) ?? new QuarantineManifest { Root = _root };
            manifest.Entries ??= new List<QuarantineEntry>();
            foreach (var entry in manifest.Entries)
            {
                entry.Files ??= new List<QuarantineFileRecord>();
                entry.Items ??= new List<QuarantineItem>();
                entry.Status ??= "Unknown";
            }
            return manifest;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read quarantine manifest: " + ex.Message);
            return new QuarantineManifest { Root = _root };
        }
    }

    private void SaveManifest(QuarantineManifest manifest)
    {
        try
        {
            Directory.CreateDirectory(_root);
            manifest.Root = _root;
            manifest.UpdatedAt = DateTime.Now;
            IniDocument.WriteTextAtomic(_manifestPath, JsonSerializer.Serialize(manifest, _json));
        }
        catch (Exception ex) { Log.Warn("Could not save quarantine manifest: " + ex.Message); }
    }

    private void SaveEntry(QuarantineEntry entry)
    {
        var manifest = ReadManifest();
        var index = manifest.Entries.FindIndex(e => e.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) manifest.Entries[index] = entry;
        else manifest.Entries.Add(entry);
        SaveManifest(manifest);
    }

    private static IEnumerable<string> ExpandFileSet(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var dir = Path.GetDirectoryName(path) ?? "";
        if (ext is ".pak" or ".utoc" or ".ucas")
        {
            var baseName = Path.GetFileNameWithoutExtension(path);
            foreach (var candidateExt in new[] { ".pak", ".utoc", ".ucas" })
            {
                var candidate = Path.Combine(dir, baseName + candidateExt);
                if (File.Exists(candidate)) yield return candidate;
            }
            yield break;
        }
        yield return path;
    }

    private static void CopyDirectory(string source, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in EnumerateDirectories(source))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(dest, relative));
        }
        foreach (var file in EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyFilePreserve(file, target);
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private static void CopyFilePreserve(string source, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest, overwrite: false);
        try
        {
            File.SetCreationTimeUtc(dest, File.GetCreationTimeUtc(source));
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(source));
            // Keep useful metadata but never propagate ReadOnly: it would block the
            // verified cleanup/restore step on some folders and files.
            var attributes = File.GetAttributes(source) & ~FileAttributes.ReadOnly;
            File.SetAttributes(dest, attributes);
        }
        catch { }
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                })
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static long EstimateSize(ModPerformanceAnalysis analysis)
    {
        try
        {
            if (Directory.Exists(analysis.Path))
                return EnumerateFiles(analysis.Path).Sum(f => new FileInfo(f).Length);
            return ExpandFileSet(analysis.Path).Sum(f => new FileInfo(f).Length);
        }
        catch { return Math.Max(0, analysis.SizeBytes); }
    }

    private static long FreeSpace(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(root)) return -1;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return -1; }
    }

    private static bool IsAllowedModPath(string path, GameInstall game)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\');
            if (IsUnder(full, game.ModsDir)) return true;
            var ue4ssMods = Path.Combine(game.BinariesDir, "ue4ss", "Mods");
            if (IsUnder(full, ue4ssMods)) return true;
            if (File.Exists(full) && full.EndsWith(".asi", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetDirectoryName(full)?.TrimEnd('\\'),
                    Path.GetFullPath(game.BinariesDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
        catch { return false; }
    }

    private static bool IsUnder(string fullPath, string root)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return (fullPath + "\\").StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsVortexManaged(string path, GameInstall game)
    {
        try
        {
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "__folder_managed_by_vortex"))) return true;
            if (File.Exists(path) && path.EndsWith(".vortex_backup", StringComparison.OrdinalIgnoreCase)) return true;
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.vortex_backup", SearchOption.TopDirectoryOnly).Any()) return true;
            if (Directory.Exists(game.BinariesDir) &&
                Directory.EnumerateFiles(game.BinariesDir, "vortex.deployment.*", SearchOption.TopDirectoryOnly).Any()) return true;
        }
        catch { }
        return false;
    }

    private static string AppendNote(string? note, string addition)
    {
        if (string.IsNullOrWhiteSpace(note)) return addition;
        return note + " " + addition;
    }
}
