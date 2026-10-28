using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZoneTune.Core.Models;

namespace ZoneTune.UI;

public static class UiKit
{
    public static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    public static readonly SolidColorBrush Amber = Brush("#E3A33C");
    public static readonly SolidColorBrush Green = Brush("#9ED36A");
    public static readonly SolidColorBrush Red = Brush("#E06C5F");
    public static readonly SolidColorBrush Blue = Brush("#79B8D1");
    public static readonly SolidColorBrush Dim = Brush("#8FA092");
    public static readonly SolidColorBrush Line = Brush("#263329");
    public static readonly SolidColorBrush Panel = Brush("#111713");
    public static readonly SolidColorBrush Panel2 = Brush("#161E18");

    public static string Icon(string name) => name switch
    {
        "home" => "\uE80F",
        "tune" => "\uE9D9",
        "camera" => "\uE91B",
        "apps" => "\uE71D",
        "settings" => "\uE713",
        "report" => "\uE9F9",
        "play" => "\uE768",
        "download" => "\uE896",
        "refresh" => "\uE72C",
        "folder" => "\uE8B7",
        "warning" => "\uE7BA",
        "check" => "\uE73E",
        "search" => "\uE721",
        "save" => "\uE74E",
        "delete" => "\uE74D",
        "undo" => "\uE7A7",
        "info" => "\uE946",
        "close" => "\uE8BB",
        "add" => "\uE710",
        "open" => "\uE8E5",
        "gauge" => "\uE9D2",
        "volume" => "\uE767",
        "fps" => "\uE7F4",
        "stutter" => "\uE9D9",
        "shield" => "\uEA18",
        "chip" => "\uE950",
        "display" => "\uE7F4",
        "storage" => "\uEDA2",
        "windows" => "\uE770",
        "game" => "\uE7FC",
        "preset" => "\uE790",
        "zip" => "\uF012",
        _ => "\uE946"
    };

    public static TextBlock Text(string text, string? style = null, double? size = null, Brush? color = null,
        FontWeight? weight = null)
    {
        var tb = new TextBlock { Text = text };
        if (style != null && Application.Current.TryFindResource(style) is Style s) tb.Style = s;
        if (size != null) tb.FontSize = size.Value;
        if (color != null) tb.Foreground = color;
        if (weight != null) tb.FontWeight = weight.Value;
        return tb;
    }

    public static TextBlock IconText(string glyph, double size = 14, Brush? color = null)
    {
        return new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = size,
            Foreground = color ?? Dim,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    public static Border Card(UIElement child, double padding = 18, bool flat = false)
    {
        return new Border
        {
            Style = (Style)Application.Current.FindResource(flat ? "CardFlat" : "Card"),
            Padding = new Thickness(padding),
            Child = child
        };
    }

    public static Border Badge(string text, Severity severity)
    {
        var (bg, fg) = severity switch
        {
            Severity.Critical => (Brush("#2B1412"), Red),
            Severity.Warning => (Brush("#2A2110"), Amber),
            Severity.Notice => (Brush("#162415"), Green),
            _ => (Brush("#18211A"), Dim)
        };
        return new Border
        {
            Background = bg,
            BorderBrush = fg,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text.ToUpperInvariant(),
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = fg
            }
        };
    }

    public static Border Chip(string text, Brush? color = null)
    {
        color ??= Dim;
        return new Border
        {
            Background = Brush("#141D16"),
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock { Text = text, FontSize = 10.5, Foreground = color }
        };
    }

    public static Button Button(string text, RoutedEventHandler click, bool primary = false, string? icon = null,
        bool danger = false, bool compact = false)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null) panel.Children.Add(new TextBlock
        {
            Text = icon,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = compact ? 11 : 12,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock { Text = text, FontSize = compact ? 11 : 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = panel,
            Style = (Style)Application.Current.FindResource(danger ? "DangerButton" : primary ? "PrimaryButton" : "ButtonBase"),
            Padding = compact ? new Thickness(10, 5, 10, 5) : new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 0, 8, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        button.Click += click;
        return button;
    }

    public static Border Stat(string label, string value, string? sub = null, Brush? accent = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(label.ToUpperInvariant(), "Overline"));
        stack.Children.Add(Text(value, size: 18, weight: FontWeights.SemiBold, color: accent));
        if (!string.IsNullOrWhiteSpace(sub)) stack.Children.Add(Text(sub, size: 10.5, color: Dim));
        return Card(stack, 13, flat: true);
    }

    public static Border SectionHeader(string title, string? subtitle = null, UIElement? right = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel();
        left.Children.Add(Text(title, "H2"));
        if (subtitle != null) left.Children.Add(Text(subtitle, "BodyDim", color: Dim).WithMargin(0, 4, 0, 0));
        grid.Children.Add(left);
        if (right != null)
        {
            Grid.SetColumn(right, 1);
            if (right is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(right);
        }
        return new Border { Child = grid, Margin = new Thickness(0, 0, 0, 12) };
    }

    public static T WithMargin<T>(this T element, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        element.Margin = new Thickness(left, top, right, bottom);
        return element;
    }

    public static Border Divider() => new()
    {
        Height = 1,
        Background = Brush("#1B251E"),
        Margin = new Thickness(0, 10, 0, 10)
    };

    public static StackPanel KeyValue(string key, string value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(Text(key, size: 10, color: Dim, weight: FontWeights.Bold));
        panel.Children.Add(Text(value, size: 12, color: Brush("#DEE7DC")));
        return panel;
    }

    public static Border Empty(string icon, string title, string body, UIElement? action = null)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(26, 30, 26, 30) };
        stack.Children.Add(IconText(icon, 28, Brush("#3E5042")));
        stack.Children.Add(Text(title, "H3", size: 15, color: Brush("#C9D6C8"), weight: FontWeights.SemiBold).WithMargin(0, 10, 0, 4));
        stack.Children.Add(Text(body, "BodyDim", color: Dim));
        if (action != null)
        {
            if (action is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(new Border { Child = action, Margin = new Thickness(0, 14, 0, 0) });
        }
        return new Border
        {
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Background = Brush("#0E1410"),
            Child = stack
        };
    }

    public static Brush SeverityColor(Severity s) => s switch
    {
        Severity.Critical => Red,
        Severity.Warning => Amber,
        Severity.Notice => Green,
        _ => Dim
    };

    public static UIElement Dot(Brush color, double size = 6) => new System.Windows.Shapes.Ellipse
    {
        Width = size,
        Height = size,
        Fill = color,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 7, 0)
    };

    public static void Clear(this Panel panel) => panel.Children.Clear();
}
