using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Views;

// shared builders for code built ui.
// the look comes from the style classes in Styles/Ui.axaml where one exists,
// so axaml and code built menus stay the same. callers pull these in with
// "using static DungeonMapper.Views.Ui;".
internal static class Ui
{
    // ---------- brushes and text ----------

    public static SolidColorBrush Hx(string hex) => new(Color.Parse(hex));
    public static SolidColorBrush Rgb((int r, int g, int b) c) => new(Color.FromRgb((byte)c.r, (byte)c.g, (byte)c.b));
    public static string HexOf((int r, int g, int b) c) => $"#{c.r:x2}{c.g:x2}{c.b:x2}";

    public static TextBlock Txt(string text, double size = 12, string color = "#e6e6ee") =>
        new() { Text = text, FontSize = size, Foreground = Hx(color), VerticalAlignment = VerticalAlignment.Center };

    // text styled only by classes from Styles/Ui.axaml
    public static TextBlock Tx(string text, params string[] classes)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in classes) t.Classes.Add(c);
        return t;
    }

    // small grey label that sits above its control
    public static TextBlock FieldLabel(string text) => new() { Text = text, Classes = { "label" } };

    // 12 semibold heading with an optional muted line under it
    public static StackPanel Section(string title, string? description = null)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock { Text = title, Classes = { "section" } });
        if (!string.IsNullOrEmpty(description))
            sp.Children.Add(new TextBlock { Text = description, Classes = { "hint" }, Margin = new Thickness(2, 0, 0, 0) });
        return sp;
    }

    // red line under a control saying why an input was refused. hidden while empty.
    public static TextBlock RefusalLine() => new() { Classes = { "refusal" }, IsVisible = false };

    public static void SetRefusal(TextBlock line, string? text)
    {
        line.Text = text ?? "";
        line.IsVisible = !string.IsNullOrEmpty(text);
    }

    // ---------- buttons ----------

    public static Button Btn(string label, bool primary = false, bool danger = false)
    {
        var b = new Button { Content = label, Classes = { "btn" } };
        if (primary) b.Classes.Add("primary");
        if (danger) b.Classes.Add("danger");
        return b;
    }

    // a rounded filter chip, optionally with an affiliation dot
    public static Button Chip(string label, bool active, Control? dot, Action onClick)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (dot is not null) sp.Children.Add(dot);
        sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = sp, Classes = { "chip" } };
        if (active) b.Classes.Add("on");
        b.Click += (_, _) => onClick();
        return b;
    }

    // a small crosshair drawn with shapes so it does not depend on font glyphs
    public static Button LocateButton(string tip, Action onClick)
    {
        var g = new Grid { Width = 12, Height = 12 };
        g.Children.Add(new Ellipse { Width = 10, Height = 10, Classes = { "ring" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(new Ellipse { Width = 3, Height = 3, Classes = { "dot" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = g, Classes = { "locate" } };
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button StarButton(bool on, Action onClick, double size = 13)
    {
        var b = new Button { Content = "★", FontSize = size, Classes = { "star" } };
        if (on) b.Classes.Add("on");
        ToolTip.SetTip(b, on ? "remove from favorites" : "add to favorites");
        b.Click += (_, _) => onClick();
        return b;
    }

    // asks before a destructive action: message, danger confirm, normal cancel
    public static void ConfirmFlyout(Control anchor, string message, Action onYes, string yesLabel = "delete")
    {
        var popup = new StackPanel { Spacing = 8 };
        popup.Children.Add(new TextBlock { Text = message, Classes = { "body" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 240 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var flyout = new Flyout { Content = popup };
        var yes = Btn(yesLabel, danger: true);
        var no = Btn("cancel");
        yes.Click += (_, _) => { flyout.Hide(); onYes(); };
        no.Click += (_, _) => flyout.Hide();
        row.Children.Add(yes);
        row.Children.Add(no);
        popup.Children.Add(row);
        flyout.ShowAt(anchor);
    }

    // ---------- inputs ----------

    public static TextBox SearchBox(string watermark, string text) => new()
    {
        Text = text,
        PlaceholderText = watermark,
        Classes = { "field" },
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    // whole number box: applies on enter or focus loss, a value outside min..max
    // is refused with a red border and the old value stays in effect
    public static TextBox NumberBox(int value, int min, int max, Action<int> onChange)
    {
        var tb = new TextBox { Text = value.ToString(), Classes = { "field", "number" } };
        ToolTip.SetTip(tb, $"{min} to {max}");
        int current = value;
        void Apply()
        {
            if (int.TryParse(tb.Text?.Trim(), out int v) && v >= min && v <= max)
            {
                tb.Classes.Remove("invalid");
                if (v != current) { current = v; onChange(v); }
            }
            else tb.Classes.Add("invalid");
        }
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Apply(); e.Handled = true; }
        };
        tb.LostFocus += (_, _) => Apply();
        return tb;
    }

    public static CheckBox ToggleRow(string label, bool on, Action<bool> onChange)
    {
        var cb = new CheckBox
        {
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
            IsChecked = on,
            Classes = { "toggle" },
        };
        cb.IsCheckedChanged += (_, _) => onChange(cb.IsChecked == true);
        return cb;
    }

    // the 10 preset colors for the color picker popup (ms paint palette style):
    // red, orange, yellow, green, blue, purple, white, black, gray, brown.
    public static List<(int r, int g, int b)> PresetColors() => new()
    {
        (255, 0, 0), (255, 128, 0), (255, 255, 0), (0, 128, 0), (0, 0, 255),
        (128, 0, 128), (255, 255, 255), (0, 0, 0), (128, 128, 128), (128, 64, 0),
    };

    // swatch + hex button that opens the preset grid and color picker
    public static Button ColorButton((int r, int g, int b) c, Action<(int r, int g, int b)> onChange, bool enabled = true)
    {
        var sw = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(3), Background = Rgb(c) };
        var hex = new TextBlock { Text = HexOf(c), Classes = { "hex" } };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(sw);
        sp.Children.Add(hex);
        var b = new Button { Content = sp, Classes = { "color" }, IsEnabled = enabled };
        void Set((int r, int g, int b) v)
        {
            sw.Background = Rgb(v);
            hex.Text = HexOf(v);
            onChange(v);
        }
        var grid = new UniformGrid { Rows = 2, Columns = 5 };
        var picker = new ColorPicker { Color = Color.FromRgb((byte)c.r, (byte)c.g, (byte)c.b), IsAlphaVisible = false };
        foreach (var p in PresetColors())
        {
            var cell = new Rectangle { Width = 26, Height = 26, Margin = new Thickness(2), Fill = Rgb(p) };
            cell.PointerPressed += (_, _) => { picker.Color = Color.FromRgb((byte)p.r, (byte)p.g, (byte)p.b); Set(p); };
            grid.Children.Add(cell);
        }
        picker.ColorChanged += (_, _) => { var k = picker.Color; Set((k.R, k.G, k.B)); };
        var pop = new StackPanel { Spacing = 6 };
        pop.Children.Add(grid);
        pop.Children.Add(picker);
        b.Flyout = new Flyout { Content = pop };
        return b;
    }

    // ---------- dots and caps ----------

    // a two-tone dot in the given colors (affiliation chips, group headers)
    public static Control TwoTone((int r, int g, int b) outer, (int r, int g, int b) inner, double size)
    {
        var g = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(new Ellipse { Width = size, Height = size, Fill = Rgb(outer) });
        g.Children.Add(new Ellipse { Width = size / 2, Height = size / 2, Fill = Rgb(inner), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return g;
    }

    public static Control AffDot(AffiliationColor ac, double size) =>
        TwoTone((ac.OuterR, ac.OuterG, ac.OuterB), (ac.InnerR, ac.InnerG, ac.InnerB), size);

    public static Border KeyCap(string text) =>
        new() { Classes = { "keycap" }, Child = new TextBlock { Text = text } };
}
