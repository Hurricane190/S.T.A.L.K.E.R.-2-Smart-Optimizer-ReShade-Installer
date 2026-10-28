using System.Text.Json;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

public sealed class SettingsStore
{
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile));
                if (settings != null)
                {
                    if (string.IsNullOrWhiteSpace(settings.BackupRoot)) settings.BackupRoot = AppPaths.BackupsDir;
                    return settings;
                }
            }
        }
        catch (Exception ex) { Log.Warn($"Could not read settings.json: {ex.Message}"); }

        return new AppSettings { BackupRoot = AppPaths.BackupsDir };
    }

    public void Save(AppSettings settings)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(settings.BackupRoot)) settings.BackupRoot = AppPaths.BackupsDir;
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsFile)!);
            IniDocument.WriteTextAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, _json));
            Log.Info("Settings saved.");
        }
        catch (Exception ex) { Log.Error("Could not save settings", ex); }
    }
}
