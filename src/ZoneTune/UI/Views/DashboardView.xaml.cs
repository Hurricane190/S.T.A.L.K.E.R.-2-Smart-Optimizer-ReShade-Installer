using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZoneTune.Core;
using ZoneTune.Core.Models;

namespace ZoneTune.UI.Views;

public partial class DashboardView : UserControl, IRefreshable
{
    private readonly MainWindow _main;
    private readonly Dictionary<ProblemKind, Border> _problemCards = new();

    public DashboardView(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        HeroActions.Children.Clear();
        HeroActions.Children.Add(UiKit.Button("Apply recommended settings", (_, _) => _ = _main.ApplyRecommendedAsync(), primary: true, icon: UiKit.Icon("tune")));
        HeroActions.Children.Add(UiKit.Button("Scan again", (_, _) => _ = _main.RunScanAsync(), icon: UiKit.Icon("refresh")));
        HeroActions.Children.Add(UiKit.Button("Undo last change", (_, _) => _ = _main.RestoreLatestAsync(), icon: UiKit.Icon("undo")));
        HeroActions.Children.Add(UiKit.Button("Open game folder", (_, _) => _main.OpenFolder(_main.LastKnownGame.Root), icon: UiKit.Icon("folder")));
    }

    public Task RefreshAsync()
    {
        RenderHero();
        RenderSystem();
        RenderProfileCard();
        RenderProblems();
        RenderFindings();
        RenderEnvironment();
        return Task.CompletedTask;
    }

    private void RenderHero()
    {
        var scan = _main.Scan;
        var hour = DateTime.Now.Hour;
        var greeting = hour < 5 ? "Still in the Zone?" : hour < 12 ? "Good morning, Stalker." : hour < 18 ? "Afternoon in the Zone." : "Evening, Stalker.";
        HeroTitle.Text = scan?.Game.IsValid == true ? greeting : "Your Zone, understood.";
        HeroTag.Text = scan == null ? "NOT SCANNED" : scan.Game.IsValid ? "ZONE READINESS" : "GAME NOT FOUND";
        HeroTag.Foreground = scan?.Game.IsValid == true ? UiKit.Green : UiKit.Red;
        HeroScanTime.Text = scan == null ? "" : $"Last scan {scan.ScannedAt:HH:mm} · {scan.Findings.Count} findings";

        HeroBody.Text = scan == null
            ? "Scan the installation to map hardware, game configuration, mods and ReShade. Nothing is changed until you approve a plan."
            : scan.Game.IsValid
                ? $"{scan.Hardware.GpuName} · {scan.Hardware.CpuName} · {scan.Hardware.PrimaryResolution} @ {scan.Hardware.PrimaryRefreshHz} Hz. " +
                  $"{scan.Mods.Count(m => m.Enabled)} mod components and {(scan.ReShade.Installed ? $"ReShade {scan.ReShade.Version}" : "no ReShade")} detected."
                : "STALKER 2 was not found. Choose the game folder manually to continue; no changes can be made without a valid install.";

        HeroStats.Children.Clear();
        if (scan != null && scan.Game.IsValid)
        {
            HeroStats.Children.Add(MiniStat("FRAME CAP", scan.Config.FrameRateLimit <= 0 ? "Unlimited" : $"{scan.Config.FrameRateLimit:0} FPS",
                scan.Config.FrameRateLimit <= 0 ? "heat risk" : "capped", scan.Config.FrameRateLimit <= 0 ? UiKit.Amber : UiKit.Green));
            HeroStats.Children.Add(MiniStat("RESHADE", scan.ReShade.Installed ? scan.ReShade.Version : "Not installed",
                scan.ReShade.Installed ? $"{scan.ReShade.PresetTechniques.Count} techniques" : "optional", scan.ReShade.Installed ? UiKit.Green : UiKit.Dim));
            HeroStats.Children.Add(MiniStat("WARNINGS", scan.Findings.Count(f => f.Severity >= Severity.Warning).ToString(),
                "review before tuning", scan.Findings.Any(f => f.Severity == Severity.Critical) ? UiKit.Red : UiKit.Amber));
        }
        else
        {
            HeroStats.Children.Add(MiniStat("HARDWARE", _main.LastKnownGame.IsValid ? "Detected" : "Unknown", "scan required", UiKit.Dim));
        }
    }

    private static Border MiniStat(string label, string value, string sub, Brush accent)
    {
        var stack = new StackPanel { MinWidth = 150 };
        stack.Children.Add(UiKit.Text(label, "Overline"));
        stack.Children.Add(UiKit.Text(value, size: 16, weight: FontWeights.SemiBold, color: accent).WithMargin(0, 2, 0, 0));
        stack.Children.Add(UiKit.Text(sub, size: 10.5, color: UiKit.Dim));
        return new Border
        {
            BorderBrush = UiKit.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack
        };
    }

    private void RenderSystem()
    {
        SystemGrid.Children.Clear();
        var scan = _main.Scan;
        if (scan == null)
        {
            SystemGrid.Children.Add(StatCard("SYSTEM", "Not scanned", "Run a scan to populate hardware details."));
            SystemGrid.Children.Add(StatCard("GAME", "Unknown", "Detection pending."));
            SystemGrid.Children.Add(StatCard("RESHADE", "Unknown", "Detection pending."));
            SystemGrid.Children.Add(StatCard("STORAGE", "Unknown", "Detection pending."));
            return;
        }

        var hw = scan.Hardware;
        SystemGrid.Children.Add(StatCard("GPU", hw.GpuName, $"{HardwareInfo.FormatGb(hw.VramMb)} VRAM · {hw.GpuVendor}"));
        SystemGrid.Children.Add(StatCard("CPU", hw.CpuName, $"{hw.PhysicalCores}C / {hw.LogicalCores}T · {hw.RamSummary} RAM"));
        SystemGrid.Children.Add(StatCard("DISPLAY", $"{hw.PrimaryResolution} @ {hw.PrimaryRefreshHz} Hz",
            hw.HdrEnabled ? "HDR enabled" : hw.AnyHdr ? "HDR capable (off)" : "SDR"));
        SystemGrid.Children.Add(StatCard("GAME DRIVE", hw.GameDriveTotalBytes > 0 ? AppPaths.FormatBytes(hw.GameDriveFreeBytes) + " free" : "Unknown",
            hw.GameDriveTotalBytes > 0 ? $"{hw.GameDriveName}: · {AppPaths.FormatBytes(hw.GameDriveTotalBytes)} total" : "Path unavailable"));
    }

    private static Border StatCard(string label, string value, string sub)
    {
        var stack = new StackPanel();
        stack.Children.Add(UiKit.Text(label, "Overline"));
        stack.Children.Add(UiKit.Text(value, size: 13.5, weight: FontWeights.SemiBold).WithMargin(0, 4, 0, 2));
        stack.Children.Add(UiKit.Text(sub, size: 10.5, color: UiKit.Dim));
        var border = UiKit.Card(stack, 13, flat: true);
        border.Margin = new Thickness(0, 0, 8, 0);
        return border;
    }

    private void RenderProfileCard()
    {
        ProfileCardHost.Content = null;
        var scan = _main.Scan;
        if (scan == null || !scan.Game.IsValid) return;
        var profile = _main.Optimizer.BuildRecommendedProfile(scan);
        var changes = profile.ConfigActions.Count();
        var cap = profile.ConfigActions.FirstOrDefault(a => a.Key == "FrameRateLimit")?.ProposedValue ?? "keep current";
        var shadows = profile.ConfigActions.FirstOrDefault(a => a.Key == "sg.ShadowQuality")?.ProposedValue;
        var gi = profile.ConfigActions.FirstOrDefault(a => a.Key == "sg.GlobalIlluminationQuality")?.ProposedValue;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        var overline = new StackPanel { Orientation = Orientation.Horizontal };
        overline.Children.Add(UiKit.Text("RECOMMENDED IN-GAME PROFILE", "Overline"));
        overline.Children.Add(UiKit.Chip($"{scan.Hardware.GpuTier} · {scan.Hardware.PrimaryResolution}", UiKit.Green).WithMargin(10, 0, 0, 0));
        text.Children.Add(overline);
        text.Children.Add(UiKit.Text($"{changes} change(s) suggested. Frame cap {cap} FPS" +
                                     (shadows != null ? $" · shadows {(int.TryParse(shadows, out var s) ? s : 3)}/3" : "") +
                                     (gi != null ? $" · GI {(int.TryParse(gi, out var g) ? g : 3)}/3" : "") +
                                     " · balanced for this GPU.", size: 11.5, color: UiKit.Dim).WithMargin(0, 4, 0, 0));
        grid.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(UiKit.Button("Review", (_, _) => _main.GoOptimize(new[] { ProblemKind.LowFps, ProblemKind.Stuttering }, generate: false), icon: UiKit.Icon("tune"), compact: true));
        var apply = UiKit.Button("Apply now", (_, _) => _ = _main.ApplyRecommendedAsync(), primary: true, icon: UiKit.Icon("save"), compact: true);
        apply.IsEnabled = !scan.GameRunning && changes > 0;
        actions.Children.Add(apply);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        var card = UiKit.Card(grid, 14);
        card.Background = UiKit.Brush("#101711");
        ProfileCardHost.Content = card;
    }

    private void RenderProblems()
    {
        ProblemGrid.Children.Clear();
        _problemCards.Clear();
        var scan = _main.Scan;
        foreach (var problem in new[] { ProblemKind.LoudPc, ProblemKind.LowFps, ProblemKind.Stuttering })
        {
            var (title, desc, icon, color) = problem switch
            {
                ProblemKind.LoudPc => ("Loud PC", "Reduce unnecessary GPU load, heat and fan noise without gutting visual quality.", UiKit.Icon("volume"), UiKit.Amber),
                ProblemKind.LowFps => ("Low FPS", "Improve frame rate with the highest value-per-visual-cost settings and upscaling guidance.", UiKit.Icon("fps"), UiKit.Green),
                _ => ("Stuttering", "Investigate frame-time spikes, streaming pressure and mod conflicts instead of chasing averages.", UiKit.Icon("stutter"), UiKit.Blue)
            };
            var count = scan?.Findings.Count(f => f.RelatedProblems.Contains(problem)) ?? 0;
            var selected = _main.SelectedProblems.Contains(problem);
            var card = BuildProblemCard(problem, title, desc, icon, color, count, selected);
            _problemCards[problem] = card;
            ProblemGrid.Children.Add(card);
        }
        var selectedCount = _main.SelectedProblems.Count;
        OptimizeSelectedButton.Content = selectedCount == 0 ? "Select a problem →" : $"Optimize {selectedCount} selected →";
        OptimizeSelectedButton.IsEnabled = selectedCount > 0 && _main.Scan?.Game.IsValid == true;
    }

    private Border BuildProblemCard(ProblemKind problem, string title, string desc, string icon, Brush color, int count, bool selected)
    {
        var outer = new Border
        {
            Background = selected ? UiKit.Brush("#162017") : UiKit.Panel,
            BorderBrush = selected ? color : UiKit.Line,
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 10, 0),
            Cursor = Cursors.Hand
        };
        var stack = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBorder = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(32, ((SolidColorBrush)color).Color.R, ((SolidColorBrush)color).Color.G, ((SolidColorBrush)color).Color.B)),
            Child = UiKit.IconText(icon, 16, color)
        };
        top.Children.Add(iconBorder);

        var titleStack = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(UiKit.Text(title, size: 14.5, weight: FontWeights.SemiBold));
        titleStack.Children.Add(UiKit.Text(count == 0 ? "No direct findings" : $"{count} related finding(s)", size: 10.5, color: UiKit.Dim));
        Grid.SetColumn(titleStack, 1);
        top.Children.Add(titleStack);

        var check = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            BorderBrush = selected ? color : UiKit.Brush("#3C4C3F"),
            BorderThickness = new Thickness(1),
            Background = selected ? color : Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Top,
            Child = selected ? UiKit.IconText(UiKit.Icon("check"), 9, UiKit.Brush("#10150F")) : null
        };
        Grid.SetColumn(check, 2);
        top.Children.Add(check);
        stack.Children.Add(top);
        stack.Children.Add(UiKit.Text(desc, "BodyDim", color: UiKit.Dim).WithMargin(0, 10, 0, 0));
        outer.Child = stack;

        outer.MouseLeftButtonUp += (_, _) =>
        {
            if (_main.SelectedProblems.Contains(problem)) _main.SelectedProblems.Remove(problem);
            else _main.SelectedProblems.Add(problem);
            RenderProblems();
        };
        outer.MouseEnter += (_, _) => { if (!_main.SelectedProblems.Contains(problem)) outer.BorderBrush = UiKit.Brush("#3A4C3D"); };
        outer.MouseLeave += (_, _) => { if (!_main.SelectedProblems.Contains(problem)) outer.BorderBrush = UiKit.Line; };
        return outer;
    }

    private void RenderFindings()
    {
        FindingsPanel.Children.Clear();
        var scan = _main.Scan;
        if (scan == null)
        {
            FindingsPanel.Children.Add(UiKit.Empty(UiKit.Icon("search"), "No scan yet", "Run a scan to collect evidence from this PC.", null));
            return;
        }
        var findings = scan.Findings.Where(f => f.Severity >= Severity.Notice)
            .OrderByDescending(f => f.Severity).Take(5).ToList();
        if (findings.Count == 0)
        {
            FindingsPanel.Children.Add(UiKit.Empty(UiKit.Icon("check"), "No urgent findings", "The scan did not find a critical configuration or log problem. Measurement is still required.", null));
            return;
        }
        foreach (var finding in findings)
        {
            var color = UiKit.SeverityColor(finding.Severity);
            var stack = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(UiKit.Badge(finding.Severity.ToString(), finding.Severity));
            header.Children.Add(UiKit.Text(finding.Title, size: 12.5, weight: FontWeights.SemiBold).WithMargin(8, 0, 0, 0));
            stack.Children.Add(header);
            var detail = finding.Detail.Length > 190 ? finding.Detail[..190] + "…" : finding.Detail;
            stack.Children.Add(UiKit.Text(detail, size: 11.5, color: UiKit.Dim).WithMargin(0, 5, 0, 0));
            var border = new Border
            {
                BorderBrush = color,
                BorderThickness = new Thickness(2, 0, 0, 0),
                Background = UiKit.Brush("#0F1511"),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 7),
                CornerRadius = new CornerRadius(4),
                Child = stack,
                Cursor = Cursors.Hand
            };
            border.MouseLeftButtonUp += (_, _) => _main.Navigate("reports");
            FindingsPanel.Children.Add(border);
        }
    }

    private void RenderEnvironment()
    {
        EnvironmentPanel.Children.Clear();
        var scan = _main.Scan;
        if (scan == null)
        {
            EnvironmentPanel.Children.Add(UiKit.Empty(UiKit.Icon("info"), "Waiting for scan", "Environment details appear after detection.", null));
            QuickActionsPanel.Children.Clear();
            return;
        }

        var rs = scan.ReShade;
        var rsStack = new StackPanel();
        rsStack.Children.Add(UiKit.Text("RESHADE", "Overline"));
        rsStack.Children.Add(UiKit.Text(rs.Installed ? $"ReShade {rs.Version}" : "Not installed", size: 13, weight: FontWeights.SemiBold).WithMargin(0, 3, 0, 2));
        rsStack.Children.Add(UiKit.Text(rs.Installed ? $"{rs.PresetName} · {rs.PresetTechniques.Count} techniques" : "Optional post-processing layer", size: 10.5, color: UiKit.Dim));
        if (rs.HdrConflict) rsStack.Children.Add(UiKit.Badge("HDR CONFLICT", Severity.Warning).WithMargin(0, 6, 0, 0));
        var rsCard = UiKit.Card(rsStack, 13, flat: true);
        rsCard.Margin = new Thickness(0, 0, 0, 8);
        rsCard.Cursor = Cursors.Hand;
        rsCard.MouseLeftButtonUp += (_, _) => _main.Navigate("reshade");
        EnvironmentPanel.Children.Add(rsCard);

        var modsStack = new StackPanel();
        modsStack.Children.Add(UiKit.Text("MODS", "Overline"));
        modsStack.Children.Add(UiKit.Text($"{scan.Mods.Count(m => m.Enabled)} enabled components", size: 13, weight: FontWeights.SemiBold).WithMargin(0, 3, 0, 2));
        modsStack.Children.Add(UiKit.Text(scan.ModConflicts.Count > 0 ? $"{scan.ModConflicts.Count} conflict note(s) — review" : "No conflict notes detected", size: 10.5,
            color: scan.ModConflicts.Count > 0 ? UiKit.Amber : UiKit.Dim));
        var modsCard = UiKit.Card(modsStack, 13, flat: true);
        modsCard.Margin = new Thickness(0, 0, 0, 8);
        modsCard.Cursor = Cursors.Hand;
        modsCard.MouseLeftButtonUp += (_, _) => _main.Navigate("mods");
        EnvironmentPanel.Children.Add(modsCard);

        var gameStack = new StackPanel();
        gameStack.Children.Add(UiKit.Text("GAME INSTALL", "Overline"));
        gameStack.Children.Add(UiKit.Text(scan.Game.IsValid ? scan.Game.Source.ToString() : "Not found", size: 13, weight: FontWeights.SemiBold).WithMargin(0, 3, 0, 2));
        gameStack.Children.Add(UiKit.Text(scan.Game.Root, size: 10, color: UiKit.Dim));
        var gameCard = UiKit.Card(gameStack, 13, flat: true);
        gameCard.Cursor = Cursors.Hand;
        gameCard.MouseLeftButtonUp += (_, _) => _main.OpenFolder(scan.Game.Root);
        EnvironmentPanel.Children.Add(gameCard);

        QuickActionsPanel.Children.Clear();
        QuickActionsPanel.Children.Add(UiKit.Button("Reports", (_, _) => _main.Navigate("reports"), icon: UiKit.Icon("report"), compact: true));
        QuickActionsPanel.Children.Add(UiKit.Button("Backups", (_, _) => _main.Navigate("settings"), icon: UiKit.Icon("shield"), compact: true));
    }

    private void OptimizeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_main.SelectedProblems.Count == 0)
        {
            _main.SetStatus("Select at least one problem first.", Severity.Notice);
            return;
        }
        _main.GoOptimize(_main.SelectedProblems, generate: true);
    }

    private void OpenReports_Click(object sender, RoutedEventArgs e) => _main.Navigate("reports");
}

