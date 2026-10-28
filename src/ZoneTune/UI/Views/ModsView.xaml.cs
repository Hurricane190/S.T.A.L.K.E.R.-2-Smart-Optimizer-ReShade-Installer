using System.Windows.Controls.Primitives;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZoneTune.Core;
using ZoneTune.Core.Models;

namespace ZoneTune.UI.Views;

public partial class ModsView : UserControl, IRefreshable
{
    private readonly MainWindow _main;
    private bool _ready;

    public ModsView(MainWindow main)
    {
        InitializeComponent();
        _main = main;
        _ready = true;
    }

    public Task RefreshAsync()
    {
        RenderSummary();
        RenderConflicts();
        RenderList();
        return Task.CompletedTask;
    }

    private void RenderSummary()
    {
        SummaryHost.Content = null;
        var scan = _main.Scan;
        if (scan == null)
        {
            SummaryHost.Content = UiKit.Empty(UiKit.Icon("search"), "Scan required", "Run a scan to inventory pak mods, UE4SS mods and ASI plugins.",
                UiKit.Button("Run scan", (_, _) => _ = _main.RunScanAsync(), primary: true));
            return;
        }
        var enabled = scan.Mods.Count(m => m.Enabled);
        var paks = scan.Mods.Count(m => m.Kind == ModKind.PakMod && m.Enabled);
        var lua = scan.Mods.Count(m => m.Kind == ModKind.Ue4ssLua && m.Enabled);
        var asi = scan.Mods.Count(m => m.Kind == ModKind.AsiPlugin && m.Enabled);
        var conflicts = scan.ModConflicts.Count;

        var stats = new UniformGrid { Columns = 4 };
        stats.Children.Add(Stat("ENABLED", enabled.ToString(), $"{scan.Mods.Count} total components", UiKit.Green));
        stats.Children.Add(Stat("PAK / LOGIC", paks.ToString(), "IoStore and legacy paks", UiKit.Dim));
        stats.Children.Add(Stat("UE4SS / ASI", $"{lua} / {asi}", "script and plugin layers", UiKit.Dim));
        stats.Children.Add(Stat("CONFLICTS", conflicts.ToString(), conflicts > 0 ? "review before tuning" : "no notes",
            conflicts > 0 ? UiKit.Amber : UiKit.Green));
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

    private void RenderConflicts()
    {
        ConflictsHost.Content = null;
        var scan = _main.Scan;
        if (scan == null || scan.ModConflicts.Count == 0)
        {
            if (scan != null)
            {
                var ok = new Border
                {
                    Background = UiKit.Brush("#0F1711"),
                    BorderBrush = UiKit.Brush("#26382A"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(13, 9, 13, 9),
                    Child = UiKit.Text("No overlapping pak names or merger conflict logs were detected. This is not proof that all mods are compatible.", size: 10.5, color: UiKit.Dim)
                };
                ConflictsHost.Content = ok;
            }
            return;
        }

        var stack = new StackPanel();
        stack.Children.Add(UiKit.Text("CONFLICT NOTES (READ-ONLY EVIDENCE)", "Overline").WithMargin(0, 0, 0, 8));
        foreach (var conflict in scan.ModConflicts)
        {
            var inner = new StackPanel();
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(UiKit.Badge(conflict.Severity.ToString(), conflict.Severity));
            header.Children.Add(UiKit.Text(conflict.Title, size: 12, weight: FontWeights.SemiBold).WithMargin(8, 0, 0, 0));
            inner.Children.Add(header);
            inner.Children.Add(UiKit.Text(conflict.Detail, size: 11, color: UiKit.Dim).WithMargin(0, 5, 0, 0));
            inner.Children.Add(UiKit.Text("MODS · " + string.Join(", ", conflict.Mods.Take(6)), size: 10, color: UiKit.Brush("#7D8E80")).WithMargin(0, 5, 0, 0));
            inner.Children.Add(UiKit.Text("SOURCE · " + conflict.Source, size: 9.5, color: UiKit.Brush("#5F6F62")).WithMargin(0, 3, 0, 0));
            var border = new Border
            {
                BorderBrush = UiKit.SeverityColor(conflict.Severity),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Background = UiKit.Brush("#101510"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Child = inner
            };
            stack.Children.Add(border);
        }
        ConflictsHost.Content = UiKit.Card(stack, 15);
    }

    private void RenderList()
    {
        ListPanel.Children.Clear();
        var scan = _main.Scan;
        if (scan == null) return;

        var query = SearchBox.Text.Trim();
        var filter = (FilterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All components";
        var mods = scan.Mods.Where(m =>
        {
            if (filter == "Enabled" && !m.Enabled) return false;
            if (filter == "Disabled" && m.Enabled) return false;
            if (filter == "Pak mods" && m.Kind is not (ModKind.PakMod or ModKind.LogicMod or ModKind.MergeOutput)) return false;
            if (filter == "UE4SS Lua" && m.Kind != ModKind.Ue4ssLua) return false;
            if (filter == "ASI plugins" && m.Kind != ModKind.AsiPlugin) return false;
            if (query.Length == 0) return true;
            return m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   m.Notes.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   m.KindText.Contains(query, StringComparison.OrdinalIgnoreCase);
        }).OrderByDescending(m => m.Enabled)
          .ThenBy(m => m.Kind)
          .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

        CountText.Text = $"{mods.Count} shown of {scan.Mods.Count}";
        if (mods.Count == 0)
        {
            ListPanel.Children.Add(UiKit.Empty(UiKit.Icon("apps"), "No matching components", "Change the filter or search text.", null));
            return;
        }

        foreach (var mod in mods)
            ListPanel.Children.Add(BuildRow(mod));
    }

    private Border BuildRow(ModEntry mod)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = mod.Enabled ? UiKit.Green : UiKit.Brush("#4A554B"),
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(dot);

        var nameStack = new StackPanel { Margin = new Thickness(8, 0, 10, 0) };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(UiKit.Text(mod.Name, size: 12, weight: FontWeights.SemiBold));
        nameRow.Children.Add(UiKit.Chip(mod.KindText, mod.Kind == ModKind.Ue4ssLua ? UiKit.Blue : UiKit.Dim).WithMargin(8, 0, 0, 0));
        if (!mod.Enabled) nameRow.Children.Add(UiKit.Badge("DISABLED", Severity.Info).WithMargin(4, 0, 0, 0));
        nameStack.Children.Add(nameRow);
        if (!string.IsNullOrWhiteSpace(mod.Notes))
            nameStack.Children.Add(UiKit.Text(mod.Notes, size: 10, color: UiKit.Dim).WithMargin(0, 3, 0, 0));
        Grid.SetColumn(nameStack, 1);
        grid.Children.Add(nameStack);

        var size = UiKit.Text(mod.SizeText, size: 10.5, color: UiKit.Dim) as TextBlock;
        size.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(size, 2);
        grid.Children.Add(size);

        var version = UiKit.Text(string.IsNullOrWhiteSpace(mod.Version) ? "" : "v" + mod.Version, size: 10, color: UiKit.Dim);
        version.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(version, 3);
        grid.Children.Add(version);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(UiKit.Button("Reveal", (_, _) => Reveal(mod.Path), icon: UiKit.Icon("folder"), compact: true));
        if (mod.Kind != ModKind.ConfigOverride)
        {
            actions.Children.Add(mod.Enabled
                ? UiKit.Button("Disable", (_, _) => Toggle(mod, false), danger: true, compact: true)
                : UiKit.Button("Enable", (_, _) => Toggle(mod, true), primary: true, compact: true));
        }
        Grid.SetColumn(actions, 4);
        grid.Children.Add(actions);

        var border = new Border
        {
            Background = mod.Enabled ? UiKit.Brush("#111713") : UiKit.Brush("#0D120E"),
            BorderBrush = UiKit.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(13, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 5),
            Child = grid,
            Opacity = mod.Enabled ? 1 : 0.72
        };
        return border;
    }

    private void Toggle(ModEntry mod, bool enable)
    {
        var verb = enable ? "Enable" : "Disable";
        if (!_main.Confirm($"{verb} mod",
                $"{verb} '{mod.Name}'?\n\n" +
                (enable ? "The files are moved back to their original location." : "The files are moved out of the load path, not deleted. Re-enable at any time.") +
                "\n\nExit the game first so the game does not hold file handles."))
            return;
        var (success, message, vortex) = enable ? _main.Mods.Enable(_main.Scan!.Game, mod) : _main.Mods.Disable(_main.Scan!.Game, mod);
        if (!success)
        {
            _main.ShowError($"{verb} failed:\n{message}");
            return;
        }
        _main.SetStatus(message, vortex ? Severity.Warning : Severity.Notice);
        if (vortex)
            MessageBox.Show(Window.GetWindow(this), message + "\n\nVortex deployment metadata was detected. Disable the mod in Vortex too, otherwise it may redeploy the file.", "Vortex note", MessageBoxButton.OK, MessageBoxImage.Information);
        _ = _main.RunScanAsync(silent: true);
    }

    private void Reveal(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
            else _main.OpenFolder(Path.GetDirectoryName(path) ?? _main.Scan!.Game.ModsDir);
        }
        catch (Exception ex) { _main.ShowError("Could not reveal the path:\n" + ex.Message); }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) RenderList(); }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) RenderList(); }
    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = _main.RunScanAsync();
    private void OpenMods_Click(object sender, RoutedEventArgs e) => _main.OpenFolder(_main.Scan?.Game.ModsDir ?? AppPaths.DataRoot);
}


