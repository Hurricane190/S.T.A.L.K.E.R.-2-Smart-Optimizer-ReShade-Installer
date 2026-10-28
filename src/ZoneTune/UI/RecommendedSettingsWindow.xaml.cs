using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZoneTune.Core.Models;

namespace ZoneTune.UI;

public partial class RecommendedSettingsWindow : Window
{
    private readonly OptimizationPlan _plan;

    public RecommendedSettingsWindow(MainWindow main, OptimizationPlan plan)
    {
        InitializeComponent();
        _plan = plan;

        SummaryText.Text = plan.Summary;
        HardwareText.Text = $"{plan.Hardware?.GpuName} · {plan.Hardware?.PrimaryResolution} @ {plan.Hardware?.PrimaryRefreshHz} Hz · " +
                            $"{HardwareInfo.FormatGb(plan.Hardware?.VramMb ?? 0)} VRAM";
        BadgePanel.Children.Clear();
        BadgePanel.Children.Add(UiKit.Chip(plan.Hardware?.GpuTier + " class", UiKit.Green));
        BadgePanel.Children.Add(UiKit.Chip(plan.Problems.Contains(ProblemKind.LoudPc) ? "quiet + balanced" : "balanced", UiKit.Dim));

        foreach (var action in plan.ConfigActions)
            RowsPanel.Children.Add(BuildRow(action));

        var recommendations = plan.Recommendations.ToList();
        if (recommendations.Count > 0)
        {
            var stack = new StackPanel();
            stack.Children.Add(UiKit.Text("MANUAL STEPS (not applied)", "Overline").WithMargin(0, 4, 0, 4));
            foreach (var rec in recommendations)
                stack.Children.Add(UiKit.Text("• " + rec.Title + " — " + rec.EstimatedEffect, size: 10.5, color: UiKit.Dim).WithMargin(0, 2, 0, 0));
            ManualPanel.Children.Add(stack);
        }

        UpdateSelection();
    }

    private Border BuildRow(OptimizationAction action)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

        var check = new CheckBox { IsChecked = action.Selected, VerticalAlignment = VerticalAlignment.Center };
        check.Checked += (_, _) => { action.Selected = true; UpdateSelection(); };
        check.Unchecked += (_, _) => { action.Selected = false; UpdateSelection(); };
        grid.Children.Add(check);

        var nameStack = new StackPanel { Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        nameStack.Children.Add(UiKit.Text(FriendlyName(action.Key), size: 11.5, weight: FontWeights.SemiBold));
        nameStack.Children.Add(UiKit.Text(action.RiskText + " · " + action.EstimatedEffect, size: 9.5, color: UiKit.Dim));
        Grid.SetColumn(nameStack, 1);
        grid.Children.Add(nameStack);

        var current = UiKit.Text(Display(action.CurrentValue, action.Key), size: 10.5, color: UiKit.Dim);
        current.HorizontalAlignment = HorizontalAlignment.Right;
        current.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(current, 2);
        grid.Children.Add(current);

        var arrow = UiKit.Text("→", size: 11, color: UiKit.Amber);
        arrow.HorizontalAlignment = HorizontalAlignment.Center;
        arrow.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(arrow, 3);
        grid.Children.Add(arrow);

        var proposed = UiKit.Text(Display(action.ProposedValue, action.Key), size: 11.5, color: UiKit.Amber, weight: FontWeights.SemiBold);
        proposed.HorizontalAlignment = HorizontalAlignment.Right;
        proposed.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(proposed, 4);
        grid.Children.Add(proposed);

        return new Border
        {
            BorderBrush = UiKit.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 9, 0, 9),
            Child = grid
        };
    }

    private void UpdateSelection()
    {
        var count = _plan.ConfigActions.Count(a => a.Selected);
        var total = _plan.ConfigActions.Count();
        SelectionText.Text = count == total ? $"All {total} setting(s) selected." : $"{count} of {total} setting(s) selected.";
        ApplyButton.IsEnabled = count > 0;
        ApplyButton.Content = count == 0 ? "Apply settings" : $"Apply {count} setting(s)";
    }

    private static string FriendlyName(string key)
    {
        if (key.Equals("FrameRateLimit", StringComparison.OrdinalIgnoreCase)) return "Frame rate limit";
        if (key.Equals("bUseVSync", StringComparison.OrdinalIgnoreCase)) return "VSync";
        if (key.Equals("bUseDynamicResolution", StringComparison.OrdinalIgnoreCase)) return "Dynamic resolution";
        if (key.Equals("r.Streaming.PoolSize", StringComparison.OrdinalIgnoreCase)) return "Texture streaming pool (MB)";
        if (key.Equals("r.Streaming.LimitPoolSizeToVRAM", StringComparison.OrdinalIgnoreCase)) return "Limit pool to VRAM";
        var text = key.Replace("sg.", "", StringComparison.OrdinalIgnoreCase).Replace("Quality", "", StringComparison.OrdinalIgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "([a-z])([A-Z])", "$1 $2");
        return key.StartsWith("sg.", StringComparison.OrdinalIgnoreCase) ? text.Trim() + " quality" : text.Trim();
    }

    private static string Display(string value, string key)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("not set", StringComparison.OrdinalIgnoreCase)) return "Not set";
        if (value.Equals("True", StringComparison.OrdinalIgnoreCase)) return "On";
        if (value.Equals("False", StringComparison.OrdinalIgnoreCase)) return "Off";
        if (key.Equals("FrameRateLimit", StringComparison.OrdinalIgnoreCase) && value == "0") return "Off";
        if (key.Equals("r.Streaming.LimitPoolSizeToVRAM", StringComparison.OrdinalIgnoreCase))
            return value == "1" ? "On" : "Off";
        if (int.TryParse(value, out var n) && n is >= 0 and <= 3)
            return n switch { 0 => "0 · Low", 1 => "1 · Medium", 2 => "2 · High", _ => "3 · Epic" };
        return value;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }
}
