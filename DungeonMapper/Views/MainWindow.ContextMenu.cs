using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using DungeonMapper.Controls;
using DungeonMapper.Models;

namespace DungeonMapper.Views;

// right click menus on the map (user, Nov 27): a right click without a drag
// (a right drag still pans) opens a menu for what is under the cursor, the
// same actions its view or tool offers. see agent_todo.md for the full list.
public partial class MainWindow
{
    /// the menu last opened (tests read its items)
    private MenuFlyout? _contextMenu;

    private void InitContextMenus()
    {
        Canvas.ContextRequested += OnContextRequested;
    }

    private void OnContextRequested(Point world, CellKey cell)
    {
        if (Canvas.PlayerPreview || Joined) return;
        var items = ContextItems(world, cell);
        if (items.Count == 0) return;
        Canvas.CancelMove();
        var menu = new MenuFlyout();
        foreach (var i in items) menu.Items.Add(i);
        _contextMenu = menu;
        menu.ShowAt(Canvas, true);
    }

    /// the menu for what is at the point, top item first
    private List<Control> ContextItems(Point world, CellKey cell)
    {
        if (_doc.Pawns.TryGetValue(cell, out var pawn)) return PawnMenu(pawn);
        return new();
    }

    private static MenuItem MenuAction(string header, Action onClick, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static MenuItem SubMenu(string header, IEnumerable<Control> items)
    {
        var mi = new MenuItem { Header = header };
        foreach (var i in items) mi.Items.Add(i);
        mi.IsEnabled = mi.Items.Count > 0;
        return mi;
    }

    /// a menu action that changes the map: one undo step, the open views follow
    private void MenuCommit(string status)
    {
        ActiveHistory.Commit(_doc);
        Canvas.Refresh();
        PawnViewsChanged();
        RefreshMaps();
        RefreshToolInfo();
        SetStatus(status);
        FocusCanvas();
    }

    /// shown / hidden for players, one undo step
    private MenuItem VisibilityItem(bool dmOnly, Action<bool> set, string what) =>
        MenuAction(dmOnly ? "show to players" : "hide from players", () =>
        {
            set(!dmOnly);
            MenuCommit(dmOnly ? $"{what} shown to players" : $"{what} hidden from players");
        });

    // ---------- pawn ----------

    private List<Control> PawnMenu(Pawn pawn)
    {
        var cell = pawn.Cell;
        var t = ActiveLibrary.Find(pawn.TemplateId);
        string name = t is null ? "pawn" : PawnName(t);
        var items = new List<Control>
        {
            MenuAction("add status...", () => { ShowPawnInfo(pawn); ShowAddStatus(cell); }),
        };
        foreach (var e in pawn.Statuses)
        {
            var status = e.Name;
            items.Add(MenuAction($"remove {status}", () => RemovePawnStatus(cell, status)));
        }
        items.Add(new Separator());
        items.Add(MenuAction("open", () => ShowPawnInfo(pawn)));
        items.Add(MenuAction("change pawn...", () => ShowChangePawn(pawn)));
        items.Add(VisibilityItem(pawn.DmOnly, v => pawn.DmOnly = v, name));
        items.Add(new Separator());
        items.Add(MenuAction("sight from here", () => { SetTool(ToolMode.Sight); Canvas.ShowSight(cell); FocusCanvas(); }));
        items.Add(MenuAction("measure from here", () =>
        {
            SetTool(ToolMode.Measure);
            Canvas.MeasureFrom(new Point(cell.X + 0.5, cell.Y + 0.5));
            SetStatus("click where to measure to");
            FocusCanvas();
        }));
        items.Add(new Separator());
        items.Add(MenuAction("copy", () => { Canvas.CopyPawn(cell); FocusCanvas(); }));
        items.Add(MenuAction("delete", () =>
        {
            if (_doc.Pawns.TryGetValue(cell, out var p) && ReferenceEquals(p, pawn))
            {
                _doc.RemovePawn(cell);
                if (ToolPanel.ItemView) BackToToolView();
                MenuCommit($"{name} deleted");
            }
        }));
        return items;
    }
}
