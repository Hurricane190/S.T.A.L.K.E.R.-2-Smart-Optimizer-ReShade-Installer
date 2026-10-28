using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZoneTune.Core;
using ZoneTune.Core.Models;
using ZoneTune.Core.Services;

namespace ZoneTune.UI.Views;

public partial class OptimizeView : UserControl, IRefreshable
{
    private readonly MainWindow _main;
    private OptimizationPlan? _plan;
    private string? _appliedChangeSetId;

    public OptimizeView(MainWindow main)
    {
        InitializeComponent();
        _main = main;
    }

    public void SetProblems(IEnumerable<ProblemKind> problems, bool generate)
    {
        _main.SelectedProblems.Clear();
        foreach (var p in problems) _main.SelectedProblems.Add(p);
        RenderProblemSelectors();
        if (generate) _ = GenerateAsync();
    }

    public Task RefreshAsync()
    {
        RenderProblemSelectors();
        RenderProfile();
        RenderPlan();
        return Task.CompletedTask;
    }

    private void RenderProfile()
    {
        var scan = _main.Scan;
        if (scan == null || !scan.Game.IsValid)
        {
            ProfileHardware.Text = "scan required";
            ProfileBody.Text = "Scan the system to derive a balanced settings profile. Applying writes GameUserSettings.ini after a backup.";
            ApplyProfileButton.IsEnabled = false;
            PreviewProfileButton.IsEnabled = false;
            return;
        }
        var profile = _main.Optimizer.BuildRecommendedProfile(scan);
        ProfileHardware.Text = $"{scan.Hardware.GpuName} · {scan.Hardware.PrimaryResolution} @ {scan.Hardware.PrimaryRefreshHz} Hz · {HardwareInfo.FormatGb(scan.Hardware.VramMb)} VRAM";
        ProfileBody.Text = profile.Summary + $" {profile.ConfigActions.Count()} change(s) differ from the current configuration." +
                           (scan.GameRunning ? " Exit the game before applying." : "");
        ApplyProfileButton.IsEnabled = !scan.GameRunning;
        PreviewProfileButton.IsEnabled = true;
    }

    private void PreviewProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_main.Scan == null) return;
        _plan = _main.Optimizer.BuildRecommendedProfile(_main.Scan);
        _main.LastPlan = _plan;
        RenderPlan();
        _main.SetStatus("Recommended profile loaded into the plan. Review and press Apply when ready.", Severity.Info);
    }

    private void ApplyProfile_Click(object sender, RoutedEventArgs e) => _ = _main.ApplyRecommendedAsync();

    private void RenderProblemSelectors()
    {
        ProblemGrid.Children.Clear();
        foreach (var problem in new[] { ProblemKind.LoudPc, ProblemKind.LowFps, ProblemKind.Stuttering })
        {
            var selected = _main.SelectedProblems.Contains(problem);
            var (title, desc, icon, color) = problem switch
            {
                ProblemKind.LoudPc => ("Loud PC", "Heat, fan noise, unnecessary load", UiKit.Icon("volume"), UiKit.Amber),
                ProblemKind.LowFps => ("Low FPS", "Frame rate and visual-quality trade-offs", UiKit.Icon("fps"), UiKit.Green),
                _ => ("Stuttering", "Frame pacing, streaming, conflicts", UiKit.Icon("stutter"), UiKit.Blue)
            };
            var stack = new StackPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(UiKit.IconText(icon, 14, color));
            top.Children.Add(UiKit.Text(title, size: 13, weight: FontWeights.SemiBold).WithMargin(8, 0, 0, 0));
            stack.Children.Add(top);
            stack.Children.Add(UiKit.Text(desc, size: 10.5, color: UiKit.Dim).WithMargin(0, 4, 0, 0));

            var check = new CheckBox
            {
                IsChecked = selected,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 10, 0),
                Foreground = color
            };
            check.Checked += (_, _) => Toggle(problem, true);
            check.Unchecked += (_, _) => Toggle(problem, false);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(check);
            Grid.SetColumn(stack, 1);
            grid.Children.Add(stack);

            var border = new Border
            {
                Background = selected ? UiKit.Brush("#162017") : UiKit.Brush("#0F1511"),
                BorderBrush = selected ? color : UiKit.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(13, 11, 13, 11),
                Margin = new Thickness(0, 0, 10, 0),
                Child = grid,
                Cursor = Cursors.Hand
            };
            border.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is CheckBox) return;
                var newState = !_main.SelectedProblems.Contains(problem);
                Toggle(problem, newState);
            };
            ProblemGrid.Children.Add(border);
        }
        GenerateButton.IsEnabled = _main.SelectedProblems.Count > 0 && _main.Scan?.Game.IsValid == true;
    }

    private void Toggle(ProblemKind problem, bool selected)
    {
        if (selected) _main.SelectedProblems.Add(problem);
        else _main.SelectedProblems.Remove(problem);
        RenderProblemSelectors();
    }

    private void Generate_Click(object sender, RoutedEventArgs e) => _ = GenerateAsync();

    public Task GenerateAsync()
    {
        if (_main.Scan == null || !_main.Scan.Game.IsValid)
        {
            _main.ShowError("Run a successful scan first. A valid game folder is required for a plan.");
            return Task.CompletedTask;
        }
        if (_main.SelectedProblems.Count == 0)
        {
            _main.SetStatus("Select at least one problem.", Severity.Notice);
            return Task.CompletedTask;
        }

        var scan = _main.Scan;
        var problems = _main.SelectedProblems.ToList();
        _plan = _main.Optimizer.BuildPlan(scan, problems, DryRunCheck.IsChecked == true);
        _main.LastPlan = _plan;
        _appliedChangeSetId = null;
        RenderPlan();
        var config = _plan.ConfigActions.Count();
        _main.SetStatus($"Plan generated: {config} config change(s), {_plan.Recommendations.Count()} recommendation(s). Nothing has been written yet.", Severity.Info);
        return Task.CompletedTask;
    }

    private void RenderPlan()
    {
        PlanHost.Content = null;
        var scan = _main.Scan;
        if (scan == null)
        {
            PlanHost.Content = UiKit.Empty(UiKit.Icon("search"), "Scan required", "The optimizer needs the current config, hardware and mod state before it can build a plan.",
                UiKit.Button("Run scan", (_, _) => _ = _main.RunScanAsync(), primary: true));
            return;
        }
        if (_plan == null)
        {
            PlanHost.Content = UiKit.Empty(UiKit.Icon("tune"), "No plan yet", "Select the problems above and press Generate plan. Every action is shown with evidence before it can be applied.",
                null);
            return;
        }

        var root = new StackPanel();

        // Summary
        var summaryStack = new StackPanel();
        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(UiKit.Text("2 · REVIEW THE PLAN", "Overline"));
        left.Children.Add(UiKit.Text(_plan.Summary, "H3").WithMargin(0, 5, 0, 0));
        topRow.Children.Add(left);

        var chips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, MaxWidth = 320 };
        foreach (var p in _plan.Problems) chips.Children.Add(UiKit.Chip(p switch
        {
            ProblemKind.LoudPc => "Loud PC",
            ProblemKind.LowFps => "Low FPS",
            _ => "Stuttering"
        }, p switch
        {
            ProblemKind.LoudPc => UiKit.Amber,
            ProblemKind.LowFps => UiKit.Green,
            _ => UiKit.Blue
        }));
        Grid.SetColumn(chips, 1);
        topRow.Children.Add(chips);
        summaryStack.Children.Add(topRow);
        summaryStack.Children.Add(UiKit.Divider());
        summaryStack.Children.Add(UiKit.Text($"Created {_plan.CreatedAt:HH:mm} · {_plan.ConfigActions.Count()} reversible config edit(s) · {_plan.Recommendations.Count()} manual recommendation(s)",
            size: 11, color: UiKit.Dim));

        if (_plan.Warnings.Count > 0)
        {
            var warnPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var warning in _plan.Warnings)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(UiKit.Text("!", size: 11, color: UiKit.Amber, weight: FontWeights.Bold));
                row.Children.Add(UiKit.Text(warning, size: 11, color: UiKit.Amber).WithMargin(7, 0, 0, 0));
                warnPanel.Children.Add(row);
            }
            summaryStack.Children.Add(warnPanel);
        }

        var summaryCard = UiKit.Card(summaryStack, 18);
        root.Children.Add(summaryCard);

        // Actions
        var actionsStack = new StackPanel();
        var actionHeader = new Grid();
        actionHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var actionTitle = new StackPanel();
        actionTitle.Children.Add(UiKit.Text("CONFIG CHANGES", "Overline"));
        actionTitle.Children.Add(UiKit.Text("Only checked changes are applied. Each one is written into a backed-up change set.", size: 11, color: UiKit.Dim).WithMargin(0, 3, 0, 0));
        actionHeader.Children.Add(actionTitle);
        var counters = new TextBlock { FontSize = 11, Foreground = UiKit.Dim, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(counters, 1);
        actionHeader.Children.Add(counters);
        actionsStack.Children.Add(actionHeader);
        actionsStack.Children.Add(new Border { Height = 10 });

        foreach (var action in _plan.Actions.Where(a => a.Kind is ActionKind.GameSetting or ActionKind.EngineCvar))
            actionsStack.Children.Add(BuildActionCard(action, counters));

        // Recommendations
        var recs = _plan.Recommendations.ToList();
        if (recs.Count > 0)
        {
            actionsStack.Children.Add(UiKit.Text("MANUAL RECOMMENDATIONS", "Overline").WithMargin(0, 18, 0, 4));
            actionsStack.Children.Add(UiKit.Text("Not applied automatically. Do these in-game or in the vendor tool, then measure.", size: 11, color: UiKit.Dim));
            foreach (var rec in recs) actionsStack.Children.Add(BuildRecommendationCard(rec));
        }

        var actionCard = UiKit.Card(actionsStack, 18);
        actionCard.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(actionCard);

        // Apply panel
        var applyStack = new StackPanel();
        applyStack.Children.Add(UiKit.Text("3 · APPLY OR EXPORT", "Overline"));
        var buttonRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var applyButton = UiKit.Button("Apply selected changes", Apply_Click, primary: true, icon: UiKit.Icon("save"));
        var packageButton = UiKit.Button("Build ZIP package", Package_Click, icon: UiKit.Icon("zip"));
        var rescanButton = UiKit.Button("Rescan", (_, _) => _ = _main.RunScanAsync(), icon: UiKit.Icon("refresh"));
        buttonRow.Children.Add(applyButton);
        buttonRow.Children.Add(packageButton);
        buttonRow.Children.Add(rescanButton);
        if (_appliedChangeSetId != null)
            buttonRow.Children.Add(UiKit.Button("Restore this change set", Restore_Click, danger: true, icon: UiKit.Icon("undo")));
        applyStack.Children.Add(buttonRow);
        applyStack.Children.Add(UiKit.Text(
            _main.Settings.DryRunByDefault
                ? "Dry run is enabled in Settings: the plan is shown but Apply will not write files."
                : "Apply creates a timestamped backup first and refuses to run while the game is open. Building the ZIP writes nothing to the game.",
            size: 10.5, color: UiKit.Dim).WithMargin(0, 8, 0, 0));
        var applyCard = UiKit.Card(applyStack, 18, flat: true);
        applyCard.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(applyCard);

        PlanHost.Content = root;
        UpdateCounters(counters);
    }

    private void UpdateCounters(TextBlock counters)
    {
        if (_plan == null) { counters.Text = ""; return; }
        var config = _plan.ConfigActions.Count(a => a.Selected);
        counters.Text = $"{config} of {_plan.ConfigActions.Count()} selected";
    }

    private Border BuildActionCard(OptimizationAction action, TextBlock counters)
    {
        var stack = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox { IsChecked = action.Selected, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 11, 0) };
        check.Checked += (_, _) => { action.Selected = true; UpdateCounters(counters); };
        check.Unchecked += (_, _) => { action.Selected = false; UpdateCounters(counters); };
        header.Children.Add(check);

        var titleStack = new StackPanel();
        titleStack.Children.Add(UiKit.Text(action.Title, size: 12.5, weight: FontWeights.SemiBold));
        titleStack.Children.Add(UiKit.Text(action.ChangeText, size: 11, color: UiKit.Amber, weight: FontWeights.SemiBold).WithMargin(0, 3, 0, 0));
        Grid.SetColumn(titleStack, 1);
        header.Children.Add(titleStack);

        var badges = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        badges.Children.Add(UiKit.Badge(action.RiskText, action.Risk == RiskLevel.Medium ? Severity.Warning : Severity.Notice));
        badges.Children.Add(UiKit.Badge("ESTIMATE", Severity.Info).WithMargin(5, 0, 0, 0));
        Grid.SetColumn(badges, 2);
        header.Children.Add(badges);

        stack.Children.Add(header);
        stack.Children.Add(UiKit.Text(action.Description, size: 11.5, color: UiKit.Dim).WithMargin(26, 6, 0, 0));
        if (!string.IsNullOrWhiteSpace(action.Evidence))
            stack.Children.Add(new Border
            {
                Background = UiKit.Brush("#0B110D"),
                BorderBrush = UiKit.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(26, 7, 0, 0),
                Child = UiKit.Text("EVIDENCE · " + action.Evidence, size: 10.5, color: UiKit.Dim)
            });
        stack.Children.Add(UiKit.Text("EFFECT · " + action.EstimatedEffect, size: 10.5, color: UiKit.Brush("#758777")).WithMargin(26, 6, 0, 0));

        var border = new Border
        {
            BorderBrush = UiKit.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = UiKit.Brush("#0F1511"),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, 0, 0, 7),
            Child = stack
        };
        return border;
    }

    private Border BuildRecommendationCard(OptimizationAction action)
    {
        var stack = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(UiKit.IconText(UiKit.Icon("info"), 12, UiKit.Blue));
        header.Children.Add(UiKit.Text(action.Title, size: 12, weight: FontWeights.SemiBold).WithMargin(7, 0, 0, 0));
        stack.Children.Add(header);
        stack.Children.Add(UiKit.Text(action.Description, size: 11.5, color: UiKit.Dim).WithMargin(0, 5, 0, 0));
        stack.Children.Add(UiKit.Text("EXPECTED · " + action.EstimatedEffect, size: 10.5, color: UiKit.Brush("#758777")).WithMargin(0, 5, 0, 0));
        if (!string.IsNullOrWhiteSpace(action.Evidence))
            stack.Children.Add(UiKit.Text("EVIDENCE · " + action.Evidence, size: 10.5, color: UiKit.Dim).WithMargin(0, 4, 0, 0));
        return new Border
        {
            BorderBrush = UiKit.Brush("#1E2A21"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 9, 0, 9),
            Child = stack
        };
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null) return;
        if (_main.Scan?.GameRunning == true || new ConfigService().IsGameRunning())
        {
            _main.ShowError("STALKER 2 is running. Exit the game before applying changes; otherwise the game may overwrite GameUserSettings.ini on exit.");
            return;
        }
        var config = _plan.ConfigActions.Count(a => a.Selected);
        if (config == 0)
        {
            _main.SetStatus("No config changes are selected.", Severity.Notice);
            return;
        }
        if (_main.Settings.DryRunByDefault || DryRunCheck.IsChecked == true)
        {
            _main.SetStatus("Dry run is enabled; no files were written.", Severity.Notice);
            return;
        }
        if (!_main.Confirm("Apply optimization plan",
                $"{config} config change(s) will be written to the game's Saved config.\n\n" +
                "A timestamped backup/change set is created first. You can restore it from Settings & Backups.\n\n" +
                "Effects are estimates until measured. Continue?"))
            return;

        _plan.DryRun = false;
        var backups = _main.GetBackups();
        var result = _main.Optimizer.Apply(_plan, backups);
        _appliedChangeSetId = result.ChangeSetId;
        if (result.Success)
        {
            _main.SetStatus($"Applied {result.FilesChanged.Count} file(s). Backup: {result.ChangeSetId}", Severity.Notice);
            _main.ShowError("Changes applied and validated.\n\nBackup change set: " + result.ChangeSetId +
                            "\n\nExit and relaunch the game, then measure the same route. Restore from Settings & Backups if needed.");
        }
        else
        {
            _main.SetStatus("Apply failed or validation failed. Check the log.", Severity.Critical);
            _main.ShowError("The plan could not be applied cleanly.\n\n" +
                            string.Join("\n", result.Errors.Take(6)) +
                            "\n\nA rollback was attempted. Review Settings & Backups and the log.");
        }
        RenderPlan();
    }

    private void Package_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null || _main.Scan == null) return;
        try
        {
            var preset = _main.Catalog.GetBuiltInPresets(_main.Scan.Hardware).First();
            var path = new PackageBuilder().Build(_plan, _main.Scan, preset);
            _main.SetStatus("Package built: " + path, Severity.Notice);
            var open = _main.Confirm("Package ready",
                "ZIP created:\n" + path + "\n\nIt contains merged configs, an installer with backups, an uninstaller, checksums and this plan's evidence.\n\nOpen the folder?");
            if (open) _main.OpenFolder(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex)
        {
            _main.ShowError("Could not build the package:\n" + ex.Message);
            _main.SetStatus("Package build failed.", Severity.Critical);
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_appliedChangeSetId == null) return;
        if (new ConfigService().IsGameRunning())
        {
            _main.ShowError("STALKER 2 is running. Exit the game before restoring configuration files.");
            return;
        }
        if (!_main.Confirm("Restore change set", "Restore the previous config state from change set " + _appliedChangeSetId + "?\n\nA safety snapshot of the current state is made first."))
            return;
        var result = _main.GetBackups().Restore(_appliedChangeSetId);
        if (result.Success)
        {
            _main.SetStatus("Change set restored.", Severity.Notice);
            _main.ShowError("Restore completed.\n\n" + string.Join("\n", result.Messages.Take(8)));
        }
        else _main.ShowError("Restore completed with errors:\n\n" + string.Join("\n", result.Errors.Take(8)));
        _ = _main.RunScanAsync(silent: true);
    }
}


