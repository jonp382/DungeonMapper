using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// a placed pawn's statuses in its view: one row per status it has (dot, name,
// turns box, x to remove) and an add status button that opens a searchable
// menu of the statuses it does not have yet (user: a checkbox per status does
// not scale to many statuses). a click in the menu adds that status with its
// default turns and the menu stays open. the DM's changes are one undo step
// each; a joined player's go to the DM. the pawn is looked up by its cell
// every time, since undo and DM updates swap the pawn objects.
public partial class MainWindow
{
    private Pawn? PawnAt(CellKey cell) => PanelDoc.Pawns.TryGetValue(cell, out var p) ? p : null;

    private void AddStatusList(Pawn pawn)
    {
        var cell = pawn.Cell;
        var items = PanelPresets.PawnStatuses.Where(s => s.Name != CampaignPresets.NoStatus && pawn.HasStatus(s.Name))
            .Select(s => (s.Name, (Control?)new Ellipse { Width = 9, Height = 9, Fill = Rgb((s.R, s.G, s.B)) },
                (int?)pawn.Statuses.First(e => e.Name == s.Name).Turns));
        ToolPanel.AddRemovableList("statuses", items,
            name => Avalonia.Threading.Dispatcher.UIThread.Post(() => RemovePawnStatus(cell, name)),
            v => v >= 1 && v <= TurnTracker.MaxTurns ? null : $"1 to {TurnTracker.MaxTurns} turns",
            (name, v) => SetPawnStatusTurns(cell, name, v),
            "turns left", "no statuses");
        ToolPanel.AddButton("add status", () => ShowAddStatus(cell));
    }

    /// a pawn's new statuses: on the DM's map (one undo step) or, joined, shown
    /// at once and sent to the DM, whose map confirms it
    private void SetPawnStatuses(Pawn pawn, List<PawnStatusEntry> list)
    {
        if (Joined)
        {
            if (_client is not { } cl) return;
            var cell = pawn.Cell;
            pawn.Statuses = list;
            Canvas.Refresh();
            _ = System.Threading.Tasks.Task.Run(() => cl.SendPawnStatus(cell, list));
            if (PanelLibrary.Find(pawn.TemplateId) is { } t) SetStatus($"{PawnName(t)}: {StatusNames(pawn)}");
            return;
        }
        pawn.Statuses = list;
        ActiveHistory.Commit(_doc);
        Canvas.Refresh();
    }

    /// adds a status with its default turns and rebuilds the pawn's view
    private void AddPawnStatus(CellKey cell, string name)
    {
        if (PawnAt(cell) is not { } p || p.HasStatus(name)) return;
        var list = p.CloneStatuses();
        list.Add(new PawnStatusEntry { Name = name, Turns = TurnTracker.CleanTurns(PanelPresets.FindPawnStatus(name).DefaultTurns) });
        SetPawnStatuses(p, list);
        ShowPawnInfo(p);
    }

    private void RemovePawnStatus(CellKey cell, string name)
    {
        if (PawnAt(cell) is not { } p || !p.HasStatus(name)) return;
        var list = p.CloneStatuses();
        list.RemoveAll(s => s.Name == name);
        SetPawnStatuses(p, list);
        ShowPawnInfo(p);
        FocusCanvas();
    }

    private void SetPawnStatusTurns(CellKey cell, string name, int turns)
    {
        if (PawnAt(cell) is not { } p) return;
        var list = p.CloneStatuses();
        if (list.FirstOrDefault(s => s.Name == name) is not { } e || e.Turns == turns) return;
        e.Turns = turns;
        SetPawnStatuses(p, list);
        // the header icon's arc follows the turns. rebuild unless the focus
        // moved on to another box in the panel (a rebuild would take it away)
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var focused = FocusManager?.GetFocusedElement() as Visual;
            bool inPanel = focused is not null && (focused == ToolPanel || focused.GetVisualAncestors().Contains(ToolPanel));
            if (!inPanel && PawnAt(cell) is { } now) ShowPawnInfo(now);
        });
    }

    /// the add status menu for the pawn at cell: search, then click a status
    /// to add it; added ones leave the list and the menu stays open
    private void ShowAddStatus(CellKey cell)
    {
        if (PawnAt(cell) is not { } pawn) return;
        var t = PanelLibrary.Find(pawn.TemplateId);
        var dock = new DockPanel { Margin = new(16, 12, 16, 16) };

        var foot = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new(0, 12, 0, 0) };
        var count = new TextBlock { Classes = { "hint" }, VerticalAlignment = VerticalAlignment.Center };
        foot.Children.Add(count);
        var done = Btn("done", primary: true);
        done.Click += (_, _) => { CloseModals(); FocusCanvas(); };
        Grid.SetColumn(done, 1);
        foot.Children.Add(done);
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(foot);

        var top = new StackPanel { Spacing = 6, Margin = new(0, 0, 0, 8) };
        top.Children.Add(new TextBlock { Text = "click a status to add it. it starts with its default turns, which you can change in the pawn's view.", Classes = { "hint" }, TextWrapping = TextWrapping.Wrap });
        var search = SearchBox("search statuses", "");
        top.Children.Add(search);
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);

        var list = new StackPanel { Spacing = 0, Name = "AddStatusList" };
        dock.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

        void Build()
        {
            list.Children.Clear();
            if (PawnAt(cell) is not { } p) { CloseModals(); return; }
            var q = (search.Text ?? "").Trim();
            var left = PanelPresets.PawnStatuses.Where(s => s.Name != CampaignPresets.NoStatus && !p.HasStatus(s.Name)).ToList();
            var shown = left.Where(s => q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var s in shown)
            {
                var name = s.Name;
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*,Auto"), ColumnSpacing = 6 };
                g.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Rgb((s.R, s.G, s.B)), VerticalAlignment = VerticalAlignment.Center });
                var label = new TextBlock { Text = name, Classes = { "body" }, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(label, 1);
                g.Children.Add(label);
                int turns = TurnTracker.CleanTurns(s.DefaultTurns);
                var dur = Txt(turns < 0 ? "permanent" : turns == 1 ? "1 turn" : $"{turns} turns", 11, "#8a8a9a");
                dur.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(dur, 2);
                g.Children.Add(dur);
                var row = new Border { Child = g, Classes = { "row" }, MinHeight = 28, Tag = name };
                ToolTip.SetTip(row, $"add {name}");
                row.PointerPressed += (_, _) => { AddPawnStatus(cell, name); Build(); };
                list.Children.Add(row);
            }
            if (shown.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = left.Count == 0 ? "this pawn has every status." : "no statuses match.",
                    Classes = { "hint" }, Margin = new(0, 6, 0, 0),
                });
            count.Text = left.Count == 0 ? "" : shown.Count == left.Count ? $"{left.Count} to add" : $"{shown.Count} of {left.Count}";
        }
        search.TextChanged += (_, _) => Build();
        Build();

        ShowModal(380, 460, "Add status", t is null ? null : PawnName(t), dock);
        _modalKind = "add-status";
        Avalonia.Threading.Dispatcher.UIThread.Post(() => search.Focus());
    }
}
