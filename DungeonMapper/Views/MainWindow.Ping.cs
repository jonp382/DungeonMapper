using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// pings (multiplayer): the ping key (backslash by default, hotkeys menu)
// marks the cell under the cursor. in a session everyone else sees rings on
// it and a toast at the top right with a jump button. the DM's pings and
// the players' pings are on the map shown to players.
public partial class MainWindow
{
    public const int MaxToasts = 4;
    public const double ToastSeconds = 10;

    private bool IsPingKey(Key k) =>
        Enum.TryParse<Key>(_settings.ToolHotkeys.GetValueOrDefault(AppSettings.PingHotkey, ""), out var p) && p == k;

    private void DoPing()
    {
        if (Canvas.CursorCell is not CellKey c) { SetStatus("point at the map to ping", StatusKind.Warning); return; }
        if (_client is not null)
        {
            Canvas.ShowPing(c, "you");
            var cl = _client;
            _ = System.Threading.Tasks.Task.Run(() => cl.SendPing(c.X, c.Y));
            SetStatus($"pinged {c.X},{c.Y}");
        }
        else if (_host is not null)
        {
            var shown = ShownMap();
            if (shown is null || shown.Doc != _doc)
            {
                SetStatus($"ping not sent: players are on {shown?.Name ?? "no map"}, not this one", StatusKind.Warning);
                return;
            }
            Canvas.ShowPing(c, "you");
            _host.Ping("DM", c.X, c.Y);
            SetStatus($"pinged {c.X},{c.Y}");
        }
        else
        {
            Canvas.ShowPing(c, "ping");
            SetStatus($"pinged {c.X},{c.Y} (nobody else sees it outside a session)");
        }
    }

    // a ping from a player, on the DM's side
    private void OnHostPing(string name, int x, int y)
    {
        var shown = ShownMap();
        var cell = new CellKey(x, y);
        bool here = shown is not null && shown.Doc == _doc;
        if (here) Canvas.ShowPing(cell, name);
        AddToast($"{name} pinged {x},{y}" + (here || shown is null ? "" : $" on {shown.Name}"), () =>
        {
            var s = ShownMap();
            if (s is null) return;
            if (s.Doc != _doc) SwitchMap(_project.Maps.IndexOf(s));
            Canvas.CenterOn(cell);
            Canvas.ShowPing(cell, name);
        });
    }

    // a ping from the DM or another player, on a player's side
    private void OnClientPing(string name, int x, int y)
    {
        var cell = new CellKey(x, y);
        Canvas.ShowPing(cell, name);
        AddToast($"{name} pinged {x},{y}", () => { Canvas.CenterOn(cell); Canvas.ShowPing(cell, name); });
    }

    /// a toast at the top right: text, jump, close. it goes by itself after a while
    private void AddToast(string text, Action jump)
    {
        Border? toast = null;
        void Close() { if (toast is not null) PingToasts.Children.Remove(toast); }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        grid.Children.Add(new TextBlock { Text = text, Classes = { "body" }, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var go = Btn("jump", primary: true);
        go.Click += (_, _) => { jump(); Close(); FocusCanvas(); };
        Grid.SetColumn(go, 1);
        grid.Children.Add(go);
        var x = Btn("x");
        x.Click += (_, _) => { Close(); FocusCanvas(); };
        Grid.SetColumn(x, 2);
        grid.Children.Add(x);
        toast = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 22, 22, 30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 220, 255)), BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6), Child = grid,
        };
        PingToasts.Children.Insert(0, toast);
        while (PingToasts.Children.Count > MaxToasts) PingToasts.Children.RemoveAt(PingToasts.Children.Count - 1);
        DispatcherTimer.RunOnce(Close, TimeSpan.FromSeconds(ToastSeconds));
    }
}
