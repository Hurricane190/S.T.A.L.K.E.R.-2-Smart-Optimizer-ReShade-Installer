using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Builds a self-contained, reversible optimization package. The ZIP contains merged
/// config files (so it never destroys unrelated keys), a manifest with evidence, an
/// installer that backs up before copying, an uninstaller, and checksums.
/// No third-party binaries or shaders are redistributed.
/// </summary>
public sealed class PackageBuilder
{
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public string Build(OptimizationPlan plan, ScanResult scan, PresetEntry? optionalPreset = null)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var name = $"ZoneTune-Package-{stamp}";
        var work = Path.Combine(Path.GetTempPath(), "ZoneTune", name);
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        var files = new List<string>();
        var configActions = plan.SelectedActions.Where(a => a.Kind is ActionKind.GameSetting or ActionKind.EngineCvar).ToList();

        if (configActions.Count > 0)
        {
            var payload = Path.Combine(work, "payload", "Config", "Windows");
            Directory.CreateDirectory(payload);
            foreach (var group in configActions.GroupBy(a => a.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(group.Key)) continue;
                var ini = File.Exists(group.Key) ? IniDocument.Load(group.Key) : IniDocument.Parse("");
                foreach (var action in group)
                    ini.Set(action.Section, action.Key, action.ProposedValue);
                var dest = Path.Combine(payload, Path.GetFileName(group.Key));
                IniDocument.WriteTextAtomic(dest, ini.ToText(), new UTF8Encoding(false));
                files.Add(dest);
            }
        }

        if (optionalPreset != null && !string.IsNullOrWhiteSpace(optionalPreset.RawPreset))
        {
            var presetDir = Path.Combine(work, "presets");
            Directory.CreateDirectory(presetDir);
            var presetFile = Path.Combine(presetDir, AppPaths.SafeFileName(optionalPreset.Name) + ".ini");
            IniDocument.WriteTextAtomic(presetFile, optionalPreset.RawPreset.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            files.Add(presetFile);
        }

        var manifest = new
        {
            schema = 1,
            product = AppPaths.ProductFullName,
            version = AppPaths.Version,
            createdUtc = DateTime.UtcNow,
            game = new
            {
                root = scan.Game.Root,
                shippingExe = scan.Game.ShippingExePath,
                source = scan.Game.Source.ToString()
            },
            hardware = new
            {
                cpu = scan.Hardware.CpuName,
                gpu = scan.Hardware.GpuName,
                vramMb = scan.Hardware.VramMb,
                ramMb = scan.Hardware.RamMb,
                windows = scan.Hardware.WindowsName,
                resolution = scan.Hardware.PrimaryResolution,
                refreshHz = scan.Hardware.PrimaryRefreshHz,
                hdrEnabled = scan.Hardware.HdrEnabled
            },
            problems = plan.Problems.Select(p => p.ToString()).ToList(),
            summary = plan.Summary,
            warnings = plan.Warnings,
            actions = plan.Actions.Select(a => new
            {
                a.Id, a.Title, a.Description, a.Rationale, a.Evidence,
                problems = a.Problems.Select(p => p.ToString()).ToList(),
                risk = a.Risk.ToString(),
                confidence = a.Confidence.ToString(),
                kind = a.Kind.ToString(),
                a.FilePath, a.Section, a.Key, a.CurrentValue, a.ProposedValue,
                a.EstimatedEffect, a.Selected
            }),
            files = new List<object>(),
            notes = new[]
            {
                "Effects are estimates. No FPS improvement is promised or claimed.",
                "Third-party mods and ReShade shaders are not redistributed in this package.",
                "The installer backs up every target file before copying; uninstall.ps1 restores it."
            }
        };

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(work, file).Replace('\\', '/');
            manifest.files.Add(new { path = rel, sha256 = Sha256(file), bytes = new FileInfo(file).Length });
        }

        IniDocument.WriteTextAtomic(Path.Combine(work, "manifest.json"), JsonSerializer.Serialize(manifest, _json));
        IniDocument.WriteTextAtomic(Path.Combine(work, "README.txt"), BuildReadme(plan, scan, optionalPreset));
        IniDocument.WriteTextAtomic(Path.Combine(work, "install.ps1"), InstallScript);
        IniDocument.WriteTextAtomic(Path.Combine(work, "uninstall.ps1"), UninstallScript);
        IniDocument.WriteTextAtomic(Path.Combine(work, "NOTICE.txt"), NoticeText);

        WriteChecksums(work);
        Directory.CreateDirectory(AppPaths.PackagesDir);
        var zipPath = Path.Combine(AppPaths.PackagesDir, name + ".zip");
        if (File.Exists(zipPath)) zipPath = Path.Combine(AppPaths.PackagesDir, name + "-" + Guid.NewGuid().ToString("N")[..4] + ".zip");
        ZipFile.CreateFromDirectory(work, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        try { Directory.Delete(work, recursive: true); } catch { }
        Log.Info($"Package built: {zipPath} ({new FileInfo(zipPath).Length / 1024.0:0.#} KB)");
        return zipPath;
    }

    private static string BuildReadme(OptimizationPlan plan, ScanResult scan, PresetEntry? preset)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ZONETUNE OPTIMIZATION PACKAGE");
        sb.AppendLine("==============================");
        sb.AppendLine();
        sb.AppendLine($"Created: {plan.CreatedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Game:    {scan.Game.Root}");
        sb.AppendLine($"System:  {scan.Hardware.CpuName}; {scan.Hardware.GpuName}; {scan.Hardware.RamSummary}");
        sb.AppendLine($"Display: {scan.Hardware.PrimaryResolution} @ {scan.Hardware.PrimaryRefreshHz} Hz; HDR {(scan.Hardware.HdrEnabled ? "on" : "off")}");
        sb.AppendLine();
        sb.AppendLine("WHAT THIS PACKAGE DOES");
        sb.AppendLine("----------------------");
        sb.AppendLine("The payload contains complete merged copies of GameUserSettings.ini and/or Engine.ini.");
        sb.AppendLine("install.ps1 copies them over the game's config files AFTER creating a timestamped backup.");
        sb.AppendLine("uninstall.ps1 restores that backup (or removes files that did not exist before).");
        sb.AppendLine("No game archives, mods, shaders or third-party binaries are included.");
        sb.AppendLine();
        sb.AppendLine("SELECTED PROBLEMS");
        sb.AppendLine("-----------------");
        foreach (var p in plan.Problems) sb.AppendLine("  - " + p);
        sb.AppendLine();
        sb.AppendLine("CONFIG CHANGES");
        sb.AppendLine("--------------");
        foreach (var a in plan.SelectedActions)
        {
            sb.AppendLine($"[{(a.Selected ? "x" : " ")}] {a.Title}");
            if (!string.IsNullOrEmpty(a.Key))
                sb.AppendLine($"    {a.Key}: {a.CurrentValue} -> {a.ProposedValue}");
            sb.AppendLine($"    Why: {a.Description}");
            sb.AppendLine($"    Evidence: {a.Evidence}");
            sb.AppendLine($"    Effect: {a.EstimatedEffect}");
            sb.AppendLine($"    Risk: {a.RiskText}; confidence: {a.Confidence}");
            sb.AppendLine();
        }
        if (plan.Recommendations.Any())
        {
            sb.AppendLine("MANUAL RECOMMENDATIONS (not applied by the installer)");
            sb.AppendLine("-----------------------------------------------------");
            foreach (var a in plan.Recommendations)
                sb.AppendLine($"  - {a.Title}: {a.Description}");
            sb.AppendLine();
        }
        sb.AppendLine("IMPORTANT LIMITS");
        sb.AppendLine("----------------");
        foreach (var w in plan.Warnings) sb.AppendLine("  ! " + w);
        sb.AppendLine();
        sb.AppendLine("HOW TO INSTALL");
        sb.AppendLine("--------------");
        sb.AppendLine("1. Exit STALKER 2 and any mod manager deployment.");
        sb.AppendLine("2. Right-click install.ps1 -> Run with PowerShell (or: powershell -ExecutionPolicy Bypass -File .\\install.ps1).");
        sb.AppendLine("3. Launch the game and play the same route for a before/after comparison.");
        sb.AppendLine("4. To revert: run uninstall.ps1. It restores the newest backup this package made.");
        sb.AppendLine();
        sb.AppendLine("HOW TO MEASURE (REQUIRED BEFORE CLAIMING AN IMPROVEMENT)");
        sb.AppendLine("---------------------------------------------------------");
        sb.AppendLine("Use PresentMon (Intel's free frame-time capture tool) or CapFrameX.");
        sb.AppendLine("Capture the same route twice: once with the original config, once after installing.");
        sb.AppendLine("Compare 1% / 0.1% lows and the 99th percentile frame time, not only average FPS.");
        sb.AppendLine("Import the CSV in ZoneTune -> Diagnostic Reports to compute the statistics.");
        sb.AppendLine();
        if (preset != null)
        {
            sb.AppendLine("OPTIONAL RESHADE PRESET");
            sb.AppendLine("-----------------------");
            sb.AppendLine($"The presets folder contains '{preset.Name}' ({preset.License}).");
            sb.AppendLine("It is NOT installed automatically. Use the Preset Studio in ZoneTune, or copy the .ini next to");
            sb.AppendLine("Stalker2-Win64-Shipping.exe and set it in ReShade's overlay.");
            sb.AppendLine($"Compatibility: {preset.StatusText} — {preset.StatusNote}");
            sb.AppendLine();
        }
        sb.AppendLine("SAFETY");
        sb.AppendLine("------");
        sb.AppendLine("- Never delete mods to apply this package.");
        sb.AppendLine("- The installer refuses to run while Stalker2-Win64-Shipping.exe is running.");
        sb.AppendLine("- Backups live next to the package in backup-<timestamp>.");
        sb.AppendLine();
        return sb.ToString();
    }

    private const string InstallScript = """
# ZoneTune package installer (PowerShell 5.1+)
# Backs up target files, refuses to run while the game is open, then copies merged configs.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$payload = Join-Path $root 'payload\Config\Windows'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $root ("backup-" + $stamp)
$configDir = Join-Path $env:LOCALAPPDATA 'Stalker2\Saved\Config\Windows'

if (-not (Test-Path $payload)) { Write-Host 'No config payload in this package. Nothing to install.'; exit 0 }
if (Get-Process -Name 'Stalker2-Win64-Shipping' -ErrorAction SilentlyContinue) {
    Write-Error 'STALKER 2 is running. Exit the game before installing.'
    exit 1
}
if (-not (Test-Path $configDir)) { Write-Error "Config folder not found: $configDir. Launch the game once."; exit 1 }

New-Item -ItemType Directory -Force -Path $backup | Out-Null
$log = @()
Get-ChildItem $payload -File | ForEach-Object {
    $target = Join-Path $configDir $_.Name
    $existed = Test-Path $target
    if ($existed) {
        Copy-Item $target (Join-Path $backup $_.Name) -Force
    }
    Copy-Item $_.FullName $target -Force
    $log += [pscustomobject]@{ name = $_.Name; existed = $existed; sha256 = (Get-FileHash $target -Algorithm SHA256).Hash }
    Write-Host ("Installed " + $_.Name + " -> " + $target)
}
$log | ConvertTo-Json | Set-Content (Join-Path $backup 'backup-manifest.json') -Encoding UTF8
Write-Host ''
Write-Host ("Backup: " + $backup)
Write-Host 'Launch the game and measure before/after. To revert, run uninstall.ps1.'
""";

    private const string UninstallScript = """
# ZoneTune package uninstaller (PowerShell 5.1+)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$configDir = Join-Path $env:LOCALAPPDATA 'Stalker2\Saved\Config\Windows'
$backup = Get-ChildItem $root -Directory -Filter 'backup-*' | Sort-Object Name -Descending | Select-Object -First 1

if (Get-Process -Name 'Stalker2-Win64-Shipping' -ErrorAction SilentlyContinue) {
    Write-Error 'STALKER 2 is running. Exit the game before restoring.'
    exit 1
}
if (-not $backup) { Write-Error 'No backup-* folder found in this package.'; exit 1 }

$manifestPath = Join-Path $backup.FullName 'backup-manifest.json'
if (-not (Test-Path $manifestPath)) { Write-Error 'backup-manifest.json missing; refusing to guess.'; exit 1 }
$entries = Get-Content $manifestPath -Raw | ConvertFrom-Json

foreach ($entry in $entries) {
    $target = Join-Path $configDir $entry.name
    $saved = Join-Path $backup.FullName $entry.name
    if ($entry.existed) {
        if (-not (Test-Path $saved)) { Write-Warning ("Backup missing for " + $entry.name); continue }
        Copy-Item $saved $target -Force
        Write-Host ("Restored " + $entry.name)
    } else {
        if (Test-Path $target) { Remove-Item $target -Force; Write-Host ("Removed created file " + $entry.name) }
    }
}
Write-Host ''
Write-Host 'Restore complete. Re-run ZoneTune detection to confirm the config state.'
""";

    private const string NoticeText = """
ZONETUNE NOTICE
===============

- ZoneTune does not redistribute ReShade, shader files, game assets, or third-party mods.
- ReShade is downloaded from the official reshade.me site and its Authenticode signature
  and certificate thumbprint are verified before execution.
- Shader files are downloaded from the official crosire/reshade-shaders repository on
  GitHub; their individual licenses remain in effect.
- Presets authored by ZoneTune are released under CC0-1.0. Third-party preset metadata
  links to the author's page and is not bundled.
- Performance effects are estimates. Measure on your own system before drawing conclusions.
- All file changes are recorded in a change set and can be restored from Settings & Backups.
""";

    private static void WriteChecksums(string root)
    {
        var sb = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(f => !Path.GetFileName(f).Equals("checksums.sha256", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            sb.AppendLine($"{Sha256(file)}  {rel}");
        }
        IniDocument.WriteTextAtomic(Path.Combine(root, "checksums.sha256"), sb.ToString(), new UTF8Encoding(false));
    }

    private static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}
