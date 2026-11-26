using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// hotkeys modal. the frame is fixed in MainWindow.axaml and styled by
// Styles/Ui.axaml. the tool rows come from _settings.ToolHotkeys and the
// fixed rows from FixedShortcuts, both built here with the shared classes.
// a letter applies as soon as it is pressed and is written to disk on close.
public partial class MainWindow
{
    private static readonly Dictionary<string, string> HotkeyDefaults = AppSettings.Default().ToolHotkeys;

    // reference only. keys split on " / " into separate caps.
    private static readonly (string keys, string what)[] FixedShortcuts =
    [
        ("Ctrl+S", "save"),
        ("Ctrl+Z / Ctrl+Y", "undo / redo"),
        ("Ctrl+C / Ctrl+X / Ctrl+V", "copy, cut, paste at the cursor"),
        ("Ctrl+F", "search pawns"),
        ("Delete", "erase the selection"),
        ("R / Shift+R", "rotate a picked up selection or an armed formation"),
        ("Enter", "close the polygon"),
        ("Esc", "cancel a move, polygon, wall line or formation deploy, or close a menu"),
        ("Right or middle drag", "pan"),
        ("Wheel", "zoom"),
    ];

    private sealed record HotkeyRow(string Mode, TextBox Box, TextBlock Error, Button Reset);

    private HotkeyRow[] _hotkeyRows = [];

    // wired once from the constructor
    private void InitHotkeys()
    {
        HotkeysClose.Click += (_, _) => CloseHotkeys();
        HotkeysResetAll.Click += (_, _) => ConfirmFlyout(HotkeysResetAll,
            "put every hotkey back to its default letter?", ResetAllHotkeys, "reset all");

        foreach (var (keys, what) in FixedShortcuts)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
            var caps = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var parts = keys.Split(" / ");
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) caps.Children.Add(new TextBlock { Text = "/", Classes = { "muted" }, Margin = new(5, 0), VerticalAlignment = VerticalAlignment.Center });
                caps.Children.Add(KeyCap(parts[i]));
            }
            g.Children.Add(caps);
            var w = new TextBlock { Text = what, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(w, 1);
            g.Children.Add(w);
            FixedKeyRows.Children.Add(g);
        }
    }

    private void ShowHotkeys()
    {
        FlushSettings();
        BuildHotkeyRows();
        HotkeysOverlay.IsVisible = true;
    }

    private void CloseHotkeys()
    {
        FlushSettings();
        HotkeysOverlay.IsVisible = false;
        FocusCanvas();
    }

    // one row per tool: name and any refusal on the left, the letter box,
    // then a reset that only shows when the letter is not the default
    private void BuildHotkeyRows()
    {
        HotkeyRows.Children.Clear();
        var rows = new List<HotkeyRow>();
        foreach (var mode in _settings.ToolHotkeys.Keys)
        {
            var g = new Grid { Classes = { "setting" }, ColumnDefinitions = new ColumnDefinitions("*,Auto,58") };
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock { Text = HotkeyName(mode), Classes = { "body" } });
            var error = new TextBlock { Classes = { "refusal" }, IsVisible = false };
            left.Children.Add(error);
            g.Children.Add(left);

            // read only so typed text never lands in it. the key handler sets
            // the letter, which is what the window's hotkey loop understands.
            var box = new TextBox { Classes = { "field", "key" }, IsReadOnly = true, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(box, 1);
            g.Children.Add(box);

            var reset = new Button { Content = "reset", Classes = { "btn", "quiet" }, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(reset, 2);
            g.Children.Add(reset);

            var row = new HotkeyRow(mode, box, error, reset);
            box.AddHandler(KeyDownEvent, (_, e) => HotkeyKeyDown(row, e), RoutingStrategies.Tunnel);
            reset.Click += (_, _) =>
            {
                if (HotkeyDefaults.TryGetValue(mode, out var d)) SetHotkey(row, d);
            };
            rows.Add(row);
            HotkeyRows.Children.Add(g);
        }
        _hotkeyRows = [.. rows];
        RefreshHotkeys();
    }

    private void HotkeyKeyDown(HotkeyRow row, KeyEventArgs e)
    {
        // tab moves on, ctrl shortcuts like save still work
        if (e.Key == Key.Tab || (e.KeyModifiers & ~KeyModifiers.Shift) != KeyModifiers.None) return;
        e.Handled = true;
        if (e.Key == Key.Escape) { RefreshHotkeys(); FocusCanvas(); return; }
        if (e.Key is Key.LeftShift or Key.RightShift) return;
        if (AppSettings.HotkeyNameOf(e.Key) is { } name)
        {
            SetHotkey(row, name);
            return;
        }
        RefreshHotkeys();
        row.Box.Classes.Add("invalid");
        row.Error.Text = "use a letter, a digit or a key like \\ [ ] ; ' , . / - =";
        row.Error.IsVisible = true;
    }

    private void SetHotkey(HotkeyRow row, string letter)
    {
        if (_settings.ToolHotkeys[row.Mode] != letter)
        {
            _settings.ToolHotkeys[row.Mode] = letter;
            MarkSetting();
            RefreshToolLabels();
        }
        RefreshHotkeys();
    }

    // letters, resets and clashes. a clash is still saved so two letters can
    // be swapped one box at a time, but both rows show it until it is fixed.
    private void RefreshHotkeys()
    {
        foreach (var r in _hotkeyRows)
        {
            string letter = _settings.ToolHotkeys.GetValueOrDefault(r.Mode, "");
            r.Box.Text = AppSettings.HotkeyLabel(letter);
            r.Reset.IsVisible = HotkeyDefaults.TryGetValue(r.Mode, out var d) && d != letter;
            var others = _hotkeyRows.Where(o => o != r && _settings.ToolHotkeys.GetValueOrDefault(o.Mode) == letter)
                .Select(o => HotkeyName(o.Mode)).ToList();
            bool clash = others.Count > 0;
            if (clash) r.Box.Classes.Add("invalid"); else r.Box.Classes.Remove("invalid");
            r.Error.Text = clash ? $"also used by {string.Join(", ", others)}." : "";
            r.Error.IsVisible = clash;
        }
    }

    private static string HotkeyName(string mode) =>
        mode == AppSettings.FitHotkey ? "zoom to fit"
        : mode == AppSettings.FormationHotkey ? "deploy last formation"
        : mode == AppSettings.PingHotkey ? "ping (sessions)"
        : mode.ToLowerInvariant();

    private void ResetAllHotkeys()
    {
        _settings.ToolHotkeys = new Dictionary<string, string>(HotkeyDefaults);
        MarkSetting();
        RefreshToolLabels();
        BuildHotkeyRows();
        StatusText.Text = "hotkeys reset to defaults";
    }
}
