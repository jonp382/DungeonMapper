using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace DungeonMapper.Views;

/// Right-docked floating panel that hosts per-tool options (erase filter,
/// paste ghost toggle) and the config fields for a selected door/stair/label.
/// Replaces the modal ConfigDialog. Fields apply live on change.
public sealed class ToolPanel : Border
{
    private readonly TextBlock _title = new() { FontSize = 14, Foreground = Brushes.White };
    private readonly StackPanel _content = new() { Spacing = 6 };

    public ToolPanel()
    {
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 38));
        Padding = new Thickness(10);
        Width = 220;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 48, 8, 0); // sits below the toolbar, over the canvas
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(_title);
        root.Children.Add(_content);
        Child = root;
    }

    public void Clear(string title)
    {
        _title.Text = title;
        _content.Children.Clear();
    }

    public void AddHint(string text)
    {
        _content.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        });
    }

    public void AddCheck(string label, bool initial, Action<bool> onChange)
    {
        var cb = new CheckBox { Content = label, IsChecked = initial, Foreground = Brushes.White };
        cb.IsCheckedChanged += (_, _) => onChange(cb.IsChecked == true);
        _content.Children.Add(cb);
    }

    public void AddText(string label, string initial, Action<string> onChange)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White });
        var tb = new TextBox { Text = initial };
        tb.TextChanged += (_, _) => onChange(tb.Text);
        panel.Children.Add(tb);
        _content.Children.Add(panel);
    }

    public void AddChoice(string label, string[] options, string initial, Action<string> onChange)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White });
        var cb = new ComboBox
        {
            ItemsSource = options,
            SelectedItem = Array.IndexOf(options, initial) >= 0 ? initial : options[0],
        };
        cb.SelectionChanged += (_, _) => onChange((string?)cb.SelectedItem ?? options[0]);
        panel.Children.Add(cb);
        _content.Children.Add(panel);
    }

    public void AddNumber(string label, int initial, Action<int> onChange)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White });
        var tb = new TextBox { Text = initial.ToString() };
        tb.TextChanged += (_, _) =>
        {
            if (int.TryParse(tb.Text, out int v)) onChange(v);
        };
        panel.Children.Add(tb);
        _content.Children.Add(panel);
    }

    public void AddButton(string label, Action onClick)
    {
        var b = new Button { Content = label };
        b.Click += (_, _) => onClick();
        _content.Children.Add(b);
    }

    /// A grid of clickable pawn swatches (concentric circles) for the pawn tool's
    /// placement submenu. Each swatch shows a template's outer/inner colors; a
    /// broken/empty entry shows an X. Clicking arms that template for placement.
    public void AddSwatchGrid(string label, IReadOnlyList<(string name, int outerR, int outerG, int outerB, int innerR, int innerG, int innerB)> items, Action<int> onPick)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White });
        var grid = new WrapPanel { MaxWidth = 200 };
        for (int i = 0; i < items.Count; i++)
        {
            var (name, oR, oG, oB, iR, iG, iB) = items[i];
            var cell = new StackPanel { Spacing = 1, Margin = new Thickness(2) };
            var canvas = new Avalonia.Controls.Canvas { Width = 26, Height = 26 };
            var center = new Point(13, 13);
            var outer = new Ellipse { Width = 24, Height = 24, Fill = new SolidColorBrush(Color.FromRgb((byte)oR, (byte)oG, (byte)oB)) };
            Avalonia.Controls.Canvas.SetLeft(outer, 1); Avalonia.Controls.Canvas.SetTop(outer, 1);
            var inner = new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.FromRgb((byte)iR, (byte)iG, (byte)iB)) };
            Avalonia.Controls.Canvas.SetLeft(inner, 7); Avalonia.Controls.Canvas.SetTop(inner, 7);
            canvas.Children.Add(outer);
            canvas.Children.Add(inner);
            var hit = new Rectangle { Width = 26, Height = 26, Fill = Brushes.Transparent };
            hit.PointerPressed += (_, _) => onPick(i);
            canvas.Children.Add(hit);
            var cap = new TextBlock { Text = name, FontSize = 10, Foreground = Brushes.White, MaxWidth = 30, TextWrapping = TextWrapping.Wrap };
            cell.Children.Add(canvas);
            cell.Children.Add(cap);
            grid.Children.Add(cell);
        }
        panel.Children.Add(grid);
        _content.Children.Add(panel);
    }

    /// A color field: 10 preset swatches (clickable) plus a #hex text box. Picking
    /// a swatch or typing a hex calls onChange with the RGB.
    public void AddColorField(string label, int r, int g, int b, IReadOnlyList<(int r, int g, int b)> presets, Action<(int r, int g, int b)> onChange)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White });
        var row = new WrapPanel { MaxWidth = 200 };
        foreach (var (pr, pg, pb) in presets)
        {
            var sw = new Rectangle { Width = 18, Height = 18, Margin = new Thickness(1), Fill = new SolidColorBrush(Color.FromRgb((byte)pr, (byte)pg, (byte)pb)) };
            sw.PointerPressed += (_, _) => onChange((pr, pg, pb));
            row.Children.Add(sw);
        }
        panel.Children.Add(row);
        var tb = new TextBox { Text = $"#{r:X2}{g:X2}{b:X2}" };
        tb.TextChanged += (_, _) =>
        {
            if (TryHexColor(tb.Text, out var c)) onChange(c);
        };
        panel.Children.Add(tb);
        _content.Children.Add(panel);
    }

    private static bool TryHexColor(string hex, out (int r, int g, int b) c)
    {
        c = (0, 0, 0);
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6) return false;
        int r, g, b;
        try { r = Convert.ToInt32(s.Substring(0, 2), 16); g = Convert.ToInt32(s.Substring(2, 2), 16); b = Convert.ToInt32(s.Substring(4, 2), 16); }
        catch { return false; }
        c = (r, g, b);
        return true;
    }
}
