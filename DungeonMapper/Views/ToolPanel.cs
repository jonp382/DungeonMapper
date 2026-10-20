using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private readonly ScrollViewer _scroll;
    // When set, all Add* builders append here instead of _content. Lets the modal
    // popup host the same field builders (color fields, text, number, button).
    private Panel _target;

    public void SetTarget(Panel? target) => _target = target;
    public Panel Target => _target ?? _content;

    public ToolPanel()
    {
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 38));
        Padding = new Thickness(10);
        Width = 220;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 48, 8, 0); // sits below the toolbar, over the canvas
        // content scrolls so long panels (pawn grid, settings) fit at low resolutions.
        // MaxHeight tracks the window height so the scrollbar appears when content
        // exceeds the visible area (the window is fixed-size on VRAM-limited setups).
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _content,
        };
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(_title);
        root.Children.Add(_scroll);
        Child = root;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var top = TopLevel.GetTopLevel(this);
        if (top is not null)
        {
            // 48 = toolbar margin, ~40 = title + padding
            _scroll.MaxHeight = Math.Max(200, top.Bounds.Height - 48 - 40);
            top.SizeChanged += (_, _) =>
                _scroll.MaxHeight = Math.Max(200, top.Bounds.Height - 48 - 40);
        }
    }

    public void Clear(string title)
    {
        _title.Text = title;
        Target.Children.Clear();
    }

    public void AddHint(string text)
    {
        Target.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        });
    }

    public CheckBox AddCheck(string label, bool initial, Action<bool> onChange)
    {
        var cb = new CheckBox { Content = label, IsChecked = initial, Foreground = Brushes.White };
        cb.IsCheckedChanged += (_, _) => onChange(cb.IsChecked == true);
        Target.Children.Add(cb);
        return cb;
    }

    public void AddText(string label, string initial, Action<string> onChange)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White });
        var tb = new TextBox { Text = initial };
        tb.TextChanged += (_, _) => onChange(tb.Text);
        panel.Children.Add(tb);
        Target.Children.Add(panel);
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
        Target.Children.Add(panel);
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
        Target.Children.Add(panel);
    }

    public void AddButton(string label, Action onClick)
    {
        var b = new Button { Content = label };
        b.Click += (_, _) => onClick();
        Target.Children.Add(b);
    }

    // Append a pre-built control (e.g. a button with its own flyout).
    public void AddButtonRaw(Control c) => Target.Children.Add(c);

    /// A grid of clickable pawn swatches (concentric circles) for the pawn tool's
    /// placement submenu. Each swatch shows a template's outer/inner colors; a
    /// broken/empty entry shows an X. Clicking arms that template for placement.
    /// Hovering shows a tooltip: name, affiliation, and instance count ("Nx").
    /// The count is recomputed live on each hover via liveCount(templateId) so it
    /// reflects pawns placed since the panel was built.
    public void AddSwatchGrid(string label, IReadOnlyList<(string id, string name, string affiliation, int outerR, int outerG, int outerB, int innerR, int innerG, int innerB)> items, Func<string, int> liveCount, Action<int> onPick)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White });
        var grid = new WrapPanel { MaxWidth = 200 };
        for (int i = 0; i < items.Count; i++)
        {
            var (id, name, affiliation, oR, oG, oB, iR, iG, iB) = items[i];
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
            // recompute the count on every hover so it reflects pawns placed since
            // the panel was built (the panel is not rebuilt on each placement).
            cell.PointerEntered += (_, _) =>
                ToolTip.SetTip(cell, $"{name}, {affiliation} ({liveCount(id)}x)");
            grid.Children.Add(cell);
        }
        panel.Children.Add(grid);
        Target.Children.Add(panel);
    }

    /// A color field: [preview square][#hex textbox][palette button][red X]. The
    /// preview square (same size as the palette button) shows the current color.
    /// The hex box is king (accepts with or without #). The palette button opens a
    /// popup flyout with a 2x5 preset grid + full color picker. A red X appears to
    /// the right of the palette ONLY when the typed hex is invalid (e.g. "123").
    public StackPanel AddColorField(string label, int r, int g, int b, IReadOnlyList<(int r, int g, int b)> presets, Action<(int r, int g, int b)> onChange)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White });
        panel.Children.Add(ColorCell(r, g, b, presets, onChange));
        Target.Children.Add(panel);
        return panel;
    }

    /// The reusable color row: [preview square][#hex textbox][palette button][red X].
    /// Used by AddColorField and the status-style grid so both share one format.
    public StackPanel ColorCell(int r, int g, int b, IReadOnlyList<(int r, int g, int b)> presets, Action<(int r, int g, int b)> onChange)
    {
        var tb = new TextBox { Text = $"#{r:X2}{g:X2}{b:X2}", Width = 70 };
        var preview = new Rectangle { Width = 28, Height = 28, Fill = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b)) };
        var palette = new Button { Content = "🎨", Width = 38, Height = 28 };
        var invalidX = new TextBlock { Text = "✕", Foreground = Brushes.Red, FontSize = 18, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };

        // 2 row x 5 column preset grid (MS Paint style): colored squares, click sets it.
        var grid = new UniformGrid { Rows = 2, Columns = 5 };
        foreach (var (pr, pg, pb) in presets)
        {
            var sw = new Rectangle { Width = 28, Height = 28, Margin = new Thickness(2), Fill = new SolidColorBrush(Color.FromRgb((byte)pr, (byte)pg, (byte)pb)) };
            sw.PointerPressed += (_, _) =>
            {
                tb.Text = $"#{pr:X2}{pg:X2}{pb:X2}";
                onChange((pr, pg, pb));
            };
            grid.Children.Add(sw);
        }

        // full color picker for custom colors, seeded from the current hex.
        var picker = new ColorPicker { Color = Color.FromRgb((byte)r, (byte)g, (byte)b), IsAlphaVisible = false };

        // guard so picker<->hex updates don't feed back into each other
        bool syncing = false;
        picker.ColorChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            var c = picker.Color;
            tb.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            preview.Fill = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            invalidX.IsVisible = false;
            onChange((c.R, c.G, c.B));
            syncing = false;
        };
        tb.TextChanged += (_, _) =>
        {
            if (syncing) return;
            if (TryHexColor(tb.Text, out var c))
            {
                syncing = true;
                picker.Color = Color.FromRgb((byte)c.r, (byte)c.g, (byte)c.b);
                preview.Fill = new SolidColorBrush(Color.FromRgb((byte)c.r, (byte)c.g, (byte)c.b));
                invalidX.IsVisible = false;
                syncing = false;
                onChange(c);
            }
            else
            {
                invalidX.IsVisible = true;
            }
        };

        var popup = new StackPanel { Spacing = 6 };
        popup.Children.Add(grid);
        popup.Children.Add(picker);
        palette.Flyout = new Flyout { Content = popup };

        // order: preview, hex, palette, red X
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(preview);
        row.Children.Add(tb);
        row.Children.Add(palette);
        row.Children.Add(invalidX);
        return row;
    }

    // Insert a control right after an existing one (used to show inner/outer color
    // fields in place when "custom colors" is checked, without rebuilding the panel).
    public void InsertAfter(Control after, Control item)
    {
        int i = Target.Children.IndexOf(after);
        Target.Children.Insert(i >= 0 ? i + 1 : Target.Children.Count, item);
    }

    public void RemoveControl(Control c) => Target.Children.Remove(c);

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
