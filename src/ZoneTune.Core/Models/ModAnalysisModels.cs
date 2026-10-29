using System.Text.Json.Serialization;

namespace ZoneTune.Core.Models;

/// <summary>
/// Static, evidence-based mod findings. A HIGH result means "strong static indicators",
/// never "proven FPS loss". Runtime impact is not measurable from files alone.
/// </summary>
public enum ModRiskLevel
{
    Low,
    Medium,
    High,
    Unknown
}

public enum ModConfidence
{
    /// <summary>The technical finding exists in the files. It does not prove runtime cost.</summary>
    Confirmed,
    /// <summary>Several related static indicators point the same way.</summary>
    Likely,
    /// <summary>A plausible explanation, not measured.</summary>
    Hypothesis,
    /// <summary>The files could not be inspected or contain no usable evidence.</summary>
    Insufficient
}

public enum ModEvidenceCategory
{
    Container,
    Script,
    Asset,
    Size,
    Overlap,
    Native,
    Config,
    Metadata
}

public sealed class ModEvidence
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Source { get; set; } = "";
    public string Metric { get; set; } = "";
    public ModEvidenceCategory Category { get; set; } = ModEvidenceCategory.Metadata;
    public ModConfidence Confidence { get; set; } = ModConfidence.Confirmed;

    [JsonIgnore]
    public string CategoryText => Category switch
    {
        ModEvidenceCategory.Container => "Container",
        ModEvidenceCategory.Script => "Script",
        ModEvidenceCategory.Asset => "Asset",
        ModEvidenceCategory.Size => "Size",
        ModEvidenceCategory.Overlap => "Overlap",
        ModEvidenceCategory.Native => "Native",
        ModEvidenceCategory.Config => "Config",
        _ => "Metadata"
    };

    [JsonIgnore]
    public string ConfidenceText => Confidence switch
    {
        ModConfidence.Confirmed => "Confirmed technical finding",
        ModConfidence.Likely => "Likely",
        ModConfidence.Hypothesis => "Hypothesis",
        _ => "Insufficient evidence"
    };
}

public sealed class ModPerformanceAnalysis
{
    public ModEntry Mod { get; set; } = new();
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Version { get; set; } = "";
    public string KindText { get; set; } = "";
    public bool Enabled { get; set; }
    public long SizeBytes { get; set; }
    public bool VortexManaged { get; set; }
    public bool IsUe4ss { get; set; }
    public bool IsNative { get; set; }
    public bool IsMergeOutput { get; set; }
    public bool IsConfigOverride { get; set; }
    public bool QuarantineEligible { get; set; }
    public string QuarantineBlockReason { get; set; } = "";
    public string QuarantineNote { get; set; } = "";
    public string ContainerSummary { get; set; } = "";
    public int ContainersRead { get; set; }
    public int ContainersUnreadable { get; set; }
    public int AssetCount { get; set; }
    public int TextureAssetCount { get; set; }
    public int VfxAssetCount { get; set; }
    public int AiAssetCount { get; set; }
    public int ShaderAssetCount { get; set; }
    public int ScriptFiles { get; set; }
    public int HookCount { get; set; }
    public int FrequentLoopCount { get; set; }
    public int ActorQueryCount { get; set; }
    public int SpawnCallCount { get; set; }
    public int NativeFileCount { get; set; }
    public ModRiskLevel Risk { get; set; } = ModRiskLevel.Unknown;
    public ModConfidence Confidence { get; set; } = ModConfidence.Insufficient;
    /// <summary>User chose to keep this mod; static warning is suppressed, evidence is retained.</summary>
    public bool Ignored { get; set; }
    public DateTime? IgnoredAt { get; set; }
    public string RiskSummary { get; set; } = "";
    public string ConfidenceNote { get; set; } = "";
    public List<string> Inspected { get; set; } = new();
    public List<ModEvidence> Evidence { get; set; } = new();
    public List<string> PossibleEffects { get; set; } = new();
    public List<string> PossibleImprovements { get; set; } = new();
    public List<string> Limitations { get; set; } = new();

    // Used by the analyzer for overlap detection; not intended for UI serialization.
    [JsonIgnore] public HashSet<string> AssetKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public List<string> AssetSamples { get; set; } = new();

    [JsonIgnore]
    public string RiskText => Risk switch
    {
        ModRiskLevel.Low => "LOW RISK",
        ModRiskLevel.Medium => "MEDIUM RISK",
        ModRiskLevel.High => "HIGH RISK",
        _ => "UNKNOWN"
    };

    [JsonIgnore]
    public string ConfidenceText => Confidence switch
    {
        ModConfidence.Confirmed => "CONFIRMED",
        ModConfidence.Likely => "LIKELY",
        ModConfidence.Hypothesis => "HYPOTHESIS",
        _ => "INSUFFICIENT"
    };

    [JsonIgnore] public string VersionText => string.IsNullOrWhiteSpace(Version) ? "version not detected" : "v" + Version;
    [JsonIgnore] public string SizeText => AppPaths.FormatBytes(SizeBytes);
    [JsonIgnore] public bool WarningActive => !Ignored && Risk is ModRiskLevel.High or ModRiskLevel.Medium;
}

public sealed class ModOverlap
{
    public string Asset { get; set; } = "";
    public List<string> SampleAssets { get; set; } = new();
    public List<string> Mods { get; set; } = new();
    public int SharedAssetCount { get; set; }
    public string Detail { get; set; } = "";
    public ModConfidence Confidence { get; set; } = ModConfidence.Confirmed;

    [JsonIgnore] public string ModsText => string.Join(", ", Mods.Take(4)) + (Mods.Count > 4 ? $" +{Mods.Count - 4}" : "");
}

public sealed class ModPerformanceReport
{
    public DateTime ScannedAt { get; set; } = DateTime.Now;
    public string GameRoot { get; set; } = "";
    public bool GameFound { get; set; }
    public List<ModPerformanceAnalysis> Mods { get; set; } = new();
    public List<ModOverlap> Overlaps { get; set; } = new();
    public List<ModConflict> ModConflicts { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public int FilesInspected { get; set; }
    public long BytesInspected { get; set; }
    public int ContainersRead { get; set; }
    public int ContainersUnreadable { get; set; }
    public TimeSpan Duration { get; set; }

    [JsonIgnore] public int EnabledCount => Mods.Count(m => m.Enabled);
    [JsonIgnore] public int HighCount => Mods.Count(m => m.Enabled && m.Risk == ModRiskLevel.High);
    [JsonIgnore] public int MediumCount => Mods.Count(m => m.Enabled && m.Risk == ModRiskLevel.Medium);
    [JsonIgnore] public int LowCount => Mods.Count(m => m.Enabled && m.Risk == ModRiskLevel.Low);
    [JsonIgnore] public int UnknownCount => Mods.Count(m => m.Enabled && m.Risk == ModRiskLevel.Unknown);
    [JsonIgnore] public int FlaggedCount => Mods.Count(m => m.Enabled && m.Risk is ModRiskLevel.High or ModRiskLevel.Medium);
}

public sealed class QuarantineFileRecord
{
    public string RelativePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTime LastWriteUtc { get; set; }
}

public sealed class QuarantineItem
{
    public string OriginalPath { get; set; } = "";
    public string StoredRelativePath { get; set; } = "";
    public bool IsDirectory { get; set; }
}

public sealed class QuarantineEntry
{
    public string Id { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string ModName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string QuarantinePath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public string Status { get; set; } = "Planned";
    public string Note { get; set; } = "";
    public string Error { get; set; } = "";
    public bool VortexManaged { get; set; }
    public bool Ue4ss { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<QuarantineItem> Items { get; set; } = new();
    public List<QuarantineFileRecord> Files { get; set; } = new();

    [JsonIgnore]
    public bool IsActive => Status is "Quarantined" or "Copied" or "Planned";

    [JsonIgnore]
    public string StatusText => (Status ?? "") switch
    {
        "Restored" => "RESTORED",
        "Quarantined" => "QUARANTINED",
        "Copied" => "COPY VERIFIED · ORIGINAL STILL PRESENT",
        "Planned" => "INTERRUPTED / INCOMPLETE",
        "Missing" => "FILES MISSING",
        "" => "UNKNOWN",
        var value => value.ToUpperInvariant()
    };

    [JsonIgnore] public string SizeText => AppPaths.FormatBytes(SizeBytes);
}

public sealed class QuarantineManifest
{
    public int Version { get; set; } = 1;
    public string Root { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<QuarantineEntry> Entries { get; set; } = new();
}

public sealed class QuarantineResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<QuarantineEntry> Entries { get; set; } = new();
}

public sealed class ModComparisonRecord
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Notes { get; set; } = "";
    public string DisabledMods { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string BeforeFile { get; set; } = "";
    public string AfterFile { get; set; } = "";
    public DateTime BeforeImportedAt { get; set; }
    public DateTime AfterImportedAt { get; set; }
    public MeasurementSummary Before { get; set; } = new();
    public MeasurementSummary After { get; set; } = new();
    public string ResultText { get; set; } = "";
    public List<string> Warnings { get; set; } = new();

    [JsonIgnore]
    public string ShortText => $"{CreatedAt:yyyy-MM-dd HH:mm} · {Label} · " +
                               $"{Before.AverageFps:0.0} → {After.AverageFps:0.0} FPS ({(After.AverageFps - Before.AverageFps) / Math.Max(0.1, Before.AverageFps) * 100:+0.0;-0.0}%)";
}
