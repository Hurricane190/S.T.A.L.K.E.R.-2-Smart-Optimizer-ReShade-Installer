using System.Diagnostics;
using System.Text.RegularExpressions;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

public sealed class ReShadeService
{
    public static readonly string[] GraphicsProxyNames =
    {
        "dxgi.dll", "d3d12.dll", "d3d11.dll", "d3d10.dll", "d3d9.dll", "opengl32.dll", "vulkan-1.dll", "ddraw.dll"
    };

    public static readonly string[] KnownWrapperNames =
    {
        "dxgi.dll", "d3d12.dll", "d3d11.dll", "d3d10.dll", "d3d9.dll", "opengl32.dll", "vulkan-1.dll", "ddraw.dll",
        "winmm.dll", "dsound.dll", "dwmapi.dll", "iphlpapi.dll", "version.dll", "wininet.dll", "winhttp.dll", "xinput1_3.dll"
    };

    public ReShadeInfo Detect(GameInstall game, HardwareInfo? hardware = null)
    {
        var info = new ReShadeInfo();
        if (!game.IsValid || !Directory.Exists(game.BinariesDir))
        {
            info.Errors.Add("Game folder not detected; ReShade state unavailable.");
            return info;
        }

        info.InstallDir = game.BinariesDir;
        var reshadeProxy = "";
        var foundProxies = new List<string>();

        foreach (var name in KnownWrapperNames)
        {
            var path = Path.Combine(game.BinariesDir, name);
            if (!File.Exists(path)) continue;
            foundProxies.Add(name);
            if (IsReShadeModule(path))
            {
                reshadeProxy = path;
                break;
            }
        }

        if (!string.IsNullOrEmpty(reshadeProxy))
        {
            info.Installed = true;
            info.ProxyPath = reshadeProxy;
            info.ProxyDll = Path.GetFileName(reshadeProxy);
            var vi = FileVersionInfo.GetVersionInfo(reshadeProxy);
            info.Version = vi.FileVersion?.Trim() ?? "";
            info.AddonBuild = (vi.ProductName ?? "").Contains("add-on", StringComparison.OrdinalIgnoreCase) ||
                              (vi.FileDescription ?? "").Contains("add-on", StringComparison.OrdinalIgnoreCase);
        }

        // A second graphics proxy that is not ReShade usually means DLL-wrapper roulette.
        foreach (var name in GraphicsProxyNames)
        {
            var path = Path.Combine(game.BinariesDir, name);
            if (!File.Exists(path)) continue;
            if (!string.IsNullOrEmpty(reshadeProxy) && path.Equals(reshadeProxy, StringComparison.OrdinalIgnoreCase)) continue;
            info.ProxyConflicts.Add($"{name} is present and is not the active ReShade module. Two graphics proxies can conflict.");
        }

        foreach (var name in foundProxies.Where(n => !GraphicsProxyNames.Contains(n, StringComparer.OrdinalIgnoreCase)))
            info.OtherProxyDlls.Add(name);

        var logPath = Path.Combine(game.BinariesDir, "ReShade.log");
        info.LogPath = logPath;
        if (File.Exists(logPath))
        {
            try
            {
                var lines = File.ReadAllLines(logPath);
                var first = lines.FirstOrDefault(l => l.Contains("ReShade version", StringComparison.OrdinalIgnoreCase));
                if (first != null)
                {
                    var match = Regex.Match(first, @"version '([^']+)'");
                    if (match.Success) info.Version = match.Groups[1].Value;
                    if (first.Contains("add-on", StringComparison.OrdinalIgnoreCase)) info.AddonBuild = true;
                }
                var errors = new List<string>();
                foreach (var line in lines)
                {
                    if (line.Contains("| ERROR |", StringComparison.OrdinalIgnoreCase))
                    {
                        info.EffectErrors++;
                        if (errors.Count < 12) errors.Add(CleanLogLine(line));
                    }
                    else if (line.Contains("| WARN  |", StringComparison.OrdinalIgnoreCase))
                        info.EffectWarnings++;
                }
                info.Errors.AddRange(errors.Distinct());
            }
            catch (Exception ex) { Log.Warn($"Could not parse ReShade.log: {ex.Message}"); }
        }

        var iniPath = Path.Combine(game.BinariesDir, "ReShade.ini");
        if (File.Exists(iniPath))
        {
            try
            {
                var ini = IniDocument.Load(iniPath);
                var preset = ini.Get("GENERAL", "PresetPath") ?? "";
                preset = preset.Trim().Trim('"');
                if (preset.Length > 0)
                {
                    info.PresetPath = Path.IsPathRooted(preset) ? preset : Path.GetFullPath(Path.Combine(game.BinariesDir, preset));
                    info.PresetName = Path.GetFileName(info.PresetPath);
                }

                var effectPaths = ini.Get("GENERAL", "EffectSearchPaths") ?? "";
                var roots = effectPaths.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim().Trim('"'))
                    .Select(p => p.Replace("\\**", "").Replace("/**", "").TrimEnd('\\', '/'))
                    .Select(p => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(game.BinariesDir, p)))
                    .Where(Directory.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (roots.Count == 0)
                {
                    var fallback = Path.Combine(game.BinariesDir, "reshade-shaders", "Shaders");
                    if (Directory.Exists(fallback)) roots.Add(fallback);
                }
                info.ShadersDir = roots.FirstOrDefault() ?? "";

                var shaders = new List<string>();
                foreach (var root in roots)
                {
                    try { shaders.AddRange(Directory.EnumerateFiles(root, "*.fx", SearchOption.AllDirectories)); }
                    catch { }
                }
                info.ShaderCount = shaders.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

                var duplicateGroups = shaders.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .ToList();
                foreach (var group in duplicateGroups)
                {
                    var hashes = group.Select(SafeHash).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (hashes.Count > 1)
                        info.ShaderDuplicates.Add($"{group.Key}: {group.Count()} copies with different content");
                }

                if (!string.IsNullOrEmpty(info.PresetPath) && File.Exists(info.PresetPath))
                {
                    var presetText = File.ReadAllText(info.PresetPath);
                    var match = Regex.Match(presetText, @"^\s*Techniques\s*=\s*(.+)$", RegexOptions.Multiline);
                    if (match.Success)
                    {
                        foreach (var token in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var t = token.Trim();
                            if (t.Length == 0) continue;
                            var at = t.IndexOf('@');
                            var technique = at > 0 ? t[..at] : "";
                            var file = at > 0 ? t[(at + 1)..] : t;
                            info.PresetTechniques.Add(technique.Length > 0 ? $"{technique}@{file}" : file);
                            var present = shaders.Any(s =>
                                s.EndsWith(Path.DirectorySeparatorChar + file, StringComparison.OrdinalIgnoreCase) ||
                                Path.GetFileName(s).Equals(file, StringComparison.OrdinalIgnoreCase));
                            info.ShaderReferences.Add(new ShaderReference
                            {
                                Technique = technique,
                                File = file,
                                Present = present
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                info.Errors.Add($"ReShade.ini parse error: {ex.Message}");
                Log.Warn($"ReShade.ini parse error: {ex.Message}");
            }
        }

        try
        {
            info.PresetCount = Directory.EnumerateFiles(game.BinariesDir, "*.ini", SearchOption.TopDirectoryOnly)
                .Count(f =>
                {
                    try
                    {
                        var name = Path.GetFileName(f);
                        if (name.Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase)) return false;
                        using var reader = new StreamReader(f);
                        var buffer = new char[2048];
                        var read = reader.Read(buffer, 0, buffer.Length);
                        return new string(buffer, 0, read).Contains("Techniques=", StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                });
        }
        catch { }

        info.HdrConflict = info.Installed && hardware?.HdrEnabled == true;
        if (info.Installed)
            Log.Info($"ReShade {info.Version} via {info.ProxyDll}; preset='{info.PresetName}'; {info.ShaderCount} shaders; {info.EffectErrors} errors; HDR={hardware?.HdrEnabled}");
        else
            Log.Info("ReShade not detected");

        return info;
    }

    private static bool IsReShadeModule(string path)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            var text = $"{vi.ProductName} {vi.FileDescription} {vi.CompanyName} {vi.OriginalFilename}";
            return text.Contains("ReShade", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string CleanLogLine(string line)
    {
        var idx = line.IndexOf("| INFO", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) line = line[..idx];
        return line.Length > 240 ? line[..240] : line.Trim();
    }

    private static string SafeHash(string path)
    {
        try { return BackupService.HashFile(path); } catch { return ""; }
    }

    public static bool IsGameRunning() => new ConfigService().IsGameRunning();

    public static List<string> FindReShadePresetFiles(GameInstall game)
    {
        var result = new List<string>();
        if (!game.IsValid) return result;
        foreach (var dir in new[] { game.BinariesDir, game.Root })
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.ini", SearchOption.TopDirectoryOnly))
                {
                    if (Path.GetFileName(file).Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var text = File.ReadAllText(file);
                        if (text.Contains("Techniques=", StringComparison.OrdinalIgnoreCase)) result.Add(file);
                    }
                    catch { }
                }
            }
            catch { }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool VersionAtLeast(string installed, string required)
    {
        if (string.IsNullOrWhiteSpace(installed)) return false;
        var a = installed.Split('.').Select(s => int.TryParse(new string(s.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();
        var b = required.Split('.').Select(s => int.TryParse(new string(s.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;
            if (av != bv) return av > bv;
        }
        return true;
    }
}
