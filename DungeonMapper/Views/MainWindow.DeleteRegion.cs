using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DungeonMapper.Controls;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// the delete key (user, Nov 26): a selected region asks what to delete (the
// eraser's kinds, each with how many are in the region, all ticked to start,
// apart from the eraser's own ticks); shift+delete deletes everything in it
// without asking. with no region it deletes the item whose view is open.
public partial class MainWindow
{
    private void DeleteKey(bool shift)
    {
        if (Canvas.SelectionRect is not null)
        {
            if (shift) { Canvas.DeleteSelection(new EraseFilter(), Canvas.EraseAdjustWalls); Canvas.Refresh(); RefreshAfterRegionDelete(); }
            else ShowDeleteRegion();
            return;
        }
        if (ToolPanel.DeleteAction is { } del) { del(); return; }
        SetStatus("nothing to delete - click an item or select a region first", StatusKind.Warning);
    }

    private void RefreshAfterRegionDelete()
    {
        PawnViewsChanged();
        RefreshMaps();
        RefreshToolInfo();
    }

    private void ShowDeleteRegion()
    {
        if (Canvas.SelectionRect is not var (x0, y0, x1, y1)) return;
        var n = Canvas.CountRegion(x0, y0, x1, y1);
        // its own ticks: the eraser's stay as they are
        var f = new EraseFilter();
        bool adjust = Canvas.EraseAdjustWalls;

        var dock = new DockPanel { Margin = new(16, 12, 16, 16) };
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        var confirm = Btn("delete", danger: true);
        var cancel = Btn("cancel");
        foot.Children.Add(confirm);
        foot.Children.Add(cancel);
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(foot);

        var summary = new TextBlock { Classes = { "body" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var info = new Border { Classes = { "infobox" }, Margin = new(0, 10, 0, 0), Child = summary };
        DockPanel.SetDock(info, Dock.Bottom);
        dock.Children.Add(info);

        var list = new StackPanel { Spacing = 2 };
        dock.Children.Add(list);
        list.Children.Add(new TextBlock { Text = "tick what to delete. shift+delete deletes everything without asking.", Classes = { "hint" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new(0, 0, 0, 4) });

        var checks = new List<(CheckBox box, int count)>();
        void Update()
        {
            summary.Text = n.Total(f) == 0 ? "nothing ticked is in the region." : $"deletes {MapCanvas.DescribeCounts(n, f)}.";
            confirm.IsEnabled = n.Total(f) > 0;
        }
        void Row(string label, int count, bool on, System.Action<bool> set, string tag)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            // nothing of that kind in the region: unticked and greyed out
            var cb = new CheckBox { Content = label, IsChecked = count > 0 && on, IsEnabled = count > 0, Tag = tag };
            set(count > 0 && on);
            cb.IsCheckedChanged += (_, _) => { set(cb.IsChecked == true); Update(); };
            g.Children.Add(cb);
            var c = Tx($"×{count}", "count");
            c.Classes.Set("zero", count == 0);
            c.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(c, 1);
            g.Children.Add(c);
            list.Children.Add(g);
            checks.Add((cb, count));
        }
        Row("cells and flooring", n.Cells, true, v => f.Cells = v, "cells");
        Row("walls and the doors on them", n.Walls, true, v => f.Walls = v, "walls");
        Row("stairs", n.Stairs, true, v => f.Stairs = v, "stairs");
        Row("labels", n.Labels, true, v => f.Labels = v, "labels");
        Row("pawns", n.Pawns, true, v => f.Pawns = v, "pawns");
        Row("objects", n.Objects, true, v => f.Objects = v, "objects");
        Row("images", n.Images, true, v => f.Images = v, "images");
        var adjustBox = new CheckBox { Content = "adjust walls (re-seal rooms on the new edge)", IsChecked = adjust, Margin = new(0, 6, 0, 0), Tag = "adjust" };
        adjustBox.IsCheckedChanged += (_, _) => adjust = adjustBox.IsChecked == true;
        list.Children.Add(adjustBox);
        Update();

        confirm.Click += (_, _) =>
        {
            CloseModals();
            Canvas.DeleteSelection(f, adjust);
            Canvas.Refresh();
            RefreshAfterRegionDelete();
            FocusCanvas();
        };
        cancel.Click += (_, _) => { CloseModals(); FocusCanvas(); };

        ShowModal(380, 420, "Delete", $"{x1 - x0 + 1}x{y1 - y0 + 1} cell region", dock);
        _modalKind = "delete-region";
        // enter confirms
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (confirm.IsEnabled) confirm.Focus(); else cancel.Focus(); });
    }
}
