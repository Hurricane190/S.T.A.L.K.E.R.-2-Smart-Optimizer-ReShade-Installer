using System.Windows.Controls.Primitives;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using ZoneTune.Core;
using ZoneTune.Core.Models;
using ZoneTune.Core.Services;
using IOPath = System.IO.Path;

namespace ZoneTune.UI.Views;

public partial class ReportsView : UserControl, IRefreshable
{
    private readonly MainWindow _main;
    private MeasurementSummary? _before;
    private MeasurementSummary? _after;
    private bool _ready;

    public ReportsView(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _ready = true;
    }

    public Task RefreshAsync()
    {
        RenderSummary();
        RenderMeasurements();
        RenderFindings();
        return Task.CompletedTask;
    }

    private void RenderSummary()
    {
        var scan = _main.Scan;
        if (scan == null)
        {
            SummaryText.Text = "Run a scan to build a report from this PC.";
            StatsGrid.Children.Clear();
            StatsGrid.Children.Add(Stat("SYSTEM", "Not scanned", "scan required", UiKit.Dim));
            StatsGrid.Children.Add(Stat("GAME", "Unknown", "detection pending", UiKit.Dim));
            StatsGrid.Children.Add(Stat("FINDINGS", "0", "no data", UiKit.Dim));
            StatsGrid.Children.Add(Stat("MEASUREMENT", "None", "import PresentMon", UiKit.Dim));
            return;
        }
        var critical = scan.Findings.Count(f => f.Severity == Severity.Critical);
        var warnings = scan.Findings.Count(f => f.Severity == Severity.Warning);
        SummaryText.Text = scan.Game.IsValid
            ? $"{critical} critical, {warnings} warning(s) · {scan.Mods.Count(m => m.Enabled)} enabled mod components · {(scan.ReShade.Installed ? "ReShade " + scan.ReShade.Version : "no ReShade")}"
            : "Game not detected. Detection findings are still included.";
        StatsGrid.Children.Clear();
        StatsGrid.Children.Add(Stat("FINDINGS", scan.Findings.Count.ToString(), $"{critical} critical · {warnings} warnings",
            critical > 0 ? UiKit.Red : warnings > 0 ? UiKit.Amber : UiKit.Green));
        StatsGrid.Children.Add(Stat("MODS", scan.Mods.Count(m => m.Enabled).ToString(), $"{scan.ModConflicts.Count} conflict notes",
            scan.ModConflicts.Count > 0 ? UiKit.Amber : UiKit.Dim));
        StatsGrid.Children.Add(Stat("RESHADE", scan.ReShade.Installed ? scan.ReShade.Version : "Not installed",
            scan.ReShade.Installed ? $"{scan.ReShade.ShaderCount} shaders" : "optional", scan.ReShade.Installed ? UiKit.Green : UiKit.Dim));
        StatsGrid.Children.Add(Stat("CAPTURE", _after != null ? "Before + after" : _before != null ? "Before only" : "None",
            _after != null ? "comparison ready" : "measure before claiming", _after != null ? UiKit.Green : UiKit.Amber));
    }

    private static Border Stat(string label, string value, string sub, Brush color)
    {
        var stack = new StackPanel();
        stack.Children.Add(UiKit.Text(label, "Overline"));
        stack.Children.Add(UiKit.Text(value, size: 17, weight: FontWeights.SemiBold, color: color).WithMargin(0, 3, 0, 1));
        stack.Children.Add(UiKit.Text(sub, size: 10, color: UiKit.Dim));
        var border = UiKit.Card(stack, 13, flat: true);
        border.Margin = new Thickness(0, 0, 8, 0);
        return border;
    }

    private void RenderMeasurements()
    {
        RenderSlot(BeforeHost, _before, "BEFORE", 0);
        RenderSlot(AfterHost, _after, "AFTER", 1);
        RenderComparison();
    }

    private void RenderSlot(ContentControl host, MeasurementSummary? summary, string label, int slot)
    {
        host.Content = null;
        if (summary == null)
        {
            var empty = UiKit.Empty(UiKit.Icon("gauge"), "No " + label.ToLowerInvariant() + " capture",
                slot == 0 ? "Capture the current config, then import the CSV." : "Apply a change, capture again, and compare.",
                UiKit.Button("Import CSV", (_, _) => Import(slot), compact: true));
            host.Content = empty;
            return;
        }

        var stack = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel();
        title.Children.Add(UiKit.Text(label, "Overline"));
        title.Children.Add(UiKit.Text(IOPath.GetFileName(summary.SourceFile), size: 11, color: UiKit.Dim).WithMargin(0, 3, 0, 0));
        header.Children.Add(title);
        var replace = UiKit.Button("Replace", (_, _) => Import(slot), compact: true);
        Grid.SetColumn(replace, 1);
        header.Children.Add(replace);
        stack.Children.Add(header);

        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 10, 0, 0) };
        stats.Children.Add(SmallStat("AVG FPS", $"{summary.AverageFps:0.0}", UiKit.Brush("#DEE7DC")));
        stats.Children.Add(SmallStat("1% LOW", $"{summary.OnePercentLowFps:0.0}", UiKit.Green));
        stats.Children.Add(SmallStat("0.1% LOW", $"{summary.PointOnePercentLowFps:0.0}", UiKit.Green));
        stats.Children.Add(SmallStat("P99", $"{summary.P99FrameMs:0.00} ms", UiKit.Amber));
        stats.Children.Add(SmallStat("STUTTER", $"{summary.StutterScore:0.0}", UiKit.Amber));
        stats.Children.Add(SmallStat("FRAMES", summary.Frames.ToString(), UiKit.Dim));
        stack.Children.Add(stats);
        stack.Children.Add(BuildChart(summary));
        host.Content = UiKit.Card(stack, 15);
    }

    private static Border SmallStat(string label, string value, Brush color)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        stack.Children.Add(UiKit.Text(label, size: 8.5, color: UiKit.Dim, weight: FontWeights.Bold));
        stack.Children.Add(UiKit.Text(value, size: 13, color: color, weight: FontWeights.SemiBold).WithMargin(0, 2, 0, 0));
        return new Border { Child = stack };
    }

    private static Canvas BuildChart(MeasurementSummary summary)
    {
        const double w = 350, h = 90;
        var canvas = new Canvas { Width = w, Height = h, Background = UiKit.Brush("#0B110D"), ClipToBounds = true, Margin = new Thickness(0, 10, 0, 0) };
        var values = summary.FrameTimesMs;
        if (values.Count < 2) return canvas;
        var min = values.Min();
        var max = values.Max();
        var range = Math.Max(0.1, max - min);
        var points = new PointCollection();
        for (var i = 0; i < values.Count; i++)
        {
            var x = 2 + (w - 4) * i / (double)(values.Count - 1);
            var y = h - 3 - (h - 6) * (values[i] - min) / range;
            points.Add(new Point(x, y));
        }
        var polyline = new Polyline
        {
            Points = points,
            Stroke = UiKit.Green,
            StrokeThickness = 1.1,
            Opacity = 0.9
        };
        canvas.Children.Add(polyline);
        var my = h - 3 - (h - 6) * (summary.MedianFrameMs - min) / range;
        canvas.Children.Add(new Line
        {
            X1 = 2, X2 = w - 2, Y1 = my, Y2 = my,
            Stroke = UiKit.Amber, StrokeThickness = 0.8, StrokeDashArray = new DoubleCollection { 3, 3 }, Opacity = 0.8
        });
        var label = UiKit.Text($"max {max:0.0} ms · median {summary.MedianFrameMs:0.0} ms", size: 8.5, color: UiKit.Dim);
        Canvas.SetLeft(label, 5);
        Canvas.SetTop(label, 3);
        canvas.Children.Add(label);
        return canvas;
    }

    private void RenderComparison()
    {
        ComparisonHost.Content = null;
        if (_before == null || _after == null)
        {
            ComparisonHost.Content = new Border
            {
                Background = UiKit.Brush("#0F1511"),
                BorderBrush = UiKit.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(13, 9, 13, 9),
                Child = UiKit.Text("Import both captures to see a comparison. Same route, same settings except the change under test.", size: 10.5, color: UiKit.Dim)
            };
            return;
        }

        var text = MeasurementService.Compare(_before, _after);
        var stack = new StackPanel();
        stack.Children.Add(UiKit.Text("BEFORE → AFTER", "Overline"));
        foreach (var line in text.Split('\n'))
            stack.Children.Add(UiKit.Text(line, size: 11, color: line.Contains(':') ? UiKit.Brush("#C7D3C6") : UiKit.Dim).WithMargin(0, 3, 0, 0));
        stack.Children.Add(UiKit.Text("Interpretation is invalid if the route, settings, background load or warm-up differed.", size: 10, color: UiKit.Amber).WithMargin(0, 7, 0, 0));
        ComparisonHost.Content = UiKit.Card(stack, 14);
    }

    private void Import(int slot)
    {
        var dialog = new OpenFileDialog
        {
            Title = slot == 0 ? "Import BEFORE capture (PresentMon CSV)" : "Import AFTER capture (PresentMon CSV)",
            Filter = "PresentMon CSV (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var summary = _main.Measurements.ImportPresentMon(dialog.FileName);
            if (slot == 0) _before = summary;
            else _after = summary;
            _main.SetStatus($"Imported {(slot == 0 ? "before" : "after")} capture: {summary.Frames} frames, {summary.AverageFps:0.0} FPS average.", Severity.Info);
            RenderSummary();
            RenderMeasurements();
        }
        catch (Exception ex)
        {
            _main.ShowError("Could not import that CSV:\n" + ex.Message);
        }
    }

    private void RenderFindings()
    {
        FindingsPanel.Children.Clear();
        var scan = _main.Scan;
        if (scan == null)
        {
            FindingsPanel.Children.Add(UiKit.Empty(UiKit.Icon("report"), "No findings", "Run a scan first.", null));
            return;
        }
        var filter = (SeverityFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All severities";
        var min = filter switch
        {
            "Critical only" => Severity.Critical,
            "Warnings+" => Severity.Warning,
            "Notices+" => Severity.Notice,
            _ => Severity.Info
        };
        var findings = scan.Findings.Where(f => f.Severity >= min).ToList();
        if (findings.Count == 0)
        {
            FindingsPanel.Children.Add(UiKit.Empty(UiKit.Icon("check"), "Nothing at this severity", "Lower the filter to see informational findings.", null));
            return;
        }
        foreach (var finding in findings)
        {
            var stack = new StackPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(UiKit.Badge(finding.Severity.ToString(), finding.Severity));
            top.Children.Add(UiKit.Text(finding.Category.ToUpperInvariant(), size: 8.5, color: UiKit.Dim, weight: FontWeights.Bold).WithMargin(8, 0, 0, 0));
            top.Children.Add(UiKit.Text(finding.Title, size: 12.5, weight: FontWeights.SemiBold).WithMargin(8, 0, 0, 0));
            stack.Children.Add(top);
            stack.Children.Add(UiKit.Text(finding.Detail, size: 11, color: UiKit.Dim).WithMargin(0, 5, 0, 0));
            if (!string.IsNullOrWhiteSpace(finding.Evidence))
                stack.Children.Add(new Border
                {
                    Background = UiKit.Brush("#0B110D"),
                    BorderBrush = UiKit.Line,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(9, 6, 9, 6),
                    Margin = new Thickness(0, 6, 0, 0),
                    Child = UiKit.Text(finding.Evidence, "Mono")
                });
            if (!string.IsNullOrWhiteSpace(finding.Recommendation))
                stack.Children.Add(UiKit.Text("NEXT · " + finding.Recommendation, size: 10.5, color: UiKit.Brush("#A9B8AA")).WithMargin(0, 6, 0, 0));
            FindingsPanel.Children.Add(new Border
            {
                BorderBrush = UiKit.SeverityColor(finding.Severity),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Background = UiKit.Brush("#0F1511"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 7),
                Child = stack
            });
        }
    }

    private void ImportMeasurement_Click(object sender, RoutedEventArgs e) => Import(_before == null ? 0 : 1);

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_main.Scan == null)
        {
            _main.ShowError("Run a scan before exporting a report.");
            return;
        }
        try
        {
            var paths = _main.Reports.Generate(_main.Scan, _after ?? _before, _main.LastPlan);
            _main.SetStatus("Report exported: " + paths.Html, Severity.Notice);
            if (_main.Settings.AutoOpenReport || _main.Confirm("Report exported", "HTML report created:\n" + paths.Html + "\n\nOpen it now?"))
                _main.OpenUrl(paths.Html);
        }
        catch (Exception ex)
        {
            _main.ShowError("Could not export the report:\n" + ex.Message);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _main.OpenFolder(AppPaths.ReportsDir);
    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = _main.RunScanAsync();
    private void Severity_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) RenderFindings(); }
}


