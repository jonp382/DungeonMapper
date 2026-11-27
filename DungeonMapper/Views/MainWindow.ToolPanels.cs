using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using DungeonMapper.Controls;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// the side panel for each tool: title, hint, field label + controls, chips for
// small fixed choices, then an info box with counts on this map
public partial class MainWindow
{
    // the info box text of the current tool view, null when the tool has none
    private TextBlock? _toolInfo;

    private void PopulatePanel(ToolMode mode)
    {
        _toolInfo = null;
        _measureInfo = null;
        _playerPawnInfoCell = null;
        // the resize handles belong to an open object view
        if (Canvas.SelectedObjectId is not null) { Canvas.SelectedObjectId = null; Canvas.Refresh(); }
        // and the label resize handles to an open label view
        if (Canvas.SelectedLabel is not null) { Canvas.SelectedLabel = null; Canvas.Refresh(); }
        if (Canvas.SelectedImageId is not null) { Canvas.SelectedImageId = null; Canvas.Refresh(); }
        _imageViewId = null;
        switch (mode)
        {
            case ToolMode.Erase:
                ToolPanel.Clear("erase tool");
                ToolPanel.AddHint("choose what the eraser removes");
                BuildEraseFilters();
                ToolPanel.AddCheck("adjust walls", Canvas.EraseAdjustWalls, v => { Canvas.EraseAdjustWalls = v; FocusCanvas(); },
                    "re-seals rooms: the new outer edge gets walls after erasing");
                break;

            case ToolMode.Select:
                ToolPanel.Clear("select tool");
                ToolPanel.AddHint("click an element to configure it, drag a region to select. drag inside the selection to move it (r / shift+r turns it, esc cancels). drag a pawn, object, label or image to move it, shift+drag to start a selection on one. ctrl+c and ctrl+v copy and paste, the paste ghost follows the cursor. r, shift+r or shift+scroll rotates the clipboard.");
                ToolPanel.AddCheck("pawns only", Canvas.SelectPawnsOnly, v => { Canvas.SelectPawnsOnly = v; FocusCanvas(); },
                    "dragging the selection moves only its pawns");
                ToolPanel.AddCheck("show paste ghost", Canvas.GhostEnabled, v => { Canvas.GhostEnabled = v; Canvas.Refresh(); FocusCanvas(); });
                ToolPanel.AddButton("clear clipboard", () => { Canvas.ClearClipboard(); FocusCanvas(); });
                break;

            case ToolMode.Room:
                ToolPanel.Clear("room tool");
                ToolPanel.AddHint("drag a rectangle, its edges get walls. drawing over an existing room merges them.");
                ToolPanel.AddCheck("strict outer walls", Canvas.RoomStrict, v => { Canvas.RoomStrict = v; FocusCanvas(); });
                ToolPanel.AddCheck("merge adjacent walls", Canvas.RoomMergeNearby, v => { Canvas.RoomMergeNearby = v; FocusCanvas(); });
                _toolInfo = ToolPanel.AddInfo("");
                break;

            case ToolMode.Door:
                // placement defaults only, the type editor is the door presets modal.
                // placed doors take these (MapCanvas.DefaultDoorType/State, reset each start)
                ToolPanel.Clear("door tool");
                ToolPanel.AddHint("click a wall edge to place a door, click a door to configure it. the defaults apply to every door you place next.");
                var types = ActivePresets.DoorTypes.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
                var names = ActivePresets.DoorTypes.Select(t => t.Name).ToArray();
                ToolPanel.AddChips(names, Canvas.DefaultDoorType,
                    t => { Canvas.DefaultDoorType = t; RefreshToolInfo(); FocusCanvas(); },
                    "new doors: type",
                    n => types.TryGetValue(n, out var t)
                        ? new Ellipse { Width = 9, Height = 9, Fill = Rgb((t.R, t.G, t.B)) }
                        : null);
                ToolPanel.AddChips(new[] { "open", "closed", "locked" }, Canvas.DefaultDoorState,
                    s => { Canvas.DefaultDoorState = s; FocusCanvas(); },
                    "new doors: state");
                _toolInfo = ToolPanel.AddInfo("");
                ToolPanel.AddButton("close open doors...", () => { CloseModals(); ShowCloseDoors(Canvas.DefaultDoorType); });
                ToolPanel.AddButton("manage door presets", () => { CloseModals(); ShowDoorPresets(); });
                break;

            case ToolMode.Stair:
                ToolPanel.Clear("stair tool");
                ToolPanel.AddHint("click a cell to place a stair, drag for a region. click a stair to configure it.");
                _toolInfo = ToolPanel.AddInfo("");
                break;

            case ToolMode.Label when _client is not null:
                ToolPanel.Clear("label tool");
                ToolPanel.AddHint("click a cell to add a label, drag a range for a label that spans it. it goes on the DM's map for everyone. click a label players can edit to change or delete it; the others are the DM's.");
                break;

            case ToolMode.Label:
                ToolPanel.Clear("label tool");
                ToolPanel.AddHint("click a cell to place a label, drag a range for a label that spans it. click a label to edit it.");
                _toolInfo = ToolPanel.AddInfo("");
                break;

            case ToolMode.Pawn:
                BuildPawnPanel();
                break;

            case ToolMode.Object:
                BuildObjectPanel();
                break;

            case ToolMode.Image:
                BuildImagePanel();
                break;

            case ToolMode.Measure:
                BuildMeasurePanel();
                break;
            case ToolMode.Reveal:
                BuildRevealPanel();
                break;

            case ToolMode.Sight:
                BuildSightPanel();
                break;

            case ToolMode.Polygon:
                ToolPanel.Clear("polygon tool");
                ToolPanel.AddHint("click grid points to add vertices. click the first point again to complete the polygon, or press enter to auto-complete.");
                ToolPanel.AddCheck("strict outer walls", Canvas.PolygonStrict, v => { Canvas.PolygonStrict = v; FocusCanvas(); });
                ToolPanel.AddCheck("merge adjacent walls", Canvas.PolygonMergeNearby, v => { Canvas.PolygonMergeNearby = v; FocusCanvas(); });
                ToolPanel.AddButton("cancel current polygon", () => { Canvas.CancelPolygon(); Canvas.Refresh(); FocusCanvas(); });
                break;

            case ToolMode.Floor:
                ToolPanel.Clear("floor tool");
                ToolPanel.AddHint("manual: drag a rectangle. flood fill: click a cell to fill the room it is in, bounded by walls, doors and stairs.");
                ToolPanel.AddChips(new[] { "manual rectangle", "flood fill" },
                    Canvas.FloorFloodFill ? "flood fill" : "manual rectangle",
                    v => { Canvas.FloorFloodFill = v == "flood fill"; FocusCanvas(); },
                    "mode");
                _toolInfo = ToolPanel.AddInfo("");
                break;

            case ToolMode.Wall:
                ToolPanel.Clear("wall tool");
                ToolPanel.AddHint("press on your starting grid point and release on your end point to draw a line.");
                break;

            default:
                ToolPanel.Clear("tool");
                ToolPanel.AddHint("no options for this tool.");
                break;
        }
        RefreshToolInfo();
    }

    // the five erase filter toggles with "all" and "none" chips above them. a chip
    // is lit when every toggle matches it, neither is lit for a mix
    private void BuildEraseFilters()
    {
        var f = Canvas.EraseFilter;
        var chips = new WrapPanel();
        ToolPanel.AddButtonRaw(chips);
        var checks = new List<CheckBox>();

        void BuildChips()
        {
            bool all = f.Cells && f.Walls && f.Stairs && f.Labels && f.Pawns && f.Objects && f.Images;
            bool none = !(f.Cells || f.Walls || f.Stairs || f.Labels || f.Pawns || f.Objects || f.Images);
            chips.Children.Clear();
            chips.Children.Add(Chip("all", all, null, () => SetAll(true)));
            chips.Children.Add(Chip("none", none, null, () => SetAll(false)));
        }
        // flipping each toggle runs its own handler, which updates the filter
        void SetAll(bool on)
        {
            ToolPanel.ButtonHook?.Invoke();
            foreach (var cb in checks) cb.IsChecked = on;
            BuildChips();
            FocusCanvas();
        }
        void Add(string label, bool initial, System.Action<bool> set) =>
            checks.Add(ToolPanel.AddCheck(label, initial, v => { set(v); BuildChips(); FocusCanvas(); }));

        Add("cells and flooring", f.Cells, v => f.Cells = v);
        Add("walls and the doors on them", f.Walls, v => f.Walls = v);
        Add("stairs", f.Stairs, v => f.Stairs = v);
        Add("labels", f.Labels, v => f.Labels = v);
        Add("pawns", f.Pawns, v => f.Pawns = v);
        Add("objects (any part in the area)", f.Objects, v => f.Objects = v);
        Add("images (any part in the area)", f.Images, v => f.Images = v);
        BuildChips();
    }

    // updates the info box counts in place. does nothing when the side panel
    // shows something else (a door, stair or label config view).
    private void RefreshToolInfo()
    {
        RefreshCombatUi(); // the turn follows the map too (undo, switch, load)
        if (_toolInfo?.Parent is not Border box || !ToolPanel.Target.Children.Contains(box)) return;
        _toolInfo.Text = ToolInfoText(Canvas.Mode, _doc, Canvas.DefaultDoorType);
    }

    public static string ToolInfoText(ToolMode mode, MapDocument doc, string doorType) => mode switch
    {
        ToolMode.Door => DoorInfo(doc, doorType),
        ToolMode.Stair => $"{Plural(doc.Stairs.Count, "stair")} on this map",
        ToolMode.Label => $"{Plural(doc.Labels.Count, "label")} on this map",
        ToolMode.Image => $"{Plural(doc.Images.Count, "image")} on this map",
        ToolMode.Room or ToolMode.Floor => $"{Plural(doc.Cells.Count, "floor cell")} on this map",
        ToolMode.Reveal => RevealInfo(doc),
        _ => "",
    };

    private static string DoorInfo(MapDocument doc, string doorType)
    {
        var doors = doc.AllDoors().ToList();
        int ofType = doors.Count(d => d.Type == doorType);
        return $"{Plural(doors.Count, "door")} on this map\n{ofType} of them {doorType}";
    }
}
