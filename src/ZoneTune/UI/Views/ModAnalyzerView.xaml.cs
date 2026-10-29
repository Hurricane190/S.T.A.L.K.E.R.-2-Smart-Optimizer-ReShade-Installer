using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Win32;
using ZoneTune.Core;
using ZoneTune.Core.Models;
using ZoneTune.Core.Services;

namespace ZoneTune.UI.Views;

public partial class ModAnalyzerView : UserControl, IRefreshable
{
    private readonly MainWindow _main;
    private readonly ModPerformanceAnalyzer _analyzer = new();
    private readonly ModQuarantineService _quarantine = new();
    private readonly ModComparisonService _comparisons = new();
    private readonly ModIgnoreService _ignores = new();
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private ModPerformanceReport? _report;
    private MeasurementSummary? _before;
    private MeasurementSummary? _after;
    private string _lastQuarantined = "";
    private bool _ready;
    private bool _busy;

    public ModAnalyzerView(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _ready = true;
        var presentMon = ModComparisonService.FindPresentMon();
        PresentMonHint.Text = string.IsNullOrWhiteSpace(presentMon)
            ? "PresentMon was not found. Install PresentMon or CapFrameX, capture a CSV, then import baseline and after files here. ZoneTune does not run benchmarks automatically."
            : "PresentMon found at " + presentMon + ". Capture a repeatable route, then import the baseline and after CSVs. ZoneTune does not run benchmarks automatically.";
        AnalyzeStatusText.Text = "Scan installed mods to inspect archives, Lua scripts, native files and overlaps. Read-only until you confirm quarantine.";
    }

    public Task RefreshAsync()
    {
        if (_report != null) _ignores.Apply(_report.Mods);
        RenderSummary();
        RenderList();
        RenderOverlaps();
        RenderQuarantine();
        RenderHistory();
        UpdateAnalyzeStatus();
        UpdateSelectionButtons();
        return Task.CompletedTask;
    }

    private GameInstall ResolveGame()
    {
        var game = _main.Scan?.Game;
        if (game is { IsValid: true }) return game;
        return new GameLocator().Locate(_main.Settings.GamePathOverride, _main.Settings.GamePathOverride);
    }

    private void RenderSummary()
    {
        SummaryHost.Content = null;
        if (_report == null)
        {
            SummaryHost.Content = UiKit.Empty(UiKit.Icon("gauge"), "No mod analysis yet",
                "Scan Mods reads pak/IoStore indexes and UE4SS Lua source. It does not execute mods, launch the game or change files.",
                UiKit.Button("Scan Mods", (_, _) => _ = RunAnalysisAsync(false), primary: true));
            return;
        }

        var report = _report;
        var high = report.Mods.Count(m => m.Enabled && m.WarningActive && m.Risk == ModRiskLevel.High);
        var medium = report.Mods.Count(m => m.Enabled && m.WarningActive && m.Risk == ModRiskLevel.Medium);
        var unknown = report.Mods.Count(m => m.Enabled && !m.Ignored && m.Risk == ModRiskLevel.Unknown);
        var ignored = report.Mods.Count(m => m.Ignored);
        var stats = new UniformGrid { Columns = 6 };
        stats.Children.Add(Stat("MODS", $"{report.EnabledCount}/{report.Mods.Count}", "enabled / total", UiKit.Dim));
        stats.Children.Add(Stat("HIGH", high.ToString(), "active warnings", high > 0 ? UiKit.Red : UiKit.Green));
        stats.Children.Add(Stat("MEDIUM", medium.ToString(), "active warnings", medium > 0 ? UiKit.Amber : UiKit.Green));
        stats.Children.Add(Stat("UNKNOWN", unknown.ToString(), "insufficient evidence", unknown > 0 ? UiKit.Blue : UiKit.Green));
        stats.Children.Add(Stat("IGNORED", ignored.ToString(), "user-suppressed", ignored > 0 ? UiKit.Blue : UiKit.Dim));
        stats.Children.Add(Stat("CONTAINERS", $"{report.ContainersRead}", $"{report.ContainersUnreadable} unreadable · {report.Overlaps.Count} overlaps",
            report.ContainersUnreadable > 0 ? UiKit.Amber : UiKit.Dim));
        SummaryHost.Content = stats;
    }

    private static Border Stat(string label, string value, string sub, Brush color)
    {
        var stack = new StackPanel();
        stack.Children.Add(UiKit.Text(label, "Overline"));
        stack.Children.Add(UiKit.Text(value, size: 19, weight: FontWeights.SemiBold, color: color).WithMargin(0, 3, 0, 1));
        stack.Children.Add(UiKit.Text(sub, size: 10, color: UiKit.Dim));
        var border = UiKit.Card(stack, 13, flat: true);
        border.Margin = new Thickness(0, 0, 8, 0);
        return border;
    }

    private void RenderList()
    {
        ListPanel.Children.Clear();
        if (_report == null) return;
        var query = SearchBox.Text.Trim();
        var filter = (FilterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All mods";
        var mods = _report.Mods.Where(a =>
        {
            if (filter == "Flagged (high/medium/unknown)" && (a.Ignored || a.Risk == ModRiskLevel.Low)) return false;
            if (filter == "High risk" && (a.Ignored || a.Risk != ModRiskLevel.High)) return false;
            if (filter == "Medium risk" && (a.Ignored || a.Risk != ModRiskLevel.Medium)) return false;
            if (filter == "Low risk" && a.Risk != ModRiskLevel.Low) return false;
            if (filter == "Unknown" && (a.Ignored || a.Risk != ModRiskLevel.Unknown)) return false;
            if (filter == "Ignored" && !a.Ignored) return false;
            if (filter == "UE4SS" && !a.IsUe4ss) return false;
            if (filter == "Scripts" && a.ScriptFiles == 0) return false;
            if (filter == "Pak / IoStore" && a.AssetCount == 0 && a.ContainersRead == 0) return false;
            if (query.Length == 0) return true;
            return a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   a.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   a.Version.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   a.KindText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   a.RiskSummary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   a.Evidence.Any(e => e.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        }).OrderByDescending(a => a.Enabled)
          .ThenBy(a => a.Risk switch { ModRiskLevel.High => 0, ModRiskLevel.Medium => 1, ModRiskLevel.Unknown => 2, _ => 3 })
          .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

        CountText.Text = $"{mods.Count} shown of {_report.Mods.Count} · {_report.FilesInspected} file(s) inspected";
        if (mods.Count == 0)
        {
            ListPanel.Children.Add(UiKit.Empty(UiKit.Icon("apps"), "No matching mods", "Change the filter or search text.", null));
            return;
        }

        foreach (var mod in mods) ListPanel.Children.Add(BuildRow(mod));
    }

    private Border BuildRow(ModPerformanceAnalysis mod)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox
        {
            IsChecked = _selected.Contains(mod.Path),
            IsEnabled = mod.QuarantineEligible,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0),
            ToolTip = mod.QuarantineEligible ? "Select for quarantine" : mod.QuarantineBlockReason
        };
        check.Checked += (_, _) => { _selected.Add(mod.Path); UpdateSelectionButtons(); };
        check.Unchecked += (_, _) => { _selected.Remove(mod.Path); UpdateSelectionButtons(); };
        grid.Children.Add(check);

        var info = new StackPanel { Margin = new Thickness(8, 0, 12, 0) };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(UiKit.Text(mod.Name, size: 12.5, weight: FontWeights.SemiBold));
        nameRow.Children.Add(UiKit.Chip(mod.KindText, mod.IsUe4ss ? UiKit.Blue : UiKit.Dim).WithMargin(8, 0, 0, 0));
        if (!string.IsNullOrWhiteSpace(mod.Version)) nameRow.Children.Add(UiKit.Chip("v" + mod.Version, UiKit.Dim).WithMargin(4, 0, 0, 0));
        if (!mod.Enabled) nameRow.Children.Add(UiKit.Badge("DISABLED", Severity.Info).WithMargin(4, 0, 0, 0));
        if (mod.VortexManaged) nameRow.Children.Add(UiKit.Badge("VORTEX", Severity.Warning).WithMargin(4, 0, 0, 0));
        if (mod.Ignored) nameRow.Children.Add(UiKit.Badge("IGNORED", Severity.Info).WithMargin(4, 0, 0, 0));
        info.Children.Add(nameRow);
        info.Children.Add(UiKit.Text(mod.Ignored
                ? "Ignored by user — the static warning is suppressed. Evidence and limitations remain in Details."
                : mod.RiskSummary,
            size: 10.5, color: UiKit.Dim).WithMargin(0, 4, 0, 0));
        info.Children.Add(UiKit.Text($"{mod.SizeText} · {mod.ContainerSummary}", size: 9.5, color: UiKit.Brush("#708274")).WithMargin(0, 3, 0, 0));
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var riskStack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        riskStack.Children.Add(mod.Ignored ? UiKit.Badge("IGNORED", Severity.Info) : RiskBadge(mod.Risk));
        riskStack.Children.Add(ConfidenceChip(mod.Confidence).WithMargin(0, 4, 0, 0));
        Grid.SetColumn(riskStack, 2);
        grid.Children.Add(riskStack);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        var details = BuildDetails(mod);
        details.Visibility = Visibility.Collapsed;
        actions.Children.Add(UiKit.Button("Details", (_, _) =>
        {
            details.Visibility = details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }, compact: true));
        actions.Children.Add(UiKit.Button("Reveal", (_, _) => Reveal(mod.Path), icon: UiKit.Icon("folder"), compact: true));
        if (mod.Ignored || mod.Risk is ModRiskLevel.High or ModRiskLevel.Medium)
            actions.Children.Add(UiKit.Button(mod.Ignored ? "Unignore" : "Ignore", (_, _) => ToggleIgnore(mod),
                icon: UiKit.Icon("shield"), compact: true));
        if (mod.QuarantineEligible)
            actions.Children.Add(UiKit.Button("Quarantine", (_, _) => QuarantineOne(mod), danger: true, compact: true));
        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);

        var content = new StackPanel();
        content.Children.Add(grid);
        content.Children.Add(details);

        var border = new Border
        {
            Background = mod.Enabled ? UiKit.Brush("#111713") : UiKit.Brush("#0D120E"),
            BorderBrush = UiKit.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 6),
            Child = content,
            Opacity = mod.Enabled ? 1 : 0.72
        };
        return border;
    }

    private FrameworkElement BuildDetails(ModPerformanceAnalysis mod)
    {
        var stack = new StackPanel { Margin = new Thickness(32, 10, 0, 2) };
        if (mod.Ignored)
        {
            stack.Children.Add(new Border
            {
                Background = UiKit.Brush("#101820"),
                BorderBrush = UiKit.Brush("#294052"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 0, 6),
                Child = UiKit.Text($"USER OVERRIDE · Ignored {mod.IgnoredAt:yyyy-MM-dd HH:mm}. " +
                                   $"Original static risk was {mod.RiskText}; the evidence below is unchanged. " +
                                   "Use Unignore to restore the warning.", size: 10, color: UiKit.Blue)
            });
        }
        AddListSection(stack, "WHAT WAS INSPECTED", mod.Inspected);
        AddListSection(stack, "WHY THIS RISK", new List<string> { mod.RiskSummary, mod.ConfidenceNote });

        if (mod.Evidence.Count > 0)
        {
            stack.Children.Add(UiKit.Text("EVIDENCE", "Overline").WithMargin(0, 10, 0, 5));
            foreach (var evidence in mod.Evidence.Take(10))
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
                var header = new StackPanel { Orientation = Orientation.Horizontal };
                header.Children.Add(UiKit.Chip(evidence.CategoryText, UiKit.Dim));
                header.Children.Add(ConfidenceChip(evidence.Confidence).WithMargin(4, 0, 0, 0));
                header.Children.Add(UiKit.Text(evidence.Title, size: 11, weight: FontWeights.SemiBold).WithMargin(8, 0, 0, 0));
                row.Children.Add(header);
                row.Children.Add(UiKit.Text(evidence.Detail, size: 10, color: UiKit.Dim).WithMargin(0, 3, 0, 0));
                if (!string.IsNullOrWhiteSpace(evidence.Source))
                    row.Children.Add(UiKit.Text("SOURCE · " + evidence.Source, size: 9, color: UiKit.Brush("#5F6F62")).WithMargin(0, 2, 0, 0));
                stack.Children.Add(row);
            }
        }

        AddListSection(stack, "POSSIBLE EFFECTS (HYPOTHESES)", mod.PossibleEffects);
        AddListSection(stack, "WHAT MIGHT IMPROVE IF DISABLED", mod.PossibleImprovements);
        AddListSection(stack, "LIMITATIONS", mod.Limitations);

        if (!mod.QuarantineEligible)
        {
            stack.Children.Add(new Border
            {
                Background = UiKit.Brush("#1A150D"),
                BorderBrush = UiKit.Brush("#3A2F18"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 6, 0, 0),
                Child = UiKit.Text("QUARANTINE NOT AVAILABLE · " + mod.QuarantineBlockReason, size: 10, color: UiKit.Amber)
            });
        }
        else if (!string.IsNullOrWhiteSpace(mod.QuarantineNote))
        {
            stack.Children.Add(UiKit.Text("QUARANTINE NOTE · " + mod.QuarantineNote, size: 9.5, color: UiKit.Amber).WithMargin(0, 6, 0, 0));
        }

        return stack;
    }

    private static void AddListSection(Panel stack, string title, IEnumerable<string> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (list.Count == 0) return;
        stack.Children.Add(UiKit.Text(title, "Overline").WithMargin(0, 10, 0, 4));
        foreach (var value in list.Take(10))
            stack.Children.Add(UiKit.Text("• " + value, size: 10.5, color: UiKit.Dim).WithMargin(0, 1, 0, 1));
    }

    private static Border RiskBadge(ModRiskLevel risk)
    {
        var severity = risk switch
        {
            ModRiskLevel.High => Severity.Critical,
            ModRiskLevel.Medium => Severity.Warning,
            ModRiskLevel.Low => Severity.Notice,
            _ => Severity.Info
        };
        return UiKit.Badge(risk switch
        {
            ModRiskLevel.High => "HIGH RISK",
            ModRiskLevel.Medium => "MEDIUM RISK",
            ModRiskLevel.Low => "LOW RISK",
            _ => "UNKNOWN"
        }, severity);
    }

    private static Border ConfidenceChip(ModConfidence confidence)
    {
        var (text, color) = confidence switch
        {
            ModConfidence.Confirmed => ("CONFIRMED FACT", UiKit.Green),
            ModConfidence.Likely => ("LIKELY", UiKit.Amber),
            ModConfidence.Hypothesis => ("HYPOTHESIS", UiKit.Amber),
            _ => ("INSUFFICIENT", UiKit.Dim)
        };
        return UiKit.Chip(text, color);
    }

    private void UpdateSelectionButtons()
    {
        var count = _selected.Count;
        QuarantineButton.Content = count > 0 ? $"Quarantine Selected ({count})" : "Quarantine Selected";
        QuarantineButton.IsEnabled = count > 0 && !_busy;
        if (_report != null)
            RestoreAllButton.IsEnabled = _quarantine.ActiveEntries().Count > 0 && !_busy;
    }

    private async Task RunAnalysisAsync(bool silent, bool force = false)
    {
        if (_busy && !force) return;
        var game = ResolveGame();
        if (!game.IsValid)
        {
            _main.ShowError("STALKER 2 was not found. Open Settings & Backups and choose the game folder manually.");
            return;
        }

        _busy = true;
        ScanButton.IsEnabled = false;
        QuarantineButton.IsEnabled = false;
        AnalyzeProgress.Visibility = Visibility.Visible;
        AnalyzeProgress.IsIndeterminate = false;
        AnalyzeProgress.Value = 0;
        AnalyzeStatusText.Text = "Inspecting mods…";
        _main.SetStatus("Analysing installed mods (read-only)…");
        try
        {
            var hardware = _main.Scan?.Hardware ?? new HardwareService().Detect(game.Root);
            var progress = new Progress<OperationProgress>(p =>
            {
                AnalyzeStatusText.Text = $"{p.Stage}: {p.Detail}";
                AnalyzeProgress.IsIndeterminate = false;
                AnalyzeProgress.Value = Math.Clamp(p.Percent, 0, 100);
            });
            _report = await Task.Run(() => _analyzer.Analyze(game, hardware, progress));
            _ignores.Apply(_report.Mods);
            _selected.RemoveWhere(path => !_report.Mods.Any(m => m.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));
            RenderSummary();
            RenderList();
            RenderOverlaps();
            RenderQuarantine();
            UpdateAnalyzeStatus();
            if (!silent)
            {
                var active = _report.Mods.Count(m => m.WarningActive);
                _main.SetStatus($"Mod analysis complete: {_report.Mods.Count} entries, {active} active warning(s).", Severity.Info);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Mod analysis UI failed", ex);
            _main.SetStatus("Mod analysis failed: " + ex.Message, Severity.Critical);
            if (!silent) _main.ShowError("Mod analysis failed:\n\n" + ex.Message);
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
            AnalyzeProgress.Visibility = Visibility.Collapsed;
            UpdateSelectionButtons();
        }
    }

    private void ToggleIgnore(ModPerformanceAnalysis mod)
    {
        if (mod.Ignored)
        {
            _ignores.Unignore(mod);
            _main.SetStatus($"Unignored '{mod.Name}'. The static warning is active again.", Severity.Info);
        }
        else
        {
            _ignores.Ignore(mod);
            _main.SetStatus($"Ignored '{mod.Name}'. The warning is hidden until you press Unignore; evidence is retained.", Severity.Info);
        }
        RenderSummary();
        RenderList();
        UpdateAnalyzeStatus();
    }

    private void UpdateAnalyzeStatus()
    {
        if (_report == null) return;
        var high = _report.Mods.Count(m => m.Enabled && m.WarningActive && m.Risk == ModRiskLevel.High);
        var medium = _report.Mods.Count(m => m.Enabled && m.WarningActive && m.Risk == ModRiskLevel.Medium);
        var ignored = _report.Mods.Count(m => m.Ignored);
        AnalyzeStatusText.Text = $"{_report.EnabledCount} enabled mod(s) · {high} high · {medium} medium · " +
                                 $"{ignored} ignored · {_report.ContainersRead} containers read · " +
                                 $"{_report.Duration.TotalSeconds:0.0}s. Static evidence only.";
    }

    private void RenderOverlaps()
    {
        OverlapPanel.Children.Clear();
        if (_report == null) return;
        var overlaps = _report.Overlaps;
        OverlapCount.Text = $"{overlaps.Count} overlap note(s) · {_report.ModConflicts.Count} scanner note(s)";
        if (overlaps.Count == 0 && _report.ModConflicts.Count == 0)
        {
            OverlapPanel.Children.Add(UiKit.Text("No shared asset paths or existing conflict notes were detected in the readable archives. " +
                                                 "This is not proof of compatibility: encrypted archives were not indexed.",
                size: 10.5, color: UiKit.Dim));
            return;
        }

        foreach (var overlap in overlaps.Take(12))
        {
            var stack = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(UiKit.Badge("SHARED ASSET", Severity.Warning));
            header.Children.Add(UiKit.Text(string.Join(" + ", overlap.Mods), size: 11, weight: FontWeights.SemiBold).WithMargin(8, 0, 0, 0));
            header.Children.Add(UiKit.Chip($"{overlap.SharedAssetCount} path(s)", UiKit.Dim).WithMargin(8, 0, 0, 0));
            stack.Children.Add(header);
            stack.Children.Add(UiKit.Text(overlap.Detail, size: 10, color: UiKit.Dim).WithMargin(0, 4, 0, 0));
            if (overlap.SampleAssets.Count > 0)
                stack.Children.Add(UiKit.Text("e.g. " + string.Join(" · ", overlap.SampleAssets.Take(3)), size: 9, color: UiKit.Brush("#5F6F62")).WithMargin(0, 3, 0, 0));
            OverlapPanel.Children.Add(new Border
            {
                BorderBrush = UiKit.Line,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 5, 0, 6),
                Child = stack
            });
        }

        foreach (var conflict in _report.ModConflicts.Take(6))
        {
            var stack = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(UiKit.Badge(conflict.Severity.ToString(), conflict.Severity));
            header.Children.Add(UiKit.Text(conflict.Title, size: 11, weight: FontWeights.SemiBold).WithMargin(8, 0, 0, 0));
            stack.Children.Add(header);
            stack.Children.Add(UiKit.Text(conflict.Detail, size: 10, color: UiKit.Dim).WithMargin(0, 4, 0, 0));
            OverlapPanel.Children.Add(new Border
            {
                BorderBrush = UiKit.Line,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 5, 0, 6),
                Child = stack
            });
        }
    }

    private void RenderQuarantine()
    {
        QuarantinePanel.Children.Clear();
        var entries = _quarantine.LoadManifest().Entries
            .OrderByDescending(e => e.CreatedAt).Take(30).ToList();
        QuarantineCount.Text = $"{entries.Count(e => e.IsActive)} active / {entries.Count} record(s)";
        RestoreAllButton.IsEnabled = entries.Any(e => e.IsActive) && !_busy;
        if (entries.Count == 0)
        {
            QuarantinePanel.Children.Add(UiKit.Empty(UiKit.Icon("shield"), "Nothing quarantined",
                "Select individual mods and use Quarantine Selected. The original folder structure is preserved in a hash-verified manifest.", null));
            return;
        }

        foreach (var entry in entries)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(UiKit.Text(entry.ModName, size: 12, weight: FontWeights.SemiBold));
            title.Children.Add(UiKit.Badge(entry.StatusText, entry.Status switch
            {
                "Quarantined" => Severity.Notice,
                "Restored" => Severity.Info,
                "Missing" => Severity.Critical,
                _ => Severity.Warning
            }).WithMargin(8, 0, 0, 0));
            info.Children.Add(title);
            info.Children.Add(UiKit.Text($"{entry.Kind} · {entry.SizeText} · {entry.CreatedAt:yyyy-MM-dd HH:mm}", size: 9.5, color: UiKit.Dim).WithMargin(0, 3, 0, 0));
            info.Children.Add(UiKit.Text(entry.OriginalPath, size: 9.5, color: UiKit.Brush("#708274")).WithMargin(0, 2, 0, 0));
            if (!string.IsNullOrWhiteSpace(entry.Note))
                info.Children.Add(UiKit.Text(entry.Note, size: 9.5, color: UiKit.Amber).WithMargin(0, 3, 0, 0));
            if (!string.IsNullOrWhiteSpace(entry.Error))
                info.Children.Add(UiKit.Text(entry.Error, size: 9.5, color: UiKit.Red).WithMargin(0, 3, 0, 0));
            grid.Children.Add(info);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            if (entry.IsActive && entry.Status != "Missing")
                actions.Children.Add(UiKit.Button("Restore", (_, _) => RestoreEntry(entry), primary: true, compact: true));
            if (Directory.Exists(entry.QuarantinePath))
                actions.Children.Add(UiKit.Button("Reveal", (_, _) => Reveal(entry.QuarantinePath), icon: UiKit.Icon("folder"), compact: true));
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);

            var border = new Border
            {
                Background = UiKit.Brush("#101510"),
                BorderBrush = entry.Status == "Missing" ? UiKit.Red : UiKit.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 10, 9),
                Margin = new Thickness(0, 0, 0, 6),
                Child = grid
            };
            QuarantinePanel.Children.Add(border);
        }
    }

    private void RenderHistory()
    {
        HistoryPanel.Children.Clear();
        var recent = _comparisons.Recent(6);
        if (recent.Count == 0)
        {
            HistoryPanel.Children.Add(UiKit.Text("No comparison records yet. A comparison is stored only after you import both CSVs and press Compare.",
                size: 10.5, color: UiKit.Dim));
            return;
        }
        foreach (var record in recent)
        {
            var text = UiKit.Text(record.ShortText + " · " + (string.IsNullOrWhiteSpace(record.DisabledMods) ? "mod set not recorded" : record.DisabledMods),
                size: 10.5, color: UiKit.Dim);
            var button = UiKit.Button("Show", (_, _) => ShowComparison(record), compact: true);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(text);
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);
            HistoryPanel.Children.Add(new Border
            {
                BorderBrush = UiKit.Line,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 5, 0, 5),
                Child = grid
            });
        }
    }

    private void ShowComparison(ModComparisonRecord record)
    {
        ComparisonPanel.Children.Clear();
        ComparisonPanel.Children.Add(UiKit.Text($"{record.Label} · {record.CreatedAt:yyyy-MM-dd HH:mm}", "H3"));
        ComparisonPanel.Children.Add(UiKit.Text(record.ResultText, "Mono").WithMargin(0, 6, 0, 0));
        var warnings = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var warning in record.Warnings.Take(6))
            warnings.Children.Add(UiKit.Text("! " + warning, size: 9.5, color: UiKit.Amber));
        ComparisonPanel.Children.Add(warnings);
        ComparisonFiles.Text = $"Baseline: {record.BeforeFile}\nAfter: {record.AfterFile}";
    }

    private async Task ImportComparisonAsync(bool before)
    {
        var dialog = new OpenFileDialog
        {
            Title = before ? "Import baseline PresentMon capture" : "Import after PresentMon capture",
            Filter = "CSV capture (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var summary = await Task.Run(() => _comparisons.Import(dialog.FileName));
            if (before) _before = summary; else _after = summary;
            ComparisonFiles.Text = $"Baseline: {(_before == null ? "not imported" : Path.GetFileName(_before.SourceFile) + $" ({_before.Frames} frames, {_before.AverageFps:0.0} FPS)")}\n" +
                                   $"After: {(_after == null ? "not imported" : Path.GetFileName(_after.SourceFile) + $" ({_after.Frames} frames, {_after.AverageFps:0.0} FPS)")}";
            CompareButton.IsEnabled = _before != null && _after != null;
            _main.SetStatus($"Imported {(before ? "baseline" : "after")} capture: {Path.GetFileName(dialog.FileName)}", Severity.Info);
        }
        catch (Exception ex)
        {
            _main.ShowError("Could not import that PresentMon CSV:\n\n" + ex.Message);
        }
    }

    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_before == null || _after == null) return;
        var label = string.IsNullOrWhiteSpace(ComparisonLabel.Text) ? "Mod comparison" : ComparisonLabel.Text.Trim();
        var notes = ComparisonNotes.Text.Trim();
        var mods = string.IsNullOrWhiteSpace(_lastQuarantined)
            ? string.Join(", ", _selected.Select(p => Path.GetFileName(p.TrimEnd('\\'))).Take(12))
            : _lastQuarantined;
        var record = _comparisons.Compare(_before, _after, label, notes, mods);
        ShowComparison(record);
        RenderHistory();
        _main.SetStatus("Comparison saved. It applies to the tested condition, not to individual mods.", Severity.Info);
        MessageBox.Show(Window.GetWindow(this),
            "Comparison saved.\n\nIt applies to the whole tested condition. ZoneTune did not fabricate the numbers and cannot attribute the difference to one mod.\n\n" +
            string.Join("\n", record.Warnings.Take(4)),
            "Before / after comparison", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void QuarantineOne(ModPerformanceAnalysis mod)
    {
        if (_busy) return;
        _selected.Clear();
        _selected.Add(mod.Path);
        await QuarantineSelectedAsync(new List<ModPerformanceAnalysis> { mod });
    }

    private async void Quarantine_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _report == null) return;
        var selected = _report.Mods.Where(m => _selected.Contains(m.Path)).ToList();
        await QuarantineSelectedAsync(selected);
    }

    private async Task QuarantineSelectedAsync(List<ModPerformanceAnalysis> selected)
    {
        if (selected.Count == 0)
        {
            _main.ShowError("Select at least one mod first.");
            return;
        }
        var game = ResolveGame();
        if (!game.IsValid) { _main.ShowError("A valid game folder is required."); return; }
        if (new ConfigService().IsGameRunning())
        {
            _main.ShowError("Exit STALKER 2 before moving mods; the game holds file handles.");
            return;
        }

        var eligible = selected.Where(m => m.QuarantineEligible).ToList();
        var blocked = selected.Where(m => !m.QuarantineEligible).ToList();
        if (eligible.Count == 0)
        {
            _main.ShowError("None of the selected mods can be quarantined safely:\n\n" +
                            string.Join("\n", blocked.Take(6).Select(m => "• " + m.Name + ": " + m.QuarantineBlockReason)));
            return;
        }

        var size = eligible.Sum(m => m.SizeBytes);
        var message = $"Move {eligible.Count} selected mod(s) to the quarantine?\n\n" +
                      string.Join("\n", eligible.Take(12).Select(m => "• " + m.Name + " (" + m.SizeText + ")")) +
                      (eligible.Count > 12 ? $"\n…and {eligible.Count - 12} more" : "") +
                      $"\n\nTotal: {AppPaths.FormatBytes(size)}\nDestination: {_quarantine.Root}\n\n" +
                      "Safety: each file is copied, SHA-256 verified, then the original is removed from the load path. " +
                      "A manifest records the original location. Restore returns the files and never overwrites an existing path.\n\n" +
                      "Exit the game before continuing.";
        var notes = eligible.Where(m => !string.IsNullOrWhiteSpace(m.QuarantineNote))
            .Select(m => "• " + m.Name + ": " + m.QuarantineNote).ToList();
        if (notes.Count > 0)
            message += "\n\nWarnings:\n" + string.Join("\n", notes.Take(5));
        if (blocked.Count > 0)
            message += "\n\nSkipped (cannot move safely):\n" +
                       string.Join("\n", blocked.Take(5).Select(m => "• " + m.Name + ": " + m.QuarantineBlockReason));
        if (!_main.Confirm("Quarantine selected mods", message)) return;

        _busy = true;
        QuarantineButton.IsEnabled = false;
        AnalyzeProgress.Visibility = Visibility.Visible;
        AnalyzeProgress.IsIndeterminate = false;
        AnalyzeProgress.Value = 0;
        AnalyzeStatusText.Text = "Quarantining selected mods…";
        try
        {
            var progress = new Progress<OperationProgress>(p =>
            {
                AnalyzeStatusText.Text = $"{p.Stage}: {p.Detail}";
                AnalyzeProgress.IsIndeterminate = false;
                AnalyzeProgress.Value = Math.Clamp(p.Percent, 0, 100);
            });
            var result = await Task.Run(() => _quarantine.Quarantine(eligible, game, progress));
            _lastQuarantined = string.Join(", ", result.Entries.Where(e => e.Status == "Quarantined").Select(e => e.ModName));
            var text = result.Success ? "Quarantine completed.\n\n" : "Quarantine completed with notes.\n\n";
            text += result.Message + "\n";
            foreach (var warning in result.Warnings.Take(5)) text += "\n! " + warning;
            foreach (var error in result.Errors.Take(6)) text += "\n× " + error;
            MessageBox.Show(Window.GetWindow(this), text, "Quarantine", MessageBoxButton.OK,
                result.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            _main.SetStatus(result.Message, result.Errors.Count == 0 ? Severity.Notice : Severity.Warning);
            _selected.Clear();
            if (result.Entries.Any(e => e.Status == "Quarantined"))
                await RunAnalysisAsync(true, force: true);
            else
                RenderQuarantine();
        }
        catch (Exception ex)
        {
            Log.Error("Quarantine UI failed", ex);
            _main.ShowError("Quarantine failed:\n\n" + ex.Message);
        }
        finally
        {
            _busy = false;
            AnalyzeProgress.Visibility = Visibility.Collapsed;
            UpdateSelectionButtons();
            RenderQuarantine();
        }
    }

    private async void RestoreEntry(QuarantineEntry entry)
    {
        if (_busy) return;
        var game = ResolveGame();
        if (!game.IsValid) { _main.ShowError("A valid game folder is required."); return; }
        if (!_main.Confirm("Restore quarantined mod",
                $"Restore '{entry.ModName}' to its original location?\n\n{entry.OriginalPath}\n\n" +
                "Every quarantined file is hash-checked first. If the original path already exists, nothing is overwritten and the restore is refused."))
            return;

        _busy = true;
        try
        {
            var progress = new Progress<OperationProgress>(p =>
            {
                AnalyzeStatusText.Text = $"{p.Stage}: {p.Detail}";
                AnalyzeProgress.Visibility = Visibility.Visible;
                AnalyzeProgress.IsIndeterminate = false;
                AnalyzeProgress.Value = Math.Clamp(p.Percent, 0, 100);
            });
            var result = await Task.Run(() => _quarantine.Restore(entry.Id, game, progress));
            MessageBox.Show(Window.GetWindow(this),
                result.Message + "\n\n" + string.Join("\n", result.Errors.Take(6)) + string.Join("\n", result.Warnings.Take(3)),
                "Restore mod", MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            _main.SetStatus(result.Message, result.Success ? Severity.Notice : Severity.Warning);
            RenderQuarantine();
            if (result.Success) await RunAnalysisAsync(true, force: true);
        }
        catch (Exception ex)
        {
            _main.ShowError("Restore failed:\n\n" + ex.Message);
        }
        finally
        {
            _busy = false;
            AnalyzeProgress.Visibility = Visibility.Collapsed;
            UpdateSelectionButtons();
        }
    }

    private async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var game = ResolveGame();
        if (!game.IsValid) { _main.ShowError("A valid game folder is required."); return; }
        var active = _quarantine.ActiveEntries();
        if (active.Count == 0) { _main.ShowError("There are no active quarantine entries."); return; }
        if (!_main.Confirm("Restore all quarantined mods",
                $"Restore {active.Count} quarantined mod(s) to their original paths?\n\n" +
                "Each entry is hash-checked. Existing files are never overwritten; collisions are reported and left in quarantine."))
            return;

        _busy = true;
        try
        {
            var progress = new Progress<OperationProgress>(p =>
            {
                AnalyzeStatusText.Text = $"{p.Stage}: {p.Detail}";
                AnalyzeProgress.Visibility = Visibility.Visible;
                AnalyzeProgress.IsIndeterminate = false;
                AnalyzeProgress.Value = Math.Clamp(p.Percent, 0, 100);
            });
            var result = await Task.Run(() => _quarantine.RestoreAll(game, progress));
            MessageBox.Show(Window.GetWindow(this),
                result.Message + "\n\n" + string.Join("\n", result.Errors.Take(8)),
                "Restore quarantined mods", MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            _main.SetStatus(result.Message, result.Success ? Severity.Notice : Severity.Warning);
            RenderQuarantine();
            if (result.Success) await RunAnalysisAsync(true, force: true);
        }
        catch (Exception ex)
        {
            _main.ShowError("Restore failed:\n\n" + ex.Message);
        }
        finally
        {
            _busy = false;
            AnalyzeProgress.Visibility = Visibility.Collapsed;
            UpdateSelectionButtons();
        }
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => _ = RunAnalysisAsync(false);
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) RenderList(); }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) RenderList(); }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_report == null) return;
        foreach (var mod in _report.Mods.Where(m => m.QuarantineEligible)) _selected.Add(mod.Path);
        RenderList();
        UpdateSelectionButtons();
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _selected.Clear();
        RenderList();
        UpdateSelectionButtons();
    }

    private void OpenQuarantine_Click(object sender, RoutedEventArgs e) => _main.OpenFolder(_quarantine.Root);
    private void OpenComparisons_Click(object sender, RoutedEventArgs e) => _main.OpenFolder(_comparisons.DirectoryPath);
    private void ImportBefore_Click(object sender, RoutedEventArgs e) => _ = ImportComparisonAsync(true);
    private void ImportAfter_Click(object sender, RoutedEventArgs e) => _ = ImportComparisonAsync(false);

    private void Reveal(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            else _main.OpenFolder(Path.GetDirectoryName(path) ?? _quarantine.Root);
        }
        catch (Exception ex) { _main.ShowError("Could not reveal the path:\n" + ex.Message); }
    }
}
