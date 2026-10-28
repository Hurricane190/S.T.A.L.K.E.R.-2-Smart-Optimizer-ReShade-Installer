using System.Text;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

public enum ReShadeMode
{
    Potato,
    Medium,
    High
}

public sealed class CustomPresetOptions
{
    public ReShadeMode Mode { get; set; } = ReShadeMode.Medium;
    public string Name { get; set; } = "My Zone Look";
    public bool UseLevels { get; set; } = true;
    public bool UseColourfulness { get; set; } = true;
    public bool UseDeband { get; set; } = true;
    public bool UseSharpen { get; set; } = true;
    public bool UseFilmic { get; set; } = false;
    public bool UseBloom { get; set; } = false;
    public double ColourIntensity { get; set; } = 0.12;
    public double SharpenStrength { get; set; } = 0.32;
    public double FilmicStrength { get; set; } = 0.45;
    public double BloomAmount { get; set; } = 0.16;
}

public sealed class EffectInfo
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Cost { get; set; } = "";
    public bool RecommendedForPotato { get; set; }
    public bool RecommendedForMedium { get; set; }
    public bool RecommendedForHigh { get; set; }
}

/// <summary>
/// Generates a custom ReShade preset using only the official crosire/reshade-shaders
/// files. Modes are starting points, not promises; the UI labels every cost estimate
/// and warns when a heavy effect is enabled on weaker hardware.
/// </summary>
public sealed class CustomPresetBuilder
{
    public static readonly EffectInfo[] Effects =
    {
        new() { Key = "levels", Name = "Colour correction", Description = "Black/white point and gamma correction. Very cheap.", Cost = "Negligible", RecommendedForPotato = true, RecommendedForMedium = true, RecommendedForHigh = true },
        new() { Key = "colour", Name = "Saturation", Description = "Colourfulness lift. One cheap pass.", Cost = "Very low", RecommendedForPotato = true, RecommendedForMedium = true, RecommendedForHigh = true },
        new() { Key = "deband", Name = "Deband", Description = "Removes colour banding in skies and fog. Cheap.", Cost = "Very low", RecommendedForPotato = true, RecommendedForMedium = true, RecommendedForHigh = true },
        new() { Key = "sharpen", Name = "Adaptive sharpen", Description = "Edge-aware sharpening. One pass; low cost.", Cost = "Low", RecommendedForPotato = true, RecommendedForMedium = true, RecommendedForHigh = true },
        new() { Key = "filmic", Name = "Filmic curve", Description = "Filmic contrast/fade. One pass; best on mid/high GPUs.", Cost = "Low–medium", RecommendedForPotato = false, RecommendedForMedium = true, RecommendedForHigh = true },
        new() { Key = "bloom", Name = "Bloom", Description = "Soft highlight bloom. Multi-tap; the most expensive option here.", Cost = "Medium", RecommendedForPotato = false, RecommendedForMedium = false, RecommendedForHigh = true }
    };

    public ReShadeMode Recommend(HardwareInfo hw)
    {
        var res = hw.PrimaryResolution == "Unknown" ? 1920 : ParseWidth(hw.PrimaryResolution);
        var vram = hw.VramMb;
        if (hw.GpuTier == "Entry" || (vram > 0 && vram < 6_000)) return ReShadeMode.Potato;
        if (hw.GpuTier == "Mid") return res >= 3400 ? ReShadeMode.Medium : ReShadeMode.Medium;
        if (hw.GpuTier == "High" && res >= 3400) return ReShadeMode.Medium;
        if (hw.GpuTier is "High" or "Enthusiast") return ReShadeMode.High;
        return ReShadeMode.Medium;
    }

    public static int ParseWidth(string resolution)
    {
        var parts = resolution.Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out var w) ? w : 1920;
    }

    public CustomPresetOptions Defaults(HardwareInfo hw, ReShadeMode mode) => new()
    {
        Mode = mode,
        Name = mode switch
        {
            ReShadeMode.Potato => "My Potato Look",
            ReShadeMode.Medium => "My Balanced Look",
            _ => "My High Look"
        },
        UseLevels = true,
        UseColourfulness = true,
        UseDeband = true,
        UseSharpen = true,
        UseFilmic = mode != ReShadeMode.Potato,
        UseBloom = mode == ReShadeMode.High,
        ColourIntensity = mode == ReShadeMode.Potato ? 0.08 : mode == ReShadeMode.Medium ? 0.12 : 0.16,
        SharpenStrength = mode == ReShadeMode.Potato ? 0.20 : mode == ReShadeMode.Medium ? 0.32 : 0.38,
        FilmicStrength = mode == ReShadeMode.Medium ? 0.40 : 0.55,
        BloomAmount = 0.16
    };

    public PresetEntry Build(HardwareInfo hw, CustomPresetOptions o)
    {
        var techniques = new List<string>();
        var sections = new List<(string file, string values)>();
        if (o.UseLevels)
        {
            techniques.Add("LevelsPlus@LevelsPlus.fx");
            sections.Add(("LevelsPlus.fx",
                "EnableLevels=1\n" +
                $"InputBlackPoint={F(0.004 + o.ColourIntensity * 0.02)},{F(0.004 + o.ColourIntensity * 0.02)},{F(0.005 + o.ColourIntensity * 0.02)}\n" +
                "InputWhitePoint=0.994,0.994,0.992\n" +
                "InputGamma=1.000,0.998,0.992\n" +
                "SmoothCurve=1\nHighlightClipping=0\nAvoidClipping=1"));
        }
        if (o.UseColourfulness)
        {
            techniques.Add("Colourfulness@Colourfulness.fx");
            sections.Add(("Colourfulness.fx", $"colourfulness={F(o.ColourIntensity)}\nlim_luma=0.68\nenable_dither=0"));
        }
        if (o.UseDeband)
        {
            techniques.Add("Deband@Deband.fx");
            sections.Add(("Deband.fx", "enable_weber=1\nenable_sdeviation=1\nenable_depthbuffer=0\nt1=0.007\nt2=0.040\nbanding_depth=1.0\nrange=24.0\niterations=1\ndebug_output=0"));
        }
        if (o.UseSharpen)
        {
            techniques.Add("AdaptiveSharpen@AdaptiveSharpen.fx");
            sections.Add(("AdaptiveSharpen.fx", $"curve_height={F(Math.Clamp(o.SharpenStrength, 0.01, 1.5))}\ncurveslope=0.60\nL_overshoot=0.010\nD_overshoot=0.010\nscale_lim=0.10"));
        }
        if (o.UseFilmic)
        {
            techniques.Add("FilmicPass@FilmicPass.fx");
            sections.Add(("FilmicPass.fx", $"Strength={F(Math.Clamp(o.FilmicStrength, 0, 1))}\nFade=0.10\nContrast=0.98\nLinearization=0.5\nBleach=0.0\nSaturation=0.95\nRedCurve=1.0\nGreenCurve=1.0\nBlueCurve=1.0\nBaseCurve=1.0\nBaseGamma=1.0\nEffectGamma=1.0\nEffectGammaR=1.0\nEffectGammaG=1.0\nEffectGammaB=1.0"));
        }
        if (o.UseBloom)
        {
            techniques.Add("BloomAndLensFlares@Bloom.fx");
            sections.Add(("Bloom.fx", $"fBloomThreshold=0.86\nfBloomAmount={F(Math.Clamp(o.BloomAmount, 0, 1))}\nfBloomSaturation=0.72\nfBloomTint=0.92,0.92,1.0\nbLensdirtEnable=0\nbAnamFlareEnable=0\nbLenzEnable=0\nbChapFlareEnable=0\nbGodrayEnable=0"));
        }

        if (techniques.Count == 0)
        {
            o.UseLevels = true;
            techniques.Add("LevelsPlus@LevelsPlus.fx");
            sections.Add(("LevelsPlus.fx", "EnableLevels=1\nInputBlackPoint=0.004,0.004,0.005\nInputWhitePoint=0.994,0.994,0.992\nInputGamma=1.0,1.0,0.99\nSmoothCurve=1\nHighlightClipping=0\nAvoidClipping=1"));
        }

        var text = BuildText(o, techniques, sections);
        var cost = o.Mode switch
        {
            ReShadeMode.Potato => "< 0.5% at 1440p (estimate; no bloom/filmic)",
            ReShadeMode.Medium => "1–2% at 1440p (estimate; filmic + sharpen)",
            _ => "2–4% at 1440p (estimate; bloom is the main cost)"
        };
        if (hw.PrimaryResolution is "3840x2160" or "3440x1440" or "5120x1440")
            cost += "; higher at this pixel count";
        return new PresetEntry
        {
            Id = "custom-" + Math.Abs($"{o.Name}|{o.Mode}|{text}".GetHashCode()).ToString("x8"),
            Name = string.IsNullOrWhiteSpace(o.Name) ? "My Zone Look" : o.Name.Trim(),
            Author = "You · ZoneTune Preset Studio",
            SourceName = "Created locally",
            SourceUrl = "",
            License = "Your own preset values; official shaders retain their licenses",
            Description = o.Mode switch
            {
                ReShadeMode.Potato => "A deliberately minimal stack for low-end GPUs: colour correction, subtle saturation, deband and light sharpening. No filmic pass or bloom.",
                ReShadeMode.Medium => "A balanced grade: colour correction, saturation, deband, adaptive sharpening and a restrained filmic curve. No bloom.",
                _ => "The fuller look: the medium stack plus a controlled bloom pass. Use this if the GPU has headroom at your resolution."
            },
            StyleTags = { o.Mode.ToString(), "Custom", o.UseBloom ? "Bloom" : "No bloom", o.UseFilmic ? "Filmic" : "Clean" },
            Requirements = { "ReShade 5.0+", "Official reshade-shaders (legacy branch)" },
            Techniques = techniques,
            EstimatedCost = cost,
            CostTier = o.Mode == ReShadeMode.Potato ? "Low" : o.Mode == ReShadeMode.Medium ? "Medium" : "Medium–High",
            Status = VerificationStatus.Estimated,
            StatusNote = "Generated locally. Shader files are validated on install; visual result and frame cost are not tested.",
            ScreenshotNote = "Synthetic colour preview generated by ZoneTune — not a game screenshot.",
            InstallMode = InstallMode.Bundled,
            IsBundled = true,
            MinReShadeVersion = "5.0",
            HdrNote = "Most ReShade effects assume SDR; disable in-game HDR if the image looks washed out.",
            Preview = new PresetProfile
            {
                Contrast = 1.02 + (o.UseFilmic ? 0.06 : 0),
                Saturation = 1.0 + o.ColourIntensity * 0.8,
                Warmth = 0.02,
                Sharpen = o.UseSharpen ? o.SharpenStrength : 0,
                Vignette = 0,
                Glow = o.UseBloom ? Math.Clamp(o.BloomAmount * 1.1, 0, 0.4) : 0
            },
            RawPreset = text
        };
    }

    private static string BuildText(CustomPresetOptions o, List<string> techniques, List<(string file, string values)> sections)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"; {o.Name}");
        sb.AppendLine($"; Generated by ZoneTune Preset Studio — {o.Mode} mode");
        sb.AppendLine("; Preset values: yours. Shader files: official crosire/reshade-shaders, downloaded at install time.");
        sb.AppendLine("; Estimates only; test in-game before judging performance.");
        sb.AppendLine();
        sb.AppendLine("Techniques=" + string.Join(",", techniques));
        sb.AppendLine();
        foreach (var (file, values) in sections)
        {
            sb.AppendLine($"[{file}]");
            sb.AppendLine(values);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string F(double value) => value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    public string SaveToDisk(PresetEntry preset)
    {
        var dir = Path.Combine(AppPaths.DataRoot, "Presets");
        Directory.CreateDirectory(dir);
        var name = AppPaths.SafeFileName(preset.Name) + ".ini";
        var path = Path.Combine(dir, name);
        IniDocument.WriteTextAtomic(path, preset.RawPreset.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
        preset.LocalPath = path;
        Log.Info("Custom preset saved: " + path);
        return path;
    }
}
