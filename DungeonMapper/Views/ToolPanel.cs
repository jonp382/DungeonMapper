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
    private readonly TextBlock _title = new() { Classes = { "title" }, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    // right of the title: the open item's hidden / visible badge
    private readonly ContentControl _badge = new() { VerticalAlignment = VerticalAlignment.Center };
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
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        head.Children.Add(_title);
        Grid.SetColumn(_badge, 1);
        head.Children.Add(_badge);
        root.Children.Add(head);
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

    /// true while the panel shows one placed item (a door, a pawn...) rather
    /// than its tool's own view
    public bool ItemView { get; private set; }

    /// starts a placed item's view (ClearItem) or a tool's view (Clear)
    public void ClearItem(string title)
    {
        Clear(title);
        ItemView = true;
    }

    public void Clear(string title)
    {
        ItemView = false;
        _title.Text = title;
        Target.Children.Clear();
        DeleteAction = null;
        _badge.Content = null;
        BadgeClick = null;
    }

    /// clicking the badge (null when the view has none)
    public Action? BadgeClick { get; private set; }
    /// the badge's text, null when there is none
    public string? BadgeText => (_badge.Content as Border)?.Child is TextBlock t ? t.Text : null;

    /// the item's hidden / visible badge by the title (user): pink "hidden"
    /// for DM only, grey "visible"; a click flips it
    public void SetBadge(bool hidden, Action onClick)
    {
        BadgeClick = onClick;
        var pill = new Border
        {
            Background = hidden ? new SolidColorBrush(Color.FromRgb(236, 90, 220)) : new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x46)) /* Ui.Bg.Button */,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 1, 8, 2),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = new TextBlock { Text = hidden ? "hidden" : "visible", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = hidden ? Brushes.Black : Brushes.White },
        };
        ToolTip.SetTip(pill, hidden ? "hidden from players (DM only), even on revealed floor. click to show it to them"
                                    : "players see it when it is on revealed floor. click to hide it from them");
        pill.PointerPressed += (_, e) => { e.Handled = true; onClick(); };
        _badge.Content = pill;
    }

    /// the open item view's delete (the delete key runs it), null when the
    /// panel shows no deletable item
    public Action? DeleteAction { get; set; }

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

    // a list of named rows under a label (the pawn view's statuses): an
    // optional dot, the name and an x that removes the row (onRemove gets the
    // name). an item with a number gets a second line: a "permanent" checkbox
    // (the number -1) and the number box (a status's turns left, applied on
    // enter or leaving the box, a bad one shows a red line). empty shows
    // emptyText.
    public StackPanel AddRemovableList(string label, IEnumerable<(string name, Control? dot, int? number)> items, Action<string> onRemove,
        Func<int, string?>? checkNumber = null, Action<string, int>? onNumber = null, string? numberTip = null, string emptyText = "none")
    {
        Target.Children.Add(FieldLabel(label));
        var list = new StackPanel { Spacing = 2 };
        foreach (var (name, dot, number) in items)
        {
            var n = name;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (dot is not null) { dot.VerticalAlignment = VerticalAlignment.Center; content.Children.Add(dot); }
            content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(content);
            var remove = new Button { Content = "✕", Classes = { "btn", "quiet" }, Tag = "remove:" + name, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2) };
            ToolTip.SetTip(remove, $"remove {name}");
            remove.Click += (_, _) => { ButtonHook?.Invoke(); onRemove(n); };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            var wrap = new StackPanel { Spacing = 0 };
            wrap.Children.Add(row);
            if (number is int start && onNumber is not null)
            {
                bool perm = start < 0;
                int last = start;
                // the turns a permanent one gets when unticked: the last count
                int count = perm ? 3 : start;
                var box = new TextBox { Text = count.ToString(), Classes = { "field", "number" }, Tag = name, Width = 56, VerticalAlignment = VerticalAlignment.Center, IsVisible = !perm };
                if (numberTip is not null) ToolTip.SetTip(box, numberTip);
                var permBox = new CheckBox { Content = "permanent", IsChecked = perm, Tag = "permanent:" + name, VerticalAlignment = VerticalAlignment.Center };
                var second = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(15, 0, 0, 0) };
                second.Children.Add(permBox);
                second.Children.Add(box);
                var refusal = RefusalLine();
                permBox.IsCheckedChanged += (_, _) =>
                {
                    bool on = permBox.IsChecked == true;
                    box.IsVisible = !on;
                    SetRefusal(refusal, null);
                    int v = on ? -1 : (int.TryParse(box.Text, out int typed) && checkNumber?.Invoke(typed) is null ? typed : count);
                    if (!on) box.Text = v.ToString();
                    if (v != last) { last = v; onNumber(n, v); }
                };
                void Apply()
                {
                    string? why = int.TryParse(box.Text, out int v) ? checkNumber?.Invoke(v) : "a whole number";
                    SetRefusal(refusal, why);
                    if (why is null && v != last) { last = v; count = v; onNumber(n, v); }
                }
                box.TextChanged += (_, _) => SetRefusal(refusal, int.TryParse(box.Text, out int v) ? checkNumber?.Invoke(v) : "a whole number");
                box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Apply(); e.Handled = true; } };
                box.LostFocus += (_, _) => Apply();
                wrap.Children.Add(second);
                wrap.Children.Add(refusal);
            }
            list.Children.Add(wrap);
        }
        if (list.Children.Count == 0) list.Children.Add(new TextBlock { Text = emptyText, Classes = { "hint" } });
        Target.Children.Add(list);
        return list;
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

    // a labelled slider snapping to step. onChange fires live while dragging
    public Slider AddSlider(string label, double min, double max, double step, double initial, Action<double> onChange)
    {
        Target.Children.Add(FieldLabel(label));
        var sl = new Slider
        {
            Minimum = min, Maximum = max, SmallChange = step, LargeChange = step,
            TickFrequency = step, IsSnapToTickEnabled = true, Value = initial,
        };
        sl.ValueChanged += (_, e) => onChange(e.NewValue);
        Target.Children.Add(sl);
        return sl;
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
