using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ZoneTune.Core;
using ZoneTune.Core.Models;
using ZoneTune.Core.Services;

namespace ZoneTune.UI.Views;

public partial class SettingsView : UserControl, IRefreshable
{
    private readonly MainWindow _main;
    private bool _fieldsLoaded;

    public SettingsView(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        LoadFields();
    }

    public Task RefreshAsync()
    {
        if (!_fieldsLoaded) LoadFields();
        RenderBackups();
        UpdateGameStatus();
        LogPathText.Text = AppPaths.LogsDir;
        return Task.CompletedTask;
    }

    private void LoadFields()
    {
        var s = _main.Settings;
        GamePathBox.Text = s.GamePathOverride ?? _main.LastKnownGame.Root;
        BackupBox.Text = s.BackupRoot;
        KeepBox.Text = s.KeepBackups.ToString();
        NetworkCheck.IsChecked = s.AllowNetwork;
        VerifyCheck.IsChecked = s.VerifyDownloads;
        GameClosedCheck.IsChecked = s.RequireGameClosed;
        DryRunCheck.IsChecked = s.DryRunByDefault;
        ZipCheck.IsChecked = s.CreateZipAfterOptimize;
        ReportCheck.IsChecked = s.AutoOpenReport;
        _fieldsLoaded = true;
        UpdateGameStatus();
    }

    private void UpdateGameStatus()
    {
        var path = GamePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            GameStatusText.Text = "No path selected.";
            GameStatusText.Foreground = UiKit.Dim;
            return;
        }
        var install = new GameLocator().Validate(path, GameSource.Manual);
        if (install.IsValid)
        {
            GameStatusText.Text = $"Valid · {install.ShippingExePath}";
            GameStatusText.Foreground = UiKit.Green;
        }
        else
        {
            GameStatusText.Text = "Not valid · " + install.ValidationMessage;
            GameStatusText.Foreground = UiKit.Red;
        }
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the STALKER 2 game folder", InitialDirectory = SafeInitial(GamePathBox.Text) };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        GamePathBox.Text = dialog.FolderName;
        UpdateGameStatus();
    }

    private void BrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select a backup root", InitialDirectory = SafeInitial(BackupBox.Text) };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        BackupBox.Text = dialog.FolderName;
    }

    private static string? SafeInitial(string path)
    {
        try { return Directory.Exists(path) ? path : Path.GetDirectoryName(path); } catch { return null; }
    }

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        var game = new GameLocator().Locate();
        if (!game.IsValid)
        {
            _main.ShowError("Automatic detection failed.\n\nChoose the folder containing Stalker2.exe manually.");
            return;
        }
        GamePathBox.Text = game.Root;
        UpdateGameStatus();
        _main.SetStatus("Detected game at " + game.Root, Severity.Notice);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var path = GamePathBox.Text.Trim();
        var install = string.IsNullOrWhiteSpace(path) ? null : new GameLocator().Validate(path, GameSource.Manual);
        var s = _main.Settings;
        s.GamePathOverride = install?.IsValid == true ? install.Root : null;
        s.BackupRoot = BackupBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(s.BackupRoot)) s.BackupRoot = AppPaths.BackupsDir;
        if (int.TryParse(KeepBox.Text.Trim(), out var keep)) s.KeepBackups = Math.Clamp(keep, 1, 100);
        s.AllowNetwork = NetworkCheck.IsChecked == true;
        s.VerifyDownloads = VerifyCheck.IsChecked == true;
        s.RequireGameClosed = GameClosedCheck.IsChecked == true;
        s.DryRunByDefault = DryRunCheck.IsChecked == true;
        s.CreateZipAfterOptimize = ZipCheck.IsChecked == true;
        s.AutoOpenReport = ReportCheck.IsChecked == true;
        _main.SettingsStore.Save(s);
        _main.UpdateBackupRoot();

        if (!string.IsNullOrWhiteSpace(path) && install?.IsValid == false)
        {
            _main.ShowError("Settings saved, but the game path is not valid:\n" + install.ValidationMessage + "\n\nThe path override was cleared; detection will run again.");
        }
        _main.SetStatus("Settings saved.", Severity.Notice);
        RenderBackups();
        if (install?.IsValid == true && _main.LastKnownGame.Root != install.Root)
            _ = _main.RunScanAsync();
    }

    private void RenderBackups()
    {
        BackupPanel.Children.Clear();
        var backups = _main.GetBackups().List();
        if (backups.Count == 0)
        {
            BackupPanel.Children.Add(UiKit.Empty(UiKit.Icon("shield"), "No backups yet",
                "Change sets appear here after an optimization or ReShade installation. ZoneTune always creates one before writing.", null));
            return;
        }

        foreach (var cs in backups.Take(50))
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(UiKit.Badge(cs.Kind, cs.Kind == "reshade" ? Severity.Notice : Severity.Info));
            header.Children.Add(UiKit.Text(cs.Description, size: 12, weight: FontWeights.SemiBold).WithMargin(8, 0, 0, 0));
            if (cs.Restored) header.Children.Add(UiKit.Badge("RESTORED", Severity.Info).WithMargin(6, 0, 0, 0));
            left.Children.Add(header);
            left.Children.Add(UiKit.Text($"{cs.CreatedAt:yyyy-MM-dd HH:mm} · {cs.Items.Count} file(s) · {cs.SizeText}", size: 10, color: UiKit.Dim).WithMargin(0, 3, 0, 0));
            left.Children.Add(UiKit.Text(cs.Id, size: 9.5, color: UiKit.Brush("#627265")).WithMargin(0, 2, 0, 0));
            grid.Children.Add(left);

            var folderButton = UiKit.Button("Folder", (_, _) => _main.OpenFolder(cs.Directory), icon: UiKit.Icon("folder"), compact: true);
            Grid.SetColumn(folderButton, 1);
            grid.Children.Add(folderButton);

            var restore = UiKit.Button("Restore", (_, _) => Restore(cs), icon: UiKit.Icon("undo"), danger: !cs.Restored, compact: true);
            restore.IsEnabled = !cs.Restored;
            Grid.SetColumn(restore, 2);
            grid.Children.Add(restore);

            var delete = UiKit.Button("Delete", (_, _) => Delete(cs), icon: UiKit.Icon("delete"), compact: true);
            Grid.SetColumn(delete, 3);
            grid.Children.Add(delete);

            var border = UiKit.Card(grid, 12);
            border.Margin = new Thickness(0, 0, 0, 6);
            if (cs.Restored) border.Opacity = 0.62;
            BackupPanel.Children.Add(border);
        }

        if (backups.Count > 50)
            BackupPanel.Children.Add(UiKit.Text($"Showing 50 of {backups.Count} change sets. Older entries remain on disk.", size: 10, color: UiKit.Dim).WithMargin(0, 5, 0, 0));
    }

    private void Restore(ChangeSet cs)
    {
        if (new ConfigService().IsGameRunning())
        {
            _main.ShowError("STALKER 2 is running. Exit the game before restoring configuration files.");
            return;
        }
        if (!_main.Confirm("Restore change set",
                $"Restore '{cs.Description}'?\n\nEvery file in this change set is restored byte-for-byte; files ZoneTune created are removed.\n\n" +
                "A safety snapshot of the current state is created first. Exit the game before restoring."))
            return;
        var result = _main.GetBackups().Restore(cs.Id);
        if (result.Success)
        {
            _main.SetStatus("Restored change set " + cs.Id, Severity.Notice);
            MessageBox.Show(Window.GetWindow(this), "Restore complete.\n\n" + string.Join("\n", result.Messages.Take(10)), "Restore",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            _main.ShowError("Restore had errors:\n" + string.Join("\n", result.Errors.Take(8)));
        }
        RenderBackups();
        _ = _main.RunScanAsync(silent: true);
    }

    private void Delete(ChangeSet cs)
    {
        if (cs.Kind == "safety")
        {
            _main.ShowError("Safety snapshots are kept intentionally so restores remain reversible. Delete older entries only if you are sure.");
            return;
        }
        if (!_main.Confirm("Delete backup", "Delete change set '" + cs.Description + "'?\n\nThis removes the backup files. It cannot be undone, but the game files themselves are not changed."))
            return;
        if (_main.GetBackups().Delete(cs.Id)) _main.SetStatus("Backup deleted.", Severity.Info);
        RenderBackups();
    }

    private void Prune_Click(object sender, RoutedEventArgs e)
    {
        var keep = _main.Settings.KeepBackups;
        if (!_main.Confirm("Prune backups", $"Keep the newest {keep} non-restored change sets and delete older ones?\n\nSafety snapshots are not pruned automatically.")) return;
        _main.GetBackups().Prune(keep);
        RenderBackups();
        _main.SetStatus("Old backups pruned.", Severity.Info);
    }

    private void RestoreLatest_Click(object sender, RoutedEventArgs e) => _ = _main.RestoreLatestAsync();

    private void OpenBackups_Click(object sender, RoutedEventArgs e) => _main.OpenFolder(_main.Settings.BackupRoot);    private void OpenLogs_Click(object sender, RoutedEventArgs e) => _main.OpenFolder(AppPaths.LogsDir);
    private void OpenData_Click(object sender, RoutedEventArgs e) => _main.OpenFolder(AppPaths.DataRoot);
}

