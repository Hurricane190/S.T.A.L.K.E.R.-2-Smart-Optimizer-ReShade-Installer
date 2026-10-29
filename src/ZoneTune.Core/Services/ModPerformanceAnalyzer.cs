using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Read-only static inspection of installed mods. It distinguishes confirmed file
/// facts (a hook exists, an asset path exists, two mods share an asset) from
/// hypotheses about runtime cost (frame time, VRAM, stutter). It never claims an
/// FPS loss and never attributes an improvement without a user-supplied measurement.
/// </summary>
public sealed class ModPerformanceAnalyzer
{
    private readonly PakContentReader _reader = new();
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, CachedInspection> _cache = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxFilesPerMod = 5000;
    private const int MaxLuaFilesPerMod = 40;
    private const long MaxLuaBytesPerMod = 16 * 1024 * 1024;
    private const int MaxAssetKeysPerMod = 20_000;

    private static readonly Regex VersionRegex = new(
        @"(?<![\d.])(\d{1,3}\.\d{1,3}(?:\.\d{1,3}){0,2})(?![\d.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LoopRegex = new(
        @"(?:LoopAsync|LoopInGameThreadWithDelay|ExecuteWithDelay)\s*\(\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LoopVariableRegex = new(
        @"(?:LoopAsync|LoopInGameThreadWithDelay|ExecuteWithDelay)\s*\(\s*([A-Za-z_][A-Za-z0-9_.]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HookRegex = new(
        @"RegisterHook\s*\(\s*[""']([^""']+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ActorQueryRegex = new(
        @"\b(FindAllOf|FindFirstOf|GetAllActorsOfClass|GetAll|NotifyOnNewObject)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SpawnRegex = new(
        @"\b(SpawnActor|SpawnActorFromClass|CreateObject|LoadAsset|SpawnNPC|SpawnCharacter)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WhileTrueRegex = new(
        @"\bwhile\s+true\b|\brepeat\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProcessEventRegex = new(
        @"\bProcessEvent\b|\bNotifyOnNewObject\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AiIdentifierRegex = new(
        @"\b(AIController|BehaviorTree|Perception|Squad|Population|Spawn|Agent|NPC|Patrol|Combat)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class CachedInspection
    {
        public string Fingerprint { get; set; } = "";
        public InspectionData Data { get; set; } = new();
    }

    private sealed class InspectionData
    {
        public long TotalSize { get; set; }
        public List<PakContainerInfo> Containers { get; set; } = new();
        public List<PakAssetEntry> Assets { get; set; } = new();
        public List<string> ContainersSkipped { get; set; } = new();
        public List<string> NativeFiles { get; set; } = new();
        public List<string> ConfigFiles { get; set; } = new();
        public List<string> LuaFiles { get; set; } = new();
        public LuaMetrics Lua { get; set; } = new();
        public string Version { get; set; } = "";
        public bool VortexManaged { get; set; }
        public bool Ue4ss { get; set; }
        public bool MergeOutput { get; set; }
        public bool ConfigOverride { get; set; }
        public int FilesInspected { get; set; }
        public long BytesInspected { get; set; }
        public int ReadableContainers { get; set; }
        public int UnreadableContainers { get; set; }
        public bool UsedCache { get; set; }
        public string ContainerSummary { get; set; } = "";
    }

    private sealed class LuaMetrics
    {
        public int FilesRead { get; set; }
        public long BytesRead { get; set; }
        public int TickHooks { get; set; }
        public int HookCount { get; set; }
        public int Loops { get; set; }
        public int FrequentLoops { get; set; }
        public int WhileLoops { get; set; }
        public int ActorQueries { get; set; }
        public int SpawnCalls { get; set; }
        public int NotifyOnNewObject { get; set; }
        public int ProcessEvent { get; set; }
        public int AiIdentifiers { get; set; }
        public List<string> HookSamples { get; set; } = new();
        public List<string> LoopSamples { get; set; } = new();
        public List<string> QuerySamples { get; set; } = new();
        public List<string> SpawnSamples { get; set; } = new();
        public List<string> AiSamples { get; set; } = new();
    }

    public ModPerformanceReport Analyze(GameInstall game, HardwareInfo? hardware = null,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var started = DateTime.Now;
        var report = new ModPerformanceReport { GameRoot = game.Root, GameFound = game.IsValid };
        if (!game.IsValid)
        {
            report.Notes.Add("Game folder is not valid; mod analysis was not run.");
            return report;
        }

        progress?.Report(new OperationProgress("Inventory", "Reading pak, UE4SS and ASI entries", 5));
        var (mods, conflicts) = new ModScanner().Scan(game);
        report.ModConflicts = conflicts;

        var unique = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        var duplicates = 0;
        foreach (var mod in mods)
        {
            var key = NormalizePath(mod.Path);
            if (unique.TryGetValue(key, out var existing))
            {
                duplicates++;
                if (mod.Enabled && !existing.Enabled) existing.Enabled = true;
                continue;
            }
            unique[key] = mod;
        }
        if (duplicates > 0)
            report.Notes.Add($"{duplicates} duplicate inventory entr{(duplicates == 1 ? "y" : "ies")} collapsed by path; repeated scans do not create duplicate analyzer rows.");

        var ordered = unique.Values
            .OrderBy(m => m.Kind)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sizes = ordered.Select(m => m.SizeBytes).Where(v => v > 0).OrderBy(v => v).ToList();
        var sizeP90 = sizes.Count == 0 ? 0 : sizes[(int)((sizes.Count - 1) * 0.90)];

        var analyses = new List<ModPerformanceAnalysis>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var mod = ordered[i];
            progress?.Report(new OperationProgress("Inspecting mods",
                $"{(i + 1)}/{ordered.Count} · {mod.Name}", 8 + (int)(82.0 * i / Math.Max(1, ordered.Count))));

            try
            {
                var inspection = Inspect(mod, game, ct);
                var analysis = Assess(mod, inspection, game, hardware, analyses.Count, ordered.Count, sizeP90);
                analyses.Add(analysis);
                report.FilesInspected += inspection.FilesInspected;
                report.BytesInspected += inspection.BytesInspected;
                report.ContainersRead += inspection.ReadableContainers;
                report.ContainersUnreadable += inspection.UnreadableContainers;
            }
            catch (Exception ex)
            {
                Log.Warn($"Mod analysis failed for {mod.Name}: {ex.Message}");
                analyses.Add(new ModPerformanceAnalysis
                {
                    Mod = mod,
                    Name = mod.Name,
                    Path = mod.Path,
                    KindText = mod.KindText,
                    Enabled = mod.Enabled,
                    SizeBytes = mod.SizeBytes,
                    Risk = ModRiskLevel.Unknown,
                    Confidence = ModConfidence.Insufficient,
                    RiskSummary = "Inspection failed; no evidence was collected.",
                    ConfidenceNote = "The analyzer caught an error while reading this entry. The file was not modified.",
                    Inspected = { "Attempted inventory and content inspection" },
                    Limitations = { ex.Message },
                    QuarantineEligible = false,
                    QuarantineBlockReason = "Inspection failed; resolve access errors before moving files."
                });
            }
        }

        report.Mods = analyses;
        progress?.Report(new OperationProgress("Cross-checking", "Overlapping archives and duplicate assets", 93));
        BuildOverlaps(report, analyses);

        report.Notes.Add("Static inspection only. File facts are confirmed; runtime cost and FPS impact are hypotheses unless you import a before/after measurement.");
        report.Notes.Add("Archives marked UNKNOWN were encrypted, unreadable or native-only; ZoneTune does not guess at their contents.");
        if (analyses.Any(a => a.VortexManaged))
            report.Notes.Add("Vortex-managed entries are detected and cannot be quarantined by ZoneTune. Disable or purge them in Vortex.");
        if (analyses.Any(a => a.IsUe4ss))
            report.Notes.Add("UE4SS state is read from enabled/disabled markers, mods.json and load-order metadata; mods.txt alone is not treated as authoritative.");
        report.Duration = DateTime.Now - started;
        progress?.Report(new OperationProgress("Done", $"{analyses.Count} mods inspected", 100));
        Log.Info($"Mod performance analysis: {analyses.Count} mods, {report.HighCount} high, {report.MediumCount} medium, " +
                 $"{report.UnknownCount} unknown, {report.Overlaps.Count} overlap note(s), {report.Duration.TotalSeconds:0.0}s");
        return report;
    }

    private InspectionData Inspect(ModEntry mod, GameInstall game, CancellationToken ct)
    {
        var fingerprint = Fingerprint(mod);
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(mod.Path, out var cached) &&
                cached.Fingerprint.Equals(fingerprint, StringComparison.Ordinal) &&
                cached.Data.TotalSize == mod.SizeBytes)
            {
                cached.Data.UsedCache = true;
                return cached.Data;
            }
        }

        var data = new InspectionData
        {
            TotalSize = mod.SizeBytes,
            VortexManaged = IsVortexManaged(mod, game),
            Ue4ss = mod.Kind == ModKind.Ue4ssLua,
            MergeOutput = mod.Kind == ModKind.MergeOutput ||
                          mod.Name.Contains("S2MUU", StringComparison.OrdinalIgnoreCase) ||
                          mod.Name.Contains("Merged", StringComparison.OrdinalIgnoreCase),
            ConfigOverride = mod.Kind == ModKind.ConfigOverride
        };

        var files = EnumerateFiles(mod.Path);
        data.FilesInspected = files.Count;
        data.Version = DetectVersion(mod.Path, files, mod.Name);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var ext = System.IO.Path.GetExtension(file);
            if (ext.Equals(".lua", StringComparison.OrdinalIgnoreCase) && data.LuaFiles.Count < MaxLuaFilesPerMod)
                data.LuaFiles.Add(file);
            else if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals(".asi", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                data.NativeFiles.Add(file);
            else if (ext.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
                     ext.Equals(".cfg", StringComparison.OrdinalIgnoreCase))
                data.ConfigFiles.Add(file);
        }

        var utocBases = files
            .Where(f => f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
            .Select(f => System.IO.Path.GetFileNameWithoutExtension(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var ext = System.IO.Path.GetExtension(file);
            if (ext.Equals(".ucas", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".vortex_backup", StringComparison.OrdinalIgnoreCase))
                continue;

            if (ext.Equals(".utoc", StringComparison.OrdinalIgnoreCase))
            {
                var info = _reader.Read(file);
                RegisterContainer(data, info);
            }
            else if (ext.Equals(".pak", StringComparison.OrdinalIgnoreCase))
            {
                var baseName = System.IO.Path.GetFileNameWithoutExtension(file);
                if (utocBases.Contains(baseName))
                {
                    data.ContainersSkipped.Add(System.IO.Path.GetFileName(file) + " (IoStore stub; paths read from .utoc)");
                    continue;
                }
                var info = _reader.Read(file);
                RegisterContainer(data, info);
            }
        }

        ReadLua(data, ct);
        ReadVersionFromMetadata(mod.Path, files, data);
        BuildContainerSummary(data);
        InspectedSummary(data, mod);
        return data;
    }

    private static void RegisterContainer(InspectionData data, PakContainerInfo info)
    {
        data.Containers.Add(info);
        data.BytesInspected += info.IndexBytesRead;
        if (info.Readable)
        {
            data.ReadableContainers++;
            foreach (var entry in info.Entries)
            {
                data.Assets.Add(entry);
                if (data.Assets.Count >= MaxFilesPerMod * 4) break;
            }
        }
        else data.UnreadableContainers++;
    }

    private void ReadLua(InspectionData data, CancellationToken ct)
    {
        long bytes = 0;
        foreach (var file in data.LuaFiles)
        {
            if (data.Lua.FilesRead >= MaxLuaFilesPerMod || bytes >= MaxLuaBytesPerMod) break;
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                if (info.Length <= 0 || info.Length > 4 * 1024 * 1024) continue;
                var text = File.ReadAllText(file);
                bytes += info.Length;
                data.BytesInspected += info.Length;
                data.Lua.FilesRead++;
                data.Lua.BytesRead += info.Length;
                AnalyzeLuaText(text, System.IO.Path.GetFileName(file), data.Lua);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read Lua file {file}: {ex.Message}");
            }
        }
    }

    private static void AnalyzeLuaText(string source, string fileName, LuaMetrics metrics)
    {
        var code = StripLuaComments(source);
        foreach (Match match in LoopRegex.Matches(code))
        {
            metrics.Loops++;
            if (int.TryParse(match.Groups[1].Value, out var ms) && ms is > 0 and <= 100)
            {
                metrics.FrequentLoops++;
                AddSample(metrics.LoopSamples, $"{fileName}: {ms} ms loop");
            }
            else AddSample(metrics.LoopSamples, $"{fileName}: {match.Value}");
        }

        // Resolve simple interval constants, e.g. LoopAsync(Config.LOOP_MS, ...) with
        // LOOP_MS = 50. This is still a source fact, not a runtime measurement.
        foreach (Match match in LoopVariableRegex.Matches(code))
        {
            var name = match.Groups[1].Value;
            var shortName = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
            var constant = Regex.Match(code, $@"\b{Regex.Escape(shortName)}\s*=\s*(\d+)");
            if (!constant.Success) continue;
            if (!int.TryParse(constant.Groups[1].Value, out var ms) || ms <= 0) continue;
            metrics.Loops++;
            if (ms <= 100)
            {
                metrics.FrequentLoops++;
                AddSample(metrics.LoopSamples, $"{fileName}: {ms} ms loop ({name})");
            }
            else AddSample(metrics.LoopSamples, $"{fileName}: {ms} ms loop ({name})");
        }

        foreach (Match match in HookRegex.Matches(code))
        {
            metrics.HookCount++;
            var target = match.Groups[1].Value;
            if (target.Contains("Tick", StringComparison.OrdinalIgnoreCase) ||
                target.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                target.Contains("ProcessEvent", StringComparison.OrdinalIgnoreCase))
            {
                metrics.TickHooks++;
                AddSample(metrics.HookSamples, $"{fileName}: {target}");
            }
        }

        metrics.WhileLoops += WhileTrueRegex.Matches(code).Count;

        foreach (Match match in ActorQueryRegex.Matches(code))
        {
            metrics.ActorQueries++;
            if (match.Value.Equals("NotifyOnNewObject", StringComparison.Ordinal))
                metrics.NotifyOnNewObject++;
            AddSample(metrics.QuerySamples, $"{fileName}: {match.Value}");
        }

        foreach (Match match in SpawnRegex.Matches(code))
        {
            metrics.SpawnCalls++;
            AddSample(metrics.SpawnSamples, $"{fileName}: {match.Value}");
        }

        metrics.ProcessEvent += ProcessEventRegex.Matches(code).Count;
        var ai = AiIdentifierRegex.Matches(code);
        metrics.AiIdentifiers += ai.Count;
        foreach (Match match in ai.Take(3)) AddSample(metrics.AiSamples, $"{fileName}: {match.Value}");
    }

    private static string StripLuaComments(string source)
    {
        var withoutBlock = Regex.Replace(source, @"--\[\[.*?\]\]", "", RegexOptions.Singleline);
        return Regex.Replace(withoutBlock, @"--[^\r\n]*", "");
    }

    private static void AddSample(List<string> samples, string value)
    {
        if (samples.Count >= 8) return;
        if (samples.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        samples.Add(value.Length > 220 ? value[..220] + "…" : value);
    }

    private static void BuildContainerSummary(InspectionData data)
    {
        if (data.Containers.Count == 0 && data.ContainersSkipped.Count == 0)
        {
            data.ContainerSummary = "No pak/utoc containers.";
            return;
        }
        var exact = data.Containers.Count(c => c.Readable && c.IndexExact);
        var heuristic = data.Containers.Count(c => c.Readable && !c.IndexExact);
        var unreadable = data.Containers.Count(c => !c.Readable);
        var parts = new List<string>();
        if (exact > 0) parts.Add($"{exact} index-readable container(s)");
        if (heuristic > 0) parts.Add($"{heuristic} heuristic container(s)");
        if (unreadable > 0) parts.Add($"{unreadable} unreadable");
        if (data.ContainersSkipped.Count > 0) parts.Add($"{data.ContainersSkipped.Count} IoStore stub(s)");
        data.ContainerSummary = string.Join(", ", parts) + $"; {data.Assets.Count} indexed asset path(s).";
    }

    private static void InspectedSummary(InspectionData data, ModEntry mod)
    {
        data.ContainerSummary = data.ContainerSummary +
                                (data.FilesInspected > 0 ? $" {data.FilesInspected} file(s) in the mod folder." : "");
    }

    private void BuildOverlaps(ModPerformanceReport report, List<ModPerformanceAnalysis> analyses)
    {
        var active = analyses.Where(a => a.Enabled && a.AssetKeys.Count > 0).ToList();
        var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < active.Count; i++)
        {
            foreach (var key in active[i].AssetKeys.Take(MaxAssetKeysPerMod))
            {
                if (key.Length < 6) continue;
                if (!map.TryGetValue(key, out var list))
                    map[key] = list = new List<int>();
                if (list.Count < 24) list.Add(i);
            }
        }

        var pairs = new Dictionary<(int A, int B), (int Count, List<string> Samples)>();
        foreach (var kv in map.Where(k => k.Value.Count >= 2))
        {
            var list = kv.Value;
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    var a = Math.Min(list[i], list[j]);
                    var b = Math.Max(list[i], list[j]);
                    if (!pairs.TryGetValue((a, b), out var info))
                        pairs[(a, b)] = info = (0, new List<string>());
                    info.Count++;
                    if (info.Samples.Count < 6) info.Samples.Add(kv.Key);
                    pairs[(a, b)] = info;
                }
            }
        }

        var overlapByMod = new Dictionary<int, (int Mods, int Assets)>();
        foreach (var pair in pairs.OrderByDescending(p => p.Value.Count).Take(40))
        {
            var left = active[pair.Key.A];
            var right = active[pair.Key.B];
            report.Overlaps.Add(new ModOverlap
            {
                Asset = pair.Value.Samples.FirstOrDefault() ?? "",
                SampleAssets = pair.Value.Samples,
                Mods = new List<string> { left.Name, right.Name },
                SharedAssetCount = pair.Value.Count,
                Detail = $"{pair.Value.Count} normalized asset path(s) appear in both archives. This is a confirmed overlap; " +
                         "it does not by itself prove duplicate work or a performance cost. Check load order and compatibility.",
                Confidence = ModConfidence.Confirmed
            });
            Bump(overlapByMod, pair.Key.A, right.Name, pair.Value.Count);
            Bump(overlapByMod, pair.Key.B, left.Name, pair.Value.Count);
        }

        foreach (var kv in overlapByMod)
        {
            var analysis = active[kv.Key];
            analysis.Evidence.Add(new ModEvidence
            {
                Title = "Shared asset paths with other mods",
                Detail = $"Shares {kv.Value.Assets} indexed asset path(s) with {kv.Value.Mods} other active mod(s). " +
                         "Overlap is a compatibility finding, not proof of a performance problem.",
                Source = "Cross-container asset index",
                Metric = $"{kv.Value.Assets} shared path(s)",
                Category = ModEvidenceCategory.Overlap,
                Confidence = ModConfidence.Confirmed
            });
        }
    }

    private static void Bump(Dictionary<int, (int Mods, int Assets)> map, int index, string other, int shared)
    {
        map.TryGetValue(index, out var current);
        map[index] = (current.Mods + 1, current.Assets + shared);
    }

    private ModPerformanceAnalysis Assess(ModEntry mod, InspectionData data, GameInstall game,
        HardwareInfo? hardware, int index, int total, long sizeP90)
    {
        var a = new ModPerformanceAnalysis
        {
            Mod = mod,
            Name = mod.Name,
            Path = mod.Path,
            Version = data.Version,
            KindText = mod.KindText,
            Enabled = mod.Enabled,
            SizeBytes = data.TotalSize > 0 ? data.TotalSize : mod.SizeBytes,
            VortexManaged = data.VortexManaged,
            IsUe4ss = data.Ue4ss,
            IsNative = data.NativeFiles.Count > 0,
            IsMergeOutput = data.MergeOutput,
            IsConfigOverride = data.ConfigOverride,
            ContainerSummary = data.ContainerSummary,
            ContainersRead = data.ReadableContainers,
            ContainersUnreadable = data.UnreadableContainers,
            ScriptFiles = data.Lua.FilesRead,
            HookCount = data.Lua.TickHooks,
            FrequentLoopCount = data.Lua.FrequentLoops,
            ActorQueryCount = data.Lua.ActorQueries,
            SpawnCallCount = data.Lua.SpawnCalls,
            NativeFileCount = data.NativeFiles.Count
        };

        a.Inspected.Add($"{data.FilesInspected} file(s), {AppPaths.FormatBytes(data.TotalSize)} on disk; " +
                        $"{data.ReadableContainers} readable and {data.UnreadableContainers} unreadable container(s).");
        if (data.Containers.Count > 0)
            a.Inspected.Add(data.ContainerSummary);
        if (data.Lua.FilesRead > 0)
            a.Inspected.Add($"{data.Lua.FilesRead} Lua file(s), {AppPaths.FormatBytes(data.Lua.BytesRead)} of script source.");
        if (data.NativeFiles.Count > 0)
            a.Inspected.Add($"{data.NativeFiles.Count} native binary file(s) (presence only; runtime code was not executed or inspected).");
        if (data.ConfigFiles.Count > 0)
            a.Inspected.Add($"{data.ConfigFiles.Count} .ini/.cfg file(s) for setting names.");
        if (!string.IsNullOrWhiteSpace(data.Version))
            a.Inspected.Add($"Version {data.Version} read from metadata or the folder name.");

        // Container facts
        if (data.UnreadableContainers > 0)
        {
            a.Evidence.Add(new ModEvidence
            {
                Title = "Unreadable container index",
                Detail = $"{data.UnreadableContainers} archive(s) could not be indexed (encrypted, compressed or truncated). " +
                         "Their contents were not guessed at.",
                Source = data.Containers.FirstOrDefault(c => !c.Readable)?.Error ?? "Container reader",
                Metric = $"{data.UnreadableContainers} unreadable",
                Category = ModEvidenceCategory.Container,
                Confidence = ModConfidence.Insufficient
            });
        }
        if (data.Assets.Count > 0)
        {
            var exact = data.Containers.Any(c => c.Readable && c.IndexExact);
            a.Evidence.Add(new ModEvidence
            {
                Title = "Archive contents indexed",
                Detail = $"{data.Assets.Count} asset path(s) read from {data.ReadableContainers} container(s). " +
                         (exact ? "Paths came from a parsed directory index." : "Paths were extracted heuristically; full-path overlaps may be incomplete."),
                Source = string.Join("; ", data.Containers.Where(c => c.Readable).Select(c => System.IO.Path.GetFileName(c.Path)).Take(4)),
                Metric = $"{data.Assets.Count} paths",
                Category = ModEvidenceCategory.Container,
                Confidence = exact ? ModConfidence.Confirmed : ModConfidence.Hypothesis
            });
        }

        // Asset categories
        var assets = data.Assets;
        var textures = assets.Count(IsTextureAsset);
        var vfx = assets.Count(IsVfxAsset);
        var aiAssets = assets.Count(IsAiAsset);
        var shader = assets.Count(e => e.Extension.Equals(".ushaderbytecode", StringComparison.OrdinalIgnoreCase));
        var audio = assets.Count(e => e.Extension.Equals(".wem", StringComparison.OrdinalIgnoreCase) ||
                                      e.Extension.Equals(".bnk", StringComparison.OrdinalIgnoreCase));
        a.AssetCount = assets.Count;
        a.TextureAssetCount = textures;
        a.VfxAssetCount = vfx;
        a.AiAssetCount = aiAssets;
        a.ShaderAssetCount = shader;

        var categoryParts = new List<string>();
        if (textures > 0) categoryParts.Add($"{textures} texture");
        if (vfx > 0) categoryParts.Add($"{vfx} VFX/lighting/shader-adjacent");
        if (aiAssets > 0) categoryParts.Add($"{aiAssets} AI/spawn/population-adjacent");
        if (shader > 0) categoryParts.Add($"{shader} shader bytecode");
        if (audio > 0) categoryParts.Add($"{audio} audio");
        if (categoryParts.Count > 0)
        {
            a.Evidence.Add(new ModEvidence
            {
                Title = "Asset categories",
                Detail = string.Join(", ", categoryParts) + ". Names and paths are confirmed; texture resolution, triangle count, " +
                         "draw calls and whether the assets are loaded at runtime are not known.",
                Source = "Normalized archive paths and extensions",
                Metric = $"{assets.Count} assets",
                Category = ModEvidenceCategory.Asset,
                Confidence = ModConfidence.Confirmed
            });
        }
        if (assets.Count > 0 && categoryParts.Count == 0)
        {
            a.Evidence.Add(new ModEvidence
            {
                Title = "Assets without a performance-relevant category",
                Detail = $"{assets.Count} indexed asset(s), mostly data/configuration/mesh/audio files without a known static cost indicator.",
                Source = "Normalized archive paths and extensions",
                Metric = $"{assets.Count} assets",
                Category = ModEvidenceCategory.Asset,
                Confidence = ModConfidence.Confirmed
            });
        }

        if (data.Lua.FilesRead == 0 && data.Assets.Count > 0 && shader == 0 && vfx == 0 &&
            textures == 0 && aiAssets == 0)
        {
            // No category indicators is a low-risk result, not proof of zero cost.
        }

        // Lua facts
        if (data.Lua.FilesRead > 0)
        {
            a.Evidence.Add(new ModEvidence
            {
                Title = "Lua source inspected",
                Detail = $"{data.Lua.FilesRead} file(s), {AppPaths.FormatBytes(data.Lua.BytesRead)}. " +
                         "The analyzer reads source; it does not execute it or know whether each branch is enabled at runtime.",
                Source = string.Join(", ", data.LuaFiles.Select(System.IO.Path.GetFileName).Take(5)),
                Metric = $"{data.Lua.FilesRead} files",
                Category = ModEvidenceCategory.Script,
                Confidence = ModConfidence.Confirmed
            });
        }
        if (data.Lua.HookCount > 0)
        {
            a.Evidence.Add(new ModEvidence
            {
                Title = "Hook registrations",
                Detail = $"{data.Lua.HookCount} RegisterHook call(s), {data.Lua.TickHooks} targeting a tick/update-style event. " +
                         "A tick hook is confirmed; how often it fires and what it costs are not measured.",
                Source = string.Join(" | ", data.Lua.HookSamples.Take(4)),
                Metric = $"{data.Lua.HookCount} hook(s), {data.Lua.TickHooks} tick-style",
                Category = ModEvidenceCategory.Script,
                Confidence = ModConfidence.Confirmed
            });
        }
        if (data.Lua.Loops > 0)
        {
            a.Evidence.Add(new ModEvidence
            {
                Title = "Scheduled loops",
                Detail = $"{data.Lua.Loops} LoopAsync/ExecuteWithDelay registration(s); {data.Lua.FrequentLoops} at 100 ms or faster. " +
                         "The interval is a confirmed source value; actual execution and cost are not measured.",
                Source = string.Join(" | ", data.Lua.LoopSamples.Take(5)),
                Metric = $"{data.Lua.Loops} loop(s), {data.Lua.FrequentLoops} high-frequency",
                Category = ModEvidenceCategory.Script,
                Confidence = ModConfidence.Confirmed
            });
        }
        if (data.Lua.ActorQueries > 0 || data.Lua.SpawnCalls > 0 || data.Lua.WhileLoops > 0)
        {
            a.Evidence.Add(new ModEvidence
            {
                Title = "Actor, spawn and loop patterns",
                Detail = $"{data.Lua.ActorQueries} actor/object query call(s), {data.Lua.SpawnCalls} spawn/create call(s), " +
                         $"{data.Lua.WhileLoops} while-true/repeat pattern(s), {data.Lua.NotifyOnNewObject} object-notification call(s). " +
                         "These are code patterns, not proof that they dominate frame time.",
                Source = string.Join(" | ", data.Lua.QuerySamples.Concat(data.Lua.SpawnSamples).Take(5)),
                Metric = $"{data.Lua.ActorQueries} queries / {data.Lua.SpawnCalls} spawns",
                Category = ModEvidenceCategory.Script,
                Confidence = ModConfidence.Confirmed
            });
        }
        if (data.Lua.AiIdentifiers > 0)
        {
            a.Evidence.Add(new ModEvidence
            {
                Title = "AI / population identifiers in script",
                Detail = $"{data.Lua.AiIdentifiers} AI, NPC, spawn, squad, population or combat identifier(s) appear in code. " +
                         "This indicates AI-related logic may exist; it does not show an increased NPC count.",
                Source = string.Join(" | ", data.Lua.AiSamples.Take(4)),
                Metric = $"{data.Lua.AiIdentifiers} identifiers",
                Category = ModEvidenceCategory.Script,
                Confidence = ModConfidence.Hypothesis
            });
        }

        // Native binaries
        if (data.NativeFiles.Count > 0)
        {
            a.Evidence.Add(new ModEvidence
            {
                Title = "Native code present",
                Detail = $"{data.NativeFiles.Count} .dll/.asi/.exe file(s): {string.Join(", ", data.NativeFiles.Select(System.IO.Path.GetFileName).Take(3))}. " +
                         "Static file analysis cannot determine what this native code does at runtime.",
                Source = string.Join(", ", data.NativeFiles.Take(4)),
                Metric = AppPaths.FormatBytes(data.NativeFiles.Sum(SafeLength)),
                Category = ModEvidenceCategory.Native,
                Confidence = ModConfidence.Confirmed
            });
        }

        // Config overrides
        var configFlags = InspectConfigFiles(data.ConfigFiles, a);
        if (configFlags > 0)
        {
            a.Evidence.Add(new ModEvidence
            {
                Title = "Performance-relevant config values",
                Detail = "Configuration files contain settings that can change rendering load. The value is confirmed; the resulting " +
                         "frame cost depends on the game build, resolution and whether the file is actually loaded.",
                Source = string.Join(", ", data.ConfigFiles.Select(System.IO.Path.GetFileName).Take(5)),
                Metric = $"{configFlags} relevant setting(s)",
                Category = ModEvidenceCategory.Config,
                Confidence = ModConfidence.Likely
            });
        }

        // Size context (never a risk by itself)
        AddSizeEvidence(a, data.TotalSize, sizeP90, index, total);

        // Score
        var scriptScore = 0;
        if (data.Lua.FrequentLoops > 0 && (data.Lua.ActorQueries > 0 || data.Lua.SpawnCalls > 0)) scriptScore += 4;
        if (data.Lua.TickHooks > 0) scriptScore += Math.Min(3, 1 + data.Lua.TickHooks);
        if (data.Lua.WhileLoops > 0 && data.Lua.ActorQueries > 0) scriptScore += 2;
        if (data.Lua.FrequentLoops > 0) scriptScore += 1;
        if (data.Lua.NotifyOnNewObject > 0) scriptScore += 1;
        if (data.Lua.ActorQueries > 0) scriptScore += 1;
        if (data.Lua.SpawnCalls > 0) scriptScore += 1;

        var assetScore = 0;
        // A single .ushaderbytecode archive is normal cook output. Only a large VFX
        // set or several shader archives is treated as a potential load point.
        if (vfx >= 20) assetScore += 2;
        if (shader >= 3) assetScore += 1;
        if (textures >= 100 && data.TotalSize >= 250L * 1024 * 1024) assetScore += 1;
        if (textures >= 500 && data.TotalSize >= 500L * 1024 * 1024) assetScore += 1;
        if (aiAssets >= 20) assetScore += 1;
        if (assets.Count >= 2000) assetScore += 1;
        if (hardware?.VramMb is > 0 and < 8192 && textures >= 100) assetScore += 1;
        assetScore = Math.Min(assetScore, 3);

        var score = scriptScore + Math.Min(assetScore, 2) + Math.Min(configFlags, 2);

        var hasContent = data.Lua.FilesRead > 0 || data.Assets.Count > 0 || data.ConfigFiles.Count > 0;
        var onlyNative = !hasContent && data.NativeFiles.Count > 0;
        var unreadableOnly = data.Containers.Count > 0 && data.ReadableContainers == 0 && data.Lua.FilesRead == 0 && data.NativeFiles.Count == 0;

        if (data.MergeOutput)
        {
            a.Risk = ModRiskLevel.Unknown;
            a.Confidence = ModConfidence.Insufficient;
            a.RiskSummary = "Merged output combines many source mods. Static inspection can list its assets, but it cannot attribute " +
                            "a performance concern to one mod or prove that disabling it would help.";
        }
        else if (onlyNative || unreadableOnly || !hasContent)
        {
            a.Risk = ModRiskLevel.Unknown;
            a.Confidence = ModConfidence.Insufficient;
            a.RiskSummary = onlyNative
                ? "Only native binaries were found. ZoneTune cannot determine their runtime behaviour from files."
                : unreadableOnly
                    ? "The archive index could not be read, so there is not enough evidence to assess performance."
                    : "No inspectable script, asset or config content was found.";
        }
        else
        {
            a.Risk = score >= 5 ? ModRiskLevel.High : score >= 2 ? ModRiskLevel.Medium : ModRiskLevel.Low;
            a.Confidence = score >= 5
                ? (data.Lua.FrequentLoops > 0 && data.Lua.ActorQueries > 0 ? ModConfidence.Likely : ModConfidence.Hypothesis)
                : score >= 2
                    ? (scriptScore > 0 && assetScore > 0 ? ModConfidence.Likely : ModConfidence.Hypothesis)
                    : (data.Containers.All(c => !c.Readable) ? ModConfidence.Hypothesis : ModConfidence.Likely);
            a.RiskSummary = a.Risk switch
            {
                ModRiskLevel.High => BuildHighSummary(data),
                ModRiskLevel.Medium => BuildMediumSummary(data, textures, vfx, shader, aiAssets, configFlags),
                _ => "No script or asset pattern associated with runtime cost was found. File size alone is not treated as evidence."
            };
        }

        a.ConfidenceNote = a.Confidence switch
        {
            ModConfidence.Confirmed => "Technical facts are confirmed, but performance impact still requires measurement.",
            ModConfidence.Likely => "Multiple static indicators point the same way. This is not a measured FPS or stutter result.",
            ModConfidence.Hypothesis => "A plausible static explanation was found. It is not proof of a performance problem.",
            _ => "The available files do not support a reliable assessment. No guess was made."
        };

        a.PossibleEffects.AddRange(PossibleEffects(data, textures, vfx, shader, aiAssets, configFlags));
        a.PossibleImprovements.AddRange(PossibleImprovements(a, data, textures, vfx, shader, aiAssets));
        a.Limitations.AddRange(new[]
        {
            "Static file analysis cannot measure FPS, frame time, 1% lows, stutter, VRAM/RAM use or CPU/GPU utilization.",
            "File size alone is not evidence of poor performance.",
            "Runtime configuration, load order and game version can change whether the inspected code or assets actually run.",
            "This assessment is not a benchmark and must not be presented as one."
        });
        if (a.ContainersUnreadable > 0)
            a.Limitations.Add("One or more archives could not be read; their contents are outside this assessment.");
        if (a.IsNative)
            a.Limitations.Add("Native code was not disassembled or executed; its runtime cost is unknown.");
        if (data.Lua.FilesRead > 0)
            a.Limitations.Add("Lua branches may be disabled by config; the analyzer only reads source patterns.");

        SetQuarantineEligibility(a, game);
        a.AssetKeys = data.Assets.Select(e => e.Key).Where(k => k.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        a.AssetSamples = data.Assets.Select(e => e.Path).Take(12).ToList();
        return a;
    }

    private static string BuildHighSummary(InspectionData data)
    {
        var parts = new List<string>();
        if (data.Lua.FrequentLoops > 0) parts.Add($"{data.Lua.FrequentLoops} script loop(s) at 100 ms or faster");
        if (data.Lua.ActorQueries > 0) parts.Add($"{data.Lua.ActorQueries} actor/object query call(s)");
        if (data.Lua.TickHooks > 0) parts.Add($"{data.Lua.TickHooks} tick-style hook(s)");
        if (data.Lua.SpawnCalls > 0) parts.Add($"{data.Lua.SpawnCalls} spawn/create call(s)");
        return "Strong static indicators: " + string.Join(", ", parts) +
               ". This could add CPU frame-time work or hitches, but FPS loss is not proven without a measurement.";
    }

    private static string BuildMediumSummary(InspectionData data, int textures, int vfx, int shader, int ai, int config)
    {
        var parts = new List<string>();
        if (data.Lua.TickHooks > 0) parts.Add("tick-style hooks");
        if (data.Lua.Loops > 0) parts.Add("scheduled script loops");
        if (data.Lua.ActorQueries > 0) parts.Add("actor queries");
        if (data.Lua.SpawnCalls > 0) parts.Add("spawn/create calls");
        if (textures >= 100) parts.Add($"{textures} texture assets");
        if (vfx > 0) parts.Add($"{vfx} VFX/lighting-adjacent assets");
        if (shader > 0) parts.Add($"{shader} shader archive(s)");
        if (ai >= 20) parts.Add($"{ai} AI/spawn-adjacent assets");
        if (config > 0) parts.Add("performance-relevant config values");
        return "Relevant static indicators: " + string.Join(", ", parts.Take(5)) +
               ". These are potential load points, not measured causes.";
    }

    private static IEnumerable<string> PossibleEffects(InspectionData data, int textures, int vfx, int shader,
        int ai, int config)
    {
        if (data.Lua.FrequentLoops > 0 || data.Lua.TickHooks > 0)
            yield return "Additional CPU work per tick/frame is possible; on a CPU-limited scene it could reduce headroom or make frame pacing less consistent.";
        if (data.Lua.ActorQueries > 0 || data.Lua.SpawnCalls > 0 || data.Lua.NotifyOnNewObject > 0)
            yield return "Actor enumeration, object notifications or spawning can produce CPU spikes and memory churn, especially in busy areas.";
        if (data.Lua.WhileLoops > 0)
            yield return "An unbounded loop can block script execution if it runs on the game thread.";
        if (textures >= 100)
            yield return "A larger texture working set can pressure VRAM and texture streaming, which may show as hitches or longer loads on a full card.";
        if (vfx > 0 || shader > 0)
            yield return "Additional visual effects, lighting or shader archives can increase GPU load and shader/PSO compilation work.";
        if (ai >= 20)
            yield return "AI/population-related assets or code can increase simulation and navigation workload if the mod actually raises counts.";
        if (config > 0)
            yield return "Config values can change rendering load directly, but the effect depends on the exact value and whether the game loads that file.";
        if (data.NativeFiles.Count > 0)
            yield return "Native plugin behaviour is unknown; it could add overlays, hooks or background work that static analysis cannot see.";
        if (data.Assets.Count > 0 && textures == 0 && vfx == 0 && shader == 0 && ai == 0)
            yield return "No category-specific runtime cost is evidenced by the indexed assets; the archive may be data, audio or cosmetic content.";
    }

    private static IEnumerable<string> PossibleImprovements(ModPerformanceAnalysis a, InspectionData data,
        int textures, int vfx, int shader, int ai)
    {
        if (a.Risk == ModRiskLevel.Low)
        {
            yield return "No specific improvement is promised. Disabling this mod is not expected to change FPS based on the available static evidence.";
            yield break;
        }
        if (a.Risk == ModRiskLevel.Unknown)
        {
            yield return "No improvement can be predicted because the contents were not assessable.";
            yield break;
        }
        yield return "If a controlled before/after capture shows a difference, disabling this mod may remove the measured workload. " +
                     "The result applies to the tested mod set and is not a guaranteed gain.";
        if (data.Lua.FrequentLoops > 0 || data.Lua.TickHooks > 0)
            yield return "Possible reduction in per-frame script CPU work and frame-time spikes, if the script is active and expensive.";
        if (textures >= 100)
            yield return "Possible reduction in VRAM/streaming pressure and related hitches, if VRAM is the limiting factor.";
        if (vfx > 0 || shader > 0)
            yield return "Possible reduction in GPU load and shader compilation work, if the effects are active.";
        if (ai >= 20)
            yield return "Possible reduction in AI/spawn simulation load, if the mod raises NPC or object counts.";
    }

    private static void AddSizeEvidence(ModPerformanceAnalysis a, long size, long sizeP90, int index, int total)
    {
        if (size <= 0) return;
        var context = total > 1 ? $" (entry {index + 1} of {total} in this scan)" : "";
        var percentile = sizeP90 > 0 && size >= sizeP90
            ? "It is in the largest 10% of this scanned mod set; that is a storage/load-time context, not proof of a frame-rate cost. "
            : "It is below the largest 10% of this scanned mod set. ";
        a.Evidence.Add(new ModEvidence
        {
            Title = "File size context",
            Detail = $"{AppPaths.FormatBytes(size)}{context}. {percentile}Size is reported for transparency; it is not used as evidence of FPS loss or stutter " +
                     "and no risk score is added for size alone.",
            Source = "Filesystem",
            Metric = AppPaths.FormatBytes(size),
            Category = ModEvidenceCategory.Size,
            Confidence = ModConfidence.Confirmed
        });
    }

    private static int InspectConfigFiles(IEnumerable<string> files, ModPerformanceAnalysis a)
    {
        var flags = 0;
        foreach (var file in files.Take(12))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length <= 0 || info.Length > 2 * 1024 * 1024) continue;
                var lines = File.ReadLines(file).Take(5000);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
                    var lower = trimmed.ToLowerInvariant();
                    var isHeavy = lower.Contains("r.screenpercentage") && TryGetNumber(lower, out var screen) && screen > 100 ||
                                  lower.Contains("r.supersampling") && TryGetNumber(lower, out var ss) && ss > 1 ||
                                  lower.Contains("r.streaming.poolsize") && TryGetNumber(lower, out var pool) && pool > 4096 ||
                                  lower.Contains("r.shadow") && TryGetNumber(lower, out var shadow) && shadow >= 3 ||
                                  lower.Contains("r.volumetric") || lower.Contains("r.lumen") ||
                                  lower.Contains("sg.") && TryGetNumber(lower, out var sg) && sg >= 4;
                    if (!isHeavy) continue;
                    flags++;
                    if (flags <= 3)
                        a.Evidence.Add(new ModEvidence
                        {
                            Title = "Config setting with possible render cost",
                            Detail = trimmed.Length > 200 ? trimmed[..200] : trimmed,
                            Source = System.IO.Path.GetFileName(file),
                            Category = ModEvidenceCategory.Config,
                            Confidence = ModConfidence.Likely
                        });
                }
            }
            catch { }
        }
        return flags;
    }

    private static bool TryGetNumber(string text, out double value)
    {
        value = 0;
        var match = Regex.Match(text, @"[-+]?\d+(?:\.\d+)?");
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsTextureAsset(PakAssetEntry entry)
    {
        var ext = entry.Extension.ToLowerInvariant();
        if (ext is not (".uasset" or ".ubulk")) return false;
        var name = entry.FileName.ToLowerInvariant();
        var path = entry.Path.ToLowerInvariant();
        return name.StartsWith("t_") || name.Contains("_d.") || name.Contains("_n.") ||
               path.Contains("/textures/") || path.Contains("/texture/");
    }

    private static bool IsVfxAsset(PakAssetEntry entry)
    {
        var ext = entry.Extension.ToLowerInvariant();
        if (ext is not (".uasset" or ".ubulk" or ".ushaderbytecode" or ".umap")) return false;
        var path = entry.Path.ToLowerInvariant();
        return ContainsToken(path, "vfx") || ContainsToken(path, "niagara") || ContainsToken(path, "particle") ||
               path.Contains("postprocess") || path.Contains("post_process") || ContainsToken(path, "lighting") ||
               ContainsToken(path, "fog") || path.Contains("volumetric") || ContainsToken(path, "shader") ||
               ContainsToken(path, "bloom") || ContainsToken(path, "lens") || ContainsToken(path, "sky") ||
               ContainsToken(path, "weather") || ContainsToken(path, "cloud") || ContainsToken(path, "rain") ||
               ContainsToken(path, "snow");
    }

    private static bool IsAiAsset(PakAssetEntry entry)
    {
        var ext = entry.Extension.ToLowerInvariant();
        if (ext is not (".uasset" or ".ubulk" or ".umap")) return false;
        var path = entry.Path.ToLowerInvariant();
        return ContainsToken(path, "ai") || ContainsToken(path, "npc") || ContainsToken(path, "spawn") ||
               ContainsToken(path, "population") || ContainsToken(path, "squad") || ContainsToken(path, "behavior") ||
               ContainsToken(path, "controller") || ContainsToken(path, "patrol") || ContainsToken(path, "agent") ||
               ContainsToken(path, "combat");
    }

    private static bool ContainsToken(string path, string token)
    {
        return Regex.IsMatch(path, $@"(?:^|[/_.\-]){Regex.Escape(token)}(?:$|[/_.\-])", RegexOptions.IgnoreCase);
    }

    private void SetQuarantineEligibility(ModPerformanceAnalysis a, GameInstall game)
    {
        a.QuarantineEligible = true;
        if (a.IsConfigOverride)
        {
            a.QuarantineEligible = false;
            a.QuarantineBlockReason = "Config overrides live in the game config folder. Manage or restore them through Settings & Backups; quarantining them would hide a configuration, not a mod.";
        }
        else if (a.VortexManaged)
        {
            a.QuarantineEligible = false;
            a.QuarantineBlockReason = "This entry is Vortex-managed. Moving it outside Vortex would desynchronise deployment metadata and Vortex may redeploy it. Disable or purge it in Vortex.";
        }
        else if (a.IsMergeOutput)
        {
            a.QuarantineEligible = false;
            a.QuarantineBlockReason = "This is merger output generated from multiple source mods. Disable the source mods in S2MUU/Vortex instead; quarantining the merged pak would not identify a cause.";
        }
        else if (!a.Enabled)
        {
            a.QuarantineEligible = false;
            a.QuarantineBlockReason = "This entry is already disabled or was moved by ZoneTune. Use Installed Mods to enable it first if you want to quarantine a new copy.";
        }
        else if (!IsAllowedModPath(a.Path, game))
        {
            a.QuarantineEligible = false;
            a.QuarantineBlockReason = "The path is not a recognized mod location (~mods, UE4SS Mods, or a top-level ASI plugin). " +
                                      "ZoneTune will not move game files or metadata as part of a mod quarantine.";
        }
        else if (!File.Exists(a.Path) && !Directory.Exists(a.Path))
        {
            a.QuarantineEligible = false;
            a.QuarantineBlockReason = "The path no longer exists.";
        }

        if (a.QuarantineEligible && a.IsUe4ss)
            a.QuarantineNote = "UE4SS safety: the whole mod folder is moved, never its mods.json/mods.txt entry. UE4SS may log a missing-mod error until the folder is restored.";
        else if (a.QuarantineEligible && a.KindText == "ASI plugin")
            a.QuarantineNote = "ASI safety: other plugins may depend on this file. The original is copied and verified before removal, and can be restored.";
        else if (a.QuarantineEligible && a.Risk == ModRiskLevel.Unknown)
            a.QuarantineNote = "Evidence is insufficient. Quarantine is still allowed as a reversible experiment, not because a problem was proven.";
    }

    private static bool IsAllowedModPath(string path, GameInstall game)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
            if (IsUnder(full, game.ModsDir)) return true;
            var ue4ssMods = System.IO.Path.Combine(game.BinariesDir, "ue4ss", "Mods");
            if (IsUnder(full, ue4ssMods)) return true;
            if (File.Exists(full) &&
                full.EndsWith(".asi", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(System.IO.Path.GetDirectoryName(full)?.TrimEnd('\\'),
                    System.IO.Path.GetFullPath(game.BinariesDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
        catch { return false; }
    }

    private static bool IsUnder(string fullPath, string root)
    {
        try
        {
            var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\";
            return (fullPath + "\\").StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private bool IsVortexManaged(ModEntry mod, GameInstall game)
    {
        try
        {
            var dir = Directory.Exists(mod.Path) ? mod.Path : System.IO.Path.GetDirectoryName(mod.Path) ?? "";
            if (dir.Length > 0 && File.Exists(System.IO.Path.Combine(dir, "__folder_managed_by_vortex"))) return true;
            if (File.Exists(mod.Path) && mod.Path.EndsWith(".vortex_backup", StringComparison.OrdinalIgnoreCase)) return true;
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.vortex_backup", SearchOption.TopDirectoryOnly).Any()) return true;
            if (Directory.Exists(game.BinariesDir) &&
                Directory.EnumerateFiles(game.BinariesDir, "vortex.deployment.*", SearchOption.TopDirectoryOnly).Any()) return true;
        }
        catch { }
        return false;
    }

    private static List<string> EnumerateFiles(string path)
    {
        try
        {
            if (File.Exists(path)) return new List<string> { path };
            if (!Directory.Exists(path)) return new List<string>();
            return Directory.EnumerateFiles(path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 6,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                })
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .Take(MaxFilesPerMod)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    private static string DetectVersion(string path, List<string> files, string folderName)
    {
        foreach (var file in files.Where(f =>
                     f.EndsWith("mod.json", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith("info.json", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith("version.txt", StringComparison.OrdinalIgnoreCase) ||
                     f.EndsWith(".version", StringComparison.OrdinalIgnoreCase)).Take(8))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length <= 0 || info.Length > 512 * 1024) continue;
                var text = File.ReadAllText(file);
                if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            var name = prop.Name.ToLowerInvariant();
                            if ((name is "version" or "modversion" or "mod_version" or "versionname" or "displayversion") &&
                                prop.Value.ValueKind == JsonValueKind.String)
                            {
                                var value = prop.Value.GetString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(value)) return NormalizeVersion(value);
                            }
                        }
                    }
                }
                else
                {
                    var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                    if (first != null && Regex.IsMatch(first, @"\d+\.\d+")) return NormalizeVersion(first);
                }
            }
            catch { }
        }

        // Prefer an explicit v/version marker, then the first version-looking token
        // that is not a multiplier such as "x1.25" in "Increased Rewards (x1.25 ...)".
        var explicitMatch = Regex.Match(folderName, @"(?i)\bv(?:ersion)?\s*(\d+(?:\.\d+){1,3})");
        if (explicitMatch.Success) return NormalizeVersion(explicitMatch.Groups[1].Value);
        foreach (Match candidate in VersionRegex.Matches(folderName))
        {
            var index = candidate.Index;
            if (index > 0 && (folderName[index - 1] == 'x' || folderName[index - 1] == 'X')) continue;
            return NormalizeVersion(candidate.Value);
        }
        return "";
    }

    private static string NormalizeVersion(string value)
    {
        value = value.Trim().TrimStart('v', 'V').Trim();
        var match = VersionRegex.Match(value);
        if (!match.Success) return "";
        var version = match.Value;
        var first = version.Split('.')[0];
        if (int.TryParse(first, out var major) && major is >= 1900 and <= 2100) return "";
        return version;
    }

    private static void ReadVersionFromMetadata(string path, List<string> files, InspectionData data)
    {
        if (!string.IsNullOrWhiteSpace(data.Version)) return;
        data.Version = DetectVersion(path, files, System.IO.Path.GetFileName(path.TrimEnd('\\')));
    }

    private static string Fingerprint(ModEntry mod)
    {
        try
        {
            var stamp = File.Exists(mod.Path)
                ? File.GetLastWriteTimeUtc(mod.Path)
                : Directory.Exists(mod.Path) ? Directory.GetLastWriteTimeUtc(mod.Path) : DateTime.MinValue;
            return $"{mod.Path}|{mod.SizeBytes}|{stamp:O}|{mod.Enabled}";
        }
        catch { return mod.Path + "|" + mod.SizeBytes; }
    }

    private static string NormalizePath(string path)
    {
        try { return System.IO.Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant(); }
        catch { return (path ?? "").TrimEnd('\\').ToLowerInvariant(); }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
