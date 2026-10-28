using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Preset discovery. Built-in presets are authored by ZoneTune and use only official
/// ReShade shaders. Third-party presets link to the author's page and are never
/// repackaged or auto-downloaded unless the repository carries an explicit permissive
/// license. Compatibility wording is deliberately conservative: verified here means
/// "parsed and shader files present", never "visually tested".
/// </summary>
public sealed class PresetCatalog
{
    private readonly string _cacheFile = Path.Combine(AppPaths.CacheDir, "github-presets.json");
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public const string OfficialShaderVersion = "ReShade 6.8 / reshade-shaders legacy branch";
    public const string ShaderArchiveUrl = "https://codeload.github.com/crosire/reshade-shaders/zip/refs/heads/legacy";
    public const string ShaderSupportArchiveUrl = "https://codeload.github.com/crosire/reshade-shaders/zip/refs/heads/slim";
    public const string ReShadeSetupUrl = "https://reshade.me/downloads/ReShade_Setup_6.8.0.exe";
    public const string ReShadeAddonSetupUrl = "https://reshade.me/downloads/ReShade_Setup_6.8.0_Addon.exe";
    public const string ReShadeVersion = "6.8.0";

    public List<PresetEntry> GetBuiltInPresets(HardwareInfo? hardware = null)
    {
        var list = new List<PresetEntry>
        {
            new()
            {
                Id = "builtin-zoneclarity-performance",
                Name = "ZoneClarity — Performance",
                Author = "ZoneTune",
                SourceName = "Bundled (authored for ZoneTune)",
                SourceUrl = "",
                License = "CC0-1.0 (preset values only; shaders keep their own licenses)",
                Description = "A restrained clarity pass: gentle black/white-point correction, a small saturation lift and " +
                              "low-strength adaptive sharpening. Designed as the lowest-cost option for 1440p/4K play and for " +
                              "anyone prioritizing frame rate over a heavily graded look.",
                StyleTags = { "Natural", "Lightweight", "Sharpen", "Neutral" },
                Requirements = { "ReShade 5.0+", "Official reshade-shaders (legacy branch): LevelsPlus, Colourfulness, AdaptiveSharpen" },
                Techniques = { "LevelsPlus@LevelsPlus.fx", "Colourfulness@Colourfulness.fx", "AdaptiveSharpen@AdaptiveSharpen.fx" },
                EstimatedCost = "< 1% at 1440p (estimate, depends on GPU and preset load)",
                CostTier = "Low",
                Status = VerificationStatus.Estimated,
                StatusNote = "Preset syntax and shader availability are checked on your install. Not visually tested by ZoneTune.",
                ScreenshotNote = "Illustrative synthetic colour preview only — not a game screenshot.",
                IsBundled = true,
                InstallMode = InstallMode.Bundled,
                MinReShadeVersion = "5.0",
                HdrNote = "ReShade post-processing is SDR-oriented. If HDR is enabled in-game, disable it or expect a washed-out image.",
                Preview = new PresetProfile { Contrast = 1.04, Saturation = 1.06, Warmth = 0.02, Sharpen = 0.25 },
                RawPreset = BuildBuiltInPreset("ZoneClarity — Performance",
                    "LevelsPlus@LevelsPlus.fx,Colourfulness@Colourfulness.fx,AdaptiveSharpen@AdaptiveSharpen.fx",
                    new[]
                    {
                        ("LevelsPlus.fx", "EnableLevels=1\nInputBlackPoint=0.004,0.004,0.004\nInputWhitePoint=0.996,0.996,0.996\nInputGamma=1.000,0.995,0.990\nSmoothCurve=1\nHighlightClipping=0\nAvoidClipping=1"),
                        ("Colourfulness.fx", "colourfulness=0.10\nlim_luma=0.7\nenable_dither=0"),
                        ("AdaptiveSharpen.fx", "curve_height=0.35\ncurveslope=0.60\nL_overshoot=0.010\nD_overshoot=0.010\nscale_lim=0.10")
                    })
            },
            new()
            {
                Id = "builtin-zoneclarity-atmosphere",
                Name = "ZoneClarity — Atmosphere",
                Author = "ZoneTune",
                SourceName = "Bundled (authored for ZoneTune)",
                SourceUrl = "",
                License = "CC0-1.0 (preset values only; shaders keep their own licenses)",
                Description = "Adds a subtle filmic curve and a low bloom pass to the performance preset. Intended to deepen " +
                              "overcast Zone lighting without crushing shadows. Medium cost; test in the most demanding areas.",
                StyleTags = { "Filmic", "Moody", "Balanced", "Light bloom" },
                Requirements = { "ReShade 5.0+", "Official reshade-shaders (legacy branch): LevelsPlus, Colourfulness, AdaptiveSharpen, FilmicPass, Bloom" },
                Techniques = { "LevelsPlus@LevelsPlus.fx", "Colourfulness@Colourfulness.fx", "FilmicPass@FilmicPass.fx", "BloomAndLensFlares@Bloom.fx", "AdaptiveSharpen@AdaptiveSharpen.fx" },
                EstimatedCost = "2–4% at 1440p (estimate; Bloom is the main cost)",
                CostTier = "Medium",
                Status = VerificationStatus.Estimated,
                StatusNote = "Preset syntax and shader availability are checked on your install. Not visually tested by ZoneTune.",
                ScreenshotNote = "Illustrative synthetic colour preview only — not a game screenshot.",
                IsBundled = true,
                InstallMode = InstallMode.Bundled,
                MinReShadeVersion = "5.0",
                HdrNote = "Disable HDR in-game before using this preset; bloom and tonemapping assume SDR.",
                Preview = new PresetProfile { Contrast = 1.10, Saturation = 0.98, Warmth = -0.06, Sharpen = 0.30, Vignette = 0.10, Glow = 0.10 },
                RawPreset = BuildBuiltInPreset("ZoneClarity — Atmosphere",
                    "LevelsPlus@LevelsPlus.fx,Colourfulness@Colourfulness.fx,FilmicPass@FilmicPass.fx,BloomAndLensFlares@Bloom.fx,AdaptiveSharpen@AdaptiveSharpen.fx",
                    new[]
                    {
                        ("LevelsPlus.fx", "EnableLevels=1\nInputBlackPoint=0.005,0.005,0.006\nInputWhitePoint=0.992,0.992,0.990\nInputGamma=1.000,1.000,0.990\nSmoothCurve=1\nHighlightClipping=0\nAvoidClipping=1"),
                        ("Colourfulness.fx", "colourfulness=0.14\nlim_luma=0.65\nenable_dither=0"),
                        ("FilmicPass.fx", "Strength=0.55\nFade=0.10\nContrast=0.95\nLinearization=0.5\nBleach=0.0\nSaturation=0.95\nRedCurve=0.95\nGreenCurve=1.0\nBlueCurve=1.02\nBaseCurve=1.0\nBaseGamma=1.0\nEffectGamma=1.0\nEffectGammaR=1.0\nEffectGammaG=1.0\nEffectGammaB=1.0"),
                        ("Bloom.fx", "fBloomThreshold=0.85\nfBloomAmount=0.18\nfBloomSaturation=0.7\nfBloomTint=0.85,0.90,1.0\nbLensdirtEnable=0\nbAnamFlareEnable=0\nbLenzEnable=0\nbChapFlareEnable=0\nbGodrayEnable=0"),
                        ("AdaptiveSharpen.fx", "curve_height=0.32\ncurveslope=0.60\nL_overshoot=0.010\nD_overshoot=0.010\nscale_lim=0.10")
                    })
            },
            new()
            {
                Id = "builtin-zoneclarity-cinematic",
                Name = "ZoneClarity — Cinematic",
                Author = "ZoneTune",
                SourceName = "Bundled (authored for ZoneTune)",
                SourceUrl = "",
                License = "CC0-1.0 (preset values only; shaders keep their own licenses)",
                Description = "A warmer, higher-contrast grade with controlled bloom and a light vignette. This is the most " +
                              "expensive bundled option and is aimed at screenshot/video sessions rather than competitive play.",
                StyleTags = { "Cinematic", "Warm", "Contrast", "Vignette" },
                Requirements = { "ReShade 5.0+", "Official reshade-shaders (legacy branch): LevelsPlus, Colourfulness, FilmicPass, Bloom, AdaptiveSharpen" },
                Techniques = { "LevelsPlus@LevelsPlus.fx", "Colourfulness@Colourfulness.fx", "FilmicPass@FilmicPass.fx", "BloomAndLensFlares@Bloom.fx", "AdaptiveSharpen@AdaptiveSharpen.fx" },
                EstimatedCost = "3–5% at 1440p (estimate; higher at 4K)",
                CostTier = "Medium",
                Status = VerificationStatus.Estimated,
                StatusNote = "Preset syntax and shader availability are checked on your install. Not visually tested by ZoneTune.",
                ScreenshotNote = "Illustrative synthetic colour preview only — not a game screenshot.",
                IsBundled = true,
                InstallMode = InstallMode.Bundled,
                MinReShadeVersion = "5.0",
                HdrNote = "Not recommended with in-game HDR enabled.",
                Preview = new PresetProfile { Contrast = 1.18, Saturation = 0.92, Warmth = 0.14, Sharpen = 0.28, Vignette = 0.22, Glow = 0.22 },
                RawPreset = BuildBuiltInPreset("ZoneClarity — Cinematic",
                    "LevelsPlus@LevelsPlus.fx,Colourfulness@Colourfulness.fx,FilmicPass@FilmicPass.fx,BloomAndLensFlares@Bloom.fx,AdaptiveSharpen@AdaptiveSharpen.fx",
                    new[]
                    {
                        ("LevelsPlus.fx", "EnableLevels=1\nInputBlackPoint=0.008,0.007,0.006\nInputWhitePoint=0.995,0.990,0.982\nInputGamma=1.000,1.000,0.985\nSmoothCurve=1\nHighlightClipping=0\nAvoidClipping=1"),
                        ("Colourfulness.fx", "colourfulness=0.20\nlim_luma=0.6\nenable_dither=0"),
                        ("FilmicPass.fx", "Strength=0.70\nFade=0.15\nContrast=1.05\nLinearization=0.5\nBleach=0.05\nSaturation=0.88\nRedCurve=1.03\nGreenCurve=1.0\nBlueCurve=0.96\nBaseCurve=1.05\nBaseGamma=1.0\nEffectGamma=1.0\nEffectGammaR=1.0\nEffectGammaG=1.0\nEffectGammaB=1.0"),
                        ("Bloom.fx", "fBloomThreshold=0.80\nfBloomAmount=0.26\nfBloomSaturation=0.8\nfBloomTint=1.0,0.92,0.80\nbLensdirtEnable=0\nbAnamFlareEnable=0\nbLenzEnable=0\nbChapFlareEnable=0\nbGodrayEnable=0"),
                        ("AdaptiveSharpen.fx", "curve_height=0.30\ncurveslope=0.60\nL_overshoot=0.010\nD_overshoot=0.010\nscale_lim=0.10")
                    })
            }
        };

        foreach (var preset in list)
        {
            if (hardware != null && hardware.HdrEnabled)
                preset.Warnings.Add("HDR is enabled on the primary display. ReShade presets generally require SDR output.");
            if (hardware != null && hardware.PrimaryResolution is "3840x2160" or "5120x1440" or "3440x1440")
                preset.EstimatedCost += "; higher pixel count than 1440p tested baseline may increase cost";
        }
        return list;
    }

    private static string BuildBuiltInPreset(string name, string techniques, IEnumerable<(string file, string values)> sections)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"; {name}");
        sb.AppendLine("; Authored by ZoneTune. Preset values released under CC0-1.0.");
        sb.AppendLine("; Shader files are NOT bundled; they are downloaded from the official crosire/reshade-shaders repository.");
        sb.AppendLine("; Compatibility: syntax-validated, not visually tested on every GPU/driver combination.");
        sb.AppendLine();
        sb.AppendLine($"Techniques={techniques}");
        sb.AppendLine();
        foreach (var (file, values) in sections)
        {
            sb.AppendLine($"[{file}]");
            sb.AppendLine(values);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public List<PresetEntry> GetThirdPartyCatalog(HardwareInfo? hardware = null)
    {
        var list = new List<PresetEntry>
        {
            ThirdParty("nexus-nog", "NOG Photorealistic Reshade", "Nexus Mods community", "https://www.nexusmods.com/stalker2heartofchornobyl/mods/226",
                "A large, popular photorealistic grade that moves away from the game's warm default look. The author reports " +
                "roughly a 3–4% FPS cost for the full effect stack.", "Photorealistic, Contrast, Popular",
                "3–4% FPS (author-reported, not independently measured)", "Medium",
                new PresetProfile { Contrast = 1.14, Saturation = 0.90, Warmth = -0.10, Sharpen = 0.35, Glow = 0.05 }),
            ThirdParty("nexus-lifelike", "Lifelike Reshade (HDR compatible)", "Nexus Mods community", "https://www.nexusmods.com/stalker2heartofchornobyl/mods/785",
                "A grounded colour and lighting grade tuned for different weather and times of day. The author lists HDR compatibility " +
                "as a feature; verify in-game because HDR behaviour depends on the ReShade build and display pipeline.", "HDR, Natural, Color grading",
                "Not stated by author", "Medium",
                new PresetProfile { Contrast = 1.05, Saturation = 1.02, Warmth = -0.04, Sharpen = 0.15 }),
            ThirdParty("nexus-true-beauty", "TRUE BEAUTY OF ZONE", "Nexus Mods community", "https://www.nexusmods.com/stalker2heartofchornobyl/mods/2372",
                "A deliberately lightweight grade aimed at a cold, authentic Zone palette with subtle depth. The author describes it " +
                "as avoiding over-saturation and crushed blacks.", "Lightweight, Cold, Natural",
                "Not stated by author", "Low",
                new PresetProfile { Contrast = 1.06, Saturation = 0.95, Warmth = -0.20, Sharpen = 0.18 }),
            ThirdParty("nexus-visuals-improved", "Stalker 2 Visuals Improved", "Nexus Mods community", "https://www.nexusmods.com/stalker2heartofchornobyl/mods/1765",
                "A simple preset intended to make the game look less like a stock UE5 title, with a rusted, cohesive palette. " +
                "Good candidate for players who want a stronger look with a small effect stack.", "Rusted, Cohesive, Simple",
                "Not stated by author", "Low–Medium",
                new PresetProfile { Contrast = 1.12, Saturation = 1.04, Warmth = 0.16, Sharpen = 0.22, Vignette = 0.15, Glow = 0.08 }),
            ThirdParty("nexus-cinematic", "STALKER 2 Cinematic Reshade", "Nexus Mods community", "https://www.nexusmods.com/stalker2heartofchornobyl/mods/2177",
                "Sharpening, contrast and black-level rebalance with a cinematic irradiance effect. Heavier than a pure colour LUT; " +
                "budget for a measurable cost on mid-range GPUs.", "Cinematic, Sharp, Contrast",
                "Not stated by author", "Medium–High",
                new PresetProfile { Contrast = 1.18, Saturation = 0.92, Warmth = -0.05, Sharpen = 0.40, Vignette = 0.25, Glow = 0.20 }),
            ThirdParty("nexus-photoreal-2790", "S.T.A.L.K.E.R. 2 Photorealistic Reshade", "Nexus Mods community", "https://www.nexusmods.com/stalker2heartofchornobyl/mods/2790",
                "A photorealistic preset whose author notes that HDR must be disabled and that unused effects showing red errors " +
                "does not necessarily affect the active look. Treat that claim as author guidance, not a verification.", "Photorealistic, HDR off",
                "Not stated by author", "Medium",
                new PresetProfile { Contrast = 1.10, Saturation = 0.96, Warmth = -0.08, Sharpen = 0.25, Glow = 0.06 }),
            ThirdParty("nexus-ultra-realistic", "Ultra Realistic Reshade", "Nexus Mods community", "https://www.nexusmods.com/stalker2heartofchornobyl/mods/25",
                "One of the earliest STALKER 2 presets, focused on correcting the game's dark environment and yellow-green volumetric " +
                "tint. Simpler stack, but authored before later patches.", "Realistic, Corrective, Legacy",
                "Not stated by author", "Medium",
                new PresetProfile { Contrast = 1.08, Saturation = 0.98, Warmth = -0.18, Sharpen = 0.20 }),
            ThirdParty("nexus-clearsky", "Clear Sky Photorealistic Preset", "Nexus Mods community", "https://www.nexusmods.com/stalker2heartofchornobyl/mods/1717",
                "A photorealistic preset with installation steps that place the .ini next to Stalker2-Win64-Shipping.exe and use " +
                "the Home key to open ReShade. Check the author's page for the current shader list.", "Photorealistic, Cool",
                "Not stated by author", "Medium",
                new PresetProfile { Contrast = 1.10, Saturation = 0.94, Warmth = -0.12, Sharpen = 0.22 }),
            ThirdParty("nexus-advanced-visual", "Advanced Visual Presets for PRZ", "Nexus Mods community", "https://www.nexusmods.com/stalker2heartofchornobyl/mods/1774",
                "Three presets for different weather states, built around the RE:LOOK film-emulation shader. Requires that shader and " +
                "its textures; installation is manual.", "Film emulation, Weather-aware, Heavy",
                "Not stated by author", "High",
                new PresetProfile { Contrast = 1.16, Saturation = 0.90, Warmth = 0.05, Sharpen = 0.15, Vignette = 0.20, Glow = 0.18 }),
            ThirdParty("nexus-technoet1c", "Technoet1c's ReShade Preset", "Nexus Mods community", "https://www.nexusmods.com/stalker2heartofchornobyl/mods/1295",
                "A community preset with its own configuration files. The author notes that ReShade presets are inherently safe and " +
                "encourages independent verification.", "Community, Custom config",
                "Not stated by author", "Medium",
                new PresetProfile { Contrast = 1.08, Saturation = 1.0, Warmth = 0.0, Sharpen = 0.20 })
        };

        foreach (var p in list)
        {
            p.Status = VerificationStatus.Unverified;
            p.StatusNote = "Not tested by ZoneTune. The author's page is the source of truth; manual download keeps their distribution terms intact.";
            p.InstallMode = InstallMode.ExternalOnly;
            p.HdrNote = p.Name.Contains("HDR", StringComparison.OrdinalIgnoreCase)
                ? "Author lists HDR compatibility. ZoneTune cannot verify HDR output without a visual test."
                : "Most ReShade presets require HDR to be disabled in-game. Check the author's page.";
            p.ScreenshotNote = "No verified screenshot bundled. Open the source page to view the author's own screenshots.";
            if (hardware?.HdrEnabled == true)
                p.Warnings.Add("HDR is currently enabled; this preset may look incorrect until HDR is disabled.");
        }
        return list;
    }

    private static PresetEntry ThirdParty(string id, string name, string author, string url, string description,
        string tags, string cost, string tier, PresetProfile profile) => new()
    {
        Id = id,
        Name = name,
        Author = author,
        SourceName = "Nexus Mods",
        SourceUrl = url,
        License = "Author-controlled distribution (see source page)",
        Description = description,
        StyleTags = tags.Split(',').Select(t => t.Trim()).ToList(),
        EstimatedCost = cost,
        CostTier = tier,
        Preview = profile,
        Requirements = { "ReShade 5.x or newer", "Author's shader/texture package", "HDR disabled unless stated" },
        MinReShadeVersion = "5.0"
    };

    public PresetEntry FromLocalFile(string path)
    {
        var text = File.ReadAllText(path);
        var techniques = ParseTechniques(text);
        var name = Path.GetFileNameWithoutExtension(path);
        return new PresetEntry
        {
            Id = "local-" + Math.Abs(path.GetHashCode()).ToString("x8"),
            Name = name,
            Author = "Local file",
            SourceName = "Imported from disk",
            SourceUrl = "",
            License = "Unknown — user-supplied",
            Description = "A preset imported from this PC. ZoneTune will validate its techniques against the installed shader folders " +
                          "before installation. It is never uploaded anywhere.",
            Techniques = techniques.Select(t => t.raw).ToList(),
            Requirements = { "ReShade 5.x or newer", "Shaders referenced by the preset must be installed" },
            EstimatedCost = "Not measured",
            CostTier = "Unknown",
            Status = VerificationStatus.Unverified,
            StatusNote = "Local import. Run validation to check which shader files are missing.",
            ScreenshotNote = "No screenshot available for a local preset.",
            InstallMode = InstallMode.LocalFile,
            LocalPath = path,
            RawPreset = text,
            MinReShadeVersion = "5.0",
            Preview = new PresetProfile { Contrast = 1.05, Saturation = 1.0 }
        };
    }

    public PresetValidation Validate(PresetEntry preset, IEnumerable<string> shaderRoots, string? installedVersion)
    {
        var result = new PresetValidation();
        var roots = shaderRoots.Where(Directory.Exists).ToList();
        var text = !string.IsNullOrWhiteSpace(preset.RawPreset)
            ? preset.RawPreset
            : preset.LocalPath != null && File.Exists(preset.LocalPath) ? File.ReadAllText(preset.LocalPath) : "";

        if (string.IsNullOrWhiteSpace(text))
        {
            result.Warnings.Add("Preset content is not available locally (author page download is required).");
            return result;
        }

        var techniques = ParseTechniques(text);
        result.Techniques = techniques.Select(t => t.technique).ToList();
        if (techniques.Count == 0)
        {
            result.Warnings.Add("No 'Techniques=' line found. This may not be a ReShade preset.");
            return result;
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.fx", SearchOption.AllDirectories))
                    existing.Add(f);
            }
            catch { }
        }

        foreach (var (technique, file, raw) in techniques)
        {
            var match = existing.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase));
            var reference = new ShaderReference { Technique = technique, File = file, Present = match != null };
            result.Shaders.Add(reference);
            if (match == null) result.Missing.Add(file);
        }

        if (!string.IsNullOrWhiteSpace(installedVersion) && !ReShadeService.VersionAtLeast(installedVersion, preset.MinReShadeVersion))
            result.Warnings.Add($"Installed ReShade {installedVersion} is older than the preset's stated minimum {preset.MinReShadeVersion}.");
        if (installedVersion == null)
            result.Warnings.Add("ReShade is not installed yet; shader-file checks use the current shader folders only.");
        if (result.Missing.Count > 0)
            result.Warnings.Add($"{result.Missing.Count} referenced shader file(s) are missing. Install the author's shader package before enabling the preset.");
        if (text.Contains("PreprocessorDefinitions", StringComparison.OrdinalIgnoreCase))
            result.Warnings.Add("Preset defines preprocessor values; verify they match your ReShade shader version.");

        result.IsValid = result.Missing.Count == 0;
        result.Missing = result.Missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Log.Info($"Preset validation '{preset.Name}': {result.Shaders.Count} techniques, {result.Missing.Count} missing");
        return result;
    }

    public static List<(string technique, string file, string raw)> ParseTechniques(string presetText)
    {
        var result = new List<(string, string, string)>();
        var match = Regex.Match(presetText, @"^\s*Techniques\s*=\s*(.+)$", RegexOptions.Multiline);
        if (!match.Success) return result;
        foreach (var token in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim();
            if (t.Length == 0 || t.StartsWith("//")) continue;
            var at = t.IndexOf('@');
            var technique = at > 0 ? t[..at].Trim() : "";
            var file = at > 0 ? t[(at + 1)..].Trim() : t;
            if (file.Length == 0) continue;
            result.Add((technique, file, t));
        }
        return result;
    }

    public async Task<List<PresetEntry>> SearchGitHubAsync(HardwareInfo? hardware = null, CancellationToken ct = default)
    {
        var cached = LoadCache();
        if (cached != null && cached.CreatedAt > DateTime.UtcNow.AddHours(-24))
            return cached.Presets;

        var results = new List<PresetEntry>();
        try
        {
            foreach (var query in new[] { "stalker 2 reshade", "stalker reshade preset" })
            {
                var url = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(query)}&per_page=10&sort=updated";
                var json = await HttpService.GetStringAsync(url, 1024 * 1024, ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("items", out var items)) continue;
                foreach (var item in items.EnumerateArray())
                {
                    var fullName = item.GetProperty("full_name").GetString() ?? "";
                    var repoUrl = item.GetProperty("html_url").GetString() ?? "";
                    var branch = item.TryGetProperty("default_branch", out var b) ? b.GetString() ?? "main" : "main";
                    var license = "No license";
                    if (item.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.Object &&
                        lic.TryGetProperty("spdx_id", out var spdx))
                        license = spdx.GetString() ?? "No license";
                    if (item.TryGetProperty("description", out var descEl)) { }
                    var description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    var permissive = new[] { "MIT", "BSD-2-Clause", "BSD-3-Clause", "Apache-2.0", "CC0-1.0", "Unlicense", "0BSD" }
                        .Contains(license, StringComparer.OrdinalIgnoreCase);

                    // Do not download or offer automatic install for repos without a permissive license.
                    if (!permissive) continue;

                    var treeUrl = $"https://api.github.com/repos/{fullName}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1";
                    var treeJson = await HttpService.GetStringAsync(treeUrl, 4 * 1024 * 1024, ct);
                    using var treeDoc = JsonDocument.Parse(treeJson);
                    if (!treeDoc.RootElement.TryGetProperty("tree", out var tree)) continue;
                    var count = 0;
                    foreach (var node in tree.EnumerateArray())
                    {
                        if (count >= 5) break;
                        var type = node.TryGetProperty("type", out var t) ? t.GetString() : "";
                        var path = node.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                        if (type != "blob" || !path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;
                        if (path.Contains("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                        var rawUrl = $"https://raw.githubusercontent.com/{fullName}/{branch}/{path}";
                        string text;
                        try { text = await HttpService.GetStringAsync(rawUrl, 512 * 1024, ct); }
                        catch { continue; }
                        var techniques = ParseTechniques(text);
                        if (techniques.Count == 0) continue;
                        var entry = new PresetEntry
                        {
                            Id = "gh-" + Math.Abs($"{fullName}/{path}".GetHashCode()).ToString("x8"),
                            Name = Path.GetFileNameWithoutExtension(path),
                            Author = fullName.Split('/')[0],
                            SourceName = $"GitHub · {fullName}",
                            SourceUrl = repoUrl,
                            License = license,
                            Description = string.IsNullOrWhiteSpace(description) ? "Public GitHub preset repository." : description,
                            Techniques = techniques.Select(x => x.raw).ToList(),
                            Requirements = { "ReShade 5.x+", "Shaders referenced by the preset" },
                            EstimatedCost = "Not measured",
                            CostTier = "Unknown",
                            Status = VerificationStatus.Unverified,
                            StatusNote = "Public GitHub source with a permissive license. Not tested by ZoneTune.",
                            ScreenshotNote = "No verified screenshot bundled.",
                            DownloadUrl = rawUrl,
                            InstallMode = InstallMode.DirectDownload,
                            MinReShadeVersion = "5.0",
                            RawPreset = text,
                            Preview = new PresetProfile { Contrast = 1.06, Saturation = 1.02 }
                        };
                        results.Add(entry);
                        count++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"GitHub preset search failed: {ex.Message}");
        }

        SaveCache(results);
        return results;
    }

    private sealed class PresetCache
    {
        public DateTime CreatedAt { get; set; }
        public List<PresetEntry> Presets { get; set; } = new();
    }

    private PresetCache? LoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFile)) return null;
            return JsonSerializer.Deserialize<PresetCache>(File.ReadAllText(_cacheFile));
        }
        catch { return null; }
    }

    private void SaveCache(List<PresetEntry> presets)
    {
        try
        {
            var cache = new PresetCache { CreatedAt = DateTime.UtcNow, Presets = presets };
            IniDocument.WriteTextAtomic(_cacheFile, JsonSerializer.Serialize(cache, _json));
        }
        catch (Exception ex) { Log.Warn($"Could not write preset cache: {ex.Message}"); }
    }

    /// <summary>Shader files (plus includes) needed by a preset, from the official legacy shader archive.</summary>
    public HashSet<string> RequiredShaderFiles(PresetEntry preset)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, file, _) in ParseTechniques(preset.RawPreset))
            files.Add(file);
        return files;
    }
}
