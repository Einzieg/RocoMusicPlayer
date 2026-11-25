using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace RocoPlayer.Prototypes;

internal static class Ui
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();
    internal static SolidColorBrush Brush(string value)
    {
        if (Brushes.TryGetValue(value, out var old)) return old;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return Brushes[value] = brush;
    }
    internal static readonly FontFamily Sans = new("Segoe UI, Microsoft YaHei UI");
    internal static readonly FontFamily Serif = new("Georgia, STZhongsong, SimSun");
    internal static TextBlock Text(string text, double size, string ink, bool bold = false, FontFamily? face = null)
        => new()
        {
            Text = text,
            FontFamily = face ?? Sans,
            FontSize = size,
            Foreground = Brush(ink),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        };
    internal static TextBlock Icon(string code, double size, string ink) => Text(code, size, ink, false, new FontFamily("Segoe MDL2 Assets"));
    internal static Border Box(UIElement? child, string fill, int radius = 0, Thickness? padding = null, string? stroke = null)
        => new()
        {
            Child = child,
            Background = Brush(fill),
            CornerRadius = new(radius),
            Padding = padding ?? new(0),
            BorderBrush = stroke == null ? null : Brush(stroke),
            BorderThickness = new(stroke == null ? 0 : 1)
        };
    internal static Grid GridOf(bool columns, params string[] parts)
    {
        var grid = new Grid();
        var lengths = new GridLengthConverter();
        foreach (string part in parts)
        {
            var length = (GridLength)lengths.ConvertFromInvariantString(part)!;
            if (columns)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = length });
            else
                grid.RowDefinitions.Add(new RowDefinition { Height = length });
        }
        return grid;
    }
    internal static void At(Grid grid, UIElement control, int row = 0, int col = 0)
    {
        Grid.SetColumn(control, col);
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }
    internal static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) row.Children.Add(c);
        return row;
    }
    internal static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }
    internal static T Space<T>(T el, double l = 0, double t = 0, double r = 0, double b = 0) where T : FrameworkElement
    {
        el.Margin = new(l, t, r, b);
        return el;
    }
    internal static Button Button(string name, UIElement child, Action handler, string fill = "Transparent", int radius = 6, Thickness? pad = null, string? border = null)
    {
        var b = new Button
        {
            Content = child,
            Background = Brush(fill),
            Tag = new CornerRadius(radius),
            Padding = pad ?? new(8, 6, 8, 6),
            BorderBrush = border == null ? null : Brush(border),
            BorderThickness = new(border == null ? 0 : 1),
            ToolTip = name
        };
        AutomationProperties.SetName(b, name);
        b.Click += (_, _) => handler();
        return b;
    }
    internal static Image Art(Piece piece, double size = 0)
    {
        var image = new Image { Source = CoverArt.Get(piece.Artwork), Stretch = Stretch.UniformToFill, ClipToBounds = true };
        if (size > 0)
        {
            image.Width = size;
            image.Height = size;
        }
        return image;
    }
    internal static Grid Brand(string ink)
    {
        var row = GridOf(true, "27", "7", "auto");
        var mark = new Grid { Width = 24, Height = 24, RenderTransform = new RotateTransform(-12), RenderTransformOrigin = new(.5, .5) };
        foreach (var (left, top, h) in new (int, int, int)[] { (2, 8, 12), (9, 1, 22), (16, 5, 14) })
            mark.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Width = 4, Height = h, Margin = new(left, top, 0, 0), CornerRadius = new(2), Background = Brush(ink) });
        At(row, mark);
        At(row, Text("RocoPlayer", 18, ink, true), 0, 2);
        return row;
    }
}
