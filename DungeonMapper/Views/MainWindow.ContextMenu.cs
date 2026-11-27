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
        // the preview edits nothing: its menu only leaves it
        var items = Canvas.PlayerPreview
            ? new List<Control> { MenuAction("leave player view preview", () => SetPlayerPreview(false)) }
            : ContextItems(world, cell);
        if (items.Count == 0) return;
        Canvas.CancelMove();
        var menu = new MenuFlyout();
        foreach (var i in items) menu.Items.Add(i);
        _contextMenu = menu;
        menu.ShowAt(Canvas, true);
    }

    /// the menu for what is at the point: the top item's menu, with the
    /// items under it in a "below" submenu
    private List<Control> ContextItems(Point world, CellKey cell)
    {
        // inside the selected region: the region's menu
        if (!Joined && Canvas.SelectionRect is var (x0, y0, x1, y1) && cell.X >= x0 && cell.X <= x1 && cell.Y >= y0 && cell.Y <= y1)
            return RegionMenu(x0, y0, x1, y1);
        var stack = StackAt(world, cell);
        if (stack.Count == 0) return Joined ? PlayerMapMenu(cell) : MapMenu(cell);
        var items = stack[0].menu();
        if (stack.Count > 1)
        {
            items.Add(new Separator());
            items.Add(SubMenu("below", stack.Skip(1).Select(s => (Control)SubMenu(s.name, s.menu()))));
        }
        return items;
    }

    /// everything under the point that has a menu, top first. a joined
    /// player gets only what players may change
    private List<(string name, Func<List<Control>> menu)> StackAt(Point world, CellKey cell)
    {
        var list = new List<(string, Func<List<Control>>)>();
        var doc = Canvas.Document;
        if (Joined)
        {
            if (doc.Pawns.TryGetValue(cell, out var p) && PlayerPawns.Controlled(Canvas.Library.Find(p.TemplateId)))
                list.Add(("pawn", () => PlayerPawnMenu(p)));
            if (Canvas.LabelAt(cell) is { } l && PlayerLabels.Editable(l)) list.Add(("label", () => LabelMenu(l)));
            if (doc.ImageAt(cell) is { } i && PlayerImages.Editable(i)) list.Add(("image", () => ImageMenu(i)));
            return list;
        }
        if (_doc.Pawns.TryGetValue(cell, out var pawn)) list.Add(("pawn", () => PawnMenu(pawn)));
        if (Canvas.DoorAt(world) is { } door) list.Add(("door", () => DoorMenu(door)));
        // labels and images draw over objects
        if (Canvas.LabelAt(cell) is { } label) list.Add(("label", () => LabelMenu(label)));
        if (_doc.ImageAt(cell) is { } image) list.Add(("image", () => ImageMenu(image)));
        if (_doc.ObjectAt(cell) is { } obj) list.Add(("object", () => ObjectMenu(obj)));
        if (_doc.Stairs.TryGetValue(cell, out var stair)) list.Add(("stair", () => StairMenu(cell, stair)));
        if (Canvas.WallAt(world) is { } wall) list.Add(("wall", () => WallMenu(wall)));
        return list;
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

    private List<Control> LabelMenu(MapLabel label) => Joined ? new()
    {
        // a player: what the label's view lets them do, sent to the DM
        MenuAction("edit text", () => BeginLabelEdit(label)),
        MenuAction("edit...", () => ShowLabelConfig(label)),
        new Separator(),
        MenuAction("delete", () => { ShowLabelConfig(label); ToolPanel.DeleteAction?.Invoke(); }),
    } : new()
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
            // objects draw in list order: the last one is on top (user)
            MenuAction("bring to front", () =>
            {
                _doc.Objects.Remove(obj);
                _doc.Objects.Add(obj);
                MenuCommit("object brought to front");
            }, _doc.Objects.Count > 1 && _doc.Objects[^1] != obj),
            MenuAction("send to back", () =>
            {
                _doc.Objects.Remove(obj);
                _doc.Objects.Insert(0, obj);
                MenuCommit("object sent to back");
            }, _doc.Objects.Count > 1 && _doc.Objects[0] != obj),
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
        return new List<Control>
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
        }.Concat(Joined
            ? new Control[]
            {
                // a player: the image view's own delete (sent to the DM)
                new Separator(),
                MenuAction("delete", () => { ShowImageConfig(im); ToolPanel.DeleteAction?.Invoke(); }),
            }
            : new Control[]
            {
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
            }).ToList();
    }

    // ---------- selected region ----------

    private List<Control> RegionMenu(int x0, int y0, int x1, int y1)
    {
        var floor = _doc.FloorCells().Where(c => c.X >= x0 && c.X <= x1 && c.Y >= y0 && c.Y <= y1).ToList();
        bool pawns = _doc.Pawns.Keys.Any(c => c.X >= x0 && c.X <= x1 && c.Y >= y0 && c.Y <= y1);
        bool items = _doc.ItemsIn(x0, y0, x1, y1, RevealItems.All).Count > 0;
        return new()
        {
            MenuAction("copy", () => { Canvas.CopySelection(); Canvas.Refresh(); FocusCanvas(); }),
            MenuAction("cut", () => { Canvas.CutSelection(); Canvas.Refresh(); FocusCanvas(); }),
            MenuAction("delete...", ShowDeleteRegion),
            new Separator(),
            MenuAction("reveal floor to players", () =>
            {
                int n = _doc.SetRevealed(floor, true);
                MenuCommit(n == 0 ? "that floor was already revealed" : $"revealed {Plural(n, "cell")}");
            }, floor.Count > 0),
            MenuAction("hide floor from players", () =>
            {
                int n = _doc.SetRevealed(floor, false);
                MenuCommit(n == 0 ? "that floor was already hidden" : $"hid {Plural(n, "cell")}");
            }, floor.Count > 0),
            MenuAction("show items to players", () =>
            {
                int n = _doc.SetDmOnly(x0, y0, x1, y1, RevealItems.All, false);
                MenuCommit($"{Plural(n, "item")} shown to players");
            }, items),
            MenuAction("hide items from players", () =>
            {
                int n = _doc.SetDmOnly(x0, y0, x1, y1, RevealItems.All, true);
                MenuCommit($"{Plural(n, "item")} hidden from players");
            }, items),
            new Separator(),
            MenuAction("new formation from its pawns", () =>
            {
                SetTool(ToolMode.Pawn);
                FormationFromArea(null, x0, y0, x1, y1);
            }, pawns),
            MenuAction("clear selection", () => { Canvas.ClearSelection(); FocusCanvas(); }),
        };
    }

    // ---------- empty map ----------

    private List<Control> MapMenu(CellKey cell)
    {
        var last = ActiveLibrary.Recent.Select(id => ActiveLibrary.Find(id)).FirstOrDefault(t => t is not null);
        var formation = _lastFormationId is null ? null : ActiveLibrary.Formations.Find(f => f.Id == _lastFormationId);
        bool floor = _doc.IsFloorCell(cell);
        return new()
        {
            MenuAction("paste here", () => { Canvas.PasteAt(cell); Canvas.Refresh(); FocusCanvas(); }, Canvas.HasClipboard),
            MenuAction(last is null ? "place last pawn here" : $"place {PawnName(last)} here", () =>
            {
                if (last is not null) Canvas.PlacePawnAt(last, cell);
                FocusCanvas();
            }, last is not null),
            MenuAction(formation is null ? "deploy last formation here" : $"deploy {formation.Name} here", () =>
            {
                if (formation is null) return;
                DeployFormation(formation);
                Canvas.DeployFormationAt(cell);
                Canvas.Refresh();
            }, formation is not null),
            new Separator(),
            MenuAction("ping here", () => DoPingAt(cell)),
            MenuAction("sight from here", () => { SetTool(ToolMode.Sight); Canvas.ShowSight(cell); FocusCanvas(); }),
            new Separator(),
            MenuAction("reveal this room to players", () =>
            {
                int n = _doc.SetRevealed(_doc.RevealRoomCells(cell), true);
                MenuCommit(n == 0 ? "that room was already revealed" : $"revealed {Plural(n, "cell")}");
            }, floor),
            MenuAction("hide this room from players", () =>
            {
                int n = _doc.SetRevealed(_doc.RevealRoomCells(cell), false);
                MenuCommit(n == 0 ? "that room was already hidden" : $"hid {Plural(n, "cell")}");
            }, floor),
            new Separator(),
            MenuAction("zoom to fit", () => { Canvas.AutoFit(); FocusCanvas(); }),
            MenuAction(Canvas.PlayerPreview ? "leave player view preview" : "player view preview", () => SetPlayerPreview(!Canvas.PlayerPreview)),
        };
    }

    // ---------- a joined player ----------

    /// a Player pawn: statuses (sent to the DM), open it, measure from it
    private List<Control> PlayerPawnMenu(Pawn pawn)
    {
        var cell = pawn.Cell;
        var items = new List<Control> { MenuAction("add status...", () => { ShowPawnInfo(pawn); ShowAddStatus(cell); }) };
        foreach (var e in pawn.Statuses)
        {
            var status = e.Name;
            items.Add(MenuAction($"remove {status}", () => RemovePawnStatus(cell, status)));
        }
        items.Add(new Separator());
        items.Add(MenuAction("open", () => ShowPawnInfo(pawn)));
        items.Add(MenuAction("measure from here", () =>
        {
            SetTool(ToolMode.Measure);
            Canvas.MeasureFrom(new Point(cell.X + 0.5, cell.Y + 0.5));
            SetStatus("click where to measure to");
            FocusCanvas();
        }));
        return items;
    }

    private List<Control> PlayerMapMenu(CellKey cell) => new()
    {
        MenuAction("ping here", () => DoPingAt(cell)),
        MenuAction("zoom to fit", () => { Canvas.AutoFit(); FocusCanvas(); }),
    };
}
