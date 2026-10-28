using System.Diagnostics;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

public sealed class ConfigService
{
    public ConfigSnapshot Read(GameInstall game)
    {
        var snap = new ConfigSnapshot();
        var gameUser = Path.Combine(game.ConfigDir, "GameUserSettings.ini");
        var engine = Path.Combine(game.ConfigDir, "Engine.ini");
        var scalability = Path.Combine(game.ConfigDir, "Scalability.ini");
        snap.RawGameUserSettingsPath = gameUser;
        snap.RawEngineIniPath = engine;
        snap.RawScalabilityIniPath = scalability;
        snap.GameUserSettingsFound = File.Exists(gameUser);
        snap.EngineIniFound = File.Exists(engine);
        snap.ScalabilityIniFound = File.Exists(scalability);

        if (snap.GameUserSettingsFound)
        {
            try
            {
                var ini = IniDocument.Load(gameUser);
                snap.ResolutionX = ini.TryGetInt("/Script/Engine.GameUserSettings", "ResolutionSizeX", out var x) ? x : 0;
                snap.ResolutionY = ini.TryGetInt("/Script/Engine.GameUserSettings", "ResolutionSizeY", out var y) ? y : 0;
                snap.VSync = ini.TryGetBool("/Script/Engine.GameUserSettings", "bUseVSync", out var vs) && vs;
                snap.DynamicResolution = ini.TryGetBool("/Script/Engine.GameUserSettings", "bUseDynamicResolution", out var dr) && dr;
                snap.FrameRateLimit = ini.TryGetDouble("/Script/Engine.GameUserSettings", "FrameRateLimit", out var fr) ? fr : 0;
                snap.HdrConfigured = ini.TryGetBool("/Script/Engine.GameUserSettings", "bUseHDRDisplayOutput", out var hdr) && hdr;
                var mode = ini.Get("/Script/Engine.GameUserSettings", "FullscreenMode");
                snap.FullscreenMode = mode switch
                {
                    "0" => "Fullscreen",
                    "1" => "Windowed",
                    "2" => "Borderless",
                    _ => mode ?? "Unknown"
                };

                var groups = ini.GetSection("ScalabilityGroups");
                if (groups.Count == 0) groups = ini.GetSection("ScalabilityGroups");
                foreach (var kv in groups)
                {
                    if (kv.Key.StartsWith("sg.", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(kv.Value, out var level))
                        snap.ScalabilityGroups[kv.Key[3..]] = level;
                }
            }
            catch (Exception ex) { Log.Warn($"Could not read GameUserSettings.ini: {ex.Message}"); }
        }

        if (snap.EngineIniFound)
        {
            try
            {
                var ini = IniDocument.Load(engine);
                foreach (var kv in ini.GetSection("SystemSettings"))
                    snap.SystemSettings[kv.Key] = kv.Value;
                if (ini.TryGetBool("SystemSettings", "r.Streaming.LimitPoolSizeToVRAM", out var limit))
                    snap.SystemSettings["r.Streaming.LimitPoolSizeToVRAM"] = limit ? "1" : "0";
            }
            catch (Exception ex) { Log.Warn($"Could not read Engine.ini: {ex.Message}"); }
        }

        snap.HasCustomScalability = snap.ScalabilityIniFound && new FileInfo(scalability).Length > 40;

        var optimax = Path.Combine(game.ConfigDir, "OptimaxP_Config.ini");
        snap.HasOptimaxConfig = File.Exists(optimax);

        var s2ae = Path.Combine(game.BinariesDir, "S2AE.cfg");
        if (File.Exists(s2ae))
        {
            snap.S2aePresent = true;
            try
            {
                foreach (var line in File.ReadLines(s2ae))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("skipStartupShaderWarmup", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = trimmed.Split('=', 2).ElementAtOrDefault(1) ?? "";
                        value = value.Split('#')[0].Trim();
                        snap.S2aeShaderWarmupSkipped = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch { }
        }

        return snap;
    }

    public bool IsGameRunning()
    {
        try
        {
            return Process.GetProcesses().Any(p =>
            {
                try
                {
                    return p.ProcessName.Equals("Stalker2-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
                           p.ProcessName.Equals("Stalker2", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
                finally { p.Dispose(); }
            });
        }
        catch { return false; }
    }

    public static int VramAwarePoolSize(long vramMb, int textureQuality)
    {
        // UE streaming pool in MB. Keeps a healthy reserve for the render target and
        // avoids the common "pool bigger than VRAM" stutter pattern. Clamped to a
        // conservative range because the exact engine overhead is not measurable here.
        var fraction = textureQuality >= 3 ? 0.45 : textureQuality == 2 ? 0.35 : 0.25;
        var pool = (int)(vramMb * fraction);
        return Math.Clamp(pool, 768, 8192);
    }
}
