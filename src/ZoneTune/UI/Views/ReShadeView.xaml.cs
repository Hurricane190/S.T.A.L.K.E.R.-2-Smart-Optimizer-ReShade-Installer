using System.Windows.Controls.Primitives;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ZoneTune.Core;
using ZoneTune.Core.Models;
using ZoneTune.Core.Services;

namespace ZoneTune.UI.Views;

public partial class ReShadeView : UserControl, IRefreshable
{
    private readonly MainWindow _main;
    private readonly List<PresetEntry> _github = new();
    private readonly List<PresetEntry> _local = new();
    private List<PresetEntry> _all = new();
    private bool _ready;
    private readonly Dictionary<string, PresetValidation> _validationCache = new();

    public ReShadeView(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _ready = true;
    }

    public Task RefreshAsync()
    {
        _all = BuildCatalog();
        RenderStatus();
        RenderCatalog();
        return Task.CompletedTask;
    }

    private List<PresetEntry> BuildCatalog()
    {
        var list = _main.Catalog.GetBuiltInPresets(_main.Scan?.Hardware);
        list.AddRange(_main.Catalog.GetThirdPartyCatalog(_main.Scan?.Hardware));
        list.AddRange(_github);
        list.AddRange(_local);
        return list;
    }

    private void RenderStatus()
    {
        var scan = _main.Scan;
        StatusHost.Content = null;
        if (scan == null)
        {
            StatusHost.Content = UiKit.Empty(UiKit.Icon("search"), "Scan required", "ReShade detection needs a valid game folder and a system scan.",
                UiKit.Button("Run scan", (_, _) => _ = _main.RunScanAsync(), primary: true));
            return;
        }

        var root = new StackPanel();

        var stats = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 12) };
        stats.Children.Add(Stat("GPU", scan.Hardware.GpuName, $"{HardwareInfo.FormatGb(scan.Hardware.VramMb)} VRAM", UiKit.Dim));
        stats.Children.Add(Stat("DISPLAY", $"{scan.Hardware.PrimaryResolution} · {scan.Hardware.PrimaryRefreshHz} Hz",
            scan.Hardware.HdrEnabled ? "HDR ON — ReShade risk" : scan.Hardware.AnyHdr ? "HDR capable, currently off" : "SDR", scan.Hardware.HdrEnabled ? UiKit.Amber : UiKit.Dim));
        stats.Children.Add(Stat("RESHADE", scan.ReShade.Installed ? scan.ReShade.Version : "Not installed",
            scan.ReShade.Installed ? scan.ReShade.ProxyDll : "optional", scan.ReShade.Installed ? UiKit.Green : UiKit.Dim));
        stats.Children.Add(Stat("GAME", scan.Game.IsValid ? "Detected" : "Missing",
            scan.Game.IsValid ? scan.Game.Source.ToString() : "choose folder in Settings", scan.Game.IsValid ? UiKit.Green : UiKit.Red));
        root.Children.Add(stats);

        var status = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(UiKit.Text(scan.ReShade.Installed ? $"ReShade {scan.ReShade.Version} detected" : "ReShade is not installed", "H2"));
        left.Children.Add(UiKit.Text(scan.ReShade.Installed
            ? $"Active through {scan.ReShade.ProxyDll}. Preset: {scan.ReShade.PresetName} · {scan.ReShade.PresetTechniques.Count} techniques · {scan.ReShade.ShaderCount} shader files."
            : "The ReShade Manager can install the official signed build and only the shader files a preset needs. It is optional.", "BodyDim").WithMargin(0, 4, 0, 0));
        header.Children.Add(left);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        if (scan.ReShade.Installed)
        {
            var cs = FindReShadeChangeSet();
            if (cs != null)
                buttons.Children.Add(UiKit.Button("Uninstall & restore", (_, _) => _ = UninstallAsync(cs.Id), danger: true, icon: UiKit.Icon("undo")));
            buttons.Children.Add(UiKit.Button("Remove ReShade", (_, _) => _ = RemoveReShadeAsync(), danger: true, icon: UiKit.Icon("delete")));
            buttons.Children.Add(UiKit.Button("Install / repair ReShade", (_, _) => _ = InstallPresetAsync(_all.First(p => p.InstallMode == InstallMode.Bundled)), icon: UiKit.Icon("download")));
        }
        else
        {
            var removed = FindReShadeRemovalChangeSet();
            buttons.Children.Add(UiKit.Button("Install ReShade + starter preset", (_, _) => _ = InstallPresetAsync(_all.First(p => p.InstallMode == InstallMode.Bundled)), primary: true, icon: UiKit.Icon("download")));
            if (removed != null)
                buttons.Children.Add(UiKit.Button("Restore removed ReShade", (_, _) => _ = UninstallAsync(removed.Id), icon: UiKit.Icon("undo")));
        }
        Grid.SetColumn(buttons, 1);
        header.Children.Add(buttons);
        status.Children.Add(header);

        var badges = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (scan.ReShade.Installed)
        {
            badges.Children.Add(UiKit.Badge(scan.ReShade.EffectErrors > 0 ? $"{scan.ReShade.EffectErrors} LOG ERRORS" : "NO LOG ERRORS",
                scan.ReShade.EffectErrors > 0 ? Severity.Warning : Severity.Notice));
            badges.Children.Add(UiKit.Chip($"{scan.ReShade.PresetCount} presets", UiKit.Dim).WithMargin(7, 0, 0, 0));
            if (scan.ReShade.ShaderReferences.Any(s => !s.Present))
                badges.Children.Add(UiKit.Badge("MISSING SHADERS", Severity.Warning).WithMargin(7, 0, 0, 0));
            if (scan.ReShade.HdrConflict)
                badges.Children.Add(UiKit.Badge("HDR ACTIVE", Severity.Warning).WithMargin(7, 0, 0, 0));
        }
        if (scan.ReShade.ProxyConflicts.Count > 0)
            badges.Children.Add(UiKit.Badge("PROXY CONFLICT", Severity.Warning).WithMargin(7, 0, 0, 0));
        status.Children.Add(badges);

        if (scan.ReShade.Errors.Count > 0)
        {
            var err = new Border
            {
                Background = UiKit.Brush("#1A1211"),
                BorderBrush = UiKit.Brush("#3A2421"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 10, 0, 0)
            };
            var errStack = new StackPanel();
            errStack.Children.Add(UiKit.Text("Recent ReShade log errors (read-only)", size: 10, color: UiKit.Red, weight: FontWeights.Bold));
            foreach (var line in scan.ReShade.Errors.Take(3))
                errStack.Children.Add(UiKit.Text(line, size: 10, color: UiKit.Dim).WithMargin(0, 3, 0, 0));
            err.Child = errStack;
            status.Children.Add(err);
        }

        var card = UiKit.Card(status, 18);
        root.Children.Add(card);
        StatusHost.Content = root;

        NoticePanel.Children.Clear();
        if (scan.ReShade.HdrConflict)
            NoticePanel.Children.Add(Notice("HDR + ReShade", "Disable in-game HDR for presets; most are authored for SDR.", UiKit.Amber));
        if (scan.ReShade.ProxyConflicts.Count > 0)
            NoticePanel.Children.Add(Notice("Proxy DLL conflict", scan.ReShade.ProxyConflicts.First(), UiKit.Red));
        if (scan.ReShade.ShaderDuplicates.Count > 0)
            NoticePanel.Children.Add(Notice("Duplicate shader names", scan.ReShade.ShaderDuplicates.First(), UiKit.Amber));
    }

    private static Border Stat(string label, string value, string sub, Brush color)
    {
        var stack = new StackPanel();
        stack.Children.Add(UiKit.Text(label, "Overline"));
        stack.Children.Add(UiKit.Text(value, size: 13, weight: FontWeights.SemiBold, color: color).WithMargin(0, 4, 0, 2));
        stack.Children.Add(UiKit.Text(sub, size: 10, color: UiKit.Dim));
        var border = UiKit.Card(stack, 12, flat: true);
        border.Margin = new Thickness(0, 0, 8, 0);
        return border;
    }

    private static Border Notice(string title, string detail, Brush color)
    {
        var stack = new StackPanel();
        stack.Children.Add(UiKit.Text(title, size: 10.5, weight: FontWeights.Bold, color: color));
        stack.Children.Add(UiKit.Text(detail, size: 10, color: UiKit.Dim).WithMargin(0, 2, 0, 0));
        var border = new Border
        {
            Background = UiKit.Brush("#121811"),
            BorderBrush = UiKit.Brush("#2A362B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(11, 7, 11, 7),
            Margin = new Thickness(0, 0, 8, 6),
            MaxWidth = 360,
            Child = stack
        };
        return border;
    }

    private ChangeSet? FindReShadeChangeSet()
    {
        if (_main.Scan == null) return null;
        return _main.GetBackups().List().FirstOrDefault(c =>
            c.Kind.StartsWith("reshade", StringComparison.OrdinalIgnoreCase) &&
            !c.Restored &&
            c.GameRoot.Equals(_main.Scan.Game.Root, StringComparison.OrdinalIgnoreCase));
    }

    private ChangeSet? FindReShadeRemovalChangeSet()
    {
        if (_main.Scan == null) return null;
        return _main.GetBackups().List().FirstOrDefault(c =>
            c.Kind.Equals("reshade-remove", StringComparison.OrdinalIgnoreCase) &&
            !c.Restored &&
            c.GameRoot.Equals(_main.Scan.Game.Root, StringComparison.OrdinalIgnoreCase));
    }

    private static string CostClass(string tier)
    {
        var t = (tier ?? "").ToLowerInvariant();
        if (t.Contains("low")) return "low";
        if (t.Contains("high")) return "high";
        return "medium";
    }

    private List<string> GetShaderRoots()
    {
        var roots = new List<string>();
        var scan = _main.Scan;
        if (scan == null) return roots;
        if (!string.IsNullOrWhiteSpace(scan.ReShade.ShadersDir) && Directory.Exists(scan.ReShade.ShadersDir))
            roots.Add(scan.ReShade.ShadersDir);
        var fallback = Path.Combine(scan.Game.BinariesDir, "reshade-shaders", "Shaders");
        if (Directory.Exists(fallback)) roots.Add(fallback);
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void RenderCatalog()
    {
        CatalogPanel.Children.Clear();
        _validationCache.Clear();
        var roots = GetShaderRoots();
        var query = SearchBox.Text.Trim();
        var filter = (FilterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All performance levels";
        var filtered = _all.Where(p =>
        {
            if (filter == "High performance · low detail" && CostClass(p.CostTier) != "low") return false;
            if (filter == "Balanced" && CostClass(p.CostTier) != "medium") return false;
            if (filter == "Ultra detail · high cost" && CostClass(p.CostTier) != "high") return false;
            if (filter == "Bundled" && p.InstallMode != InstallMode.Bundled) return false;
            if (filter == "Author pages" && p.InstallMode != InstallMode.ExternalOnly) return false;
            if (filter == "Direct download" && p.InstallMode is not (InstallMode.DirectDownload or InstallMode.LocalFile)) return false;
            if (query.Length == 0) return true;
            return p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   p.Author.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   p.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   p.StyleTags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase));
        }).OrderBy(p => p.InstallMode == InstallMode.Bundled ? 0 : 1)
          .ThenBy(p => CostClass(p.CostTier) == "low" ? 0 : CostClass(p.CostTier) == "medium" ? 1 : 2)
          .ThenBy(p => p.Name).ToList();

        CatalogCount.Text = $"{filtered.Count} of {_all.Count}";
        CatalogSubtitle.Text = filter switch
        {
            "High performance · low detail" => "Smallest effect stacks and bundled lightweight grades. Best starting point when FPS matters more than filmic grading.",
            "Balanced" => "Moderate effect stacks — a compromise between atmosphere and frame cost. Costs are author estimates unless marked otherwise.",
            "Ultra detail · high cost" => "Heavier grades (film emulation, bloom, multi-pass colour). Expect a measurable cost; test on your own GPU.",
            _ => "Bundled presets are authored by ZoneTune; third-party entries link to the author's page. Structural checks are local."
        };
        if (filtered.Count == 0)
        {
            CatalogPanel.Children.Add(UiKit.Empty(UiKit.Icon("search"), "No presets match", "Clear the search or choose another filter. Public-source search only returns repositories with a permissive license.", null));
            return;
        }

        foreach (var preset in filtered)
            CatalogPanel.Children.Add(BuildPresetCard(preset, filtered, roots));
    }

    private PresetValidation GetValidation(PresetEntry preset, List<string> roots)
    {
        if (_validationCache.TryGetValue(preset.Id, out var cached)) return cached;
        var validation = preset.InstallMode == InstallMode.ExternalOnly
            ? new PresetValidation { Warnings = { "Author page only; content not downloaded." } }
            : _main.Catalog.Validate(preset, roots, _main.Scan?.ReShade.Version);
        _validationCache[preset.Id] = validation;
        return validation;
    }

    private static Border CornerLabel(string text, HorizontalAlignment align)
    {
        return new Border
        {
            Background = UiKit.Brush("#C0000000"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(4),
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Top,
            Child = UiKit.Text(text, size: 7, color: UiKit.Brush("#E8F0E6"), weight: FontWeights.Bold)
        };
    }

    private Border BuildPresetCard(PresetEntry preset, List<PresetEntry> visible, List<string> roots)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var imageBorder = new Border
        {
            Background = UiKit.Brush("#0B110D"),
            BorderBrush = UiKit.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            Height = 116,
            VerticalAlignment = VerticalAlignment.Top
        };
        var imageGrid = new Grid();
        imageGrid.Children.Add(new Image { Source = PresetPreviewRenderer.RenderComparison(preset), Stretch = Stretch.UniformToFill });
        imageGrid.Children.Add(CornerLabel("BEFORE", HorizontalAlignment.Left));
        imageGrid.Children.Add(CornerLabel("AFTER", HorizontalAlignment.Right));
        var tag = new Border
        {
            Background = UiKit.Brush("#B0000000"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 2, 5, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(5),
            Child = UiKit.Text("SYNTHETIC · BEFORE | AFTER", size: 7.5, color: UiKit.Brush("#C9D6C8"), weight: FontWeights.Bold)
        };
        imageGrid.Children.Add(tag);
        imageBorder.Child = imageGrid;
        grid.Children.Add(imageBorder);

        var content = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleStack = new StackPanel();
        titleStack.Children.Add(UiKit.Text(preset.Name, size: 14, weight: FontWeights.SemiBold));
        titleStack.Children.Add(UiKit.Text($"{preset.Author} · {preset.SourceName}", size: 10, color: UiKit.Dim).WithMargin(0, 2, 0, 0));
        header.Children.Add(titleStack);

        var validation = GetValidation(preset, roots);
        var shadersPresent = preset.InstallMode != InstallMode.ExternalOnly && validation.IsValid && validation.Shaders.Count > 0;
        var statusText = preset.InstallMode == InstallMode.ExternalOnly
            ? "UNVERIFIED SOURCE"
            : shadersPresent ? "SHADERS PRESENT" : "SHADERS MISSING";
        var statusSeverity = preset.InstallMode == InstallMode.ExternalOnly
            ? Severity.Warning
            : shadersPresent ? Severity.Notice : Severity.Warning;

        var badges = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        badges.Children.Add(UiKit.Badge(statusText, statusSeverity));
        badges.Children.Add(UiKit.Chip(preset.CostTier + " cost", CostClass(preset.CostTier) == "low" ? UiKit.Green : UiKit.Amber).WithMargin(6, 0, 0, 0));
        Grid.SetColumn(badges, 1);
        header.Children.Add(badges);
        content.Children.Add(header);

        var desc = preset.Description.Length > 210 ? preset.Description[..210] + "…" : preset.Description;
        content.Children.Add(UiKit.Text(desc, "BodyDim", color: UiKit.Brush("#A9B8AA")).WithMargin(0, 8, 0, 0));

        var tagRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var t in preset.StyleTags.Take(4)) tagRow.Children.Add(UiKit.Chip(t, UiKit.Dim));
        tagRow.Children.Add(UiKit.Chip($"{preset.Techniques.Count} techniques", UiKit.Dim));
        content.Children.Add(tagRow);

        var bottom = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cost = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        cost.Children.Add(UiKit.Text("ESTIMATED IMPACT", size: 8.5, color: UiKit.Dim, weight: FontWeights.Bold));
        cost.Children.Add(UiKit.Text(preset.EstimatedCost, size: 10.5, color: UiKit.Brush("#C7D3C6")));
        bottom.Children.Add(cost);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(UiKit.Button("Preview", (_, _) => OpenPreview(preset, visible), icon: UiKit.Icon("camera"), compact: true));
        if (!string.IsNullOrWhiteSpace(preset.SourceUrl))
            actions.Children.Add(UiKit.Button("Author page", (_, _) => _main.OpenUrl(preset.SourceUrl), icon: UiKit.Icon("open"), compact: true));
        if (preset.InstallMode == InstallMode.ExternalOnly)
            actions.Children.Add(UiKit.Button("Manual install", (_, _) => _main.OpenUrl(preset.SourceUrl), icon: UiKit.Icon("open"), primary: true, compact: true));
        else
            actions.Children.Add(UiKit.Button("Install", (_, _) => _ = InstallPresetAsync(preset), icon: UiKit.Icon("download"),
                primary: preset.InstallMode == InstallMode.Bundled, compact: true));
        Grid.SetColumn(actions, 1);
        bottom.Children.Add(actions);
        content.Children.Add(bottom);

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        var card = UiKit.Card(grid, 14);
        card.Margin = new Thickness(0, 0, 0, 9);
        return card;
    }

    private void OpenPreview(PresetEntry preset, List<PresetEntry> visible)
    {
        var window = new PresetPreviewWindow(_main, preset, visible, async p => await InstallPresetAsync(p))
        {
            Owner = Window.GetWindow(this)
        };
        window.ShowDialog();
    }

    private async Task InstallPresetAsync(PresetEntry preset)
    {
        if (_main.Scan == null || !_main.Scan.Game.IsValid)
        {
            _main.ShowError("A valid game folder is required. Open Settings & Backups and choose the STALKER 2 folder.");
            return;
        }
        if (preset.InstallMode == InstallMode.ExternalOnly)
        {
            _main.OpenUrl(preset.SourceUrl);
            _main.SetStatus("Opened the author's page. Download the preset there, then use Import preset file.", Severity.Info);
            return;
        }
        if (ReShadeService.IsGameRunning())
        {
            _main.ShowError("Exit STALKER 2 before installing ReShade or a preset.");
            return;
        }

        var action = _main.Scan.ReShade.Installed
            ? $"Install '{preset.Name}' and point ReShade at it?"
            : $"ReShade is not installed.\n\nThis will download the official signed ReShade {PresetCatalog.ReShadeVersion} setup, install it for DXGI, " +
              $"download only the shader files this preset needs, and install '{preset.Name}'.";
        var message = action + "\n\nBefore writing, ZoneTune backs up ReShade.ini, the current preset and proxy DLLs. " +
                      "You can undo everything with 'Uninstall & restore'.\n\nCompatibility will be checked structurally, not visually.";
        if (_main.Scan.Hardware.HdrEnabled)
            message += "\n\nWarning: HDR is enabled. Most presets expect SDR; disable HDR in-game before judging the look.";
        if (!_main.Confirm("Install ReShade preset", message)) return;

        ShowProgress("Installing ReShade preset", "Preparing…", 0, true);
        try
        {
            var progress = new Progress<OperationProgress>(p => ShowProgress("Installing ReShade preset", $"{p.Stage} · {p.Detail}", p.Percent, p.Percent <= 0));
            var result = await _main.ReShadeInstaller.InstallAsync(_main.Scan.Game, _main.Scan.Hardware, preset,
                _main.GetBackups(), _main.Settings, progress);
            HideProgress();

            var text = result.Success ? "Preset installed.\n\n" : "Installation did not complete.\n\n";
            if (!string.IsNullOrWhiteSpace(result.Verification)) text += result.Verification + "\n\n";
            foreach (var m in result.Messages.Take(5)) text += "• " + m + "\n";
            if (result.Warnings.Count > 0)
            {
                text += "\nWarnings:\n";
                foreach (var w in result.Warnings.Take(5)) text += "! " + w + "\n";
            }
            if (result.Errors.Count > 0)
            {
                text += "\nErrors:\n";
                foreach (var e in result.Errors.Take(5)) text += "× " + e + "\n";
            }
            text += "\nLaunch the game and press Home to open ReShade. Check the effect list before judging the preset.";
            MessageBox.Show(Window.GetWindow(this), text, result.Success ? "ReShade installed" : "ReShade install failed",
                MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

            _main.Settings.LastReShadePresetId = preset.Id;
            _main.SetStatus(result.Success ? $"ReShade preset installed: {preset.Name}" : "ReShade installation failed; backups preserved.",
                result.Success ? Severity.Notice : Severity.Critical);
            if (result.Success) await _main.RunScanAsync(silent: true);
            else await RefreshAsync();
        }
        catch (Exception ex)
        {
            HideProgress();
            Log.Error("ReShade install UI failed", ex);
            _main.ShowError("ReShade installation failed:\n\n" + ex.Message);
        }
    }

    private async Task RemoveReShadeAsync()
    {
        if (_main.Scan == null) return;
        if (!_main.Confirm("Remove ReShade",
                "Remove the ReShade module using its official uninstaller?\n\n" +
                "ZoneTune first backs up the proxy DLL, ReShade.ini and the active preset. Shader files stay on disk for a quick reinstall. " +
                "You can restore the module from Settings & Backups.\n\n" +
                "Exit the game before continuing."))
            return;

        ShowProgress("Removing ReShade", "Official uninstaller…", 0, true);
        try
        {
            var result = await _main.ReShadeInstaller.RemoveReShadeAsync(_main.Scan.Game, _main.GetBackups(), _main.Settings,
                new Progress<OperationProgress>(p => ShowProgress("Removing ReShade", $"{p.Stage} · {p.Detail}", p.Percent, p.Percent <= 0)));
            HideProgress();
            var text = result.Success ? "ReShade removed.\n\n" : "ReShade removal did not complete.\n\n";
            foreach (var m in result.Messages.Take(8)) text += "• " + m + "\n";
            foreach (var e in result.Errors.Take(6)) text += "× " + e + "\n";
            MessageBox.Show(Window.GetWindow(this), text, "Remove ReShade", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            _main.SetStatus(result.Success ? "ReShade removed; backup kept." : "ReShade removal failed.", result.Success ? Severity.Notice : Severity.Critical);
            await _main.RunScanAsync(silent: true);
        }
        catch (Exception ex)
        {
            HideProgress();
            _main.ShowError("ReShade removal failed:\n" + ex.Message);
        }
    }

    private async Task UninstallAsync(string changeSetId)
    {
        if (_main.Scan == null) return;
        if (!_main.Confirm("Uninstall ReShade changes",
                "Restore the state captured before ZoneTune's ReShade installation?\n\nThis restores ReShade.ini, the previous preset and proxy DLLs, " +
                "and removes only files that ZoneTune created (shaders it added, the installed preset, the ReShade module if it was not there before).\n\n" +
                "A safety snapshot is taken first."))
            return;
        ShowProgress("Restoring ReShade state", "Applying backup…", 0, true);
        try
        {
            var result = await Task.Run(() => _main.ReShadeInstaller.Uninstall(_main.Scan.Game, _main.GetBackups(), changeSetId));
            HideProgress();
            var text = result.Success ? "Restore completed.\n\n" : "Restore completed with errors.\n\n";
            foreach (var m in result.Messages.Take(10)) text += "• " + m + "\n";
            foreach (var e in result.Errors.Take(6)) text += "× " + e + "\n";
            MessageBox.Show(Window.GetWindow(this), text, "ReShade uninstall", MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            _main.SetStatus(result.Success ? "ReShade state restored." : "Restore had errors; see log.", result.Success ? Severity.Notice : Severity.Critical);
            await _main.RunScanAsync(silent: true);
        }
        catch (Exception ex)
        {
            HideProgress();
            _main.ShowError("Uninstall failed:\n" + ex.Message);
        }
    }

    private void ShowProgress(string title, string detail, int percent, bool indeterminate)
    {
        ProgressCard.Visibility = Visibility.Visible;
        ProgressTitle.Text = title;
        ProgressDetail.Text = detail;
        ProgressPercent.Text = indeterminate ? "" : percent + "%";
        ProgressBar.IsIndeterminate = indeterminate;
        ProgressBar.Value = percent;
    }

    private void HideProgress() => ProgressCard.Visibility = Visibility.Collapsed;

    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) RenderCatalog(); }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) RenderCatalog(); }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a ReShade preset",
            Filter = "ReShade preset (*.ini)|*.ini|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var preset = _main.Catalog.FromLocalFile(dialog.FileName);
            _local.Add(preset);
            _all = BuildCatalog();
            RenderCatalog();
            OpenPreview(preset, _all);
            _main.SetStatus($"Imported local preset '{preset.Name}'. Validate it in the preview before installing.", Severity.Info);
        }
        catch (Exception ex)
        {
            _main.ShowError("Could not import that preset:\n" + ex.Message);
        }
    }

    private async void PublicSearch_Click(object sender, RoutedEventArgs e)
    {
        if (!_main.Settings.AllowNetwork)
        {
            _main.ShowError("Network access is disabled in Settings & Backups. Enable it to search public sources.");
            return;
        }
        PublicSearchButton.IsEnabled = false;
        ShowProgress("Searching public presets", "GitHub repositories with permissive licenses only…", 0, true);
        try
        {
            var results = await _main.Catalog.SearchGitHubAsync(_main.Scan?.Hardware);
            _github.Clear();
            _github.AddRange(results);
            _all = BuildCatalog();
            RenderCatalog();
            HideProgress();
            _main.SetStatus(results.Count == 0
                ? "No permissively licensed GitHub presets found. Nexus presets are author-page links by design."
                : $"Found {results.Count} directly installable preset(s) with a permissive license.", Severity.Info);
            if (results.Count == 0)
                MessageBox.Show(Window.GetWindow(this),
                    "The public search returned no presets that ZoneTune may install directly.\n\n" +
                    "This is expected: most STALKER 2 presets are distributed on Nexus Mods under author-controlled terms. " +
                    "Use the Author page buttons, download manually, then Import preset file.",
                    "No direct-download presets", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            HideProgress();
            _main.SetStatus("Public preset search failed: " + ex.Message, Severity.Warning);
            _main.ShowError("Public search failed:\n" + ex.Message);
        }
        finally { PublicSearchButton.IsEnabled = true; }
    }
}



