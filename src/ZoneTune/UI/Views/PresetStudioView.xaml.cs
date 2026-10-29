using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ZoneTune.Core;
using ZoneTune.Core.Models;
using ZoneTune.Core.Services;

namespace ZoneTune.UI.Views;

public partial class PresetStudioView : UserControl, IRefreshable
{
    private readonly MainWindow _main;
    private readonly CustomPresetBuilder _builder = new();
    private readonly List<PresetEntry> _github = new();
    private readonly List<PresetEntry> _local = new();
    private readonly Dictionary<string, PresetValidation> _validationCache = new();
    private List<PresetEntry> _all = new();
    private CustomPresetOptions? _options;
    private bool _ready;
    private bool _suppress;
    private readonly System.Windows.Threading.DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };

    public PresetStudioView(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _ready = true;
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); UpdatePreview(); };
        Slider_Changed(null, null);
    }

    public Task RefreshAsync()
    {
        _all = BuildCatalog();
        RenderReShadeStatus();
        RenderCatalog();

        var scan = _main.Scan;
        if (scan == null || !scan.Game.IsValid)
        {
            HardwareText.Text = "Scan the system and detect the game to generate a preset.";
            RecommendedBadge.Text = "SCAN REQUIRED";
            InstallButton.IsEnabled = false;
            return Task.CompletedTask;
        }

        var recommended = _builder.Recommend(scan.Hardware);
        if (_options == null)
        {
            _options = _builder.Defaults(scan.Hardware, recommended);
            NameBox.Text = _options.Name;
        }
        HardwareText.Text = $"{scan.Hardware.GpuName} · {HardwareInfo.FormatGb(scan.Hardware.VramMb)} VRAM · " +
                            $"{scan.Hardware.PrimaryResolution} @ {scan.Hardware.PrimaryRefreshHz} Hz · " +
                            $"{scan.Hardware.GpuTier} class";
        RecommendedBadge.Text = "RECOMMENDED: " + recommended.ToString().ToUpperInvariant();
        RenderModes(recommended);
        RenderEffects();
        if (!_suppress) SetSliders();
        UpdatePreview();
        UndoButton.Visibility = FindPresetChangeSet() != null ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = !scan.GameRunning;
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

    private void RenderReShadeStatus()
    {
        var scan = _main.Scan;
        ReShadeStatusHost.Content = null;
        ReShadeNoticePanel.Children.Clear();
        if (scan == null)
        {
            ReShadeStatusHost.Content = UiKit.Empty(UiKit.Icon("search"), "Scan required",
                "ReShade detection and installation options need a valid game folder and a system scan.",
                UiKit.Button("Run scan", (_, _) => _ = _main.RunScanAsync(), primary: true));
            return;
        }

        var root = new StackPanel();
        var stats = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 12) };
        stats.Children.Add(Stat("GPU", scan.Hardware.GpuName, $"{HardwareInfo.FormatGb(scan.Hardware.VramMb)} VRAM", UiKit.Dim));
        stats.Children.Add(Stat("DISPLAY", $"{scan.Hardware.PrimaryResolution} · {scan.Hardware.PrimaryRefreshHz} Hz",
            scan.Hardware.HdrEnabled ? "HDR ON — ReShade risk" : scan.Hardware.AnyHdr ? "HDR capable, currently off" : "SDR",
            scan.Hardware.HdrEnabled ? UiKit.Amber : UiKit.Dim));
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
            : "Install the official signed build and only the shader files a preset needs. ReShade is optional.", "BodyDim").WithMargin(0, 4, 0, 0));
        header.Children.Add(left);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        if (scan.ReShade.Installed)
        {
            var cs = FindReShadeChangeSet();
            if (cs != null)
                buttons.Children.Add(UiKit.Button("Uninstall & restore", (_, _) => _ = UninstallReShadeAsync(cs.Id), danger: true, icon: UiKit.Icon("undo")));
            buttons.Children.Add(UiKit.Button("Remove ReShade", (_, _) => _ = RemoveReShadeAsync(), danger: true, icon: UiKit.Icon("delete")));
            var repair = _all.FirstOrDefault(p => p.InstallMode == InstallMode.Bundled);
            if (repair != null)
                buttons.Children.Add(UiKit.Button("Install / repair ReShade", (_, _) => _ = InstallCatalogPresetAsync(repair),
                    icon: UiKit.Icon("download")));
        }
        else
        {
            var removed = FindReShadeRemovalChangeSet();
            var starter = _all.FirstOrDefault(p => p.InstallMode == InstallMode.Bundled);
            if (starter != null)
                buttons.Children.Add(UiKit.Button("Install ReShade + starter preset", (_, _) => _ = InstallCatalogPresetAsync(starter), primary: true, icon: UiKit.Icon("download")));
            if (removed != null)
                buttons.Children.Add(UiKit.Button("Restore removed ReShade", (_, _) => _ = UninstallReShadeAsync(removed.Id), icon: UiKit.Icon("undo")));
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

        root.Children.Add(UiKit.Card(status, 18));
        ReShadeStatusHost.Content = root;

        if (scan.ReShade.HdrConflict)
            ReShadeNoticePanel.Children.Add(Notice("HDR + ReShade", "Disable in-game HDR for presets; most are authored for SDR.", UiKit.Amber));
        if (scan.ReShade.ProxyConflicts.Count > 0)
            ReShadeNoticePanel.Children.Add(Notice("Proxy DLL conflict", scan.ReShade.ProxyConflicts.First(), UiKit.Red));
        if (scan.ReShade.ShaderDuplicates.Count > 0)
            ReShadeNoticePanel.Children.Add(Notice("Duplicate shader names", scan.ReShade.ShaderDuplicates.First(), UiKit.Amber));
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
        return new Border
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
        if (_main.Scan == null || !_main.Scan.Game.IsValid)
        {
            CatalogCount.Text = $"{_all.Count} available";
            CatalogSubtitle.Text = "Run a system scan to validate shader presence and detect the installed ReShade version.";
            CatalogPanel.Children.Add(UiKit.Empty(UiKit.Icon("search"), "Scan required for validation",
                "The catalog is available, but installing needs a detected game folder and a shader check.", null));
            return;
        }
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
            CatalogPanel.Children.Add(UiKit.Empty(UiKit.Icon("search"), "No presets match",
                "Clear the search or choose another filter. Public-source search only returns repositories with a permissive license.", null));
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
        imageGrid.Children.Add(new Border
        {
            Background = UiKit.Brush("#B0000000"),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 2, 5, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(5),
            Child = UiKit.Text("SYNTHETIC · BEFORE | AFTER", size: 7.5, color: UiKit.Brush("#C9D6C8"), weight: FontWeights.Bold)
        });
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
            actions.Children.Add(UiKit.Button("Install", (_, _) => _ = InstallCatalogPresetAsync(preset), icon: UiKit.Icon("download"),
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
        var window = new PresetPreviewWindow(_main, preset, visible, async p => await InstallCatalogPresetAsync(p))
        {
            Owner = Window.GetWindow(this)
        };
        window.ShowDialog();
    }

    private async Task InstallCatalogPresetAsync(PresetEntry preset)
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
                "You can restore the module from Settings & Backups or this tab.\n\n" +
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

    private async Task UninstallReShadeAsync(string changeSetId)
    {
        if (_main.Scan == null) return;
        if (!_main.Confirm("Uninstall ReShade changes",
                "Restore the state captured before ZoneTune's ReShade installation?\n\nThis restores ReShade.ini, the previous preset and proxy DLLs, " +
                "and removes only files that ZoneTune created.\n\nA safety snapshot is taken first."))
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

    private void RenderModes(ReShadeMode recommended)
    {
        ModeGrid.Children.Clear();
        foreach (var mode in new[] { ReShadeMode.Potato, ReShadeMode.Medium, ReShadeMode.High })
        {
            var (title, sub, icon, color) = mode switch
            {
                ReShadeMode.Potato => ("Potato Mode", "Low-end GPUs · 1080p · minimal stack", UiKit.Icon("gauge"), UiKit.Green),
                ReShadeMode.Medium => ("Medium Mode", "Mid-range GPUs · balanced grade", UiKit.Icon("tune"), UiKit.Amber),
                _ => ("High Mode", "Good GPUs · full stack with bloom", UiKit.Icon("preset"), UiKit.Blue)
            };
            var stack = new StackPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(UiKit.IconText(icon, 15, color));
            top.Children.Add(UiKit.Text(title, size: 13.5, weight: FontWeights.SemiBold).WithMargin(8, 0, 0, 0));
            if (mode == recommended)
                top.Children.Add(UiKit.Badge("RECOMMENDED", Severity.Notice).WithMargin(8, 0, 0, 0));
            stack.Children.Add(top);
            stack.Children.Add(UiKit.Text(sub, size: 10.5, color: UiKit.Dim).WithMargin(0, 4, 0, 0));
            var cost = mode switch
            {
                ReShadeMode.Potato => "Est. < 0.5% at 1440p",
                ReShadeMode.Medium => "Est. 1–2% at 1440p",
                _ => "Est. 2–4% at 1440p"
            };
            stack.Children.Add(UiKit.Text(cost, size: 10, color: UiKit.Amber).WithMargin(0, 7, 0, 0));

            var selected = _options?.Mode == mode;
            var card = new Border
            {
                Background = selected ? UiKit.Brush("#162017") : UiKit.Brush("#0F1511"),
                BorderBrush = selected ? color : UiKit.Line,
                BorderThickness = new Thickness(selected ? 1.5 : 1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand,
                Child = stack
            };
            card.MouseLeftButtonUp += (_, _) =>
            {
                _options = _builder.Defaults(_main.Scan!.Hardware, mode);
                NameBox.Text = _options.Name;
                RenderModes(_builder.Recommend(_main.Scan.Hardware));
                RenderEffects();
                SetSliders();
                UpdatePreview();
            };
            ModeGrid.Children.Add(card);
        }
    }

    private void RenderEffects()
    {
        if (_options == null) return;
        EffectsPanel.Children.Clear();
        var effects = CustomPresetBuilder.Effects;
        for (var i = 0; i < effects.Length; i++)
        {
            var effect = effects[i];
            var enabled = effect.Key switch
            {
                "levels" => _options.UseLevels,
                "colour" => _options.UseColourfulness,
                "deband" => _options.UseDeband,
                "sharpen" => _options.UseSharpen,
                "filmic" => _options.UseFilmic,
                _ => _options.UseBloom
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(UiKit.Text(effect.Name, size: 11.5, weight: FontWeights.SemiBold));
            top.Children.Add(UiKit.Chip(effect.Cost, UiKit.Dim).WithMargin(7, 0, 0, 0));
            text.Children.Add(top);
            text.Children.Add(UiKit.Text(effect.Description, size: 9.5, color: UiKit.Dim).WithMargin(0, 2, 0, 0));
            grid.Children.Add(text);

            var check = new CheckBox { IsChecked = enabled, VerticalAlignment = VerticalAlignment.Center };
            check.Checked += (_, _) => SetEffect(effect.Key, true);
            check.Unchecked += (_, _) => SetEffect(effect.Key, false);
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
            EffectsPanel.Children.Add(new Border
            {
                BorderBrush = UiKit.Line,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 7, 0, 7),
                Child = grid
            });
        }
    }

    private void SetEffect(string key, bool value)
    {
        if (_options == null) return;
        switch (key)
        {
            case "levels": _options.UseLevels = value; break;
            case "colour": _options.UseColourfulness = value; break;
            case "deband": _options.UseDeband = value; break;
            case "sharpen": _options.UseSharpen = value; break;
            case "filmic": _options.UseFilmic = value; break;
            default: _options.UseBloom = value; break;
        }
        UpdateControls();
        UpdatePreview();
    }

    private void SetSliders()
    {
        if (_options == null) return;
        _suppress = true;
        ColourSlider.Value = _options.ColourIntensity;
        SharpenSlider.Value = _options.SharpenStrength;
        FilmSlider.Value = _options.FilmicStrength;
        BloomSlider.Value = _options.BloomAmount;
        _suppress = false;
        UpdateControls();
    }

    private void UpdateControls()
    {
        if (_options == null) return;
        ColourSlider.IsEnabled = _options.UseColourfulness;
        SharpenSlider.IsEnabled = _options.UseSharpen;
        FilmSlider.IsEnabled = _options.UseFilmic;
        BloomSlider.IsEnabled = _options.UseBloom;
        ColourValue.Text = _options.ColourIntensity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        SharpenValue.Text = _options.SharpenStrength.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        FilmValue.Text = _options.FilmicStrength.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        BloomValue.Text = _options.BloomAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        var budget = _options.Mode switch
        {
            ReShadeMode.Potato => "Potato budget: colour, saturation, deband and light sharpening. Filmic and bloom exceed this budget.",
            ReShadeMode.Medium => "Medium budget: filmic is allowed; bloom is above this budget.",
            _ => "High budget: all listed effects fit this tier. 4K/ultrawide still costs more."
        };
        BudgetText.Text = budget;
    }

    private void UpdatePreview()
    {
        if (_options == null || _main.Scan?.Hardware == null) return;
        var preset = _builder.Build(_main.Scan.Hardware, _options);
        PreviewName.Text = preset.Name;
        PreviewCost.Text = preset.EstimatedCost;
        PreviewTechCount.Text = $"{preset.Techniques.Count} effect(s)";
        OriginalImage.Source = PresetPreviewRenderer.Render(preset, original: true);
        ProcessedImage.Source = PresetPreviewRenderer.Render(preset);
        PreviewStatusText.Text = "ESTIMATED";

        WarningPanel.Children.Clear();
        var hw = _main.Scan.Hardware;
        if (_options.Mode == ReShadeMode.Potato && (_options.UseFilmic || _options.UseBloom))
            WarningPanel.Children.Add(Warning("Above the Potato budget", "Filmic and/or bloom are enabled. On a low-end GPU this can cost more than the rest of the stack; measure before keeping."));
        if (_options.Mode == ReShadeMode.Medium && _options.UseBloom)
            WarningPanel.Children.Add(Warning("Bloom on Medium", "Bloom is a multi-tap pass. If FPS drops, disable it first."));
        if (hw.HdrEnabled)
            WarningPanel.Children.Add(Warning("HDR is enabled", "Most ReShade effects assume SDR. Disable in-game HDR if the image looks washed out."));
        if (hw.GpuTier is "Entry" or "Mid" && _options.Mode == ReShadeMode.High)
            WarningPanel.Children.Add(Warning("High mode exceeds the recommended class", $"Your GPU is classified as {hw.GpuTier}. High adds bloom and filmic cost; if FPS drops, switch to Medium or Potato."));
        if (hw.GpuTier == "Entry" && _options.Mode == ReShadeMode.Medium)
            WarningPanel.Children.Add(Warning("Medium on an entry GPU", "The recommended baseline for this class is Potato. Try Medium only if you have headroom."));
        if (hw.GpuTier == "Entry" && _options.UseBloom)
            WarningPanel.Children.Add(Warning("Bloom on an entry GPU", "Bloom is the most expensive effect here. Expect a visible cost on low-end cards."));
        if (hw.PrimaryResolution is "3840x2160" or "3440x1440" or "5120x1440")
            WarningPanel.Children.Add(Warning("High pixel count", $"{hw.PrimaryResolution} makes every effect more expensive. Consider Potato/Medium or lower the bloom first."));

        InstallButton.Content = _main.Scan.ReShade.Installed ? "Install this preset" : "Install ReShade + this preset";
    }

    private static Border Warning(string title, string detail)
    {
        var stack = new StackPanel();
        stack.Children.Add(UiKit.Text(title, size: 10, color: UiKit.Amber, weight: FontWeights.Bold));
        stack.Children.Add(UiKit.Text(detail, size: 9.5, color: UiKit.Dim).WithMargin(0, 2, 0, 0));
        return new Border
        {
            Background = UiKit.Brush("#1A150D"),
            BorderBrush = UiKit.Brush("#3A2F18"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 4, 0, 0),
            Child = stack
        };
    }

    private void Slider_Changed(object? sender, RoutedPropertyChangedEventArgs<double>? e)
    {
        if (!_ready || _suppress || _options == null) return;
        _options.ColourIntensity = ColourSlider.Value;
        _options.SharpenStrength = SharpenSlider.Value;
        _options.FilmicStrength = FilmSlider.Value;
        _options.BloomAmount = BloomSlider.Value;
        UpdateControls();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void Name_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _options == null) return;
        _options.Name = NameBox.Text;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_main.Scan == null || !_main.Scan.Game.IsValid) { _main.ShowError("Scan and detect the game first."); return; }
        if (new ConfigService().IsGameRunning()) { _main.ShowError("Exit STALKER 2 before installing a preset."); return; }
        if (_options == null) return;
        var preset = _builder.Build(_main.Scan.Hardware, _options);
        var effects = preset.Techniques.Count;
        var message = $"Install '{preset.Name}' ({_options.Mode} mode, {effects} effect(s))?\n\n" +
                      $"{preset.EstimatedCost}\n\n" +
                      "ReShade and the previous preset/ReShade.ini are backed up first. You can undo this from Preset Studio or Settings & Backups.\n\n" +
                      "Compatibility is checked structurally after install; no visual guarantee.";
        if (!_main.Confirm("Install custom preset", message)) return;

        InstallButton.IsEnabled = false;
        InstallMessages.Children.Clear();
        InstallMessages.Children.Add(UiKit.Text("Installing… check the status bar for progress.", size: 10, color: UiKit.Amber));
        _ = InstallAsync(preset);
    }

    private async Task InstallAsync(PresetEntry preset)
    {
        try
        {
            var progress = new Progress<OperationProgress>(p => _main.SetStatus($"{p.Stage}: {p.Detail}", Severity.Info));
            var result = await _main.ReShadeInstaller.InstallAsync(_main.Scan!.Game, _main.Scan.Hardware, preset,
                _main.GetBackups(), _main.Settings, progress);
            InstallMessages.Children.Clear();
            if (result.Success)
            {
                InstallMessages.Children.Add(UiKit.Text("Installed · " + result.Verification, size: 10, color: UiKit.Green));
                _main.SetStatus($"Custom preset installed: {preset.Name}", Severity.Notice);
                await _main.RunScanAsync(silent: true);
            }
            else
            {
                InstallMessages.Children.Add(UiKit.Text("Install failed. " + string.Join(" ", result.Errors.Take(3)), size: 10, color: UiKit.Red));
                _main.SetStatus("Custom preset install failed.", Severity.Critical);
            }
            InstallButton.IsEnabled = true;
            UndoButton.Visibility = FindPresetChangeSet() != null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            InstallMessages.Children.Clear();
            InstallMessages.Children.Add(UiKit.Text("Install failed: " + ex.Message, size: 10, color: UiKit.Red));
            InstallButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_main.Scan == null || _options == null) return;
        var preset = _builder.Build(_main.Scan.Hardware, _options);
        var path = _builder.SaveToDisk(preset);
        InstallMessages.Children.Clear();
        InstallMessages.Children.Add(UiKit.Text("Saved: " + path, size: 10, color: UiKit.Green));
        _main.SetStatus("Preset saved to " + path, Severity.Info);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_main.Scan == null || _options == null) return;
        var preset = _builder.Build(_main.Scan.Hardware, _options);
        var dialog = new SaveFileDialog
        {
            Title = "Export ReShade preset",
            Filter = "ReShade preset (*.ini)|*.ini",
            FileName = AppPaths.SafeFileName(preset.Name) + ".ini"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        IniDocument.WriteTextAtomic(dialog.FileName, preset.RawPreset.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        _main.SetStatus("Preset exported to " + dialog.FileName, Severity.Info);
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        var cs = FindPresetChangeSet();
        if (cs == null) { _main.ShowError("No preset install change set found."); return; }
        if (!_main.Confirm("Undo preset install",
                $"Restore the state before '{cs.Description}'?\n\nReShade.ini, the previous preset and any created shader files are restored. " +
                "A safety snapshot is taken first."))
            return;
        var result = await Task.Run(() => _main.ReShadeInstaller.Uninstall(_main.Scan!.Game, _main.GetBackups(), cs.Id));
        if (result.Success)
        {
            _main.SetStatus("Preset install undone.", Severity.Notice);
            await _main.RunScanAsync(silent: true);
        }
        else _main.ShowError("Undo failed:\n" + string.Join("\n", result.Errors.Take(6)));
        UndoButton.Visibility = FindPresetChangeSet() != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private ChangeSet? FindPresetChangeSet()
    {
        if (_main.Scan == null) return null;
        return _main.GetBackups().List().FirstOrDefault(c =>
            c.Kind.Equals("reshade", StringComparison.OrdinalIgnoreCase) &&
            !c.Restored &&
            c.GameRoot.Equals(_main.Scan.Game.Root, StringComparison.OrdinalIgnoreCase));
    }

    private void OpenPresets_Click(object sender, RoutedEventArgs e) =>
        _main.OpenFolder(Path.Combine(AppPaths.DataRoot, "Presets"));
}
