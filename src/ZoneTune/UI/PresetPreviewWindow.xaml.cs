using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZoneTune.Core.Models;
using ZoneTune.Core.Services;

namespace ZoneTune.UI;

public partial class PresetPreviewWindow : Window
{
    private readonly MainWindow _main;
    private readonly PresetEntry _preset;
    private readonly List<PresetEntry> _all;
    private readonly Func<PresetEntry, Task> _install;
    private readonly PresetCatalog _catalog = new();

    public PresetPreviewWindow(MainWindow main, PresetEntry preset, List<PresetEntry> all, Func<PresetEntry, Task> install)
    {
        InitializeComponent();
        _main = main;
        _preset = preset;
        _all = all;
        _install = install;

        OriginalImage.Source = PresetPreviewRenderer.Render(preset, original: true);
        SelectedImage.Source = PresetPreviewRenderer.Render(preset);
        NameText.Text = preset.Name;
        AuthorText.Text = string.IsNullOrWhiteSpace(preset.Author) ? preset.SourceName : $"{preset.Author} · {preset.SourceName}";
        DescriptionText.Text = preset.Description;
        CostText.Text = preset.EstimatedCost;
        SourceText.Text = string.IsNullOrWhiteSpace(preset.SourceUrl) ? preset.SourceName : preset.SourceUrl;
        LicenseText.Text = preset.License;

        BadgePanel.Children.Clear();
        BadgePanel.Children.Add(UiKit.Badge(preset.StatusText, StatusSeverity(preset.Status)));
        BadgePanel.Children.Add(UiKit.Chip(preset.CostTier + " cost", UiKit.Amber).WithMargin(8, 0, 0, 0));
        BadgePanel.Children.Add(UiKit.Chip(preset.InstallModeText, UiKit.Dim).WithMargin(8, 0, 0, 0));

        TagsPanel.Children.Clear();
        foreach (var tag in preset.StyleTags) TagsPanel.Children.Add(UiKit.Chip(tag, UiKit.Dim));

        TechniquesPanel.Children.Clear();
        foreach (var t in preset.Techniques.Take(8))
            TechniquesPanel.Children.Add(UiKit.Text("· " + t, size: 10.5, color: UiKit.Dim));

        RequirementsPanel.Children.Clear();
        foreach (var r in preset.Requirements)
            RequirementsPanel.Children.Add(UiKit.Text("· " + r, size: 10.5, color: UiKit.Dim));

        var shaderRoots = GetShaderRoots();
        var validation = _catalog.Validate(preset, shaderRoots, main.Scan?.ReShade.Version);
        RenderValidation(validation);

        WarningsPanel.Children.Clear();
        if (main.Scan?.Hardware.HdrEnabled == true)
            WarningsPanel.Children.Add(Warning("HDR is enabled. Most ReShade presets expect SDR output; expect a washed-out image."));
        if (!string.IsNullOrWhiteSpace(preset.HdrNote))
            WarningsPanel.Children.Add(Warning(preset.HdrNote));
        if (preset.Warnings.Count > 0)
            foreach (var w in preset.Warnings) WarningsPanel.Children.Add(Warning(w));
        foreach (var w in validation.Warnings.Take(4)) WarningsPanel.Children.Add(Warning(w));

        SelectedStatusText.Text = validation.IsValid && preset.InstallMode != InstallMode.ExternalOnly
            ? "SHADERS PRESENT"
            : preset.InstallMode == InstallMode.ExternalOnly ? "UNVERIFIED" : "REVIEW";
        SelectedStatusText.Foreground = validation.IsValid && preset.InstallMode != InstallMode.ExternalOnly ? UiKit.Green : UiKit.Amber;

        SourceButton.Content = string.IsNullOrWhiteSpace(preset.SourceUrl) ? "No author page" : "Open author's page";
        SourceButton.IsEnabled = !string.IsNullOrWhiteSpace(preset.SourceUrl);

        InstallButton.Content = preset.InstallMode switch
        {
            InstallMode.ExternalOnly => "Open author page (manual download)",
            InstallMode.LocalFile => "Install local preset",
            _ => main.Scan?.ReShade.Installed == true ? "Install preset (one click)" : "Install ReShade + preset (one click)"
        };
        InstallButton.IsEnabled = main.Scan?.Game.IsValid == true;

        CompareCombo.ItemsSource = _all.Where(p => p.Id != preset.Id).ToList();
        var installedName = main.Scan?.ReShade.PresetName;
        var preferred = _all.FirstOrDefault(p => p.Id != preset.Id &&
                                                  !string.IsNullOrWhiteSpace(installedName) &&
                                                  installedName.Contains(p.Name, StringComparison.OrdinalIgnoreCase))
                        ?? _all.FirstOrDefault(p => p.Id != preset.Id);
        if (preferred != null) CompareCombo.SelectedItem = preferred;
        else
        {
            CompareCombo.IsEnabled = false;
            CompareImage.Source = PresetPreviewRenderer.Render(preset);
            CompareLabel.Text = "No other preset available for comparison";
        }
    }

    private List<string> GetShaderRoots()
    {
        var roots = new List<string>();
        var scan = _main.Scan;
        if (scan != null && !string.IsNullOrWhiteSpace(scan.ReShade.ShadersDir) && Directory.Exists(scan.ReShade.ShadersDir))
            roots.Add(scan.ReShade.ShadersDir);
        if (scan?.Game.IsValid == true)
        {
            var fallback = Path.Combine(scan.Game.BinariesDir, "reshade-shaders", "Shaders");
            if (Directory.Exists(fallback)) roots.Add(fallback);
        }
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void RenderValidation(PresetValidation validation)
    {
        ValidationPanel.Children.Clear();
        if (validation.Shaders.Count == 0)
        {
            ValidationPanel.Children.Add(UiKit.Text("Preset content is not available locally. Download it from the author's page, then use Import.", size: 10.5, color: UiKit.Amber));
            return;
        }
        foreach (var reference in validation.Shaders)
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            stack.Children.Add(UiKit.IconText(reference.Present ? UiKit.Icon("check") : UiKit.Icon("close"), 10,
                reference.Present ? UiKit.Green : UiKit.Red));
            stack.Children.Add(UiKit.Text(reference.File, size: 10.5, color: reference.Present ? UiKit.Dim : UiKit.Amber).WithMargin(7, 0, 0, 0));
            if (!reference.Present) stack.Children.Add(UiKit.Text("missing", size: 9.5, color: UiKit.Red).WithMargin(7, 0, 0, 0));
            ValidationPanel.Children.Add(stack);
        }
        var version = _main.Scan?.ReShade.Version;
        if (!string.IsNullOrWhiteSpace(version))
            ValidationPanel.Children.Add(UiKit.Text($"Installed ReShade {version}; preset minimum {_preset.MinReShadeVersion} → " +
                                                    (ReShadeService.VersionAtLeast(version, _preset.MinReShadeVersion) ? "compatible" : "too old"),
                size: 10, color: ReShadeService.VersionAtLeast(version, _preset.MinReShadeVersion) ? UiKit.Green : UiKit.Red).WithMargin(0, 6, 0, 0));
        ValidationPanel.Children.Add(UiKit.Text(validation.IsValid
            ? "All referenced shader files are present. Syntax checked; visual result and frame cost are not verified."
            : "One or more shader files are missing. Installation can still proceed if you install the author's shader package.",
            size: 10, color: validation.IsValid ? UiKit.Dim : UiKit.Amber).WithMargin(0, 5, 0, 0));
    }

    private static Border Warning(string text)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(UiKit.IconText(UiKit.Icon("warning"), 11, UiKit.Amber));
        stack.Children.Add(UiKit.Text(text, size: 10.5, color: UiKit.Amber).WithMargin(7, 0, 0, 0));
        return new Border
        {
            Background = UiKit.Brush("#1D180D"),
            BorderBrush = UiKit.Brush("#3A2F18"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 4, 0, 0),
            Child = stack
        };
    }

    private static Severity StatusSeverity(VerificationStatus status) => status switch
    {
        VerificationStatus.StructurallyVerified => Severity.Notice,
        VerificationStatus.Estimated => Severity.Info,
        VerificationStatus.Unverified => Severity.Warning,
        _ => Severity.Critical
    };

    private void Compare_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CompareCombo.SelectedItem is not PresetEntry other) return;
        CompareImage.Source = PresetPreviewRenderer.Render(other);
        CompareLabel.Text = $"{other.Name} · {other.EstimatedCost}";
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_preset.InstallMode == InstallMode.ExternalOnly)
        {
            _main.OpenUrl(_preset.SourceUrl);
            return;
        }
        InstallButton.IsEnabled = false;
        Close();
        await _install(_preset);
    }

    private void Source_Click(object sender, RoutedEventArgs e) => _main.OpenUrl(_preset.SourceUrl);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }
}

