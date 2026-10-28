using System.Text.RegularExpressions;
using Microsoft.Win32;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Steam/Xbox/manual STALKER 2 locator. Steam libraries are read from
/// libraryfolders.vdf and appmanifest_1643320.acf; no hard-coded drive letters.
/// </summary>
public sealed class GameLocator
{
    public const string FolderName = "S.T.A.L.K.E.R. 2 Heart of Chornobyl";
    public const string SteamAppId = "1643320";
    private const string ShippingRelative = @"Stalker2\Binaries\Win64\Stalker2-Win64-Shipping.exe";

    public GameInstall Locate(string? manualPath = null, string? savedPath = null)
    {
        if (!string.IsNullOrWhiteSpace(manualPath))
        {
            var manual = Validate(manualPath, GameSource.Manual);
            if (manual.IsValid) return manual;
            Log.Warn($"Manual game path rejected: {manualPath} ({manual.ValidationMessage})");
        }

        if (!string.IsNullOrWhiteSpace(savedPath))
        {
            var saved = Validate(savedPath, GameSource.Manual);
            if (saved.IsValid) return saved;
        }

        foreach (var candidate in EnumerateSteamCandidates())
        {
            var install = Validate(candidate.path, GameSource.Steam);
            if (install.IsValid)
            {
                install.SteamAppId = SteamAppId;
                Log.Info($"Game found via Steam library: {install.Root}");
                return install;
            }
        }

        foreach (var root in CommonRoots())
        {
            var direct = Path.Combine(root, FolderName);
            var install = Validate(direct, GameSource.Xbox);
            if (install.IsValid)
            {
                Log.Info($"Game found in common path: {install.Root}");
                return install;
            }
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "*STALKER*", SearchOption.TopDirectoryOnly))
                {
                    var found = Validate(dir, GameSource.Xbox);
                    if (found.IsValid)
                    {
                        Log.Info($"Game found in common path: {found.Root}");
                        return found;
                    }
                }
            }
            catch { }
        }

        // Last resort: shallow scan of drive roots for the well-known folder. Bounded to
        // avoid walking entire disks.
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(drive.RootDirectory.FullName, "S.T.A.L.K.E.R.*", SearchOption.TopDirectoryOnly))
                {
                    var install = Validate(dir, GameSource.Manual);
                    if (install.IsValid) return install;
                }
                foreach (var games in new[] { "Games", "SteamLibrary\\steamapps\\common", "XboxGames", "Program Files (x86)\\Steam\\steamapps\\common" })
                {
                    var path = Path.Combine(drive.RootDirectory.FullName, games, FolderName);
                    var install = Validate(path, GameSource.Manual);
                    if (install.IsValid) return install;
                }
            }
            catch { }
        }

        return new GameInstall
        {
            Source = GameSource.NotFound,
            IsValid = false,
            ValidationMessage = "STALKER 2 was not found automatically. Choose the game folder manually."
        };
    }

    public IEnumerable<(string path, string source)> EnumerateSteamCandidates()
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var steamRoot in SteamRoots())
        {
            var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            foreach (var lib in ParseLibraryFolders(libraryFile)) libraries.Add(lib);
            libraries.Add(steamRoot);
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var defaultLib = Path.Combine(drive.RootDirectory.FullName, "SteamLibrary");
            if (Directory.Exists(defaultLib)) libraries.Add(defaultLib);
        }

        foreach (var lib in libraries)
        {
            var common = Path.Combine(lib, "steamapps", "common");
            string? folder = null;
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{SteamAppId}.acf");
            if (File.Exists(manifest))
            {
                try
                {
                    var text = File.ReadAllText(manifest);
                    var match = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                    if (match.Success) folder = match.Groups[1].Value;
                }
                catch { }
            }
            folder ??= FolderName;
            yield return (Path.Combine(common, folder), $"Steam ({lib})");
        }
    }

    private static IEnumerable<string> SteamRoots()
    {
        var roots = new List<string>();
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var sub in new[]
                     {
                         @"Software\Valve\Steam",
                         @"SOFTWARE\WOW6432Node\Valve\Steam"
                     })
            {
                try
                {
                    using var key = hive.OpenSubKey(sub);
                    var path = key?.GetValue("SteamPath") as string ?? key?.GetValue("InstallPath") as string;
                    if (!string.IsNullOrWhiteSpace(path)) roots.Add(path.Replace('/', '\\'));
                }
                catch { }
            }
        }
        roots.Add(@"C:\Program Files (x86)\Steam");
        roots.Add(@"C:\Steam");
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists);
    }

    public static List<string> ParseLibraryFolders(string vdfPath)
    {
        var result = new List<string>();
        if (!File.Exists(vdfPath)) return result;
        try
        {
            var text = File.ReadAllText(vdfPath);
            foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
            {
                var path = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(path)) result.Add(path);
            }
        }
        catch (Exception ex) { Log.Warn($"Could not parse {vdfPath}: {ex.Message}"); }
        return result;
    }

    private static IEnumerable<string> CommonRoots()
    {
        yield return @"C:\XboxGames";
        yield return @"D:\XboxGames";
        yield return @"E:\XboxGames";
        yield return @"C:\Games";
        yield return @"D:\Games";
        yield return @"E:\Games";
        yield return @"C:\Program Files (x86)\Steam\steamapps\common";
        yield return @"C:\Steam\steamapps\common";
        yield return @"D:\SteamLibrary\steamapps\common";
        yield return @"E:\SteamLibrary\steamapps\common";
    }

    /// <summary>Accepts a game root, the Binaries\Win64 folder, or a parent folder.</summary>
    public GameInstall Validate(string path, GameSource source)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return Invalid(path ?? "", "Empty path.");
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            if (File.Exists(full))
            {
                var name = Path.GetFileName(full);
                if (name.Equals("Stalker2-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Stalker2.exe", StringComparison.OrdinalIgnoreCase))
                    full = Path.GetDirectoryName(full)!;
                else return Invalid(path, "Select the game folder, not a file.");
            }
            if (!Directory.Exists(full)) return Invalid(path, "Folder does not exist.");

            // Walk up to 5 parents looking for the known shipping executable.
            var probe = full;
            for (var depth = 0; depth < 6 && !string.IsNullOrEmpty(probe); depth++)
            {
                var shipping = Path.Combine(probe, ShippingRelative);
                if (File.Exists(shipping)) return Build(probe, shipping, source);

                // Xbox/MSIX layouts place the build under Content\.
                var contentShipping = Path.Combine(probe, "Content", ShippingRelative);
                if (File.Exists(contentShipping))
                {
                    var root = probe;
                    return Build(root, contentShipping, source, binariesOverride: Path.GetDirectoryName(contentShipping));
                }

                probe = Path.GetDirectoryName(probe);
            }

            // Bounded recursive search (max 4 levels) as a final attempt.
            var found = FindShipping(full, 4);
            if (found != null)
            {
                var root = full;
                var marker = Path.Combine("Stalker2", "Binaries", "Win64");
                var idx = found.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx > 0) root = found[..idx].TrimEnd('\\');
                return Build(root, found, source);
            }

            return Invalid(path, "Stalker2-Win64-Shipping.exe was not found under this folder.");
        }
        catch (Exception ex)
        {
            return Invalid(path, ex.Message);
        }
    }

    private static string? FindShipping(string root, int depth)
    {
        if (depth < 0) return null;
        try
        {
            var direct = Path.Combine(root, ShippingRelative);
            if (File.Exists(direct)) return direct;
            var content = Path.Combine(root, "Content", ShippingRelative);
            if (File.Exists(content)) return content;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var found = FindShipping(dir, depth - 1);
                if (found != null) return found;
            }
        }
        catch { }
        return null;
    }

    private static GameInstall Build(string root, string shipping, GameSource source, string? binariesOverride = null)
    {
        var binaries = binariesOverride ?? Path.GetDirectoryName(shipping)!;
        var paks = Path.Combine(root, "Stalker2", "Content", "Paks");
        if (!Directory.Exists(paks))
        {
            // MSIX/Content layout
            var alt = Path.Combine(root, "Content", "Stalker2", "Content", "Paks");
            if (Directory.Exists(alt)) paks = alt;
        }
        var saved = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stalker2", "Saved");
        var config = Path.Combine(saved, "Config", "Windows");
        var logs = Path.Combine(saved, "Logs");
        var exe = Path.Combine(root, "Stalker2.exe");
        if (!File.Exists(exe))
        {
            var alt = Path.Combine(root, "Content", "Stalker2.exe");
            if (File.Exists(alt)) exe = alt;
        }

        return new GameInstall
        {
            Root = root,
            ExePath = exe,
            ShippingExePath = shipping,
            BinariesDir = binaries,
            PaksDir = paks,
            ModsDir = Path.Combine(paks, "~mods"),
            DisabledModsDir = Path.Combine(paks, "~mods.disabled"),
            ConfigDir = config,
            LogsDir = logs,
            AppDataDir = saved,
            Source = source,
            IsValid = true,
            ValidationMessage = "OK",
            SteamAppId = SteamAppId
        };
    }

    private static GameInstall Invalid(string path, string message) => new()
    {
        Root = path,
        Source = GameSource.NotFound,
        IsValid = false,
        ValidationMessage = message
    };
}
