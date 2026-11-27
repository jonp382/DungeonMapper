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
        if (Canvas.DoorAt(world) is { } door) return DoorMenu(door);
        // labels and images draw over objects, so they come first
        if (Canvas.LabelAt(cell) is { } label) return LabelMenu(label);
        if (_doc.ImageAt(cell) is { } image) return ImageMenu(image);
        if (_doc.ObjectAt(cell) is { } obj) return ObjectMenu(obj);
        if (_doc.Stairs.TryGetValue(cell, out var stair)) return StairMenu(cell, stair);
        if (Canvas.WallAt(world) is { } wall) return WallMenu(wall);
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

    /// a ticked item in a pick-one list (door state, type, stair direction)
    private static MenuItem MenuPick(string header, bool current, Action onClick) =>
        MenuAction((current ? "✓ " : "    ") + header, onClick);

    // ---------- door ----------

    private List<Control> DoorMenu(IDoorConfig door)
    {
        var spot = _doc.DoorSpotOf(door);
        var items = new List<Control>();
        foreach (var st in new[] { DoorState.Open, DoorState.Closed, DoorState.Locked })
        {
            var s = st;
            items.Add(MenuPick(s.ToString().ToLowerInvariant(), door.State == s, () =>
            {
                if (door.State == s) return;
                door.State = s;
                MenuCommit($"door {s.ToString().ToLowerInvariant()}");
            }));
        }
        var types = ActivePresets.DoorTypes.GroupBy(t => t.Name).Select(g => g.First()).ToList();
        items.Add(SubMenu("type", types.Select(t =>
        {
            var name = t.Name;
            return (Control)MenuPick(name, door.Type == name, () =>
            {
                if (door.Type == name) return;
                door.Type = name;
                MenuCommit($"door type {name}");
            });
        })));
        items.Add(new Separator());
        items.Add(VisibilityItem(door.DmOnly, v => door.DmOnly = v, "door"));
        items.Add(MenuAction("edit...", () => ShowDoorConfig(door)));
        items.Add(new Separator());
        items.Add(MenuAction("delete door (keep the wall)", () =>
        {
            if (!_doc.RemoveDoorConfig(door)) return;
            if (ToolPanel.ItemView) BackToToolView();
            MenuCommit("door deleted");
        }));
        if (spot is EdgeKey edge)
            items.Add(MenuAction("delete door and wall", () =>
            {
                _doc.SetWall(edge, false); // the door goes with its wall
                if (ToolPanel.ItemView) BackToToolView();
                MenuCommit("door and wall deleted");
            }));
        return items;
    }

    // ---------- stair ----------

    private List<Control> StairMenu(CellKey cell, Stair stair)
    {
        var group = _doc.ConnectedStairs(cell);
        var items = new List<Control>
        {
            SubMenu("direction", new[] { StairDirection.Up, StairDirection.Down, StairDirection.Left, StairDirection.Right }.Select(d =>
                (Control)MenuPick(d.ToString().ToLowerInvariant(), stair.Direction == d, () =>
                {
                    // like the stair's view: the whole connected group turns
                    if (group.All(s => s.Direction == d)) return;
                    foreach (var s in group) s.Direction = d;
                    MenuCommit($"stairs go {d.ToString().ToLowerInvariant()}");
                }))),
            MenuAction("edit...", () => ShowStairConfig(stair)),
            new Separator(),
            MenuAction(group.Count > 1 ? $"delete ({group.Count} connected stairs)" : "delete", () =>
            {
                int n = _doc.RemoveConnectedStairs(cell);
                if (n == 0) return;
                if (ToolPanel.ItemView) BackToToolView();
                MenuCommit(n == 1 ? "stair deleted" : $"{n} stairs deleted");
            }),
        };
        return items;
    }

    // ---------- wall ----------

    private List<Control> WallMenu(EdgeKey wall)
    {
        var types = ActivePresets.DoorTypes.GroupBy(t => t.Name).Select(g => g.First()).ToList();
        return new List<Control>
        {
            SubMenu("add door", types.Select(t =>
            {
                var name = t.Name;
                return (Control)MenuAction(name, () =>
                {
                    if (!_doc.TryPlaceDoor(wall, name, Canvas.DefaultDoorState, out _)) { SetStatus("can't place a door here", StatusKind.Error); return; }
                    MenuCommit($"{name} door added");
                });
            })),
            new Separator(),
            MenuAction("delete wall", () =>
            {
                _doc.SetWall(wall, false);
                MenuCommit("wall deleted");
            }),
        };
    }

    // ---------- label ----------

    private List<Control> LabelMenu(MapLabel label) => new()
    {
        MenuAction("edit text", () => BeginLabelEdit(label)),
        MenuAction("edit...", () => ShowLabelConfig(label)),
        MenuPick("players can edit", label.ClientEditable, () =>
        {
            label.ClientEditable = !label.ClientEditable;
            if (label.ClientEditable && label.Id.Length == 0) label.Id = MapLabel.NewId();
            MenuCommit(label.ClientEditable ? "players can edit the label" : "only the DM edits the label");
        }),
        VisibilityItem(label.DmOnly, v => label.DmOnly = v, "label"),
        new Separator(),
        MenuAction("copy", () => { Canvas.CopyItem(label, null, null); FocusCanvas(); }),
        MenuAction("delete", () =>
        {
            if (!_doc.Labels.Remove(label)) return;
            if (ToolPanel.ItemView) BackToToolView();
            MenuCommit("label deleted");
        }),
    };

    // ---------- object ----------

    private List<Control> ObjectMenu(MapObject obj)
    {
        var t = ActiveObjects.Find(obj.TypeId);
        return new()
        {
            MenuAction("edit...", () => ShowObjectInfo(obj)),
            MenuAction("change type...", () => { ShowObjectInfo(obj); ShowChangeObjectType(obj); }),
            MenuAction("rotate", () =>
            {
                (obj.W, obj.H) = (obj.H, obj.W);
                MenuCommit("object rotated");
                ObjectViewsChanged();
            }),
            MenuPick("use the type's look", obj.Look is null, () =>
            {
                obj.Look = obj.Look is null ? (t?.Look ?? new ObjectLook()).Clone() : null;
                MenuCommit(obj.Look is null ? "object uses its type's look" : "object has its own look");
                ObjectViewsChanged();
            }),
            VisibilityItem(obj.DmOnly, v => obj.DmOnly = v, "object"),
            new Separator(),
            MenuAction("copy", () => { Canvas.CopyItem(null, obj, null); FocusCanvas(); }),
            MenuAction("delete", () =>
            {
                if (!_doc.Objects.Remove(obj)) return;
                if (Canvas.SelectedObjectId == obj.Id) Canvas.SelectedObjectId = null;
                if (ToolPanel.ItemView) BackToToolView();
                MenuCommit("object deleted");
                ObjectViewsChanged();
            }),
        };
    }

    // ---------- image ----------

    private List<Control> ImageMenu(MapImage im)
    {
        void Changed(string status)
        {
            CommitImage(im);
            Canvas.Refresh();
            if (ToolPanel.ItemView && Canvas.SelectedImageId == im.Id) ShowImageConfig(im);
            SetStatus(status);
            FocusCanvas();
        }
        return new()
        {
            MenuAction("edit...", () => ShowImageConfig(im)),
            MenuAction("turn left", () => { im.Turns -= 1; (im.W, im.H) = (im.H, im.W); Changed("image turned left"); }),
            MenuAction("turn right", () => { im.Turns += 1; (im.W, im.H) = (im.H, im.W); Changed("image turned right"); }),
            SubMenu("fit", new[] { ImageFit.Fit, ImageFit.Fill, ImageFit.Stretch }.Select(f =>
                (Control)MenuPick(f.ToString().ToLowerInvariant(), im.Fit == f, () =>
                {
                    if (im.Fit == f) return;
                    im.Fit = f;
                    Changed($"image set to {f.ToString().ToLowerInvariant()}");
                }))),
            MenuAction("picture size", () =>
            {
                if (im.PixelW <= 0) { SetStatus("the picture's size is not known", StatusKind.Warning); return; }
                (im.W, im.H) = MapImage.NaturalCells(im.PixelW, im.PixelH, im.Turns);
                Changed($"back to the picture's size ({im.W * 5}x{im.H * 5} ft)");
            }, im.PixelW > 0),
            VisibilityItem(im.DmOnly, v => im.DmOnly = v, "image"),
            new Separator(),
            MenuAction("copy", () => { Canvas.CopyItem(null, null, im); FocusCanvas(); }),
            MenuAction("delete", () =>
            {
                if (!_doc.Images.Remove(im)) return;
                if (Canvas.SelectedImageId == im.Id) Canvas.SelectedImageId = null;
                if (ToolPanel.ItemView) BackToToolView();
                MenuCommit("image deleted");
                RefreshImagePanel();
            }),
        };
    }
}
