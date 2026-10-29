using System.Text;

namespace ZoneTune.Core;

public static class AppPaths
{
    public const string ProductName = "ZoneTune";
    public const string ProductFullName = "ZoneTune — STALKER 2 Smart Optimizer";
    public const string Version = "1.1.1";

    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoneTune");

    public static string LogsDir => Ensure(Path.Combine(DataRoot, "Logs"));
    public static string CacheDir => Ensure(Path.Combine(DataRoot, "Cache"));
    public static string DownloadsDir => Ensure(Path.Combine(DataRoot, "Downloads"));
    public static string BackupsDir => Ensure(Path.Combine(DataRoot, "Backups"));
    public static string PackagesDir => Ensure(Path.Combine(DataRoot, "Packages"));
    public static string ReportsDir => Ensure(Path.Combine(DataRoot, "Reports"));
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string ChangeSetsDir => Ensure(Path.Combine(DataRoot, "ChangeSets"));

    public static string DefaultLocalSavedDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stalker2", "Saved");

    public static string Ensure(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* logged elsewhere */ }
        return path;
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        if (result.Length == 0) result = "item";
        return result.Length > 120 ? result[..120] : result;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:0.##} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024:0.#} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes} B";
    }
}

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>Small thread-safe rotating file logger. No external dependencies.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string _logFile = "";
    private static long _bytes;
    private const long MaxBytes = 4 * 1024 * 1024;

    public static string LogFile => _logFile;

    public static void Initialize()
    {
        lock (Gate)
        {
            AppPaths.Ensure(AppPaths.LogsDir);
            _logFile = Path.Combine(AppPaths.LogsDir, "zonetune.log");
            try
            {
                if (File.Exists(_logFile))
                {
                    _bytes = new FileInfo(_logFile).Length;
                    if (_bytes > MaxBytes)
                    {
                        var old = Path.Combine(AppPaths.LogsDir, $"zonetune-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                        File.Move(_logFile, old, overwrite: true);
                        _bytes = 0;
                    }
                }
            }
            catch { _bytes = 0; }
        }
        Info($"=== {AppPaths.ProductFullName} v{AppPaths.Version} starting — {Environment.OSVersion} / {Environment.Version} ===");
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message, Exception? ex = null)
        => Write(LogLevel.Error, ex == null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(LogLevel level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] {message}";
        lock (Gate)
        {
            try
            {
                if (string.IsNullOrEmpty(_logFile)) Initialize();
                File.AppendAllText(_logFile, line + Environment.NewLine, new UTF8Encoding(false));
                _bytes += Encoding.UTF8.GetByteCount(line) + 2;
                if (_bytes > MaxBytes)
                {
                    var old = Path.Combine(AppPaths.LogsDir, $"zonetune-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                    File.Move(_logFile, old, overwrite: true);
                    _bytes = 0;
                }
            }
            catch { /* logging must never crash the app */ }
        }
        try { System.Diagnostics.Debug.WriteLine(line); } catch { }
    }

    public static string Tail(int lines = 200)
    {
        try
        {
            if (!File.Exists(_logFile)) return "";
            var all = File.ReadAllLines(_logFile);
            return string.Join(Environment.NewLine, all.TakeLast(lines));
        }
        catch (Exception ex) { return $"Could not read log: {ex.Message}"; }
    }
}
