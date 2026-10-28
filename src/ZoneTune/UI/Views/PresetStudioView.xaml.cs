using System.IO;
using System.Windows;
using System.Windows.Controls;
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
                      "ReShade and the previous preset/ReShade.ini are backed up first. You can undo this from Preset Studio, the ReShade Manager, or Settings & Backups.\n\n" +
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
