using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

/// Right-docked floating panel that hosts per-tool options (erase filter,
/// paste ghost toggle) and the config fields for a selected door/stair/label.
/// Replaces the modal ConfigDialog. Fields apply live on change.
public sealed class ToolPanel : Border
{
    // side panel titles are 14, a step below the modal titles
    private readonly TextBlock _title = new() { Classes = { "title" }, FontSize = 14 };
    private readonly StackPanel _content = new() { Spacing = 6 };
    private readonly ScrollViewer _scroll;
    // When set, all Add* builders append here instead of _content. Lets the modal
    // popup host the same field builders (color fields, text, number, button).
    private Panel? _target;

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
        Target.Children.Add(new TextBlock { Text = text, Classes = { "body" }, TextWrapping = TextWrapping.Wrap });
    }

    // a toggle, with an optional muted line under it explaining what it does
    public CheckBox AddCheck(string label, bool initial, Action<bool> onChange, string? hint = null)
    {
        var cb = ToggleRow(label, initial, onChange);
        if (hint is null) { Target.Children.Add(cb); return cb; }
        var sp = new StackPanel { Spacing = 0 };
        sp.Children.Add(cb);
        sp.Children.Add(new TextBlock { Text = hint, Classes = { "hint" }, Margin = new Thickness(28, 0, 0, 2) });
        Target.Children.Add(sp);
        return cb;
    }

    // a read only facts box (counts). returns the text so the caller can update it in place
    public TextBlock AddInfo(string text)
    {
        var tb = new TextBlock { Text = text, Classes = { "body" }, TextWrapping = TextWrapping.Wrap };
        Target.Children.Add(new Border { Classes = { "infobox" }, Child = tb, Margin = new Thickness(0, 4, 0, 0) });
        return tb;
    }

    // onChange fires on every keystroke for a live preview. onDone fires when
    // the edit ends: focus leaves the box or the box is removed from the panel.
    public TextBox AddText(string label, string initial, Action<string> onChange, bool multiline = false, Action? onDone = null)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(FieldLabel(label));
        var tb = new TextBox { Text = initial, AcceptsReturn = multiline, Classes = { "field" } };
        tb.TextChanged += (_, _) => onChange(tb.Text ?? "");
        if (onDone is not null)
        {
            tb.LostFocus += (_, _) => onDone();
            tb.DetachedFromVisualTree += (_, _) => onDone();
        }
        panel.Children.Add(tb);
        Target.Children.Add(panel);
        return tb;
    }

    // onChange fires on every parsed keystroke for a live preview. onDone fires
    // when the edit ends: enter, focus leaving the box, or the box being removed.
    public TextBox AddNumber(string label, int initial, Action<int> onChange, Action? onDone = null)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(FieldLabel(label));
        var tb = new TextBox { Text = initial.ToString(), Classes = { "field", "number" }, HorizontalAlignment = HorizontalAlignment.Left };
        tb.TextChanged += (_, _) =>
        {
            if (int.TryParse(tb.Text, out int v)) onChange(v);
        };
        if (onDone is not null)
        {
            tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { onDone(); e.Handled = true; } };
            tb.LostFocus += (_, _) => onDone();
            tb.DetachedFromVisualTree += (_, _) => onDone();
        }
        panel.Children.Add(tb);
        Target.Children.Add(panel);
        return tb;
    }

    // a row of chips where exactly one is active, clicking one makes it active.
    // label puts a field label above the row, dot gives a chip a leading marker.
    public void AddChips(string[] options, string current, Action<string> onChange, string? label = null, Func<string, Control?>? dot = null)
    {
        if (label is not null) Target.Children.Add(FieldLabel(label));
        var row = new WrapPanel();
        void Build(string active)
        {
            row.Children.Clear();
            foreach (var o in options)
            {
                var opt = o;
                row.Children.Add(Chip(opt, opt == active, dot?.Invoke(opt), () =>
                {
                    ButtonHook?.Invoke();
                    Build(opt);
                    onChange(opt);
                }));
            }
        }
        Build(current);
        Target.Children.Add(row);
    }

    public void AddButton(string label, Action onClick)
    {
        var b = Btn(label);
        b.Click += (_, _) =>
        {
            // a panel button click counts as a cancel of an armed move (restores
            // hotkeys); the hook fires before the button's own action.
            ButtonHook?.Invoke();
            onClick();
        };
        Target.Children.Add(b);
    }

    /// Fires on every panel button click. MainWindow wires this to cancel an
    /// armed move (a button click counts as a cancel, restoring hotkeys).
    public Action? ButtonHook { get; set; }

    // Append a pre-built control (e.g. a button with its own flyout).
    public void AddButtonRaw(Control c) => Target.Children.Add(c);

    /// A color field: [preview square][#hex textbox][palette button][red X]. The
    /// preview square (same size as the palette button) shows the current color.
    /// The hex box is king (accepts with or without #). The palette button opens a
    /// popup flyout with a 2x5 preset grid + full color picker. A red X appears to
    /// the right of the palette ONLY when the typed hex is invalid (e.g. "123").
    public StackPanel AddColorField(string label, int r, int g, int b, IReadOnlyList<(int r, int g, int b)> presets, Action<(int r, int g, int b)> onChange)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(FieldLabel(label));
        panel.Children.Add(ColorCell(r, g, b, presets, onChange));
        Target.Children.Add(panel);
        return panel;
    }

    /// The reusable color row: [preview square][palette button]. The preview square
    /// shows the current color; the palette button opens a flyout with a 2x5 preset
    /// grid + full color picker (the picker has its own built-in hex entry, so a
    /// separate hex textbox is redundant). showPreview=false drops the square (used
    /// for affiliation rows, which are palette-only).
    public StackPanel ColorCell(int r, int g, int b, IReadOnlyList<(int r, int g, int b)> presets, Action<(int r, int g, int b)> onChange, bool showPreview = true)
    {
        var preview = new Rectangle { Width = 28, Height = 28, Fill = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b)) };
        var palette = new Button { Content = "🎨", Width = 38, Height = 28 };

        // 2 row x 5 column preset grid (MS Paint style): colored squares, click sets it.
        var grid = new UniformGrid { Rows = 2, Columns = 5 };
        foreach (var (pr, pg, pb) in presets)
        {
            var sw = new Rectangle { Width = 28, Height = 28, Margin = new Thickness(2), Fill = new SolidColorBrush(Color.FromRgb((byte)pr, (byte)pg, (byte)pb)) };
            sw.PointerPressed += (_, _) =>
            {
                preview.Fill = new SolidColorBrush(Color.FromRgb((byte)pr, (byte)pg, (byte)pb));
                onChange((pr, pg, pb));
            };
            grid.Children.Add(sw);
        }

        // full color picker for custom colors (built-in hex entry), seeded from current.
        var picker = new ColorPicker { Color = Color.FromRgb((byte)r, (byte)g, (byte)b), IsAlphaVisible = false };

        // guard so picker updates don't feed back into each other
        bool syncing = false;
        picker.ColorChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            var c = picker.Color;
            preview.Fill = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            onChange((c.R, c.G, c.B));
            syncing = false;
        };

        var popup = new StackPanel { Spacing = 6 };
        popup.Children.Add(grid);
        popup.Children.Add(picker);
        palette.Flyout = new Flyout { Content = popup };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (showPreview) row.Children.Add(preview);
        row.Children.Add(palette);
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
}
