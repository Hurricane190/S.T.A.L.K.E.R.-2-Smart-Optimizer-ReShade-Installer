using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZoneTune.Core;
using ZoneTune.Core.Models;
using ZoneTune.Core.Services;
using ZoneTune.UI.Views;

namespace ZoneTune;

public interface IRefreshable
{
    Task RefreshAsync();
}

public partial class MainWindow : Window
{
    public AppSettings Settings { get; private set; }
    public ScanResult? Scan { get; private set; }
    public OptimizationPlan? LastPlan { get; set; }
    public GameInstall LastKnownGame { get; private set; } = new();
    public HashSet<ProblemKind> SelectedProblems { get; } = new();

    public SettingsStore SettingsStore { get; } = new();
    public ScanService ScanService { get; } = new();
    public OptimizationEngine Optimizer { get; } = new();
    public PresetCatalog Catalog { get; } = new();
    public ReShadeInstaller ReShadeInstaller { get; } = new();
    public ReportService Reports { get; } = new();
    public MeasurementService Measurements { get; } = new();
    public ModService Mods { get; } = new();
    public BackupService Backups { get; private set; }

    private readonly Dictionary<string, UserControl> _views = new();
    private readonly Dictionary<string, (string title, string subtitle)> _headers = new()
    {
        ["dashboard"] = ("Dashboard", "A clear picture of the Zone on your hardware."),
        ["optimize"] = ("Optimize", "Select the problems, review every change, then apply or export a reversible package."),
        ["reshade"] = ("ReShade Manager", "Hardware-aware preset discovery, structural validation, and backed-up installation."),
        ["studio"] = ("Preset Studio", "Build your own Potato, Medium or High ReShade look. Preview it, save it, install it, undo it."),
        ["mods"] = ("Installed Mods", "Read-only inventory first. Disable is a reversible move, never a delete."),
        ["settings"] = ("Settings & Backups", "Detection, network policy, backup retention, and one-click restore."),
        ["reports"] = ("Diagnostic Reports", "Evidence from this PC. Measurement is required before claiming an improvement.")
    };

    private bool _scanning;
    private string _currentView = "dashboard";

    public MainWindow()
    {
        InitializeComponent();
        FooterVersion.Text = "v" + AppPaths.Version;
        Settings = SettingsStore.Load();
        Backups = new BackupService(Settings.BackupRoot);

        foreach (var token in Settings.LastSelectedProblems.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("loud", StringComparison.OrdinalIgnoreCase)) SelectedProblems.Add(ProblemKind.LoudPc);
            else if (token.Equals("fps", StringComparison.OrdinalIgnoreCase)) SelectedProblems.Add(ProblemKind.LowFps);
            else if (token.Equals("stutter", StringComparison.OrdinalIgnoreCase)) SelectedProblems.Add(ProblemKind.Stuttering);
        }

        LastKnownGame = new GameLocator().Locate(Settings.GamePathOverride, Settings.GamePathOverride);
        SidebarGame.Text = LastKnownGame.IsValid ? Shorten(LastKnownGame.Root, 34) : "Not detected — open Settings";

        Loaded += async (_, _) =>
        {
            Navigate("dashboard");
            await Task.Delay(120);
            await RunScanAsync(silent: true);
        };
        Closing += (_, _) =>
        {
            Settings.LastSelectedProblems = string.Join(",", SelectedProblems.Select(p => p switch
            {
                ProblemKind.LoudPc => "loud",
                ProblemKind.LowFps => "fps",
                _ => "stutter"
            }));
            SettingsStore.Save(Settings);
        };
    }

    public BackupService GetBackups() => new(Settings.BackupRoot);

    public void UpdateBackupRoot()
    {
        Backups = new BackupService(Settings.BackupRoot);
        Log.Info("Backup root updated: " + Settings.BackupRoot);
    }

    public ChangeSet? LatestRestorableChangeSet()
    {
        var sets = GetBackups().List().Where(c => !c.Restored).ToList();
        return sets.FirstOrDefault(c => !c.Kind.Equals("safety", StringComparison.OrdinalIgnoreCase)) ?? sets.FirstOrDefault();
    }

    public async Task RestoreLatestAsync()
    {
        if (new ConfigService().IsGameRunning())
        {
            ShowError("STALKER 2 is running. Exit the game before restoring configuration or ReShade files.");
            return;
        }
        var cs = LatestRestorableChangeSet();
        if (cs == null)
        {
            ShowError("There is no restorable change set yet.\n\nEvery Apply/install creates one automatically.");
            return;
        }
        if (!Confirm("Undo last change",
                $"Restore this change set?\n\n{cs.Description}\n\nCreated: {cs.CreatedAt:yyyy-MM-dd HH:mm}\n" +
                $"Kind: {cs.Kind} · {cs.Items.Count} file(s) · {cs.SizeText}\n\n" +
                "A safety snapshot of the current state is created first, so this undo can itself be undone."))
            return;

        var result = GetBackups().Restore(cs.Id);
        if (result.Success)
        {
            SetStatus("Restored: " + cs.Description, Severity.Notice);
            MessageBox.Show(this, "Undo completed.\n\n" + string.Join("\n", result.Messages.Take(10)) +
                                  "\n\nRescanning the current state.", "Undo last change",
                MessageBoxButton.OK, MessageBoxImage.Information);
            await RunScanAsync(silent: true);
        }
        else
        {
            ShowError("Undo completed with errors:\n\n" + string.Join("\n", result.Errors.Take(8)) +
                      "\n\nOpen Settings & Backups to inspect the change set.");
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : "…" + text[^Math.Max(4, max - 1)..];

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is not RadioButton rb) return;
        var key = rb == NavDashboard ? "dashboard"
            : rb == NavOptimize ? "optimize"
            : rb == NavReShade ? "reshade"
            : rb == NavStudio ? "studio"
            : rb == NavMods ? "mods"
            : rb == NavSettings ? "settings"
            : "reports";
        Navigate(key);
    }

    public void Navigate(string key)
    {
        if (!_headers.TryGetValue(key, out var header)) return;
        _currentView = key;
        PageTitle.Text = header.title;
        PageSubtitle.Text = header.subtitle;

        (NavDashboard.IsChecked, NavOptimize.IsChecked, NavReShade.IsChecked, NavStudio.IsChecked, NavMods.IsChecked,
            NavSettings.IsChecked, NavReports.IsChecked) = (key == "dashboard", key == "optimize", key == "reshade",
            key == "studio", key == "mods", key == "settings", key == "reports");

        if (!_views.TryGetValue(key, out var view))
        {
            view = key switch
            {
                "dashboard" => new DashboardView(this),
                "optimize" => new OptimizeView(this),
                "reshade" => new ReShadeView(this),
                "studio" => new PresetStudioView(this),
                "mods" => new ModsView(this),
                "settings" => new SettingsView(this),
                _ => new ReportsView(this)
            };
            _views[key] = view;
        }

        ContentHost.Content = view;
        _ = RefreshViewAsync(view);
    }

    private async Task RefreshViewAsync(UserControl view)
    {
        if (view is not IRefreshable refreshable) return;
        try { await refreshable.RefreshAsync(); }
        catch (Exception ex)
        {
            Log.Error("View refresh failed: " + view.GetType().Name, ex);
            SetStatus("View refresh failed: " + ex.Message, Severity.Warning);
        }
    }

    public async Task RefreshAllAsync()
    {
        foreach (var view in _views.Values.OfType<IRefreshable>().ToList())
            await view.RefreshAsync();
    }

    public async Task RunScanAsync(bool silent = false)
    {
        if (_scanning) return;
        _scanning = true;
        ScanButton.IsEnabled = false;
        ScanProgress.Visibility = Visibility.Visible;
        ScanPillText.Text = "SCANNING";
        ScanDot.Fill = (Brush)FindResource("Amber");
        SetStatus("Scanning hardware, game configuration, mods and ReShade…");

        try
        {
            var progress = new Progress<OperationProgress>(p =>
            {
                StatusText.Text = $"{p.Stage}: {p.Detail}";
            });
            var settings = Settings;
            var result = await Task.Run(() => ScanService.Scan(settings, includeMods: true, progress));
            Scan = result;
            LastKnownGame = result.Game;
            Settings.LastScanUtc = DateTime.UtcNow;
            ApplyScanState();
            await RefreshAllAsync();
            if (!silent) SetStatus($"Scan complete — {result.Findings.Count} findings, {result.Mods.Count} mod components.", Severity.Info);
            if (!result.Game.IsValid)
                ShowError("STALKER 2 was not found.\n\nOpen Settings & Backups and choose the game folder manually.\n\n" + result.Game.ValidationMessage);
        }
        catch (Exception ex)
        {
            Log.Error("Scan failed", ex);
            SetStatus("Scan failed: " + ex.Message, Severity.Critical);
            ScanPillText.Text = "SCAN FAILED";
            ScanDot.Fill = (Brush)FindResource("Red");
            if (!silent) ShowError("Scan failed:\n\n" + ex.Message + "\n\nSee the log for details.");
        }
        finally
        {
            _scanning = false;
            ScanButton.IsEnabled = true;
            ScanProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyScanState()
    {
        if (Scan == null) return;
        SidebarGame.Text = Scan.Game.IsValid ? Shorten(Scan.Game.Root, 34) : "Not detected — open Settings";
        SidebarScan.Text = $"Scanned {Scan.ScannedAt:HH:mm} · {Scan.Findings.Count} findings";
        if (!Scan.Game.IsValid)
        {
            ScanPillText.Text = "GAME MISSING";
            ScanDot.Fill = (Brush)FindResource("Red");
        }
        else
        {
            var critical = Scan.Findings.Count(f => f.Severity == Severity.Critical);
            var warnings = Scan.Findings.Count(f => f.Severity == Severity.Warning);
            ScanPillText.Text = critical > 0 ? $"{critical} CRITICAL" : warnings > 0 ? $"{warnings} WARNINGS" : "SYSTEM READY";
            ScanDot.Fill = (Brush)FindResource(critical > 0 ? "Red" : warnings > 0 ? "Amber" : "Green");
        }
    }

    public void SetStatus(string message, Severity severity = Severity.Info)
    {
        StatusText.Text = message;
        StatusText.Foreground = severity switch
        {
            Severity.Critical => (Brush)FindResource("Red"),
            Severity.Warning => (Brush)FindResource("Amber"),
            Severity.Notice => (Brush)FindResource("Green"),
            _ => (Brush)FindResource("Dim")
        };
    }

    public void ShowError(string message) =>
        MessageBox.Show(this, message, "ZoneTune", MessageBoxButton.OK, MessageBoxImage.Warning);

    public bool Confirm(string title, string message) =>
        MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("Could not open folder:\n" + ex.Message); }
    }

    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { ShowError("Could not open the link:\n" + ex.Message); }
    }

    public void GoOptimize(IEnumerable<ProblemKind> problems, bool generate = false)
    {
        SelectedProblems.Clear();
        foreach (var p in problems) SelectedProblems.Add(p);
        NavOptimize.IsChecked = true;
        Navigate("optimize");
        if (_views["optimize"] is OptimizeView ov)
        {
            ov.SetProblems(SelectedProblems, generate);
            if (generate) _ = ov.GenerateAsync();
        }
    }

    public async Task ApplyRecommendedAsync()
    {
        if (Scan == null || !Scan.Game.IsValid)
        {
            ShowError("Run a successful scan first. A valid game folder is required to apply settings.");
            return;
        }
        if (Scan.GameRunning)
        {
            ShowError("STALKER 2 is running. Exit the game before applying settings; the game rewrites GameUserSettings.ini on exit.");
            return;
        }
        if (new ConfigService().IsGameRunning())
        {
            ShowError("STALKER 2 is running. Exit the game before applying settings; the game rewrites GameUserSettings.ini on exit.");
            return;
        }
        if (Settings.DryRunByDefault)
        {
            ShowError("Dry run is enabled in Settings & Backups. Disable it to write changes.");
            return;
        }

        var plan = Optimizer.BuildRecommendedProfile(Scan);
        LastPlan = plan;
        var preview = new UI.RecommendedSettingsWindow(this, plan) { Owner = this };
        if (preview.ShowDialog() != true)
        {
            SetStatus("Recommended settings were not applied (review cancelled).", Severity.Info);
            return;
        }

        var changes = plan.ConfigActions.Count(a => a.Selected);
        if (changes == 0)
        {
            ShowError("No settings were selected. Nothing was applied.");
            return;
        }

        var result = Optimizer.Apply(plan, GetBackups());
        if (result.Success)
        {
            SetStatus($"Recommended settings applied to {result.FilesChanged.Count} file(s). Backup: {result.ChangeSetId}", Severity.Notice);
            MessageBox.Show(this,
                "Recommended in-game settings applied and validated.\n\n" +
                $"Backup change set: {result.ChangeSetId}\n\n" +
                "Exit and relaunch the game for the resolution/scalability changes to be re-read. " +
                "Then measure the same route; restore from Settings & Backups if needed.",
                "Settings applied", MessageBoxButton.OK, MessageBoxImage.Information);
            await RunScanAsync(silent: true);
        }
        else
        {
            SetStatus("Recommended settings could not be applied; rollback attempted.", Severity.Critical);
            ShowError("Apply failed or validation failed:\n\n" + string.Join("\n", result.Errors.Take(6)) +
                      "\n\nA rollback was attempted. Check the log and Settings & Backups.");
        }
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e) => _ = RunScanAsync();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        try { DragMove(); } catch { }
    }

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void OpenLogs_Click(object sender, MouseButtonEventArgs e) => OpenFolder(AppPaths.LogsDir);
    private void OpenBackups_Click(object sender, MouseButtonEventArgs e) => OpenFolder(Settings.BackupRoot);
}

