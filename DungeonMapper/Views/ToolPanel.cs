using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// right docked floating panel that hosts the per-tool options and the config
// views for a selected door, stair, label or pawn. fields apply live on change.
public sealed class ToolPanel : Border
{
    // side panel titles are 14, a step below the modal titles
    private readonly TextBlock _title = new() { Classes = { "title" }, FontSize = 14 };
    private readonly StackPanel _content = new() { Spacing = 6 };
    private readonly ScrollViewer _scroll;
    // where the Add* builders append
    public Panel Target => _content;

    public ToolPanel()
    {
        Padding = new Thickness(10);
        Width = 220;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 48, 8, 0); // sits below the toolbar, over the canvas
        // content scrolls so long panels (pawn grid) fit. MaxHeight tracks the
        // window height so the scrollbar appears when content runs past it.
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

    // Insert a control right after an existing one (used to show inner/outer color
    // fields in place when "custom colors" is checked, without rebuilding the panel).
    public void InsertAfter(Control after, Control item)
    {
        int i = Target.Children.IndexOf(after);
        Target.Children.Insert(i >= 0 ? i + 1 : Target.Children.Count, item);
    }

    public void RemoveControl(Control c) => Target.Children.Remove(c);
}
