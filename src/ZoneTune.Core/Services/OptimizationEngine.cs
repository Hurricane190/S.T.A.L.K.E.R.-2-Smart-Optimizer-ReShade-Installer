using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Rule-based, evidence-linked optimization planner. Every action states why it was
/// selected and whether the effect is measured or estimated. No numeric FPS promises.
/// </summary>
public sealed class OptimizationEngine
{
    private readonly ConfigService _config = new();

    public OptimizationPlan BuildPlan(ScanResult scan, IEnumerable<ProblemKind> problems, bool dryRun = false)
    {        var selected = problems.Distinct().ToList();
        var plan = new OptimizationPlan
        {
            Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            Problems = selected,
            Hardware = scan.Hardware,
            GameRoot = scan.Game.Root,
            DryRun = dryRun
        };

        if (!scan.Game.IsValid)
        {
            plan.Warnings.Add("Game folder is not valid. Fix detection before generating a package.");
            return plan;
        }

        var cfg = scan.Config;
        var hw = scan.Hardware;
        var actions = new List<OptimizationAction>();
        var refresh = hw.PrimaryRefreshHz > 0 ? hw.PrimaryRefreshHz : 60;

        if (selected.Contains(ProblemKind.LoudPc))
            AddLoudActions(actions, scan, cfg, hw, refresh);
        if (selected.Contains(ProblemKind.LowFps))
            AddLowFpsActions(actions, scan, cfg, hw, refresh);
        if (selected.Contains(ProblemKind.Stuttering))
            AddStutterActions(actions, scan, cfg, hw, refresh);

        AddSharedActions(actions, scan, cfg);

        // Deduplicate by target key, keeping the more conservative (lower) proposed value
        // for quality levels and the stricter cap for frame rate. Recommendations are
        // distinct pieces of advice and must not collapse into one another.
        var deduped = new List<OptimizationAction>();
        foreach (var group in actions.GroupBy(a => a.Kind is ActionKind.Recommendation or ActionKind.ModAction or ActionKind.ReShade
                     ? $"{a.Kind}|{a.Id}"
                     : $"{a.Kind}|{a.FilePath}|{a.Section}|{a.Key}", StringComparer.OrdinalIgnoreCase))
        {
            var list = group.ToList();
            if (list.Count == 1) { deduped.Add(list[0]); continue; }
            OptimizationAction keep;
            if (list[0].Key.Equals("FrameRateLimit", StringComparison.OrdinalIgnoreCase))
                keep = selected.Contains(ProblemKind.LowFps)
                    ? list.OrderByDescending(a => ParseDouble(a.ProposedValue)).First()
                    : list.OrderBy(a => ParseDouble(a.ProposedValue)).First();
            else if (list[0].Key.StartsWith("sg.", StringComparison.OrdinalIgnoreCase) ||
                     list[0].Key.StartsWith("r.", StringComparison.OrdinalIgnoreCase))
                keep = list.OrderBy(a => ParseDouble(a.ProposedValue)).First();
            else keep = list[0];
            keep.Problems = list.SelectMany(a => a.Problems).Distinct().ToList();
            keep.Rationale = string.Join(" ", list.Select(a => a.Rationale).Distinct());
            deduped.Add(keep);
        }

        var capAction = deduped.FirstOrDefault(a => a.Key.Equals("FrameRateLimit", StringComparison.OrdinalIgnoreCase));
        if (capAction != null && capAction.Problems.Contains(ProblemKind.LowFps))
        {
            capAction.Title = "Cap frame rate just below refresh";
            capAction.Description = $"Limit the game to {capAction.ProposedValue} FPS (3 below the {refresh} Hz refresh). This keeps frame pacing " +
                                    "consistent without sacrificing most of the available frame rate. For a quieter PC, a lower cap (for example 60) " +
                                    "is more effective but reduces FPS.";
            capAction.Rationale = "Frame pacing improves when the render rate sits consistently below the display refresh. Loud-PC heat reduction is smaller at this cap than at 60.";
            capAction.Risk = RiskLevel.Low;
        }

        plan.Actions = deduped;
        plan.Warnings.AddRange(BuildWarnings(scan, cfg, hw, selected));
        plan.Summary = BuildSummary(plan, scan);
        Log.Info($"Plan {plan.Id}: problems={string.Join(",", selected)} actions={plan.Actions.Count} warnings={plan.Warnings.Count}");
        return plan;
    }

    /// <summary>
    /// A complete, hardware-derived "in-game settings" profile. Unlike the problem-based
    /// plan, this targets a balanced quality/performance point for the detected GPU,
    /// VRAM and resolution. It is still a starting point, not a guarantee.
    /// </summary>
    public OptimizationPlan BuildRecommendedProfile(ScanResult scan, bool dryRun = false)
    {
        var plan = new OptimizationPlan
        {
            Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-profile-{Guid.NewGuid().ToString("N")[..5]}",
            Problems = new List<ProblemKind> { ProblemKind.LowFps, ProblemKind.Stuttering },
            Hardware = scan.Hardware,
            GameRoot = scan.Game.Root,
            DryRun = dryRun
        };

        if (!scan.Game.IsValid)
        {
            plan.Warnings.Add("Game folder is not valid. Fix detection before applying a profile.");
            return plan;
        }

        var hw = scan.Hardware;
        var cfg = scan.Config;
        var resClass = ResolutionClass(hw);
        var tier = hw.GpuTier;
        var targets = ProfileTargets(tier, resClass, hw.VramMb, cfg.ScalabilityGroups.TryGetValue("TextureQuality", out var currentTexture) ? currentTexture : 3);
        var refresh = hw.PrimaryRefreshHz > 0 ? hw.PrimaryRefreshHz : 60;
        var cap = tier switch
        {
            "Enthusiast" => Math.Max(60, refresh - 3),
            "High" => Math.Max(60, Math.Min(refresh - 3, 144)),
            "Mid" => Math.Max(60, Math.Min(refresh - 3, 100)),
            _ => Math.Max(30, Math.Min(refresh - 3, 60))
        };

        plan.Summary = $"{tier} · {resClass} · balanced profile. Target cap {cap} FPS; motion blur reduced; texture streaming sized for " +
                       $"{HardwareInfo.FormatGb(hw.VramMb)} VRAM. Effects are estimates until measured.";

        foreach (var kv in targets)
        {
            var key = "sg." + kv.Key;
            var current = cfg.ScalabilityGroups.TryGetValue(kv.Key, out var value) ? value : -1;
            if (current == kv.Value) continue;
            plan.Actions.Add(Action($"profile-{kv.Key}", $"Set {Humanize(kv.Key)} to {kv.Value}",
                $"{Humanize(kv.Key)}: {Describe(current)} → {Describe(kv.Value)} for the {tier} / {resClass} target.",
                $"Detected {hw.GpuName} ({HardwareInfo.FormatGb(hw.VramMb)} VRAM) at {hw.PrimaryResolution}; current value {Describe(current)}.",
                cfg.RawGameUserSettingsPath, "ScalabilityGroups", key,
                current >= 0 ? current.ToString() : "not set", kv.Value.ToString(),
                ProblemKind.LowFps, RiskLevel.Low,
                "Expected: balanced image quality and GPU cost for this hardware class. Visual impact and FPS gain are scene-dependent; measure.",
                "Hardware-derived balanced profile; every value is reversible."));
        }

        if (cfg.FrameRateLimit <= 0 || Math.Abs(cfg.FrameRateLimit - cap) > 1)
        {
            plan.Actions.Add(Action("profile-cap", $"Cap frame rate to {cap} FPS",
                $"Set a {cap} FPS cap (just below the {refresh} Hz refresh) to stabilise pacing and reduce unnecessary GPU load. " +
                "Lower it manually if fan noise matters more than frame rate.",
                $"Current cap: {(cfg.FrameRateLimit <= 0 ? "unlimited" : cfg.FrameRateLimit.ToString("0.##"))}; refresh {refresh} Hz.",
                cfg.RawGameUserSettingsPath, "/Script/Engine.GameUserSettings", "FrameRateLimit",
                cfg.FrameRateLimit <= 0 ? "0" : cfg.FrameRateLimit.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                cap.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ProblemKind.Stuttering, RiskLevel.Low,
                "Expected: steadier frame pacing and lower peak power. Not a guaranteed average-FPS increase.",
                "A cap below refresh improves consistency; lower caps trade responsiveness for quietness."));
        }

        if (cfg.VSync)
        {
            plan.Actions.Add(Action("profile-vsync", "Disable VSync (use the frame cap)",
                "VSync is on. With a cap below refresh, VSync is usually unnecessary and adds latency.",
                "bUseVSync=True in GameUserSettings.ini.",
                cfg.RawGameUserSettingsPath, "/Script/Engine.GameUserSettings", "bUseVSync", "True", "False",
                ProblemKind.Stuttering, RiskLevel.Low,
                "Expected: lower input latency; screen tearing is possible if the cap is removed.", "VRR/cap usually replaces VSync."));
        }

        if (cfg.DynamicResolution)
        {
            plan.Actions.Add(Action("profile-dynres", "Disable dynamic resolution",
                "Dynamic resolution changes internal resolution at runtime. With a fixed cap and upscaling, disabling it keeps the image stable.",
                "bUseDynamicResolution=True in GameUserSettings.ini.",
                cfg.RawGameUserSettingsPath, "/Script/Engine.GameUserSettings", "bUseDynamicResolution", "True", "False",
                ProblemKind.Stuttering, RiskLevel.Medium,
                "Expected: more stable sharpness and pacing; GPU cost may rise if the game was dropping resolution heavily.",
                "Reversible; test both ways if you rely on dynamic resolution for low-end GPUs."));
        }

        var pool = ConfigService.VramAwarePoolSize(hw.VramMb > 0 ? hw.VramMb : 8192,
            targets.TryGetValue("TextureQuality", out var tq) ? tq : 3);
        var currentPool = cfg.SystemSettings.TryGetValue("r.Streaming.PoolSize", out var cp) ? cp : "not set";
        if (currentPool == "not set" || ParseDouble(currentPool) != pool)
        {
            plan.Actions.Add(Action("profile-pool", "Size the texture pool for this GPU",
                $"Set r.Streaming.PoolSize={pool} MB (about 45% of {HardwareInfo.FormatGb(hw.VramMb)} VRAM). This is a conservative starting point.",
                $"Current pool: {currentPool}; VRAM {HardwareInfo.FormatGb(hw.VramMb)}.",
                Path.Combine(scan.Game.ConfigDir, "Engine.ini"), "SystemSettings", "r.Streaming.PoolSize",
                currentPool, pool.ToString(), ProblemKind.Stuttering, RiskLevel.Medium,
                "Expected: fewer streaming hitches if the previous pool was mismatched. Watch for texture pop-in.",
                "Vram-aware pool; clamped 768–8192 MB."));
        }

        var limit = cfg.SystemSettings.TryGetValue("r.Streaming.LimitPoolSizeToVRAM", out var lv) ? lv : "not set";
        if (limit != "1")
        {
            plan.Actions.Add(Action("profile-limit", "Keep the texture pool inside VRAM",
                "Set r.Streaming.LimitPoolSizeToVRAM=1 so UE clamps its pool to the VRAM budget.",
                $"r.Streaming.LimitPoolSizeToVRAM is {limit}.",
                Path.Combine(scan.Game.ConfigDir, "Engine.ini"), "SystemSettings", "r.Streaming.LimitPoolSizeToVRAM",
                limit, "1", ProblemKind.Stuttering, RiskLevel.Medium,
                "Expected: fewer VRAM-pressure hitches; not a shader-compilation fix.", "Complements the pool size."));
        }

        var upscaler = hw.GpuVendor switch
        {
            "NVIDIA" => "DLSS Quality (if the game offers it)",
            "AMD" => "FSR Quality (if the game offers it)",
            "Intel" => "XeSS or FSR Quality (if the game offers it)",
            _ => "the game's Quality upscaler"
        };
        plan.Actions.Add(Recommendation("profile-upscaler", "Use " + upscaler,
            "The profile assumes native resolution output with a Quality upscaler when available. Enable it in the game's display menu " +
            "and compare 1% lows against native TSR before keeping it.",
            $"{hw.GpuVendor} GPU at {hw.PrimaryResolution}.",
            ProblemKind.LowFps, "Expected: meaningful GPU cost reduction. Not measured by ZoneTune."));

        if (hw.HdrEnabled)
            plan.Warnings.Add("HDR is enabled. The profile does not change HDR; ReShade presets may look wrong until HDR is disabled.");

        var changed = plan.Actions.Count(a => a.Kind is ActionKind.GameSetting or ActionKind.EngineCvar);
        plan.Warnings.Add($"This profile would change {changed} setting(s). Review them before applying; nothing is written until you press Apply.");
        plan.Warnings.Add("A balanced profile is a starting point. Measure the same route before/after; reduce or raise individual groups to taste.");
        plan.Warnings.Add("The in-game upscaler selection is not written by ZoneTune; set it in the game's display menu.");
        Log.Info($"Recommended profile {plan.Id}: {tier}/{resClass}, cap {cap}, {changed} changes");
        return plan;
    }

    private static string ResolutionClass(HardwareInfo hw)
    {
        if (hw.PrimaryResolution == "Unknown") return "unknown resolution";
        var parts = hw.PrimaryResolution.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h)) return hw.PrimaryResolution;
        if (w >= 3840 || h >= 2160) return "4K";
        if (w >= 3440) return "ultrawide 1440p";
        if (w >= 2560 || h >= 1440) return "1440p";
        if (w >= 1920 || h >= 1080) return "1080p";
        return "sub-1080p";
    }

    private static Dictionary<string, int> ProfileTargets(string tier, string resolution, long vramMb, int currentTexture)
    {
        // Ordered from most visually important to least. Values are intentionally
        // conservative: a balanced profile should not turn the Zone into soup.
        var high = new Dictionary<string, int>
        {
            ["ViewDistanceQuality"] = 3,
            ["AntiAliasingQuality"] = 3,
            ["ShadowQuality"] = 3,
            ["GlobalIlluminationQuality"] = 3,
            ["ReflectionQuality"] = 3,
            ["PostProcessQuality"] = 3,
            ["TextureQuality"] = 3,
            ["EffectsQuality"] = 3,
            ["FoliageQuality"] = 2,
            ["ShadingQuality"] = 3,
            ["HairQuality"] = 2,
            ["ObjectDetailQuality"] = 3,
            ["VolumetricFogQuality"] = 2,
            ["VolumetricCloudsQuality"] = 2,
            ["SkyQuality"] = 3,
            ["MaterialQuality"] = 3,
            ["MotionBlurQuality"] = 1,
            ["DOFQuality"] = 3,
            ["LandscapeQuality"] = 3
        };
        var mid = new Dictionary<string, int>
        {
            ["ViewDistanceQuality"] = 2,
            ["AntiAliasingQuality"] = 3,
            ["ShadowQuality"] = 2,
            ["GlobalIlluminationQuality"] = 2,
            ["ReflectionQuality"] = 2,
            ["PostProcessQuality"] = 2,
            ["TextureQuality"] = vramMb >= 8000 ? 3 : 2,
            ["EffectsQuality"] = 2,
            ["FoliageQuality"] = 2,
            ["ShadingQuality"] = 3,
            ["HairQuality"] = 1,
            ["ObjectDetailQuality"] = 2,
            ["VolumetricFogQuality"] = 1,
            ["VolumetricCloudsQuality"] = 1,
            ["SkyQuality"] = 2,
            ["MaterialQuality"] = 2,
            ["MotionBlurQuality"] = 1,
            ["DOFQuality"] = 2,
            ["LandscapeQuality"] = 2
        };
        var entry = new Dictionary<string, int>
        {
            ["ViewDistanceQuality"] = 1,
            ["AntiAliasingQuality"] = 2,
            ["ShadowQuality"] = 1,
            ["GlobalIlluminationQuality"] = 1,
            ["ReflectionQuality"] = 1,
            ["PostProcessQuality"] = 1,
            ["TextureQuality"] = vramMb >= 6000 ? 2 : 1,
            ["EffectsQuality"] = 1,
            ["FoliageQuality"] = 1,
            ["ShadingQuality"] = 2,
            ["HairQuality"] = 1,
            ["ObjectDetailQuality"] = 1,
            ["VolumetricFogQuality"] = 0,
            ["VolumetricCloudsQuality"] = 0,
            ["SkyQuality"] = 1,
            ["MaterialQuality"] = 1,
            ["MotionBlurQuality"] = 0,
            ["DOFQuality"] = 1,
            ["LandscapeQuality"] = 1
        };

        var targets = tier switch
        {
            "Enthusiast" => new Dictionary<string, int>(high)
            {
                ["MotionBlurQuality"] = 1,
                ["VolumetricFogQuality"] = 2,
                ["VolumetricCloudsQuality"] = 2
            },
            "High" => high,
            "Mid" => mid,
            "Entry" => entry,
            _ => mid
        };

        // Resolution pressure: 4K/ultrawide on mid/entry drops one notch on GPU-heavy groups.
        if ((resolution is "4K" or "ultrawide 1440p") && tier is "Mid" or "Entry")
        {
            foreach (var key in new[] { "ShadowQuality", "GlobalIlluminationQuality", "ReflectionQuality", "VolumetricFogQuality", "VolumetricCloudsQuality", "FoliageQuality" })
                if (targets.TryGetValue(key, out var v)) targets[key] = Math.Max(0, v - 1);
        }
        // 1080p on high-end can afford the full texture/quality ceiling; do not exceed 3.
        if (resolution == "1080p" && tier is "High" or "Enthusiast")
            foreach (var key in targets.Keys.ToList()) targets[key] = Math.Min(3, targets[key]);

        return targets;
    }

    private static string Describe(int value) => value < 0 ? "not set" : value == 0 ? "Low" : value == 1 ? "Medium" : value == 2 ? "High" : "Epic";

    private static void AddLoudActions(List<OptimizationAction> actions, ScanResult scan, ConfigSnapshot cfg, HardwareInfo hw, int refresh)
    {
        var cap = Math.Max(30, Math.Min(60, refresh > 0 ? refresh - 2 : 60));
        if (cfg.FrameRateLimit <= 0 || cfg.FrameRateLimit > cap + 1)
        {
            actions.Add(Action("loud-cap", "Cap frame rate to reduce heat",
                $"Limit the game to {cap} FPS instead of an uncapped/high frame rate. A cap is the most direct way to lower GPU " +
                "utilisation, heat and fan speed; it trades some responsiveness and peak FPS for a quieter, cooler system.",
                "Current limit is " + (cfg.FrameRateLimit <= 0 ? "unlimited" : $"{cfg.FrameRateLimit:0} FPS") + $"; display refresh is {refresh} Hz.",
                cfg.RawGameUserSettingsPath, "/Script/Engine.GameUserSettings", "FrameRateLimit",
                cfg.FrameRateLimit <= 0 ? "0" : cfg.FrameRateLimit.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                cap.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ProblemKind.LoudPc, RiskLevel.Low,
                "Expected: lower GPU power draw and fan noise in GPU-bound scenes. Measure power/noise before and after; the exact reduction depends on the scene and power limit.",
                "Frame-rate cap is the first lever for a loud PC; it does not reduce visual quality."));
        }

        AddQualityDrop(actions, scan, "loud-fog", "sg.VolumetricFogQuality", 2, ProblemKind.LoudPc,
            "Volumetric fog is a per-pixel lighting cost. One notch down reduces GPU load in foggy weather; visual difference is usually subtle.",
            "Current volumetric fog is at level {0}.");
        AddQualityDrop(actions, scan, "loud-clouds", "sg.VolumetricCloudsQuality", 2, ProblemKind.LoudPc,
            "Volumetric clouds are expensive and mostly affect the sky. Lowering one notch is a low-visibility-impact heat reduction.",
            "Current volumetric clouds are at level {0}.");
        AddQualityDrop(actions, scan, "loud-shadows", "sg.ShadowQuality", 2, ProblemKind.LoudPc,
            "Shadow resolution/filtering scales with this group and is a steady GPU cost. One notch down is a common quiet-PC trade.",
            "Current shadow quality is at level {0}.");

        actions.Add(new OptimizationAction
        {
            Id = "loud-gpu-limit",
            Title = "Use a GPU power limit or undervolt (manual, vendor tool)",
            Description = "Set a conservative power limit (for example 80–85%) or a tested undervolt in NVIDIA App / AMD Adrenalin. " +
                          "This is deliberately not automated: voltage/frequency curves are silicon-specific.",
            Rationale = "Power limits reduce heat and fan noise more effectively than most in-game settings, but stability must be validated per GPU.",
            Evidence = $"GPU: {hw.GpuName}; driver {hw.GpuDriverVersion}.",
            Problems = { ProblemKind.LoudPc },
            Risk = RiskLevel.Medium,
            Confidence = VerificationStatus.Estimated,
            Kind = ActionKind.Recommendation,
            EstimatedEffect = "Potentially the largest noise reduction; not measured by ZoneTune.",
            Selected = true
        });
    }

    private static void AddLowFpsActions(List<OptimizationAction> actions, ScanResult scan, ConfigSnapshot cfg, HardwareInfo hw, int refresh)
    {
        if (hw.GpuVendor == "NVIDIA")
        {
            actions.Add(Recommendation("fps-upscaler", "Enable DLSS Quality (in-game)",
                "If the game offers DLSS, start with Quality mode at your native resolution. It reconstructs from a lower internal resolution " +
                "and usually recovers more performance than dropping shadow quality. Test the HUD and fine foliage for shimmer.",
                "GPU vendor is NVIDIA; this card supports DLSS on supported titles.",
                ProblemKind.LowFps, "Expected: meaningful GPU cost reduction, resolution-dependent. Not measured by ZoneTune."));
        }
        else if (hw.GpuVendor == "AMD")
        {
            actions.Add(Recommendation("fps-upscaler", "Enable FSR Quality (in-game)",
                "If the game offers FSR, start with Quality mode. It is vendor-agnostic and usually cheaper than native TSR at the same output resolution. " +
                "Check thin geometry and foliage for shimmer before keeping it.",
                "GPU vendor is AMD; FSR is available on supported titles.",
                ProblemKind.LowFps, "Expected: meaningful GPU cost reduction, resolution-dependent. Not measured by ZoneTune."));
        }
        else if (hw.GpuVendor == "Intel")
        {
            actions.Add(Recommendation("fps-upscaler", "Enable XeSS or FSR Quality (in-game)",
                "Use the upscaler offered by the game (XeSS or FSR), starting at Quality. Compare against native TSR for stability and ghosting.",
                "GPU vendor is Intel.",
                ProblemKind.LowFps, "Expected: meaningful GPU cost reduction. Not measured by ZoneTune."));
        }

        AddQualityDrop(actions, scan, "fps-shadows", "sg.ShadowQuality", 2, ProblemKind.LowFps,
            "Shadows are one of the highest-value quality settings to lower for FPS; the visual cost is mostly in distant shadow detail.",
            "Current shadow quality is at level {0}.");
        AddQualityDrop(actions, scan, "fps-gi", "sg.GlobalIlluminationQuality", 2, ProblemKind.LowFps,
            "Lumen/GI quality has a large GPU cost in outdoor scenes. One notch down is usually the best FPS-per-visual-loss trade after upscaling.",
            "Current global illumination is at level {0}.");
        AddQualityDrop(actions, scan, "fps-reflections", "sg.ReflectionQuality", 2, ProblemKind.LowFps,
            "Screen-space reflections cost GPU time in wet/indoor areas. Lowering one notch is often hard to notice during movement.",
            "Current reflection quality is at level {0}.");
        AddQualityDrop(actions, scan, "fps-fog", "sg.VolumetricFogQuality", 2, ProblemKind.LowFps,
            "Volumetric fog is a per-pixel cost that scales with resolution; one notch down helps without changing geometry quality.",
            "Current volumetric fog is at level {0}.");
        AddQualityDrop(actions, scan, "fps-clouds", "sg.VolumetricCloudsQuality", 2, ProblemKind.LowFps,
            "Clouds fill many pixels in outdoor scenes. Lowering them frees GPU time with little gameplay impact.",
            "Current volumetric clouds are at level {0}.");
        AddQualityDrop(actions, scan, "fps-foliage", "sg.FoliageQuality", 2, ProblemKind.LowFps,
            "Foliage density/draw distance is CPU- and GPU-relevant in the Zone. One notch down is a reasonable FPS trade.",
            "Current foliage quality is at level {0}.");

        if (hw.PrimaryRefreshHz >= 100 && hw.VramMb > 0 && hw.VramMb < 10_000)
        {
            actions.Add(Recommendation("fps-resolution", "Consider 1440p or Quality upscaling instead of native 4K",
                $"The display is {hw.PrimaryResolution} at {hw.PrimaryRefreshHz} Hz, while the GPU reports {HardwareInfo.FormatGb(hw.VramMb)} VRAM. " +
                "At 4K, VRAM and bandwidth are often the limit before compute. Native 1440p or a Quality upscaler is usually a larger win than further quality cuts.",
                $"Detected {hw.GpuName}, {HardwareInfo.FormatGb(hw.VramMb)} VRAM, {hw.PrimaryResolution}.",
                ProblemKind.LowFps, "Expected: large; resolution scaling dominates. Not measured by ZoneTune."));
        }

        if ((hw.GpuName.Contains("RTX 40", StringComparison.OrdinalIgnoreCase) ||
             hw.GpuName.Contains("RTX 50", StringComparison.OrdinalIgnoreCase)) && hw.PrimaryRefreshHz >= 100)
        {
            actions.Add(Recommendation("fps-framegen", "Test DLSS Frame Generation (if offered), but measure latency",
                "Frame Generation can raise displayed FPS on RTX 40/50 cards, but it adds latency and can amplify frame-pacing artefacts. " +
                "ZoneTune does not enable it automatically. Compare 1% lows and input feel, not just the average counter.",
                $"GPU: {hw.GpuName}; display: {hw.PrimaryRefreshHz} Hz.",
                ProblemKind.LowFps, "Expected: higher displayed FPS; latency impact must be measured."));
        }
    }

    private static void AddStutterActions(List<OptimizationAction> actions, ScanResult scan, ConfigSnapshot cfg, HardwareInfo hw, int refresh)
    {
        var target = Math.Max(60, refresh > 0 ? refresh - 3 : 57);
        if (cfg.FrameRateLimit <= 0 || cfg.FrameRateLimit > target + 1)
        {
            actions.Add(Action("stutter-cap", "Stabilise frame pacing with a cap below refresh",
                $"Cap to {target} FPS, a few frames below the {refresh} Hz refresh. A small cap prevents the GPU from oscillating between " +
                "VRR/refresh boundaries and often improves frame-time consistency even when average FPS falls slightly.",
                cfg.FrameRateLimit <= 0 ? "Frame rate is currently unlimited." : $"Current cap is {cfg.FrameRateLimit:0} FPS.",
                cfg.RawGameUserSettingsPath, "/Script/Engine.GameUserSettings", "FrameRateLimit",
                cfg.FrameRateLimit <= 0 ? "0" : cfg.FrameRateLimit.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
                target.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ProblemKind.Stuttering, RiskLevel.Low,
                "Expected: better 1% lows/frame-time consistency, not necessarily higher average FPS. Measure with PresentMon.",
                "Frame pacing is about consistency, not peak frame rate."));
        }

        var pool = ConfigService.VramAwarePoolSize(hw.VramMb > 0 ? hw.VramMb : 8192,
            cfg.ScalabilityGroups.TryGetValue("TextureQuality", out var tq) ? tq : 3);
        var currentPool = cfg.SystemSettings.TryGetValue("r.Streaming.PoolSize", out var cp) ? cp : "not set";
        if (currentPool == "not set" || ParseDouble(currentPool) != pool)
        {
            actions.Add(Action("stutter-pool", "Size the texture streaming pool to available VRAM",
                $"Set the UE streaming pool to {pool} MB (about 45% of {HardwareInfo.FormatGb(hw.VramMb)} VRAM at texture quality " +
                $"{tq}). An oversized pool competes with render targets and can cause hitches; an undersized pool causes texture pop-in. " +
                "This is a conservative starting point, not a universal optimum.",
                $"Current Engine.ini pool: {currentPool}; GPU VRAM: {HardwareInfo.FormatGb(hw.VramMb)}.",
                Path.Combine(scan.Game.ConfigDir, "Engine.ini"), "SystemSettings", "r.Streaming.PoolSize",
                currentPool, pool.ToString(), ProblemKind.Stuttering, RiskLevel.Medium,
                "Expected: fewer streaming hitches if the pool was oversized; texture pop-in may appear if set too low. Measure frame-time spikes.",
                "VRAM-aware pool sizing; clamped to 768–8192 MB."));
        }

        var limit = cfg.SystemSettings.TryGetValue("r.Streaming.LimitPoolSizeToVRAM", out var lv) ? lv : "not set";
        if (limit != "1")
        {
            actions.Add(Action("stutter-limit", "Keep the streaming pool inside VRAM",
                "Set r.Streaming.LimitPoolSizeToVRAM=1 so UE clamps its texture pool to the reported VRAM budget. This reduces the chance " +
                "of driver-level eviction and long hitches when textures compete with ray tracing/Lumen allocations.",
                $"r.Streaming.LimitPoolSizeToVRAM is currently {limit}.",
                Path.Combine(scan.Game.ConfigDir, "Engine.ini"), "SystemSettings", "r.Streaming.LimitPoolSizeToVRAM",
                limit, "1", ProblemKind.Stuttering, RiskLevel.Medium,
                "Expected: fewer VRAM-pressure hitches. Not a shader-compilation fix.",
                "Do not combine with an aggressively large pool; the cap is the point."));
        }

        if (cfg.DynamicResolution)
        {
            actions.Add(Recommendation("stutter-dynamicres", "Test with dynamic resolution disabled",
                "Dynamic resolution changes internal resolution on the fly, which can look like softness/pumping and can interact badly with " +
                "upscalers. If your frame times are already inconsistent, test off and compare 1% lows before keeping it.",
                "bUseDynamicResolution is enabled in GameUserSettings.ini.",
                ProblemKind.Stuttering, "Expected: more stable image and possibly more consistent frame time, at higher GPU cost. Test both ways."));
        }

        if (cfg.S2aeShaderWarmupSkipped)
        {
            actions.Add(Recommendation("stutter-warmup", "Re-enable startup shader warmup in S2AE",
                "S2AE is configured to skip the blocking shader warmup. That makes the menu load faster but can move compilation hitches into " +
                "the first minutes of play. Set skipStartupShaderWarmup=false in S2AE.cfg and retest.",
                "S2AE.cfg: skipStartupShaderWarmup=true.",
                ProblemKind.Stuttering, "Expected: fewer first-session compile hitches, longer startup. Not measured."));
        }

        if (scan.ModConflicts.Count > 0)
        {
            actions.Add(Recommendation("stutter-modconflicts", "Resolve overlapping mods before tuning further",
                $"{scan.ModConflicts.Count} mod conflict note(s) were found (pak overlaps/duplicate names). Overlapping pak mods can introduce " +
                "asset duplication, memory pressure and inconsistent behaviour that no config tweak can fix. Disable one variant and retest.",
                string.Join(" | ", scan.ModConflicts.Take(3).Select(c => c.Title)),
                ProblemKind.Stuttering, "Expected: removes a known source of instability. Effect depends on the conflict."));
        }

        if (scan.ReShade.Installed && scan.ReShade.PresetTechniques.Count >= 4)
        {
            actions.Add(Recommendation("stutter-reshade", "Trim heavy ReShade effects while diagnosing stutter",
                $"ReShade is running '{scan.ReShade.PresetName}' with {scan.ReShade.PresetTechniques.Count} techniques. Toggle the preset off " +
                "with the Home overlay and compare frame-time spikes. If the spikes disappear, switch to a lighter preset in the ReShade Manager.",
                string.Join(", ", scan.ReShade.PresetTechniques.Take(6)),
                ProblemKind.Stuttering, "Expected: removes post-processing cost; helps only if ReShade is the cause."));
        }

        if (hw.GameDriveTotalBytes > 0 && hw.GameDriveFreeBytes < hw.GameDriveTotalBytes * 0.08)
        {
            actions.Add(Recommendation("stutter-storage", "Free space on the game drive",
                $"Only {AppPaths.FormatBytes(hw.GameDriveFreeBytes)} is free ({hw.GameDriveFreeBytes * 100.0 / hw.GameDriveTotalBytes:0.#}%). " +
                "Low free space on an SSD can reduce write caching and shader-cache headroom. Aim for at least 10–15% free.",
                $"Drive {hw.GameDriveName}: {AppPaths.FormatBytes(hw.GameDriveFreeBytes)} free of {AppPaths.FormatBytes(hw.GameDriveTotalBytes)}.",
                ProblemKind.Stuttering, "Expected: removes a storage-pressure risk; not a guaranteed frame-time fix."));
        }
    }

    private static void AddSharedActions(List<OptimizationAction> actions, ScanResult scan, ConfigSnapshot cfg)
    {
        if (cfg.HasOptimaxConfig)
        {
            actions.Add(Recommendation("shared-optimax", "Keep OptimaxP, but test config changes one at a time",
                "OptimaxP_Config.ini is active. It may set the same cvars as ZoneTune's Engine.ini. ZoneTune does not delete or overwrite it; " +
                "if a tweak appears to have no effect, compare the two files and remove the duplicate from one side.",
                "OptimaxP_Config.ini found in the Saved config folder.",
                ProblemKind.LowFps, "Expected: prevents silent override; no direct performance effect."));
        }

        if (scan.ReShade.Installed && scan.ReShade.HdrConflict)
        {
            actions.Add(Recommendation("shared-hdr-reshade", "Disable HDR while using ReShade",
                "HDR is enabled and ReShade is active. Most ReShade effects and presets are authored for SDR; HDR10 output can look washed out " +
                "or clipped. Disable HDR in-game (or remove ReShade) and compare. The ReShade Manager flags this on every preset.",
                $"HDR enabled={scan.Hardware.HdrEnabled}; ReShade {scan.ReShade.Version} via {scan.ReShade.ProxyDll}.",
                ProblemKind.LowFps, "Expected: correct colour rather than a performance change."));
        }
    }

    private static List<string> BuildWarnings(ScanResult scan, ConfigSnapshot cfg, HardwareInfo hw, List<ProblemKind> problems)
    {
        var warnings = new List<string>();
        if (scan.GameRunning)
            warnings.Add("STALKER 2 is running. Exit the game before applying changes; the game rewrites GameUserSettings.ini on exit.");
        if (!cfg.GameUserSettingsFound)
            warnings.Add("GameUserSettings.ini was not found. Launch the game once so settings exist before applying a plan.");
        if (cfg.HasCustomScalability)
            warnings.Add("A custom Scalability.ini is present. ZoneTune does not overwrite it; the package only edits GameUserSettings.ini and Engine.ini.");
        if (cfg.HasOptimaxConfig)
            warnings.Add("OptimaxP is installed. Engine.ini changes may partially overlap with its cvars.");
        if (scan.ReShade.Installed && scan.ReShade.EffectErrors > 0)
            warnings.Add($"ReShade.log contains {scan.ReShade.EffectErrors} error line(s). Fix shader errors before judging visual/performance changes.");
        if (scan.ModConflicts.Count > 0)
            warnings.Add($"{scan.ModConflicts.Count} mod conflict note(s) exist. Config tweaks cannot fix asset-level conflicts.");
        if (hw.VramMb > 0 && hw.VramMb < 8_000 && hw.PrimaryResolution is "3840x2160" or "3440x1440")
            warnings.Add("VRAM is tight for this resolution. Prefer upscaling and lower texture quality before adding post-processing.");
        if (problems.Count == 0)
            warnings.Add("No problems were selected; the plan is empty.");
        warnings.Add("All performance effects are estimates until measured on this PC. Use the Diagnostic Reports tab to import a PresentMon capture.");
        return warnings;
    }

    private static string BuildSummary(OptimizationPlan plan, ScanResult scan)
    {
        var config = plan.Actions.Count(a => a.Kind is ActionKind.GameSetting or ActionKind.EngineCvar && a.Selected);
        var recs = plan.Actions.Count(a => a.Kind == ActionKind.Recommendation && a.Selected);
        var labels = plan.Problems.Select(p => p switch
        {
            ProblemKind.LoudPc => "Loud PC",
            ProblemKind.LowFps => "Low FPS",
            _ => "Stuttering"
        });
        return $"{config} reversible config change(s) and {recs} recommendation(s) for {string.Join(" + ", labels)}. " +
               $"Hardware: {scan.Hardware.GpuName}, {scan.Hardware.CpuName}, {scan.Hardware.PrimaryResolution}. " +
               "Effects are estimates until a before/after measurement is captured.";
    }

    public ApplyResult Apply(OptimizationPlan plan, BackupService backups, bool ignoreRunningGame = false)
    {
        var result = new ApplyResult();
        if (!ignoreRunningGame && new ConfigService().IsGameRunning())
        {
            result.Success = false;
            result.Errors.Add("STALKER 2 is running. Exit the game before applying changes; it rewrites GameUserSettings.ini on exit and can hold config files.");
            return result;
        }
        if (plan.DryRun)
        {
            result.Success = true;
            result.Messages.Add("Dry run: no files were written.");
            return result;
        }

        var actions = plan.ConfigActions.Where(a => a.Selected).ToList();
        if (actions.Count == 0)
        {
            result.Success = true;
            result.Messages.Add("No config actions selected; nothing to write.");
            return result;
        }

        var files = actions.Select(a => a.FilePath).Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var cs = backups.CreateChangeSet("optimize", plan.Summary, plan.GameRoot, files);
        result.ChangeSetId = cs.Id;
        result.BackupPath = cs.Directory;

        try
        {
            foreach (var group in actions.GroupBy(a => a.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                var ini = IniDocument.Load(group.Key);
                foreach (var action in group)
                {
                    if (string.IsNullOrWhiteSpace(action.Section) || string.IsNullOrWhiteSpace(action.Key))
                        continue;
                    ini.Set(action.Section, action.Key, action.ProposedValue);
                    result.Messages.Add($"{action.Key}: {action.CurrentValue} → {action.ProposedValue}");
                }
                ini.Save();
                backups.RecordAfter(cs, group.Key);
                result.FilesChanged.Add(group.Key);
                Log.Info($"Applied {group.Count()} settings to {group.Key}");
            }

            // Validate by re-reading, not by trusting the write.
            foreach (var action in actions)
            {
                var ini = IniDocument.Load(action.FilePath);
                var actual = ini.Get(action.Section, action.Key);
                if (!string.Equals(actual?.Trim(), action.ProposedValue.Trim(), StringComparison.OrdinalIgnoreCase))
                    result.Errors.Add($"Validation failed for {action.Key}: found '{actual ?? "missing"}'.");
            }

            result.Success = result.Errors.Count == 0;
            if (!result.Success)
            {
                result.Messages.Add("Validation failed; rolling back automatically.");
                backups.Restore(cs.Id);
                result.Success = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Optimization apply failed; rolling back.", ex);
            result.Errors.Add(ex.Message);
            try { backups.Restore(cs.Id); } catch (Exception rex) { result.Errors.Add("Rollback failed: " + rex.Message); }
            result.Success = false;
        }

        return result;
    }

    private static void AddQualityDrop(List<OptimizationAction> actions, ScanResult scan, string id, string key, int target,
        ProblemKind problem, string rationale, string evidenceTemplate)
    {
        var shortKey = key.StartsWith("sg.", StringComparison.OrdinalIgnoreCase) ? key[3..] : key;
        if (!scan.Config.ScalabilityGroups.TryGetValue(shortKey, out var current) || current <= target)
            return;

        actions.Add(Action(id, $"Lower {Humanize(shortKey)} to {target}",
            $"{Humanize(shortKey)} is at {current}/3. Lower it to {target}/3.",
            string.Format(evidenceTemplate, current),
            scan.Config.RawGameUserSettingsPath, "ScalabilityGroups", key,
            current.ToString(), target.ToString(), problem, RiskLevel.Low,
            "Expected: moderate GPU cost reduction; visual impact is scene-dependent. Measure with PresentMon.",
            rationale));
    }

    private static OptimizationAction Action(string id, string title, string description, string evidence, string file,
        string section, string key, string current, string proposed, ProblemKind problem, RiskLevel risk,
        string estimatedEffect, string rationale) => new()
    {
        Id = id,
        Title = title,
        Description = description,
        Rationale = rationale,
        Evidence = evidence,
        Problems = { problem },
        Risk = risk,
        Confidence = VerificationStatus.Estimated,
        Kind = file.EndsWith("GameUserSettings.ini", StringComparison.OrdinalIgnoreCase)
            ? ActionKind.GameSetting
            : ActionKind.EngineCvar,
        FilePath = file,
        Section = section,
        Key = key,
        CurrentValue = current,
        ProposedValue = proposed,
        EstimatedEffect = estimatedEffect,
        Selected = true
    };

    private static OptimizationAction Recommendation(string id, string title, string description, string evidence,
        ProblemKind problem, string effect) => new()
    {
        Id = id,
        Title = title,
        Description = description,
        Rationale = "Manual step: measured comparison is required before keeping this change.",
        Evidence = evidence,
        Problems = { problem },
        Risk = RiskLevel.Low,
        Confidence = VerificationStatus.Estimated,
        Kind = ActionKind.Recommendation,
        EstimatedEffect = effect,
        Selected = true
    };

    private static string Humanize(string key)
    {
        var text = key.Replace("sg.", "").Replace("r.", "");
        return System.Text.RegularExpressions.Regex.Replace(text, "([a-z])([A-Z])", "$1 $2");
    }

    private static double ParseDouble(string value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
}

