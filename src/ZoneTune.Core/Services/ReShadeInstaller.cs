using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Installs the official, signed ReShade build (headless setup), only the shader files
/// a preset actually needs, and the preset itself. Every touched file is recorded in a
/// change set first. "Verified" here means the preset parsed and its shader files are
/// present — never that it was visually validated in-game.
/// </summary>
public sealed class ReShadeInstaller
{
    private readonly PresetCatalog _catalog = new();
    private readonly ReShadeService _detector = new();

    public async Task<ReShadeInstallResult> InstallAsync(
        GameInstall game,
        HardwareInfo hardware,
        PresetEntry preset,
        BackupService backups,
        AppSettings settings,
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default,
        bool ignoreRunningGame = false)
    {
        var result = new ReShadeInstallResult { PresetName = preset.Name };
        if (!game.IsValid) return Fail(result, "Game folder is not valid.");
        if (!ignoreRunningGame && ReShadeService.IsGameRunning()) return Fail(result, "Exit STALKER 2 before installing ReShade (DLLs are locked while the game runs).");
        if (!hardware.PrimaryResolution.Contains('x') && hardware.PrimaryResolution != "Unknown")
            Log.Warn("Unexpected display resolution: " + hardware.PrimaryResolution);

        if (preset.InstallMode == InstallMode.ExternalOnly)
        {
            result.Errors.Add("This preset is distributed on the author's page. Open the source page, download it manually, then use " +
                              "'Import preset file' in the Preset Studio. ZoneTune does not bypass author distribution.");
            return result;
        }

        // 1. Materialise preset content.
        progress?.Report(new OperationProgress("Preparing preset", preset.Name, 5));
        string presetText;
        if (!string.IsNullOrWhiteSpace(preset.RawPreset))
        {
            presetText = preset.RawPreset;
        }
        else if (preset.LocalPath != null && File.Exists(preset.LocalPath))
        {
            presetText = File.ReadAllText(preset.LocalPath);
        }
        else if (!string.IsNullOrWhiteSpace(preset.DownloadUrl))
        {
            if (!settings.AllowNetwork) return Fail(result, "Network access is disabled in Settings.");
            try
            {
                progress?.Report(new OperationProgress("Downloading preset", "From licensed public source", 10));
                presetText = await HttpService.GetStringAsync(preset.DownloadUrl, 1024 * 1024, ct);
                if (string.IsNullOrWhiteSpace(preset.RawPreset)) preset.RawPreset = presetText;
            }
            catch (Exception ex) { return Fail(result, "Preset download failed: " + ex.Message); }
        }
        else return Fail(result, "Preset has no installable content. Import a local file or open the author page.");

        if (PresetCatalog.ParseTechniques(presetText).Count == 0)
            return Fail(result, "The selected file is not a ReShade preset (no Techniques= line).");

        // 2. Snapshot everything we may modify before touching it.
        progress?.Report(new OperationProgress("Backing up", "Recording original state", 15));
        var shaderDir = Path.Combine(game.BinariesDir, "reshade-shaders", "Shaders");
        var existingProxy = ReShadeService.KnownWrapperNames
            .Select(n => Path.Combine(game.BinariesDir, n)).Where(File.Exists).ToList();
        var presetFileName = AppPaths.SafeFileName(preset.Name) + ".ini";
        var presetDest = Path.Combine(game.BinariesDir, presetFileName);
        var cs = backups.CreateChangeSet("reshade", $"Install ReShade preset: {preset.Name}", game.Root,
            existingProxy.Concat(new[]
            {
                Path.Combine(game.BinariesDir, "ReShade.ini"),
                Path.Combine(game.BinariesDir, "ReShade.log"),
                Path.Combine(game.BinariesDir, "ReShadePreset.ini"),
                presetDest
            }));
        result.ChangeSetId = cs.Id;

        try
        {
            var before = _detector.Detect(game, hardware);

            // 3. Install ReShade itself only when missing.
            if (!before.Installed)
            {
                if (!settings.AllowNetwork) return Fail(result, "ReShade is not installed and network access is disabled.");
                progress?.Report(new OperationProgress("Downloading ReShade", "Official reshade.me installer", 20));
                var setup = await GetSetupAsync(progress, settings, ct);
                if (!VerifySetup(setup, settings)) return Fail(result, "ReShade setup failed signature verification. Installation aborted.");

                progress?.Report(new OperationProgress("Installing ReShade", "Headless DXGI install (64-bit)", 35));
                var (ok, output) = await RunSetupAsync(setup, game, ct);
                result.Messages.Add(output.Length > 400 ? output[..400] : output);
                if (!ok)
                {
                    result.Errors.Add("ReShade's headless setup did not report success. No preset was installed.");
                    result.Errors.Add("You can run the official setup tool interactively, then retry: " + setup);
                    return result;
                }

                var afterInstall = _detector.Detect(game, hardware);
                if (!afterInstall.Installed)
                {
                    result.Errors.Add("The setup tool exited but no ReShade module was detected. Installation was not verified.");
                    return result;
                }
                result.DownloadedReShade = true;
                result.Version = afterInstall.Version;
                result.Messages.Add($"ReShade {afterInstall.Version} installed via {afterInstall.ProxyDll}.");
                backups.RecordCreatedFile(cs, afterInstall.ProxyPath, "ReShade proxy DLL installed by official setup");
                if (File.Exists(Path.Combine(game.BinariesDir, "ReShade.ini")))
                    backups.RecordAfter(cs, Path.Combine(game.BinariesDir, "ReShade.ini"));
            }
            else
            {
                result.Version = before.Version;
                result.Messages.Add($"ReShade {before.Version} already installed; keeping the existing module and shaders.");
            }

            // 4. Extract only the shader files the preset needs (+ include closure).
            progress?.Report(new OperationProgress("Installing shaders", "Required effects only", 55));
            var required = PresetCatalog.ParseTechniques(presetText).Select(t => t.file).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var (added, missingShaders, shaderWarnings) = await InstallShadersAsync(required, shaderDir, cs, backups, progress, settings, ct);
            result.Warnings.AddRange(shaderWarnings);
            if (added.Count > 0) result.Messages.Add($"Installed {added.Count} shader file(s) into {shaderDir}.");
            if (missingShaders.Count > 0)
                result.Warnings.Add("Some shaders are not in the official archive: " + string.Join(", ", missingShaders) +
                                    ". The preset may require the author's shader package.");

            // 5. Write preset (UTF-8, no BOM, CRLF).
            progress?.Report(new OperationProgress("Installing preset", presetFileName, 75));
            var normalized = presetText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            IniDocument.WriteTextAtomic(presetDest, normalized, new System.Text.UTF8Encoding(false));
            if (cs.Items.All(i => !i.Path.Equals(presetDest, StringComparison.OrdinalIgnoreCase)))
                backups.RecordCreatedFile(cs, presetDest, "Preset installed by ZoneTune");
            else
                backups.RecordAfter(cs, presetDest);
            result.Messages.Add($"Preset written to {presetDest}.");

            // 6. Point ReShade at the preset and ensure shader search paths exist.
            progress?.Report(new OperationProgress("Updating ReShade.ini", "PresetPath + shader paths", 85));
            var iniPath = Path.Combine(game.BinariesDir, "ReShade.ini");
            var ini = File.Exists(iniPath) ? IniDocument.Load(iniPath) : IniDocument.Parse("[GENERAL]\r\n");
            ini.Set("GENERAL", "PresetPath", ".\\" + presetFileName);
            var effectPaths = ini.Get("GENERAL", "EffectSearchPaths") ?? "";
            if (!effectPaths.Contains("reshade-shaders", StringComparison.OrdinalIgnoreCase))
            {
                var value = string.IsNullOrWhiteSpace(effectPaths)
                    ? @".\reshade-shaders\Shaders\**"
                    : effectPaths.TrimEnd(',', ';') + @",.\reshade-shaders\Shaders\**";
                ini.Set("GENERAL", "EffectSearchPaths", value);
            }
            var texturePaths = ini.Get("GENERAL", "TextureSearchPaths") ?? "";
            if (string.IsNullOrWhiteSpace(texturePaths))
                ini.Set("GENERAL", "TextureSearchPaths", @".\reshade-shaders\Textures\**");
            ini.Save();
            backups.RecordAfter(cs, iniPath);

            // 7. Structural verification against the installed files.
            progress?.Report(new OperationProgress("Validating", "Parsing techniques and shader paths", 93));
            var after = _detector.Detect(game, hardware);
            var validation = _catalog.Validate(preset, new[] { shaderDir }, after.Version);
            result.Version = after.Version;
            result.Warnings.AddRange(validation.Warnings);
            if (after.PresetPath.EndsWith(presetFileName, StringComparison.OrdinalIgnoreCase))
                result.Messages.Add("ReShade.ini PresetPath points to the new preset.");
            else
                result.Warnings.Add("ReShade.ini PresetPath could not be confirmed after writing.");

            if (validation.Missing.Count == 0)
            {
                result.Success = true;
                result.Verification = $"Structurally verified: {validation.Shaders.Count} technique(s) parsed; all referenced shader files are present " +
                                      $"in the configured shader folder. This is not a visual test — launch the game, press Home, and check the effect list.";
            }
            else
            {
                result.Success = true;
                result.Verification = $"Installed with warnings: {validation.Missing.Count} referenced shader file(s) are missing. " +
                                      "The preset may load partially. Install the author's shader package or choose another preset.";
            }
            result.Messages.Add("Backup/change set: " + cs.Id + ". Use 'Uninstall & restore' in the Preset Studio to roll back.");
            result.Messages.Add("Compatibility status is structural only. Frame cost has not been measured on this machine.");
            backups.Save(cs);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("ReShade installation failed; restoring backups.", ex);
            result.Errors.Add(ex.Message);
            try
            {
                var restore = backups.Restore(cs.Id);
                if (restore.Success) result.Messages.Add("Automatic rollback completed.");
                else result.Errors.AddRange(restore.Errors);
            }
            catch (Exception rex) { result.Errors.Add("Rollback failed: " + rex.Message); }
            return result;
        }
    }

    private async Task<string> GetSetupAsync(IProgress<OperationProgress>? progress, AppSettings settings, CancellationToken ct)
    {
        var dir = AppPaths.DownloadsDir;
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, Path.GetFileName(PresetCatalog.ReShadeSetupUrl));
        if (File.Exists(dest))
        {
            var sig = HttpService.VerifyAuthenticode(dest, requireReShadeThumbprint: true);
            if (sig.IsValid)
            {
                Log.Info("Using cached ReShade setup (signature still valid).");
                return dest;
            }
            Log.Warn("Cached ReShade setup failed signature check; re-downloading.");
            File.Delete(dest);
        }
        await HttpService.DownloadAsync(PresetCatalog.ReShadeSetupUrl, dest, progress, 300L * 1024 * 1024, ct);
        return dest;
    }

    private static bool VerifySetup(string path, AppSettings settings)
    {
        if (!settings.VerifyDownloads) return true;
        var hash = BackupService.HashFile(path);
        if (!hash.Equals(HttpService.ReShadeSetupSha256, StringComparison.OrdinalIgnoreCase))
        {
            Log.Error($"ReShade setup hash mismatch. Expected {HttpService.ReShadeSetupSha256}, got {hash}. Refusing to execute.");
            return false;
        }
        var sig = HttpService.VerifyAuthenticode(path, requireReShadeThumbprint: true);
        if (!sig.IsValid)
        {
            Log.Error($"ReShade setup signature invalid: {sig.Error}");
            return false;
        }
        if (!string.IsNullOrWhiteSpace(sig.Warning)) Log.Warn(sig.Warning);
        return true;
    }

    private static async Task<(bool ok, string output)> RunSetupAsync(string setup, GameInstall game, CancellationToken ct)
    {
        var args = new List<string>
        {
            game.ShippingExePath,
            "--headless",
            "--api", "dxgi"
        };
        return await RunSetupArgsAsync(setup, game, args, ct);
    }

    private static async Task<(bool ok, string output)> RunSetupArgsAsync(string setup, GameInstall game, List<string> args, CancellationToken ct)
    {
        var output = new System.Text.StringBuilder();
        var needsElevation = !CanWrite(game.BinariesDir);

        if (needsElevation)
        {
            // The official setup may need to elevate for protected installs. UseShellExecute
            // is required for UAC; output is not capturable in that mode.
            var psi = new ProcessStartInfo
            {
                FileName = setup,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = game.BinariesDir
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return (false, "Could not start the setup tool.");
                await p.WaitForExitAsync(ct);
                return (p.ExitCode == 0, $"Setup exited with code {p.ExitCode} (elevated).");
            }
            catch (Exception ex)
            {
                return (false, "Elevated setup failed: " + ex.Message);
            }
        }

        var info = new ProcessStartInfo
        {
            FileName = setup,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = game.BinariesDir
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        try
        {
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            if (!process.Start()) return (false, "Could not start the setup tool.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(8));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "ReShade setup timed out.");
            }
            var text = output.ToString();
            Log.Info($"ReShade setup exit={process.ExitCode}: {text}");
            if (process.ExitCode == 740)
                return await RunElevatedFallback(setup, game, args);
            return (process.ExitCode == 0, text);
        }
        catch (Exception ex)
        {
            Log.Error("Could not run ReShade setup", ex);
            return (false, ex.Message);
        }
    }

    private static async Task<(bool ok, string output)> RunElevatedFallback(string setup, GameInstall game, List<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = setup,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = game.BinariesDir
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (false, "Could not relaunch elevated.");
            await p.WaitForExitAsync();
            return (p.ExitCode == 0, "Elevated setup exit code " + p.ExitCode);
        }
        catch (Exception ex) { return (false, "Elevation cancelled or failed: " + ex.Message); }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".zonetune-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "test");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private async Task<(List<string> added, List<string> missing, List<string> warnings)> InstallShadersAsync(
        List<string> required, string shaderDir, ChangeSet cs, BackupService backups,
        IProgress<OperationProgress>? progress, AppSettings settings, CancellationToken ct)
    {
        var added = new List<string>();
        var missing = new List<string>();
        var warnings = new List<string>();
        if (required.Count == 0) return (added, missing, warnings);
        if (!settings.AllowNetwork)
        {
            warnings.Add("Network disabled; shader files were not downloaded.");
            return (added, missing, warnings);
        }

        var zipPath = Path.Combine(AppPaths.DownloadsDir, "reshade-shaders-legacy.zip");
        try
        {
            if (!File.Exists(zipPath))
            {
                progress?.Report(new OperationProgress("Downloading shaders", "Official crosire/reshade-shaders", 60));
                await HttpService.DownloadAsync(PresetCatalog.ShaderArchiveUrl, zipPath, progress, 128L * 1024 * 1024, ct);
            }

            using var archive = ZipFile.OpenRead(zipPath);
            ZipArchive? support = null;
            try
            {
                support = await OpenSupportArchiveAsync(progress, ct);
                Directory.CreateDirectory(shaderDir);
                var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var queue = new Queue<string>(required);

                while (queue.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var fileName = queue.Dequeue();
                    if (!extracted.Add(fileName)) continue;

                    var entry = FindShaderEntry(archive, fileName);
                    if (entry == null && support != null &&
                        (fileName.EndsWith(".fxh", StringComparison.OrdinalIgnoreCase) ||
                         fileName.EndsWith(".fx", StringComparison.OrdinalIgnoreCase)))
                        entry = FindShaderEntry(support, fileName);
                    if (entry == null)
                    {
                        // Might already exist in the user's shader folder.
                        if (File.Exists(Path.Combine(shaderDir, fileName))) continue;
                        missing.Add(fileName);
                        continue;
                    }

                    var dest = Path.Combine(shaderDir, fileName);
                    if (!File.Exists(dest))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        entry.ExtractToFile(dest, overwrite: false);
                        added.Add(dest);
                        backups.RecordCreatedFile(cs, dest, "Shader installed by ZoneTune");
                    }

                    // Resolve #include dependencies from the official archives.
                    var content = File.ReadAllText(dest);
                    foreach (Match m in Regex.Matches(content, @"#include\s+""([^""]+)"""))
                    {
                        var include = m.Groups[1].Value.Replace('/', '\\');
                        var includeName = Path.GetFileName(include);
                        if (!extracted.Contains(includeName)) queue.Enqueue(includeName);
                    }
                }
            }
            finally { support?.Dispose(); }

            // Texture search path is created if absent; no textures are downloaded for
            // built-in presets. Third-party presets must bring their own licensed textures.
            var textureDir = Path.Combine(Path.GetDirectoryName(shaderDir)!, "Textures");
            Directory.CreateDirectory(textureDir);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("Shader installation failed", ex);
            warnings.Add("Shader download/extract failed: " + ex.Message);
        }
        return (added, missing, warnings);
    }

    private async Task<ZipArchive?> OpenSupportArchiveAsync(IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        try
        {
            var path = Path.Combine(AppPaths.DownloadsDir, "reshade-shaders-slim.zip");
            if (!File.Exists(path))
            {
                progress?.Report(new OperationProgress("Downloading shader headers", "Official ReShade.fxh / ReShadeUI.fxh", 62));
                await HttpService.DownloadAsync(PresetCatalog.ShaderSupportArchiveUrl, path, progress, 64L * 1024 * 1024, ct);
            }
            return ZipFile.OpenRead(path);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open support shader archive: " + ex.Message);
            return null;
        }
    }

    private static ZipArchiveEntry? FindShaderEntry(ZipArchive archive, string fileName) =>
        archive.Entries.FirstOrDefault(e =>
        {
            var path = e.FullName.Replace('\\', '/');
            if (!path.Contains("/Shaders/", StringComparison.OrdinalIgnoreCase)) return false;
            return path.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase);
        });

    public async Task<ReShadeInstallResult> RemoveReShadeAsync(
        GameInstall game, BackupService backups, AppSettings settings,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default, bool ignoreRunningGame = false)
    {
        var result = new ReShadeInstallResult { PresetName = "", Verification = "" };
        var info = _detector.Detect(game, null);
        if (!info.Installed)
        {
            result.Success = true;
            result.Messages.Add("ReShade is not installed; nothing to remove.");
            return result;
        }
        if (!ignoreRunningGame && ReShadeService.IsGameRunning()) return Fail(result, "Exit STALKER 2 before removing ReShade.");
        if (!settings.AllowNetwork)
            return Fail(result, "ReShade removal uses the official setup tool; enable network access in Settings.");

        progress?.Report(new OperationProgress("Backing up ReShade", "Proxy DLL, config and active preset", 10));
        var cs = backups.CreateChangeSet("reshade-remove", "Remove ReShade (official setup uninstall)", game.Root,
            new[]
            {
                info.ProxyPath,
                Path.Combine(game.BinariesDir, "ReShade.ini"),
                Path.Combine(game.BinariesDir, "ReShade.log"),
                Path.Combine(game.BinariesDir, "ReShadePreset.ini"),
                info.PresetPath
            }.Where(p => !string.IsNullOrWhiteSpace(p)));
        result.ChangeSetId = cs.Id;

        try
        {
            progress?.Report(new OperationProgress("Downloading ReShade setup", "Official reshade.me installer", 30));
            var setup = await GetSetupAsync(progress, settings, ct);
            if (!VerifySetup(setup, settings)) return Fail(result, "ReShade setup verification failed. Removal aborted.");

            progress?.Report(new OperationProgress("Removing ReShade", "Official headless uninstall", 60));
            var args = new List<string> { game.ShippingExePath, "--headless", "--state", "uninstall" };
            var (ok, output) = await RunSetupArgsAsync(setup, game, args, ct);
            if (output.Length > 0) result.Messages.Add(output.Length > 500 ? output[..500] : output);
            var after = _detector.Detect(game, null);
            if (after.Installed)
            {
                result.Success = false;
                result.Errors.Add("The official uninstaller did not remove the ReShade module. Nothing else was deleted; your backup is intact.");
                result.Messages.Add("Restore the change set if the game no longer launches.");
                return result;
            }
            result.Success = true;
            result.Messages.Add("ReShade module removed with the official uninstaller.");
            result.Messages.Add("The preset and shader files were left in place so you can reinstall without re-downloading. " +
                                "Restore change set " + cs.Id + " to bring the module back.");
            result.Verification = "Removal verified: no ReShade proxy module remains in the game folder.";
            backups.Save(cs);
            Log.Info("ReShade removed: " + cs.Id);
            return result;
        }
        catch (Exception ex)
        {
            Log.Error("ReShade removal failed", ex);
            result.Errors.Add(ex.Message);
            return result;
        }
    }

    public (bool Success, List<string> Messages, List<string> Errors) Uninstall(GameInstall game, BackupService backups, string? changeSetId = null, bool ignoreRunningGame = false)
    {
        var messages = new List<string>();
        var errors = new List<string>();
        if (!ignoreRunningGame && ReShadeService.IsGameRunning())
            return (false, messages, new List<string> { "Exit STALKER 2 before uninstalling ReShade." });

        var cs = changeSetId != null
            ? backups.Load(changeSetId)
            : backups.List().FirstOrDefault(c => c.Kind.Equals("reshade", StringComparison.OrdinalIgnoreCase) &&
                                                 !c.Restored &&
                                                 c.GameRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase));
        if (cs == null)
            return (false, messages, new List<string> { "No ReShade installation change set was found. Refusing to delete files without a backup manifest." });

        var restore = backups.Restore(cs.Id);
        messages.AddRange(restore.Messages);
        errors.AddRange(restore.Errors);

        // Only remove directories that ZoneTune created and that are now empty.
        foreach (var dir in new[] { Path.Combine(game.BinariesDir, "reshade-shaders") })
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir, recursive: false);
            }
            catch { }
        }

        messages.Add("Restored from change set " + cs.Id + " (originals or prior state).");
        var info = _detector.Detect(game, null);
        messages.Add(info.Installed
            ? $"ReShade still detected ({info.Version}) because it existed before this change set or another proxy remains."
            : "No ReShade module remains in the game folder.");
        return (errors.Count == 0, messages, errors);
    }

    private static ReShadeInstallResult Fail(ReShadeInstallResult result, string error)
    {
        result.Success = false;
        result.Errors.Add(error);
        Log.Warn("ReShade install aborted: " + error);
        return result;
    }
}
