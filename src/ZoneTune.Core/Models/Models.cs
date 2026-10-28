using System.Collections.ObjectModel;

namespace ZoneTune.Core.Models;

public enum ProblemKind
{
    LoudPc,
    LowFps,
    Stuttering
}

public enum RiskLevel
{
    Low,
    Medium,
    High
}

public enum VerificationStatus
{
    /// <summary>Parsed and checked against the installed game/ReShade (not a visual test).</summary>
    StructurallyVerified,
    /// <summary>Based on author/publisher data or hardware estimates. Not tested on this machine.</summary>
    Estimated,
    /// <summary>Found in a public source but not checked against this installation.</summary>
    Unverified,
    /// <summary>Known missing requirement or conflict.</summary>
    Incompatible
}

public enum Severity
{
    Info,
    Notice,
    Warning,
    Critical
}

public sealed class DisplayInfo
{
    public string DeviceName { get; set; } = "";
    public string MonitorName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshHz { get; set; }
    public bool IsPrimary { get; set; }
    public bool HdrSupported { get; set; }
    public bool HdrEnabled { get; set; }
    public int BitsPerColor { get; set; }

    public string Resolution => Width > 0 ? $"{Width}x{Height}" : "Unknown";
    public string Summary => $"{Resolution} @ {RefreshHz} Hz" + (HdrEnabled ? " · HDR on" : HdrSupported ? " · HDR capable" : "");
}

public sealed class HardwareInfo
{
    public string CpuName { get; set; } = "Unknown CPU";
    public int PhysicalCores { get; set; }
    public int LogicalCores { get; set; }
    public int CpuMhz { get; set; }
    public string GpuName { get; set; } = "Unknown GPU";
    public string GpuVendor { get; set; } = "Unknown";
    public long VramMb { get; set; }
    public string GpuDriverVersion { get; set; } = "";
    public long RamMb { get; set; }
    public string WindowsName { get; set; } = "Windows";
    public int WindowsBuild { get; set; }
    public string SystemModel { get; set; } = "";
    public string GpuTier { get; set; } = "Unknown";
    public List<DisplayInfo> Displays { get; set; } = new();
    public string PrimaryResolution { get; set; } = "Unknown";
    public int PrimaryRefreshHz { get; set; }
    public bool AnyHdr { get; set; }
    public bool HdrEnabled { get; set; }
    public long GameDriveFreeBytes { get; set; }
    public long GameDriveTotalBytes { get; set; }
    public string GameDriveName { get; set; } = "";
    public DateTime DetectedAt { get; set; } = DateTime.Now;

    public string CpuSummary => $"{CpuName} · {PhysicalCores}C/{LogicalCores}T";
    public string GpuSummary => $"{GpuName} · {FormatGb(VramMb)} VRAM";
    public string RamSummary => $"{RamMb / 1024.0:0.#} GB";
    public string WindowsSummary => $"{WindowsName} (build {WindowsBuild})";
    public string StorageSummary => GameDriveTotalBytes > 0
        ? $"{GameDriveName}: {FormatGb(GameDriveFreeBytes / 1024 / 1024)} free of {FormatGb(GameDriveTotalBytes / 1024 / 1024)}"
        : "Unknown";

    public static string FormatGb(long megabytes) => megabytes >= 1024
        ? $"{megabytes / 1024.0:0.#} GB"
        : $"{megabytes} MB";
}

public enum GameSource
{
    Steam,
    Xbox,
    Epic,
    Manual,
    NotFound
}

public sealed class GameInstall
{
    public string Root { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string ShippingExePath { get; set; } = "";
    public string BinariesDir { get; set; } = "";
    public string PaksDir { get; set; } = "";
    public string ModsDir { get; set; } = "";
    public string DisabledModsDir { get; set; } = "";
    public string ConfigDir { get; set; } = "";
    public string LogsDir { get; set; } = "";
    public string AppDataDir { get; set; } = "";
    public GameSource Source { get; set; } = GameSource.NotFound;
    public bool IsValid { get; set; }
    public string ValidationMessage { get; set; } = "";
    public string SteamAppId { get; set; } = "1643320";

    public string BuildDisplay => IsValid ? $"{Root}" : ValidationMessage;
}

public sealed class IniValue
{
    public string Section { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class ConfigSnapshot
{
    public bool GameUserSettingsFound { get; set; }
    public bool EngineIniFound { get; set; }
    public bool ScalabilityIniFound { get; set; }
    public int ResolutionX { get; set; }
    public int ResolutionY { get; set; }
    public string FullscreenMode { get; set; } = "";
    public bool VSync { get; set; }
    public bool DynamicResolution { get; set; }
    public double FrameRateLimit { get; set; }
    public bool HdrEnabled { get; set; }
    public bool HdrConfigured { get; set; }
    public Dictionary<string, int> ScalabilityGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string UpscalerHint { get; set; } = "";
    public bool HasOptimaxConfig { get; set; }
    public bool HasCustomScalability { get; set; }
    public bool S2aeShaderWarmupSkipped { get; set; }
    public bool S2aePresent { get; set; }
    public Dictionary<string, string> SystemSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string RawGameUserSettingsPath { get; set; } = "";
    public string RawEngineIniPath { get; set; } = "";
    public string RawScalabilityIniPath { get; set; } = "";
}

public enum ModKind
{
    PakMod,
    LogicMod,
    Ue4ssLua,
    AsiPlugin,
    ConfigOverride,
    MergeOutput,
    Other
}

public sealed class ModEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public ModKind Kind { get; set; }
    public long SizeBytes { get; set; }
    public bool Enabled { get; set; } = true;
    public string Version { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool HasPak { get; set; }
    public bool HasUtoc { get; set; }
    public bool IsDirectory { get; set; }
    public List<string> Files { get; set; } = new();

    public string SizeText => SizeBytes >= 1024L * 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024 / 1024:0.##} GB"
        : SizeBytes >= 1024 * 1024
            ? $"{SizeBytes / 1024.0 / 1024:0.#} MB"
            : SizeBytes >= 1024 ? $"{SizeBytes / 1024.0:0.#} KB" : $"{SizeBytes} B";

    public string KindText => Kind switch
    {
        ModKind.PakMod => "Pak mod",
        ModKind.LogicMod => "Logic mod",
        ModKind.Ue4ssLua => "UE4SS Lua",
        ModKind.AsiPlugin => "ASI plugin",
        ModKind.ConfigOverride => "Config override",
        ModKind.MergeOutput => "Merged output",
        _ => "Other"
    };
}

public sealed class ModConflict
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public List<string> Mods { get; set; } = new();
    public Severity Severity { get; set; } = Severity.Warning;
    public string Source { get; set; } = "Heuristic";
}

public sealed class ShaderReference
{
    public string Technique { get; set; } = "";
    public string File { get; set; } = "";
    public bool Present { get; set; }
}

public sealed class ReShadeInfo
{
    public bool Installed { get; set; }
    public string Version { get; set; } = "";
    public string ProxyDll { get; set; } = "";
    public string ProxyPath { get; set; } = "";
    public string InstallDir { get; set; } = "";
    public bool AddonBuild { get; set; }
    public string PresetPath { get; set; } = "";
    public string PresetName { get; set; } = "";
    public List<string> PresetTechniques { get; set; } = new();
    public List<ShaderReference> ShaderReferences { get; set; } = new();
    public int ShaderCount { get; set; }
    public int PresetCount { get; set; }
    public int EffectErrors { get; set; }
    public int EffectWarnings { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> ProxyConflicts { get; set; } = new();
    public List<string> ShaderDuplicates { get; set; } = new();
    public List<string> OtherProxyDlls { get; set; } = new();
    public string ShadersDir { get; set; } = "";
    public bool HdrConflict { get; set; }
    public string LogPath { get; set; } = "";
}

public sealed class DiagnosticFinding
{
    public string Id { get; set; } = "";
    public Severity Severity { get; set; } = Severity.Info;
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public List<ProblemKind> RelatedProblems { get; set; } = new();
}

public enum ActionKind
{
    GameSetting,
    EngineCvar,
    ReShade,
    ModAction,
    Recommendation
}

public sealed class OptimizationAction
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Rationale { get; set; } = "";
    public string Evidence { get; set; } = "";
    public List<ProblemKind> Problems { get; set; } = new();
    public RiskLevel Risk { get; set; } = RiskLevel.Low;
    public VerificationStatus Confidence { get; set; } = VerificationStatus.Estimated;
    public ActionKind Kind { get; set; } = ActionKind.Recommendation;
    public string FilePath { get; set; } = "";
    public string Section { get; set; } = "";
    public string Key { get; set; } = "";
    public string CurrentValue { get; set; } = "";
    public string ProposedValue { get; set; } = "";
    public bool RequiresGameClosed { get; set; } = true;
    public bool Selected { get; set; } = true;
    public string EstimatedEffect { get; set; } = "Not measured";
    public string Note { get; set; } = "";
    public List<string> ConflictsWith { get; set; } = new();

    public string KindText => Kind switch
    {
        ActionKind.GameSetting => "Game setting",
        ActionKind.EngineCvar => "Engine setting",
        ActionKind.ReShade => "ReShade",
        ActionKind.ModAction => "Mod action",
        _ => "Recommendation"
    };

    public string ChangeText => string.IsNullOrEmpty(Key)
        ? Description
        : $"{Key}: {CurrentValue} → {ProposedValue}";

    public string RiskText => Risk switch
    {
        RiskLevel.Low => "Low risk",
        RiskLevel.Medium => "Medium risk",
        _ => "High risk"
    };
}

public sealed class OptimizationPlan
{
    public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<ProblemKind> Problems { get; set; } = new();
    public List<OptimizationAction> Actions { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public HardwareInfo? Hardware { get; set; }
    public string GameRoot { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool DryRun { get; set; }

    public IEnumerable<OptimizationAction> SelectedActions => Actions.Where(a => a.Selected);
    public IEnumerable<OptimizationAction> ConfigActions => SelectedActions.Where(a =>
        a.Kind is ActionKind.GameSetting or ActionKind.EngineCvar);
    public IEnumerable<OptimizationAction> Recommendations => SelectedActions.Where(a => a.Kind == ActionKind.Recommendation);
}

public sealed class ApplyResult
{
    public bool Success { get; set; }
    public string ChangeSetId { get; set; } = "";
    public List<string> Messages { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> FilesChanged { get; set; } = new();
    public string BackupPath { get; set; } = "";
    public string? PackagePath { get; set; }
}

public sealed class PresetProfile
{
    public double Contrast { get; set; } = 1.0;
    public double Saturation { get; set; } = 1.0;
    public double Warmth { get; set; }
    public double Sharpen { get; set; }
    public double Vignette { get; set; }
    public double Exposure { get; set; }
    public double Glow { get; set; }
}

public sealed class PresetEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string License { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> StyleTags { get; set; } = new();
    public List<string> Requirements { get; set; } = new();
    public List<string> Techniques { get; set; } = new();
    public string EstimatedCost { get; set; } = "Not measured";
    public string CostTier { get; set; } = "Medium";
    public VerificationStatus Status { get; set; } = VerificationStatus.Estimated;
    public string StatusNote { get; set; } = "";
    public string? ScreenshotUrl { get; set; }
    public string ScreenshotNote { get; set; } = "No verified screenshot available.";
    public string? DownloadUrl { get; set; }
    public InstallMode InstallMode { get; set; } = InstallMode.ExternalOnly;
    public string MinReShadeVersion { get; set; } = "5.0";
    public string HdrNote { get; set; } = "";
    public PresetProfile Preview { get; set; } = new();
    public string? LocalPath { get; set; }
    public string RawPreset { get; set; } = "";
    public bool IsBundled { get; set; }
    public List<string> Warnings { get; set; } = new();

    public string StatusText => Status switch
    {
        VerificationStatus.StructurallyVerified => "Structurally verified",
        VerificationStatus.Estimated => "Estimated",
        VerificationStatus.Unverified => "Unverified",
        _ => "Incompatible"
    };

    public string InstallModeText => InstallMode switch
    {
        InstallMode.Bundled => "Bundled · one-click",
        InstallMode.DirectDownload => "Direct download",
        InstallMode.LocalFile => "Local file",
        _ => "Author page (manual)"
    };
}

public enum InstallMode
{
    Bundled,
    DirectDownload,
    LocalFile,
    ExternalOnly
}

public sealed class PresetValidation
{
    public bool IsValid { get; set; }
    public List<ShaderReference> Shaders { get; set; } = new();
    public List<string> Missing { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Techniques { get; set; } = new();
}

public sealed class ReShadeInstallResult
{
    public bool Success { get; set; }
    public string ChangeSetId { get; set; } = "";
    public string Version { get; set; } = "";
    public string PresetName { get; set; } = "";
    public bool DownloadedReShade { get; set; }
    public List<string> Messages { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public string Verification { get; set; } = "";
}

public sealed class MeasurementSummary
{
    public string SourceFile { get; set; } = "";
    public int Frames { get; set; }
    public double DurationSeconds { get; set; }
    public double AverageFps { get; set; }
    public double OnePercentLowFps { get; set; }
    public double PointOnePercentLowFps { get; set; }
    public double MedianFrameMs { get; set; }
    public double P95FrameMs { get; set; }
    public double P99FrameMs { get; set; }
    public double MaxFrameMs { get; set; }
    public double StutterScore { get; set; }
    public List<double> FrameTimesMs { get; set; } = new();
    public DateTime ImportedAt { get; set; } = DateTime.Now;
}

public sealed class DiagnosticReport
{
    public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public HardwareInfo? Hardware { get; set; }
    public GameInstall? Game { get; set; }
    public ConfigSnapshot? Config { get; set; }
    public ReShadeInfo? ReShade { get; set; }
    public List<ModEntry> Mods { get; set; } = new();
    public List<ModConflict> ModConflicts { get; set; } = new();
    public List<DiagnosticFinding> Findings { get; set; } = new();
    public MeasurementSummary? Measurement { get; set; }
    public string Summary { get; set; } = "";
    public List<string> GeneratedFiles { get; set; } = new();
}

public sealed class BackupItem
{
    public string Path { get; set; } = "";
    public bool Existed { get; set; }
    public string? BackupRelativePath { get; set; }
    public string? Sha256Before { get; set; }
    public string? Sha256After { get; set; }
    public string Note { get; set; } = "";
}

public sealed class ChangeSet
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string GameRoot { get; set; } = "";
    public List<BackupItem> Items { get; set; } = new();
    public List<string> Messages { get; set; } = new();
    public bool Restored { get; set; }
    public DateTime? RestoredAt { get; set; }
    public string Directory { get; set; } = "";

    public long SizeBytes { get; set; }
    public string SizeText => SizeBytes >= 1024L * 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024 / 1024:0.##} GB"
        : SizeBytes >= 1024 * 1024 ? $"{SizeBytes / 1024.0 / 1024:0.#} MB"
        : SizeBytes >= 1024 ? $"{SizeBytes / 1024.0:0.#} KB" : $"{SizeBytes} B";
}

public sealed class AppSettings
{
    public string? GamePathOverride { get; set; }
    public string BackupRoot { get; set; } = "";
    public int KeepBackups { get; set; } = 10;
    public bool AllowNetwork { get; set; } = true;
    public bool VerifyDownloads { get; set; } = true;
    public bool DryRunByDefault { get; set; } = false;
    public bool CreateZipAfterOptimize { get; set; } = true;
    public bool AutoOpenReport { get; set; } = false;
    public string LastSelectedProblems { get; set; } = "";
    public string? LastReShadePresetId { get; set; }
    public bool RequireGameClosed { get; set; } = true;
    public DateTime? LastScanUtc { get; set; }
}

public sealed class ScanResult
{
    public HardwareInfo Hardware { get; set; } = new();
    public GameInstall Game { get; set; } = new();
    public ConfigSnapshot Config { get; set; } = new();
    public List<ModEntry> Mods { get; set; } = new();
    public List<ModConflict> ModConflicts { get; set; } = new();
    public ReShadeInfo ReShade { get; set; } = new();
    public List<DiagnosticFinding> Findings { get; set; } = new();
    public DateTime ScannedAt { get; set; } = DateTime.Now;
    public List<string> ScanErrors { get; set; } = new();
    public bool GameRunning { get; set; }
}

public sealed class OperationProgress
{
    public string Stage { get; set; } = "";
    public string Detail { get; set; } = "";
    public int Percent { get; set; }
    public OperationProgress() { }
    public OperationProgress(string stage, string detail, int percent)
    {
        Stage = stage;
        Detail = detail;
        Percent = percent;
    }
}
