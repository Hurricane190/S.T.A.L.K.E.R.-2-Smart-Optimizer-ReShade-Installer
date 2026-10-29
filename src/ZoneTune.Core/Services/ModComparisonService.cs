using System.Text.Json;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Manual before/after comparison. ZoneTune cannot drive an in-game benchmark or
/// know whether two captures used identical conditions, so it reports what the
/// user supplied and refuses to attribute the result to individual mods.
/// </summary>
public sealed class ModComparisonService
{
    private readonly MeasurementService _measurements = new();
    private readonly string _directory;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public ModComparisonService(string? directory = null)
    {
        _directory = directory ?? Path.Combine(AppPaths.DataRoot, "ModComparisons");
    }

    public string DirectoryPath => _directory;

    public MeasurementSummary Import(string path) => _measurements.ImportPresentMon(path);

    public ModComparisonRecord Compare(MeasurementSummary before, MeasurementSummary after, string label,
        string notes, string disabledMods)
    {
        var record = new ModComparisonRecord
        {
            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            Label = string.IsNullOrWhiteSpace(label) ? "Mod comparison" : label.Trim(),
            Notes = notes?.Trim() ?? "",
            DisabledMods = disabledMods?.Trim() ?? "",
            Before = before,
            After = after,
            BeforeFile = before.SourceFile,
            AfterFile = after.SourceFile,
            BeforeImportedAt = before.ImportedAt,
            AfterImportedAt = after.ImportedAt
        };

        var warnings = new List<string>();
        if (before.DurationSeconds < 30 || after.DurationSeconds < 30)
            warnings.Add("One capture is shorter than 30 seconds. Use a longer, repeatable route.");
        if (before.Frames < 1800 || after.Frames < 1800)
            warnings.Add("One capture has fewer than ~1,800 frames. Short captures are noisy.");
        if (before.DurationSeconds > 0 && after.DurationSeconds > 0 &&
            Math.Abs(before.DurationSeconds - after.DurationSeconds) / before.DurationSeconds > 0.30)
            warnings.Add("Capture durations differ by more than 30%; the scenes may not be comparable.");
        if (string.IsNullOrWhiteSpace(record.Notes))
            warnings.Add("No scene/settings notes were recorded. Document the route, graphics settings, upscaler and warm-up.");
        if (string.IsNullOrWhiteSpace(record.DisabledMods))
            warnings.Add("No mod set was recorded. The result cannot be linked to the mods under test.");
        if (before.AverageFps > 0 && Math.Abs(after.AverageFps - before.AverageFps) / before.AverageFps > 0.50)
            warnings.Add("The average FPS difference is larger than 50%. Verify that the same save, route, settings and background load were used.");
        warnings.Add("This compares the tested condition as a whole. It does not attribute the difference to an individual mod.");
        warnings.Add("ZoneTune did not fabricate a benchmark and does not promise this result will repeat.");

        record.Warnings = warnings;
        record.ResultText = MeasurementService.Compare(before, after) + Environment.NewLine +
                            "Tested set: " + (string.IsNullOrWhiteSpace(record.DisabledMods) ? "(not recorded)" : record.DisabledMods) + Environment.NewLine +
                            "Notes: " + (string.IsNullOrWhiteSpace(record.Notes) ? "(none)" : record.Notes);
        Save(record);
        Log.Info($"Mod comparison saved: {record.Label} ({record.ShortText})");
        return record;
    }

    public List<ModComparisonRecord> Recent(int count = 8)
    {
        try
        {
            if (!Directory.Exists(_directory)) return new List<ModComparisonRecord>();
            return Directory.EnumerateFiles(_directory, "*.json")
                .Select(f =>
                {
                    try { return JsonSerializer.Deserialize<ModComparisonRecord>(File.ReadAllText(f)); }
                    catch { return null; }
                })
                .Where(r => r != null)
                .Select(r => r!)
                .OrderByDescending(r => r.CreatedAt)
                .Take(count)
                .ToList();
        }
        catch { return new List<ModComparisonRecord>(); }
    }

    private void Save(ModComparisonRecord record)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            IniDocument.WriteTextAtomic(Path.Combine(_directory, record.Id + ".json"),
                JsonSerializer.Serialize(record, _json));
        }
        catch (Exception ex) { Log.Warn("Could not save mod comparison: " + ex.Message); }
    }

    /// <summary>Finds a likely PresentMon executable for instructions; never runs it automatically.</summary>
    public static string FindPresentMon()
    {
        var names = new[] { "PresentMon.exe", "PresentMon-1.10.0-x64.exe", "PresentMon-1.9.3-x64.exe" };
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads",
            AppPaths.DownloadsDir,
            AppPaths.DataRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Microsoft\WinGet\Links"
        };
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var name in names)
                {
                    var path = Path.Combine(folder, name);
                    if (File.Exists(path)) return path;
                }
                var found = Directory.EnumerateFiles(folder, "PresentMon*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
            catch { }
        }
        try
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var found = Directory.EnumerateFiles(dir, "PresentMon*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
        }
        catch { }
        return "";
    }
}
