using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Reactive;
using Avalonia.Styling;
using DungeonMapper.Controls;
using DungeonMapper.Models;

namespace DungeonMapper.Views;

public partial class MainWindow : Window
{
    private MapProject _project = new();
    private MapDocument _doc => _project.Active;
    // per-map undo/redo: one History per MapEntry, keyed by reference. The old
    // single shared History was re-Initialize'd on every map switch, wiping the
    // snapshots of the map you left (symptom: switching maps loses undo history).
    // keyed by entry, not index: RemoveMap shifts indices.
    private readonly Dictionary<MapEntry, History> _histories = new();
    private History HistoryFor(MapEntry e)
    {
        if (!_histories.TryGetValue(e, out var h))
        {
            h = new History();
            h.Initialize(e.Doc);
            _histories[e] = h;
        }
        return h;
    }
    private History ActiveHistory => HistoryFor(_project.Maps[_project.ActiveIndex]);
    private readonly AppSettings _settings = AppSettings.Load();
    // per-project pawn library: each project file carries its own library
    // (embedded "pawn_library" field, v3), keyed by project reference, like the
    // per-map histories. there is NO global library: a project's library starts
    // empty and its embedded templates are appended on load (fresh ids for
    // collisions), remapping the loaded pawns' ids. template edits persist with
    // the project file (Ctrl+S / autosave), not to any global file.
    private readonly Dictionary<MapProject, PawnLibrary> _libraries = new();
    private PawnLibrary ActiveLibrary => LibraryFor(_project);
    private PawnLibrary LibraryFor(MapProject p)
    {
        if (!_libraries.TryGetValue(p, out var lib))
        {
            lib = new PawnLibrary();
            _libraries[p] = lib;
        }
        return lib;
    }
    // settings dirty flag: fields mutate live, the json write flushes once at
    // modal close / tool switch / menu open / autosave tick (avoids hundreds of
    // writes during color-wheel drags)
    private bool _settingsDirty;
    // the file the current map was loaded from / saved to; Ctrl+S saves over it.
    // null for a brand-new map (picker default maps/, then set after first save).
    private string? _currentMapPath;
    private Avalonia.Threading.DispatcherTimer? _autosaveTimer;
    private DateTime _lastAutosave = DateTime.MinValue;
    // the side panel's Status dropdown, so it can be refreshed when statuses change
    private ComboBox? _statusCombo;
    private PawnTemplate? _statusComboTemplate;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, DisarmDeleteOnPress, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

        Canvas.Document = _doc;
        Canvas.Settings = _settings;
        Canvas.Library = ActiveLibrary;
        HistoryFor(_project.Maps[0]); // snapshot the starting map at startup

        // hotbar icons. tools without a png yet get a drawn placeholder.
        SetIcon(ToolSelect, PngIcon("DMSelect"));
        SetIcon(ToolMove, PngIcon("DMMove"));
        SetIcon(ToolRoom, PngIcon("DMRoom2"));
        SetIcon(ToolFloor, PngIcon("DMFloor"));
        SetIcon(ToolWall, PngIcon("DMWall"));
        SetIcon(ToolPolygon, PngIcon("DMPolygon"));
        SetIcon(ToolDoor, PngIcon("DMDoor"));
        SetIcon(ToolStair, PngIcon("DMStairs"));
        SetIcon(ToolLabel, PngIcon("DMLabel"));
        SetIcon(ToolPawn, PngIcon("DMPawn"));
        SetIcon(ToolErase, PngIcon("DMErase"));
        SetIcon(BtnManageMaps, PngIcon("DMMaps", 18));
        ToolTip.SetTip(BtnManageMaps, "Manage maps");
        ToolTip.SetTip(MapList, "Switch map");
        RefreshToolLabels();

        // map list (item 8)
        RefreshMapList();
        MapList.SelectionChanged += (_, _) =>
        {
            if (_suppressMapSelect) return;
            int idx = MapList.SelectedIndex;
            if (idx >= 0 && idx != _project.ActiveIndex) SwitchMap(idx);
        };
        BtnManageMaps.Click += (_, _) => ToggleMaps();
        MapsClose.Click += (_, _) => { MapsOverlay.IsVisible = false; FocusCanvas(); };
        MapsAdd.Click += (_, _) => { AddMap(); ShowMaps(); };
        MapsImport.Click += (_, _) => ImportMaps();

        // Tool buttons
        ToolSelect.Click += (_, _) => SetTool(ToolMode.Select);
        ToolMove.Click += (_, _) => SetTool(ToolMode.Move);
        ToolRoom.Click += (_, _) => SetTool(ToolMode.Room);
        ToolFloor.Click += (_, _) => SetTool(ToolMode.Floor);
        ToolWall.Click += (_, _) => SetTool(ToolMode.Wall);
        ToolDoor.Click += (_, _) => SetTool(ToolMode.Door);
        ToolStair.Click += (_, _) => SetTool(ToolMode.Stair);
        ToolLabel.Click += (_, _) => SetTool(ToolMode.Label);
        ToolErase.Click += (_, _) => SetTool(ToolMode.Erase);
        ToolPawn.Click += (_, _) => SetTool(ToolMode.Pawn);
        ToolPolygon.Click += (_, _) => SetTool(ToolMode.Polygon);


        // file and manage menus
        MenuSave.Icon = MenuIcon("DMSave");
        MenuLoad.Icon = MenuIcon("DMOpen");
        MenuSettings.Icon = MenuIcon("DMSettings");
        MenuDoorPresets.Icon = MenuIcon("DMDoor");
        MenuUndo.Icon = MenuIcon("DMUndo");
        MenuRedo.Icon = MenuIcon("DMRedo");
        MenuFit.Icon = MenuIcon("DMFit");
        MenuMaps.Icon = MenuIcon("DMMaps");
        MenuPawns.Icon = MenuIcon("DMPawn");
        MenuHotkeys.Icon = MenuIcon("DMHotkeys");
        MenuUndo.Click += (_, _) => DoUndo();
        MenuRedo.Click += (_, _) => DoRedo();
        MenuFit.Click += (_, _) => Canvas.AutoFit();
        MenuSave.Click += (_, _) => SaveCurrent();
        MenuSaveAs.Click += (_, _) => SaveMap();
        MenuLoad.Click += (_, _) => LoadMap();
        MenuImportMaps.Click += (_, _) => ImportMaps();
        MenuExportMap.Click += (_, _) => ExportMap(_project.Maps[_project.ActiveIndex]);
        MenuMaps.Click += (_, _) => { CloseModals(); ShowMaps(); };
        MenuSettings.Click += (_, _) => { CloseModals(); ShowSettings(); };
        MenuPawns.Click += (_, _) => { CloseModals(); ShowPawnManagement(); };
        MenuDoorPresets.Click += (_, _) => { CloseModals(); ShowDoorPresets(); };
        MenuHotkeys.Click += (_, _) => { CloseModals(); ShowHotkeys(); };
        HotkeysClose.Click += (_, _) => { FlushSettings(); HotkeysOverlay.IsVisible = false; };

        // settings + door presets modal popups
        SettingsClose.Click += (_, _) => { FlushSettings(); SettingsOverlay.IsVisible = false; };
        InitDoorPresets();

        // autosave: ticks every minute; writes to maps/autosaves when the
        // configured interval has elapsed (0 = off). Flushes pending settings.
        _autosaveTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autosaveTimer.Tick += (_, _) =>
        {
            FlushSettings();
            if (_settings.AutosaveMinutes <= 0) return;
            if ((DateTime.Now - _lastAutosave).TotalMinutes < _settings.AutosaveMinutes) return;
            _lastAutosave = DateTime.Now;
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "maps", "autosaves");
            System.IO.Directory.CreateDirectory(dir);
            var name = $"autosave_{_project.ActiveIndex}_{Stamp()}.json";
            MapSerializer.SaveProject(_project, System.IO.Path.Combine(dir, name), ActiveLibrary);
            StatusText.Text = $"autosaved {name}";
        };
        _autosaveTimer.Start();

        Canvas.ActionCompleted += () =>
        {
            ActiveHistory.Commit(_doc);
        };
        Canvas.DoorConfigRequested += d => ShowDoorConfig(d);
        Canvas.StairConfigRequested += s => ShowStairConfig(s);
        Canvas.LabelConfigRequested += l => ShowLabelConfig(l);
        Canvas.LabelEditRequested += l => BeginLabelEdit(l);
        Canvas.ViewChanged += PlaceLabelEditor;
        Canvas.PawnConfigRequested += p => ShowPawnInfo(p);
        Canvas.PawnPlaced += id =>
        {
            ActiveLibrary.NoteRecent(id);
            PawnViewsChanged();
            RefreshDoorPresets();
        };
        Canvas.TakeHeroFromOtherMaps = id =>
        {
            // each other map that loses the hero gets its own undo step
            Pawn? taken = null;
            foreach (var m in _project.Maps)
            {
                if (m.Doc == _doc) continue;
                var cells = m.Doc.Pawns.Where(kv => kv.Value.TemplateId == id).Select(kv => kv.Key).ToList();
                if (cells.Count == 0) continue;
                foreach (var c in cells) { taken ??= m.Doc.Pawns[c]; m.Doc.Pawns.Remove(c); }
                HistoryFor(m).Commit(m.Doc);
            }
            return taken;
        };
        Canvas.StatusMessage += msg =>
        {
            StatusText.Text = msg;
        };

        // a panel button click counts as a cancel of an armed move: the ghost is
        // disarmed, rotation resets, and hotkeys are restored. the hook fires
        // before the button's own action (see ToolPanel.AddButton).
        ToolPanel.ButtonHook = () => { Canvas.CancelMove(); Canvas.Refresh(); FocusCanvas(); };

        SetTool(ToolMode.Room);
        Canvas.Refresh();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // typing in a field (label text, rename, settings, dropdowns) must not
        // trigger tool hotkeys
        if (IsTyping()) { base.OnKeyDown(e); return; }

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.S: SaveCurrent(); e.Handled = true; return; // Ctrl+S: save over the current map
                case Key.Z: DoUndo(); e.Handled = true; return;
                case Key.Y: DoRedo(); e.Handled = true; return;
                case Key.C: Canvas.CopySelection(); Canvas.Refresh(); e.Handled = true; return;
                case Key.V: Canvas.PasteAtCursor(); Canvas.Refresh(); e.Handled = true; return;
                case Key.X: Canvas.CutSelection(); Canvas.Refresh(); e.Handled = true; return;
                case Key.F: FocusPawnSearch(); e.Handled = true; return;
            }
        }
        else if (e.Key == Key.Delete)
        {
            // hotkeys are disabled while a move is armed and not yet placed
            if (Canvas.HotkeysDisabled) { e.Handled = true; return; }
            // delete = erase the current selection with everything selected
            Canvas.DeleteSelection(); Canvas.Refresh(); e.Handled = true; return;
        }
        else if (e.Key == Key.Enter && Canvas.Mode == ToolMode.Polygon && !IsTyping())
        {
            // Enter closes the polygon by appending the first vertex
            Canvas.ClosePolygon(); Canvas.Refresh(); e.Handled = true; return;
        }
        else if (e.Key == Key.Escape)
        {
            if (DoorPresetsOverlay.IsVisible) { CloseDoorPresets(); e.Handled = true; return; }
            // Escape cancels an armed move (disarm ghost, reset rotation, restore
            // hotkeys) and discards polygon vertices.
            if (Canvas.MoveArmed) { Canvas.CancelMove(); Canvas.Refresh(); e.Handled = true; return; }
            if (Canvas.Mode == ToolMode.Polygon && !IsTyping())
            { Canvas.CancelPolygon(); Canvas.Refresh(); e.Handled = true; return; }
            if (Canvas.Mode == ToolMode.Wall && !IsTyping())
            { Canvas.CancelLine(); Canvas.Refresh(); e.Handled = true; return; }
        }
        else if (e.Key == Key.R && Canvas.CanRotate && !IsTyping())
        {
            // R rotates the armed snapshot clockwise, Shift+R counterclockwise.
            // Only fires when a snapshot is armed in Move/Select mode; otherwise
            // R falls through to the Room tool hotkey.
            Canvas.RotateRegion(e.KeyModifiers == KeyModifiers.Shift ? 1 : -1);
            Canvas.Refresh(); e.Handled = true; return;
        }
        else if (e.KeyModifiers == KeyModifiers.None)
        {
            // tool hotkeys from the configurable table (item 7). disabled while a
            // move is armed and not yet placed.
            if (!Canvas.HotkeysDisabled)
                foreach (var (modeName, keyName) in _settings.ToolHotkeys)
                {
                    if (Enum.TryParse<Key>(keyName, out var k) && k == e.Key)
                    {
                        if (Enum.TryParse<ToolMode>(modeName, out var mode))
                        {
                            SetTool(mode);
                            e.Handled = true;
                            return;
                        }
                    }
                }
        }
        base.OnKeyDown(e);
    }

    // True when keyboard focus sits in a text field - typing there must not
    // fire tool hotkeys. ComboBox/CheckBox focus is NOT guarded: clicking a
    // checkbox (e.g. paste ghost toggle) leaves focus on it, and Ctrl+V /
    // hotkeys must still work. Ctrl+ shortcuts are never blocked.
    private bool IsTyping()
    {
        var f = this.FocusManager.GetFocusedElement();
        return f is TextBox;
    }

    // Returns keyboard focus to the canvas after toolbar/panel interactions.
    // A ComboBox, ScrollBar, or CheckBox that keeps focus consumes Ctrl+V and
    // tool-letter keys (type-ahead), so hotkeys stop working until the canvas
    // is refocused. The canvas is Focusable, so this restores the hotkeys.
    private void FocusCanvas()
    {
        Canvas.Focus();
    }

    // "Tool (K)" tooltip from the configurable hotkey table
    private void LabelTool(Button b, string modeName)
    {
        string key = _settings.ToolHotkeys.TryGetValue(modeName, out var k) ? k : "";
        ToolTip.SetTip(b, string.IsNullOrEmpty(key) ? modeName : $"{modeName} ({key})");
    }

    // re-tooltip all tool buttons, also after a hotkey edit
    private void RefreshToolLabels()
    {
        LabelTool(ToolSelect, "Select");
        LabelTool(ToolMove, "Move");
        LabelTool(ToolRoom, "Room");
        LabelTool(ToolFloor, "Floor");
        LabelTool(ToolWall, "Wall");
        LabelTool(ToolDoor, "Door");
        LabelTool(ToolStair, "Stair");
        LabelTool(ToolLabel, "Label");
        LabelTool(ToolErase, "Erase");
        LabelTool(ToolPawn, "Pawn");
        LabelTool(ToolPolygon, "Polygon");
    }

    private Button ToolButton(ToolMode mode) => mode switch
    {
        ToolMode.Select => ToolSelect,
        ToolMode.Move => ToolMove,
        ToolMode.Room => ToolRoom,
        ToolMode.Floor => ToolFloor,
        ToolMode.Wall => ToolWall,
        ToolMode.Door => ToolDoor,
        ToolMode.Stair => ToolStair,
        ToolMode.Label => ToolLabel,
        ToolMode.Erase => ToolErase,
        ToolMode.Pawn => ToolPawn,
        _ => ToolPolygon,
    };

    // the asset pngs are 1080px, decode them small once and share them
    private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap> _iconCache = new();
    private static Avalonia.Media.Imaging.Bitmap IconBitmap(string name)
    {
        if (_iconCache.TryGetValue(name, out var bmp)) return bmp;
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://DungeonMapper/Assets/{name}.png"));
        bmp = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 64, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        _iconCache[name] = bmp;
        return bmp;
    }

    private static Control PngIcon(string name, double size = 22)
    {
        var img = new Image { Source = IconBitmap(name), Width = size, Height = size };
        RenderOptions.SetBitmapInterpolationMode(img, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        return img;
    }

    private static Control MenuIcon(string name) => PngIcon(name, 16);

    private static void SetIcon(Button b, Control icon) => b.Content = icon;

    // the popups share the space over the map, only one shows at a time
    private void CloseModals()
    {
        FlushSettings();
        ModalOverlay.IsVisible = false;
        _modalKind = null;
        SettingsOverlay.IsVisible = false;
        DoorPresetsOverlay.IsVisible = false;
        HotkeysOverlay.IsVisible = false;
        MapsOverlay.IsVisible = false;
        DisarmDelete();
    }

    private void SetTool(ToolMode mode)
    {
        FlushSettings(); // switching tools flushes pending settings changes
        // switching tools counts as a cancel of an armed move: the ghost is
        // disarmed, rotation resets, and hotkeys are restored.
        Canvas.CancelMove();
        Canvas.Mode = mode;
        foreach (var m in Enum.GetValues<ToolMode>())
            ToolButton(m).Classes.Set("active", m == mode);
        // re-selecting a tool resets the armed pawn template: the user expects
        // clicking the pawn tool again to clear a stale selection.
        Canvas.ArmedPawn = null;
        Canvas.CancelLine(); // a half-dragged line ghost must not survive a tool switch
        StatusText.Text = mode switch
        {
            ToolMode.Select => "select: click an element to configure, drag a region to select, Ctrl+C copy, Ctrl+V paste, shift+scroll / R / Shift+R rotates the clipboard",
            ToolMode.Move => "move: drag a region to select, then click the destination to move it. shift+scroll / R / Shift+R rotates the armed region, Escape cancels",
            ToolMode.Room => "room: click-drag a rectangle. edges get auto-walls",
            ToolMode.Floor => "floor: manual mode drags a rectangle; flood fill mode clicks a cell to fill the room it is in",
            ToolMode.Door => "door: click a wall edge to place, click a door to configure",
            ToolMode.Stair => "stair: click a cell to place, click a stair to configure, drag for a region",
            ToolMode.Label => "label: click a cell to place, drag a range for a label that spans it, click a label to edit",
            ToolMode.Erase => "erase: click-drag to remove cells, walls, doors, stairs, labels. delete key deletes the selection",
            ToolMode.Pawn => "pawn: click a cell to place, click a pawn to configure. one pawn per cell",
            ToolMode.Wall => "wall: click on your starting grid point and release on your end point to draw a line",
            _ => "",
        };
        PopulatePanel(mode);
        Canvas.Refresh();
        FocusCanvas(); // restore hotkey focus after the tool button click
    }

    // Builds the floating panel's options for the active tool.
    private void PopulatePanel(ToolMode mode)
    {
        switch (mode)
        {
            case ToolMode.Erase:
                ToolPanel.Clear("Erase options");
                ToolPanel.AddHint("choose what the eraser removes:");
                ToolPanel.AddCheck("cells / flooring", Canvas.EraseFilter.Cells, v => Canvas.EraseFilter.Cells = v);
                ToolPanel.AddCheck("walls (and doors on them)", Canvas.EraseFilter.Walls, v => Canvas.EraseFilter.Walls = v);
                ToolPanel.AddCheck("stairs", Canvas.EraseFilter.Stairs, v => Canvas.EraseFilter.Stairs = v);
                ToolPanel.AddCheck("labels", Canvas.EraseFilter.Labels, v => Canvas.EraseFilter.Labels = v);
                ToolPanel.AddCheck("pawns", Canvas.EraseFilter.Pawns, v => Canvas.EraseFilter.Pawns = v);
                ToolPanel.AddCheck("adjust walls (re-seal rooms: the new outer edge gets walls after erasing)", Canvas.EraseAdjustWalls, v => { Canvas.EraseAdjustWalls = v; FocusCanvas(); });
                break;

            case ToolMode.Select:
                ToolPanel.Clear("Select options");
                ToolPanel.AddHint("copy/paste the selection with Ctrl+C / Ctrl+V. the paste ghost follows the cursor.");
                break;

            case ToolMode.Move:
                ToolPanel.Clear("Move options");
                ToolPanel.AddHint("click + drag on empty space to select a region, then click the destination point. drag a pawn directly to move just that pawn.");
                ToolPanel.AddCheck("move pawns only", Canvas.MovePawnsOnly, v => { Canvas.MovePawnsOnly = v; FocusCanvas(); });
                ToolPanel.AddCheck("show destination ghost", Canvas.GhostEnabled, v => { Canvas.GhostEnabled = v; Canvas.Refresh(); FocusCanvas(); });
                break;

            case ToolMode.Room:
                ToolPanel.Clear("Room settings");
                ToolPanel.AddHint("drawing over an existing room merges them. use the options below for fine control.");
                ToolPanel.AddCheck("strict outer walls", Canvas.RoomStrict, v => { Canvas.RoomStrict = v; FocusCanvas(); });
                ToolPanel.AddCheck("merge adjacent walls", Canvas.RoomMergeNearby, v => { Canvas.RoomMergeNearby = v; FocusCanvas(); });
                break;

            case ToolMode.Door:
                // placement options only (type + state); the category editor lives
                // in the Manage Door Presets modal. Placed doors inherit these
                // defaults (MapCanvas reads Settings.DefaultDoorType/State).
                ToolPanel.Clear("Door settings");
                ToolPanel.AddHint("defaults apply to every door you place next.");
                var names = new List<string>();
                foreach (var t in _settings.DoorTypes) names.Add(t.Name);
                ToolPanel.AddChoice("default door type", names.ToArray(), _settings.DefaultDoorType, t =>
                {
                    _settings.DefaultDoorType = t;
                    MarkSetting();
                });
                ToolPanel.AddChoice("default door state", new[] { "open", "closed", "locked" },
                    _settings.DefaultDoorState, s =>
                    {
                        _settings.DefaultDoorState = s;
                        MarkSetting();
                    });
                ToolPanel.AddButton("manage door presets", () => { CloseModals(); ShowDoorPresets(); });
                break;

            case ToolMode.Pawn:
                BuildPawnPanel();
                break;

            case ToolMode.Polygon:
                ToolPanel.Clear("Polygon");
                ToolPanel.AddHint("click grid points to add vertices. click the first point again to complete the polgyon, or hit enter to auto-complete.");
                ToolPanel.AddCheck("strict outer walls", Canvas.PolygonStrict, v => { Canvas.PolygonStrict = v; FocusCanvas(); });
                ToolPanel.AddCheck("merge adjacent walls", Canvas.PolygonMergeNearby, v => { Canvas.PolygonMergeNearby = v; FocusCanvas(); });
                ToolPanel.AddButton("cancel current polygon", () => { Canvas.CancelPolygon(); Canvas.Refresh(); FocusCanvas(); });
                break;

            case ToolMode.Floor:
                ToolPanel.Clear("Floor");
                ToolPanel.AddHint("manual: drag a rectangle. flood fill: click a cell to fill the room it is in, bounded by walls, doors and stairs");
                ToolPanel.AddButton("manual rectangle", () => { Canvas.FloorFloodFill = false; FocusCanvas(); });
                ToolPanel.AddButton("flood fill", () => { Canvas.FloorFloodFill = true; FocusCanvas(); });
                break;

            case ToolMode.Wall:
                ToolPanel.Clear("Wall");
                ToolPanel.AddHint("click on your starting grid point and release on your end point to draw a line");
                break;

            default:
                ToolPanel.Clear("Tool options");
                ToolPanel.AddHint("no options for this tool.");
                break;
        }
    }

    // Rebuilds the map dropdown from the project, keeping the active map selected.
    // Deselects before mutating items so the selection model never enumerates a
    // stale index against a cleared list (avoids ArgumentOutOfRangeException), and
    // suppresses the SelectionChanged handler so a programmatic reselect does not
    // re-trigger SwitchMap.
    private bool _suppressMapSelect;
    private void RefreshMapList()
    {
        _suppressMapSelect = true;
        MapList.SelectedIndex = -1;
        MapList.Items.Clear();
        foreach (var m in _project.Maps) MapList.Items.Add(m.Name);
        MapList.SelectedIndex = _project.ActiveIndex;
        _suppressMapSelect = false;
    }

    // Switches the canvas to a map by index. History is per-map, so switching
    // preserves each map's undo/redo stack.
    private void SwitchMap(int index)
    {
        _project.SetActive(index);
        Canvas.Document = _doc;
        HistoryFor(_project.Maps[index]); // snapshot at switch time, not first commit
        // keep the armed selection + clipboard so cross-map move and cut/paste
        // work: select on map1, switch to map2, click the destination.
        Canvas.AutoFit();
        RefreshMapList();
        if (MapsOverlay.IsVisible) ShowMaps();
        StatusText.Text = $"switched to {_project.ActiveName}";
        Canvas.Refresh();
        PawnViewsChanged();
        RefreshDoorPresets();
        FocusCanvas(); // combo kept focus; restore it so hotkeys/paste work
    }

    private void AddMap()
    {
        string name = _project.UniqueName($"Map {_project.Maps.Count + 1}");
        _project.AddMap(name);
        Canvas.Document = _doc;
        HistoryFor(_project.Maps[^1]); // snapshot the new map at add time
        Canvas.ClearSelection();
        Canvas.ResetClipboard(); // new empty map: a cut from the old map must not paste/delete here
        Canvas.AutoFit();
        RefreshMapList();
        PawnViewsChanged();
        RefreshDoorPresets();
        StatusText.Text = $"added {name}";
        Canvas.Refresh();
        FocusCanvas();
    }

    // ---- maps panel: floating list of the campaign's maps ----


    // the map whose name is being edited inline
    private MapEntry? _renamingMap;

    // delete takes two clicks: x arms a red "delete?" button in its place. it
    // disarms after 10 seconds, on a click anywhere else, or when the panel
    // rebuilds or reopens. disarming flips visibility in place instead of
    // rebuilding, so the click that disarmed it still lands on its target.
    private MapEntry? _confirmDeleteMap;
    private Button? _confirmDeleteButton, _deleteArmButton;
    private Avalonia.Threading.DispatcherTimer? _confirmDeleteTimer;

    private void ArmDelete(MapEntry entry, Button arm, Button confirm)
    {
        DisarmDelete();
        _confirmDeleteMap = entry;
        _deleteArmButton = arm;
        _confirmDeleteButton = confirm;
        arm.IsVisible = false;
        confirm.IsVisible = true;
        if (_confirmDeleteTimer is null)
        {
            _confirmDeleteTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _confirmDeleteTimer.Tick += (_, _) => DisarmDelete();
        }
        _confirmDeleteTimer.Stop();
        _confirmDeleteTimer.Start();
    }

    private void DisarmDelete()
    {
        _confirmDeleteTimer?.Stop();
        if (_deleteArmButton is not null) _deleteArmButton.IsVisible = true;
        if (_confirmDeleteButton is not null) _confirmDeleteButton.IsVisible = false;
        _confirmDeleteMap = null;
        _deleteArmButton = _confirmDeleteButton = null;
    }

    // tunnels ahead of every control: a press anywhere but the armed "delete?"
    // button disarms it
    private void DisarmDeleteOnPress(object? sender, PointerPressedEventArgs e)
    {
        if (_confirmDeleteButton is null) return;
        for (var v = e.Source as Avalonia.Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
            if (ReferenceEquals(v, _confirmDeleteButton)) return;
        DisarmDelete();
    }

    private void ToggleMaps()
    {
        if (MapsOverlay.IsVisible) { MapsOverlay.IsVisible = false; FocusCanvas(); return; }
        CloseModals();
        ShowMaps();
    }

    // rebuilds the rows. each row: the name (click to switch, the active map is
    // highlighted), then rename, move up, move down, export and delete.
    private void ShowMaps()
    {
        MapsOverlay.IsVisible = true;
        DisarmDelete();
        MapsRows.Children.Clear();
        for (int i = 0; i < _project.Maps.Count; i++)
        {
            int idx = i;
            var entry = _project.Maps[i];
            bool active = idx == _project.ActiveIndex;
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"),
                ColumnSpacing = 2,
            };

            Control nameCell;
            if (ReferenceEquals(_renamingMap, entry))
            {
                var box = new TextBox { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center };
                bool done = false;
                void Finish(bool commit)
                {
                    if (done) return;
                    done = true;
                    _renamingMap = null;
                    var v = box.Text?.Trim();
                    if (commit && !string.IsNullOrEmpty(v) && v != entry.Name)
                    {
                        entry.Name = _project.UniqueName(v, idx);
                        StatusText.Text = $"renamed to {entry.Name}";
                        RefreshMapList();
                    }
                    ShowMaps();
                }
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter) { Finish(true); FocusCanvas(); e.Handled = true; }
                    else if (e.Key == Key.Escape) { Finish(false); FocusCanvas(); e.Handled = true; }
                };
                box.LostFocus += (_, _) => Finish(true);
                box.AttachedToVisualTree += (_, _) => { box.Focus(); box.SelectAll(); };
                nameCell = box;
            }
            else
            {
                var name = new Button
                {
                    Content = new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = active ? new SolidColorBrush(Color.Parse("#3a4a6a")) : Brushes.Transparent,
                    FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
                };
                ToolTip.SetTip(name, active ? "Current map" : "Switch to this map");
                name.Click += (_, _) => { if (idx != _project.ActiveIndex) SwitchMap(idx); };
                name.DoubleTapped += (_, _) => { _renamingMap = entry; ShowMaps(); };
                nameCell = name;
            }
            row.Children.Add(nameCell);

            Button Small(int col, string icon, string tip, Action click, bool enabled = true)
            {
                var b = new Button
                {
                    Content = PngIcon(icon, 14),
                    Width = 28, Height = 28, Padding = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    IsEnabled = enabled,
                };
                ToolTip.SetTip(b, tip);
                b.Click += (_, _) => click();
                Grid.SetColumn(b, col);
                row.Children.Add(b);
                return b;
            }

            Small(1, "DMRename", "Rename", () => { _renamingMap = entry; ShowMaps(); });
            Small(2, "DMUp", "Move up", () => { _project.MoveMap(idx, idx - 1); RefreshMapList(); ShowMaps(); }, idx > 0);
            Small(3, "DMDown", "Move down", () => { _project.MoveMap(idx, idx + 1); RefreshMapList(); ShowMaps(); }, idx < _project.Maps.Count - 1);
            Small(4, "DMExport", "Export this map to a file", () => ExportMap(entry));

            // second step: an explicit red button, so a stray click never deletes
            var confirm = new Button
            {
                Content = "delete?",
                Height = 28, Padding = new Thickness(6, 0),
                Background = new SolidColorBrush(Color.Parse("#8a2a2a")),
                Foreground = Brushes.White,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsVisible = false,
            };
            ToolTip.SetTip(confirm, "Click again to delete this map");
            confirm.Click += (_, _) => DeleteMap(entry);
            Grid.SetColumn(confirm, 5);
            row.Children.Add(confirm);
            Button? arm = null;
            arm = Small(5, "DMDelete", _project.Maps.Count > 1 ? "Delete" : "A campaign keeps at least one map",
                () => ArmDelete(entry, arm!, confirm), _project.Maps.Count > 1);

            MapsRows.Children.Add(row);
        }
    }

    private void DeleteMap(MapEntry entry)
    {
        int idx = _project.Maps.IndexOf(entry);
        _confirmDeleteMap = null;
        if (idx < 0 || _project.Maps.Count <= 1) { ShowMaps(); return; }
        bool wasActive = idx == _project.ActiveIndex;
        var keepActive = _project.Maps[_project.ActiveIndex];
        _project.RemoveMap(idx);
        if (!wasActive) _project.SetActive(_project.Maps.IndexOf(keepActive));
        _histories.Remove(entry);
        // the clipboard or selection may point into the deleted map
        Canvas.ClearSelection();
        Canvas.ResetClipboard();
        Canvas.Document = _doc;
        HistoryFor(_project.Maps[_project.ActiveIndex]);
        if (wasActive) Canvas.AutoFit();
        RefreshMapList();
        StatusText.Text = $"deleted {entry.Name}";
        Canvas.Refresh();
        ShowMaps();
    }

    private static string MapsDir()
    {
        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "maps");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    // writes one map (and the pawn templates it uses) to its own file
    private async void ExportMap(MapEntry entry)
    {
        var startFolder = await this.StorageProvider.TryGetFolderFromPathAsync(new Uri(MapsDir() + "/"));
        var safe = string.Concat(entry.Name.Split(System.IO.Path.GetInvalidFileNameChars()));
        var file = await this.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export map",
            SuggestedFileName = $"{(string.IsNullOrWhiteSpace(safe) ? "Map" : safe)}.json",
            DefaultExtension = "json",
            SuggestedStartLocation = startFolder,
            FileTypeChoices = new[] { new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } } },
        });
        if (file is null) return;
        try
        {
            var path = file.Path.LocalPath;
            MapSerializer.SaveMapFile(entry, path, ActiveLibrary);
            StatusText.Text = $"exported {entry.Name} to {path}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"export failed: {ex.Message}";
        }
    }

    // adds maps from map files or campaign files to the current campaign. a
    // campaign file brings all of its maps. pawn templates that look the same
    // as ones already in the library are reused instead of duplicated.
    private async void ImportMaps()
    {
        var startFolder = await this.StorageProvider.TryGetFolderFromPathAsync(new Uri(MapsDir() + "/"));
        var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import maps",
            AllowMultiple = true,
            SuggestedStartLocation = startFolder,
            FileTypeFilter = new[] { new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } } },
        });
        if (files.Count == 0) return;
        int added = 0, firstNew = _project.Maps.Count;
        var failed = new List<string>();
        foreach (var f in files)
        {
            var path = f.Path.LocalPath;
            try
            {
                var text = System.IO.File.ReadAllText(path);
                var imported = MapSerializer.ImportFromJson(text, System.IO.Path.GetFileNameWithoutExtension(path));
                var idMap = ActiveLibrary.MergeImport(MapSerializer.LibraryFromDtos(imported.Library));
                foreach (var m in imported.Maps)
                {
                    foreach (var p in m.Doc.Pawns.Values)
                        if (idMap.TryGetValue(p.TemplateId, out var nid)) p.TemplateId = nid;
                    m.Name = _project.UniqueName(m.Name);
                    _project.Maps.Add(m);
                    added++;
                }
            }
            catch (Exception ex)
            {
                failed.Add($"{System.IO.Path.GetFileName(path)} ({ex.Message})");
            }
        }
        if (added > 0)
        {
            Canvas.Library = ActiveLibrary;
            Canvas.InvalidateImageCache();
            SwitchMap(firstNew);
        }
        if (MapsOverlay.IsVisible || added > 0) ShowMaps();
        StatusText.Text = failed.Count == 0
            ? $"imported {added} map{(added == 1 ? "" : "s")}"
            : $"imported {added}, failed: {string.Join(", ", failed)}";
    }

    private void DoUndo()
    {
        var snap = ActiveHistory.Undo();
        if (snap is null) { StatusText.Text = "nothing to undo"; return; }
        _doc.ApplyFrom(snap);
        Canvas.Refresh();
        PawnViewsChanged();
        RefreshDoorPresets();
        StatusText.Text = "undo";
    }

    private void DoRedo()
    {
        var snap = ActiveHistory.Redo();
        if (snap is null) { StatusText.Text = "nothing to redo"; return; }
        _doc.ApplyFrom(snap);
        Canvas.Refresh();
        PawnViewsChanged();
        RefreshDoorPresets();
        StatusText.Text = "redo";
    }

    private async void SaveMap()
    {
        // brand-new map: default save location is the maps subfolder in the app's
        // base directory, name pre-filled Map_{datetime}.json, json typing enforced.
        var mapsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "maps");
        System.IO.Directory.CreateDirectory(mapsDir);
        var startFolder = await this.StorageProvider.TryGetFolderFromPathAsync(new Uri(mapsDir + "/"));
        var file = await this.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"Map_{Stamp()}.json",
            DefaultExtension = "json",
            SuggestedStartLocation = startFolder,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } },
            },
        });
        if (file is null) return;
        var path = file.Path.LocalPath;
        MapSerializer.SaveProject(_project, path, ActiveLibrary);
        _currentMapPath = path; // Ctrl+S saves over this file from now on
        StatusText.Text = $"saved {path}";
    }

    // Ctrl+S: save over the map the user loaded (the user-selected json, NOT the
    // backup). A brand-new map with no load/save history opens the picker once,
    // then Ctrl+S saves over that file.
    private void SaveCurrent()
    {
        if (_currentMapPath is null) { SaveMap(); return; }
        MapSerializer.SaveProject(_project, _currentMapPath, ActiveLibrary);
        StatusText.Text = $"saved {_currentMapPath}";
    }

    private async void LoadMap()
    {
        var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
        });
        if (files.Count == 0) return;
        try
        {
            var path = files[0].Path.LocalPath;
            // VERY FIRST step, before ANY parsing/rendering/modification: save a
            // backup copy in maps/backups, preserving the name + datetime stamp.
            string text = System.IO.File.ReadAllText(path);
            var backupDir = System.IO.Path.Combine(AppContext.BaseDirectory, "maps", "backups");
            System.IO.Directory.CreateDirectory(backupDir);
            var backupName = $"{System.IO.Path.GetFileNameWithoutExtension(path)}_{Stamp()}.json";
            System.IO.File.WriteAllText(System.IO.Path.Combine(backupDir, backupName), text);

            // a campaign opens as is. a single map file (or an old bare map)
            // opens as a new campaign holding just that map.
            var kind = MapSerializer.DetectKind(text);
            var imported = MapSerializer.ImportFromJson(text, System.IO.Path.GetFileNameWithoutExtension(path));
            var project = new MapProject();
            project.Maps.Clear();
            project.Maps.AddRange(imported.Maps);
            if (project.Maps.Count == 0) project.Maps.Add(new MapEntry());
            project.ActiveIndex = Math.Clamp(imported.Active, 0, project.Maps.Count - 1);
            _project = project;
            // the embedded library goes into the new campaign's (empty) library,
            // pawn references remapped through the id map. images travel as
            // base64 on the templates, so nothing is written to disk.
            var templates = MapSerializer.LibraryFromDtos(imported.Library);
            var idMap = ActiveLibrary.MergeAppend(templates);
            RemapPawns(_project, idMap);
            Canvas.Library = ActiveLibrary;
            Canvas.InvalidateImageCache();
            // Ctrl+S saves over the opened campaign. a map file is not a
            // campaign, so the first save asks where to put the new campaign.
            _currentMapPath = kind == SaveFileKind.Campaign ? path : null;
            Canvas.Document = _doc;
            _histories.Clear(); // fresh project = fresh entries
            HistoryFor(_project.Maps[_project.ActiveIndex]); // snapshot loaded state
            Canvas.ClearSelection();
            Canvas.ResetClipboard(); // loaded project replaces all maps; old cut sources are stale
            Canvas.AutoFit();
            RefreshMapList();
            if (MapsOverlay.IsVisible) ShowMaps();
            StatusText.Text = $"loaded {path} ({_project.Maps.Count} maps)";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"load failed: {ex.Message}";
        }
    }

    // settings dirty flag helpers: fields mutate live, json write flushes once
    private void MarkSetting() { _settingsDirty = true; }
    private void FlushSettings()
    {
        if (!_settingsDirty) return;
        _settingsDirty = false;
        _settings.Save();
    }

    // datetime stamp for autosave/backup names: valid on linux and windows
    // (no : / etc). yyyy-MM-dd_HH-mm-ss.
    private static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

    // Settings: modal popup centered over the map (like Pawn Management). All
    // fields apply live (canvas refresh) and mark the settings dirty; the json
    // write flushes on modal close, tool switch, other menu open, autosave tick.
    private void ShowSettings()
    {
        FlushSettings(); // opening any menu flushes pending changes
        ToolPanel.SetTarget(SettingsSections);
        SettingsSections.Children.Clear();

        // grid draw spacing (item 6): major line every N cells, minor at every
        // 5ft cell edge.
        ToolPanel.AddNumber("grid draw spacing (cells between major lines)", _settings.GridDrawSpacing, v =>
        {
            _settings.GridDrawSpacing = Math.Max(1, v);
            Canvas.Refresh();
            MarkSetting();
        });

        // gridline colors (item 3): major at spacing boundaries, minor at every
        // 5ft cell edge. At 1x spacing they coincide so only major is drawn.
        var gpresets = PresetColors();
        ToolPanel.AddColorField("major gridline color", _settings.MajorGridR, _settings.MajorGridG, _settings.MajorGridB, gpresets, c =>
        {
            _settings.MajorGridR = c.r; _settings.MajorGridG = c.g; _settings.MajorGridB = c.b;
            Canvas.Refresh();
            MarkSetting();
        });
        ToolPanel.AddColorField("minor gridline color", _settings.MinorGridR, _settings.MinorGridG, _settings.MinorGridB, gpresets, c =>
        {
            _settings.MinorGridR = c.r; _settings.MinorGridG = c.g; _settings.MinorGridB = c.b;
            Canvas.Refresh();
            MarkSetting();
        });
        // toggle minor gridline visibility, on the same row as the minor color,
        // with padding. Unchecked = minor lines not drawn at all.
        var minorPanel = ToolPanel.Target.Children[^1];
        var minorRow = (StackPanel)((StackPanel)minorPanel).Children[^1];
        var minorCheck = new CheckBox { Content = "show minor", IsChecked = _settings.ShowMinorGrid, Foreground = Brushes.White, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        minorCheck.IsCheckedChanged += (_, _) =>
        {
            _settings.ShowMinorGrid = minorCheck.IsChecked == true;
            Canvas.Refresh();
            MarkSetting();
        };
        minorRow.Children.Add(minorCheck);
        ToolPanel.AddColorField("grid dot color (empty space)", _settings.GridDotR, _settings.GridDotG, _settings.GridDotB, gpresets, c =>
        {
            _settings.GridDotR = c.r; _settings.GridDotG = c.g; _settings.GridDotB = c.b;
            Canvas.Refresh();
            MarkSetting();
        });

        // canvas element colors (item 5): background, cell fill, wall, stairs,
        // labels. The old static GridLine/FaintGridLine brushes were dead code
        // (grid uses the major/minor settings colors) and are removed.
        ToolPanel.AddColorField("background color", _settings.BackgroundR, _settings.BackgroundG, _settings.BackgroundB, gpresets, c =>
        {
            _settings.BackgroundR = c.r; _settings.BackgroundG = c.g; _settings.BackgroundB = c.b;
            Canvas.Refresh();
            MarkSetting();
        });
        ToolPanel.AddColorField("cell fill color", _settings.CellFillR, _settings.CellFillG, _settings.CellFillB, gpresets, c =>
        {
            _settings.CellFillR = c.r; _settings.CellFillG = c.g; _settings.CellFillB = c.b;
            Canvas.Refresh();
            MarkSetting();
        });
        ToolPanel.AddColorField("wall color", _settings.WallR, _settings.WallG, _settings.WallB, gpresets, c =>
        {
            _settings.WallR = c.r; _settings.WallG = c.g; _settings.WallB = c.b;
            Canvas.Refresh();
            MarkSetting();
        });
        ToolPanel.AddNumber($"wall thickness in pixels ({AppSettings.MinWallThickness} to {AppSettings.MaxWallThickness})", _settings.WallThickness, v =>
        {
            _settings.WallThickness = Math.Clamp(v, AppSettings.MinWallThickness, AppSettings.MaxWallThickness);
            Canvas.Refresh();
            MarkSetting();
        });
        ToolPanel.AddColorField("stair color", _settings.StairColorR, _settings.StairColorG, _settings.StairColorB, gpresets, c =>
        {
            _settings.StairColorR = c.r; _settings.StairColorG = c.g; _settings.StairColorB = c.b;
            Canvas.Refresh();
            MarkSetting();
        });
        ToolPanel.AddColorField("label color", _settings.LabelR, _settings.LabelG, _settings.LabelB, gpresets, c =>
        {
            _settings.LabelR = c.r; _settings.LabelG = c.g; _settings.LabelB = c.b;
            Canvas.Refresh();
            MarkSetting();
        });

        // default label text (item 1)
        ToolPanel.AddText("default label text", _settings.DefaultLabelText, t =>
        {
            _settings.DefaultLabelText = t;
            MarkSetting();
        });

        // autosave interval (minutes, 0 = off)
        ToolPanel.AddNumber("autosave every N minutes (0 = off)", _settings.AutosaveMinutes, v =>
        {
            _settings.AutosaveMinutes = Math.Max(0, v);
            MarkSetting();
        });

        SettingsOverlay.IsVisible = true;
        ToolPanel.SetTarget(null);
    }

    // hotkeys: editable single letter per tool, then the fixed shortcuts.
    // edits apply live to the tooltips and flush like settings.
    private void ShowHotkeys()
    {
        FlushSettings();
        ToolPanel.SetTarget(HotkeysSections);
        HotkeysSections.Children.Clear();

        // grid of small single-letter boxes with a label above each. reset
        // restores the hardcoded defaults.
        ToolPanel.AddHint("tool hotkeys (single letter):");
        var hotkeyGrid = new WrapPanel { MaxWidth = 420 };
        foreach (var modeName in _settings.ToolHotkeys.Keys)
        {
            var captured = modeName;
            var cell = new StackPanel { Spacing = 1, Margin = new Thickness(2) };
            cell.Children.Add(new TextBlock { Text = captured, Foreground = Brushes.White, FontSize = 11 });
            var tb = new TextBox { Text = _settings.ToolHotkeys[captured], Width = 30, HorizontalAlignment = HorizontalAlignment.Left };
            tb.TextChanged += (_, _) =>
            {
                var k = tb.Text.Trim().ToUpperInvariant();
                if (k.Length == 1)
                {
                    _settings.ToolHotkeys[captured] = k;
                    MarkSetting();
                    RefreshToolLabels();
                }
            };
            cell.Children.Add(tb);
            hotkeyGrid.Children.Add(cell);
        }
        ToolPanel.Target.Children.Add(hotkeyGrid);
        ToolPanel.AddButton("reset hotkeys to default", () =>
        {
            _settings.ToolHotkeys = AppSettings.Default().ToolHotkeys;
            MarkSetting();
            FlushSettings();
            ShowHotkeys(); // rebuild the grid with the restored defaults
            RefreshToolLabels();
        });


        // fixed shortcuts, listed for reference only
        ToolPanel.AddHint("fixed shortcuts:");
        var fixedGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(2, 0, 0, 0) };
        (string keys, string what)[] fixedKeys =
        {
            ("Ctrl+S", "save"),
            ("Ctrl+Z / Ctrl+Y", "undo / redo"),
            ("Ctrl+C / X / V", "copy / cut / paste at cursor"),
            ("Delete", "erase the selection"),
            ("R / Shift+R", "rotate a picked up selection"),
            ("Enter", "close the polygon"),
            ("Esc", "cancel a move, polygon or wall line"),
            ("Right drag", "pan"),
            ("Wheel", "zoom"),
        };
        for (int i = 0; i < fixedKeys.Length; i++)
        {
            fixedGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var k = new TextBlock { Text = fixedKeys[i].keys, Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(0, 2, 16, 2) };
            var w = new TextBlock { Text = fixedKeys[i].what, Foreground = new SolidColorBrush(Color.FromRgb(0xaa, 0xaa, 0xb4)), FontSize = 12, Margin = new Thickness(0, 2) };
            Grid.SetRow(k, i); Grid.SetRow(w, i); Grid.SetColumn(w, 1);
            fixedGrid.Children.Add(k); fixedGrid.Children.Add(w);
        }
        ToolPanel.Target.Children.Add(fixedGrid);

        HotkeysOverlay.IsVisible = true;
        ToolPanel.SetTarget(null);
    }

    private void ShowDoorConfig(IDoorConfig door)
    {
        ToolPanel.Clear("Door");
        var names = new List<string>();
        foreach (var t in _settings.DoorTypes) names.Add(t.Name);
        ToolPanel.AddChoice("Type", names.ToArray(), door.Type, t =>
        {
            door.Type = t;
            ActiveHistory.Commit(_doc);
            Canvas.Refresh();
        });
        ToolPanel.AddChoice("State", new[] { "open", "closed", "locked" },
            door.State.ToString().ToLowerInvariant(), s =>
            {
                door.State = Enum.Parse<DoorState>(s, ignoreCase: true);
                ActiveHistory.Commit(_doc);
                Canvas.Refresh();
            });
    }

    private void ShowStairConfig(Stair stair)
    {
        // find the cell this stair lives on so we can update its connected group
        var cell = default(CellKey);
        foreach (var kv in _doc.Stairs)
            if (ReferenceEquals(kv.Value, stair)) { cell = kv.Key; break; }

        ToolPanel.Clear("Stair");
        ToolPanel.AddHint("changing direction updates all connected stairs.");
        // direction dropdown (user request: axis-aligned up/down/left/right,
        // drag sets the default, the dropdown overrides). Selecting a direction
        // SETS every connected stair to it, so a group that drifted out of sync
        // re-syncs on one choice.
        ToolPanel.AddChoice("Direction", new[] { "Up", "Down", "Left", "Right" },
            stair.Direction.ToString(), v =>
            {
                var target = (StairDirection)Enum.Parse<StairDirection>(v);
                foreach (var s2 in _doc.ConnectedStairs(cell))
                    s2.Direction = target;
                ActiveHistory.Commit(_doc);
                Canvas.Refresh();
            });
    }

    // ---- in place label editing: a text box laid over the label on the map,
    // like editing a cell in a spreadsheet. enter keeps the text, shift+enter
    // or alt+enter starts a new line, escape puts the old text back, and
    // clicking away keeps it.

    private TextBox? _labelEditor;
    private MapLabel? _editingLabel;
    private string _editOriginal = "";

    private TextBox LabelEditor()
    {
        if (_labelEditor is not null) return _labelEditor;
        var tb = new TextBox
        {
            IsVisible = false,
            AcceptsReturn = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(2, 0),
            MinHeight = 0,
            Background = new SolidColorBrush(Color.FromArgb(200, 20, 24, 32)),
            BorderBrush = new SolidColorBrush(Color.Parse("#5b8def")),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(0),
        };
        // the fluent theme repaints the box when it is focused or hovered,
        // keep it looking like the label instead
        foreach (var state in new[] { ":focus", ":pointerover" })
        {
            var st = new Avalonia.Styling.Style(x => x.OfType<TextBox>().Class(state)
                .Template().OfType<Border>().Name("PART_BorderElement"));
            st.Setters.Add(new Avalonia.Styling.Setter(Border.BackgroundProperty, tb.Background));
            st.Setters.Add(new Avalonia.Styling.Setter(Border.BorderBrushProperty, tb.BorderBrush));
            st.Setters.Add(new Avalonia.Styling.Setter(Border.BorderThicknessProperty, tb.BorderThickness));
            tb.Styles.Add(st);
        }
        tb.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { EndLabelEdit(false); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if ((e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Alt)) == 0) { EndLabelEdit(true); return; }
                var t = tb.Text ?? "";
                int a = Math.Min(tb.SelectionStart, tb.SelectionEnd), b = Math.Max(tb.SelectionStart, tb.SelectionEnd);
                a = Math.Clamp(a, 0, t.Length); b = Math.Clamp(b, a, t.Length);
                tb.Text = t[..a] + "\n" + t[b..];
                tb.SelectionStart = tb.SelectionEnd = tb.CaretIndex = a + 1;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        tb.TextChanged += (_, _) =>
        {
            if (_editingLabel is null) return;
            _editingLabel.Text = (tb.Text ?? "").Replace("\r\n", "\n");
            // a range label's default size follows its text
            PlaceLabelEditor();
        };
        tb.LostFocus += (_, _) => EndLabelEdit(true);
        // sits just above the map, under the tool panel and the popups
        var host = (Panel)Canvas.Parent!;
        host.Children.Insert(host.Children.IndexOf(Canvas) + 1, tb);
        return _labelEditor = tb;
    }

    private void BeginLabelEdit(MapLabel label)
    {
        if (ReferenceEquals(_editingLabel, label)) return;
        EndLabelEdit(true);
        var tb = LabelEditor();
        _editOriginal = label.Text;
        tb.Text = label.Text;
        _editingLabel = label;
        Canvas.EditingLabel = label;
        tb.Foreground = Canvas.LabelTextBrush(label);
        tb.TextAlignment = label.Align switch
        {
            LabelAlign.Left => TextAlignment.Left,
            LabelAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Center,
        };
        PlaceLabelEditor();
        tb.IsVisible = true;
        tb.Focus();
        tb.SelectAll();
        Canvas.Refresh();
    }

    // keeps the box over the label at its drawn size as the map pans or zooms
    private void PlaceLabelEditor()
    {
        if (_editingLabel is null || _labelEditor is null) return;
        var r = Canvas.LabelScreenRect(_editingLabel);
        _labelEditor.Margin = new Thickness(r.X, r.Y, 0, 0);
        _labelEditor.MinWidth = r.Width;
        _labelEditor.MinHeight = r.Height;
        _labelEditor.FontSize = Canvas.LabelRenderSize(_editingLabel);
    }

    private void EndLabelEdit(bool keep)
    {
        if (_editingLabel is null || _labelEditor is null) return;
        var label = _editingLabel;
        _editingLabel = null;
        Canvas.EditingLabel = null;
        _labelEditor.IsVisible = false;
        if (!keep) label.Text = _editOriginal;
        else if (label.Text != _editOriginal) ActiveHistory.Commit(_doc);
        Canvas.Refresh();
        ShowLabelConfig(label);
        FocusCanvas();
    }

    private void ShowLabelConfig(MapLabel label)
    {
        ToolPanel.Clear(label.IsRange ? $"Label ({label.W}x{label.H})" : "Label");
        // the size box shows the size the label draws at. while the label has
        // no size of its own that is its default, which for a range depends on
        // the text, so the box follows the text until a number is typed in.
        TextBox? sizeBox = null;
        bool settingSize = false;
        void ShowSize()
        {
            if (sizeBox is null) return;
            settingSize = true;
            sizeBox.Text = ((int)Math.Round(MapCanvas.LabelSize(label))).ToString();
            settingSize = false;
        }
        ToolPanel.AddText("Text (double click the label to edit it on the map)", label.Text, t =>
        {
            label.Text = t.Replace("\r\n", "\n");
            if (label.FontSize == 0) ShowSize();
            ActiveHistory.Commit(_doc);
            Canvas.Refresh();
        }, multiline: true);
        // font size at 100% zoom, it scales with the map. color uses the
        // standard color picker, -1 = inherit the settings label color.
        sizeBox = ToolPanel.AddNumber("Font size", (int)Math.Round(MapCanvas.LabelSize(label)), v =>
        {
            if (settingSize) return;
            label.FontSize = Math.Max(0, v);
            ActiveHistory.Commit(_doc);
            Canvas.Refresh();
        });
        ToolPanel.AddChoice("Alignment", new[] { "Left", "Center", "Right" }, label.Align.ToString(), v =>
        {
            if (!Enum.TryParse<LabelAlign>(v, out var a) || a == label.Align) return;
            label.Align = a;
            ActiveHistory.Commit(_doc);
            Canvas.Refresh();
        });
        ToolPanel.AddColorField("Color (inherit = settings label color)",
            label.R, label.G, label.B, PresetColors(), c =>
            {
                label.R = c.r; label.G = c.g; label.B = c.b;
                ActiveHistory.Commit(_doc);
                Canvas.Refresh();
            });
        ToolPanel.AddButton("reset to defaults", () =>
        {
            label.FontSize = 0; label.R = -1; label.G = -1; label.B = -1;
            label.Align = LabelAlign.Center;
            ActiveHistory.Commit(_doc);
            Canvas.Refresh();
            // rebuild so every field shows the defaults
            ShowLabelConfig(label);
        });
    }

    // Parses "#RRGGBB" into RGB components.
    private static bool TryHex(string hex, out (int r, int g, int b) c)
    {
        c = default;
        if (hex.Length == 7 && hex[0] == '#')
        {
            c = (Convert.ToInt32(hex.Substring(1, 2), 16),
                 Convert.ToInt32(hex.Substring(3, 2), 16),
                 Convert.ToInt32(hex.Substring(5, 2), 16));
            return true;
        }
        return false;
    }

    // Placed-pawn click: READ-ONLY info view (name, affiliation, instance count,
    // status) + Delete + Change Pawn. Editing the template is done in the
    // management menu, not here (the select tool must not modify the pawn).
    private void ShowPawnInfo(Pawn pawn)
    {
        var t = ActiveLibrary.Find(pawn.TemplateId);
        ToolPanel.Clear("Pawn");
        int count = 0;
        foreach (var p in _doc.Pawns.Values)
            if (p.TemplateId == pawn.TemplateId) count++;

        if (t is null)
        {
            ToolPanel.AddHint("broken reference - this pawn's template is missing from the library (shown as X).");
            ToolPanel.AddButton("change pawn", () => ShowChangePawn(pawn));
            ToolPanel.AddButton("delete pawn", () =>
            {
                _doc.RemovePawn(pawn.Cell);
                ActiveHistory.Commit(_doc);
                Canvas.Refresh();
                StatusText.Text = "pawn deleted";
            });
            return;
        }

        ToolPanel.AddHint($"name: {t.Name}");
        ToolPanel.AddHint($"affiliation: {PawnTemplate.NormalizeAffiliation(t.Affiliation)}");
        ToolPanel.AddHint($"instances on map: {count}");
        // per-instance status: the placed pawn overrides its template's status.
        // "inherit" clears the override; picking a status sets it. Undoable.
        var statusNames = new List<string> { "inherit" };
        foreach (var st in _settings.PawnStatuses) statusNames.Add(st.Name);
        ToolPanel.AddChoice("status", statusNames.ToArray(),
            pawn.Status ?? "inherit", s =>
            {
                pawn.Status = s == "inherit" ? null : s;
                ActiveHistory.Commit(_doc);
                Canvas.Refresh();
            });
        ToolPanel.AddButton("change pawn", () => ShowChangePawn(pawn));
        ToolPanel.AddButton("delete pawn", () =>
        {
            _doc.RemovePawn(pawn.Cell);
            ActiveHistory.Commit(_doc);
            Canvas.Refresh();
            StatusText.Text = "pawn deleted";
        });
    }

    // Change Pawn: replace this placed pawn's template reference with a different
    // library template. The placed pawn keeps its cell; only the reference changes.
    private void ShowChangePawn(Pawn pawn)
    {
        ToolPanel.Clear("Change pawn");
        ToolPanel.AddHint("pick a template to replace this pawn with:");
        foreach (var t in ActiveLibrary.Pawns)
        {
            var captured = t;
            var label = string.IsNullOrWhiteSpace(captured.Name) ? "pawn" : captured.Name;
            ToolPanel.AddButton(label, () =>
            {
                pawn.TemplateId = captured.Id;
                ActiveHistory.Commit(_doc);
                Canvas.Refresh();
                StatusText.Text = $"pawn changed to {captured.Name}";
                ShowPawnInfo(pawn);
            });
        }
        ToolPanel.AddButton("back", () => ShowPawnInfo(pawn));
    }


    // Delete a template, warning if live pawns reference it. Counting live pawns
    // from the canvas document; a confirmation flyout requires an explicit "delete".
    private void DeleteTemplateWithWarning(PawnTemplate t, Control anchor)
    {
        int live = 0;
        foreach (var p in Canvas.Document.Pawns.Values)
            if (p.TemplateId == t.Id) live++;

        void doDelete()
        {
            ActiveLibrary.Remove(t.Id);
            if (Canvas.ArmedPawn == t) Canvas.ArmedPawn = null;
            if (_manageEditId == t.Id) _manageEditId = null;
            PawnsChanged();
            RefreshManageModal();
            // a side panel still showing the deleted pawn goes back to the tool panel
            if (Canvas.Mode == ToolMode.Pawn) BuildPawnPanel(); else PopulatePanel(Canvas.Mode);
        }

        if (live == 0)
        {
            doDelete();
            return;
        }

        var popup = new StackPanel { Spacing = 6 };
        popup.Children.Add(new TextBlock
        {
            Text = $"deleting removes {live} live pawn(s) on the map. they will render as X (broken reference) and can be replaced with Change Pawn. delete?",
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 240,
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var flyout = new Flyout { Content = popup };
        var yes = new Button { Content = "delete" };
        var no = new Button { Content = "cancel" };
        yes.Click += (_, _) => { flyout.Hide(); doDelete(); };
        no.Click += (_, _) => flyout.Hide();
        row.Children.Add(yes);
        row.Children.Add(no);
        popup.Children.Add(row);
        flyout.ShowAt(anchor);
    }

    // Delete an affiliation. Every template in that affiliation is reassigned to
    // "Neutral". Player/Ally/Enemy/Neutral are hard defaults and
    // undeletable. Confirmation flyout counts affected templates.
    private void DeleteAffiliationWithWarning(AffiliationColor ac, Control anchor)
    {
        if (ac.Name == "Player" || ac.Name == "Ally" || ac.Name == "Enemy" || ac.Name == "Neutral") return;

        int affected = 0;
        foreach (var t in ActiveLibrary.Pawns)
            if (PawnTemplate.NormalizeAffiliation(t.Affiliation) == ac.Name) affected++;

        void doDelete()
        {
            foreach (var t in ActiveLibrary.Pawns)
                if (PawnTemplate.NormalizeAffiliation(t.Affiliation) == ac.Name)
                {
                    t.Affiliation = "Neutral";
                    if (!t.ColorOverride) t.ApplyAffiliationDefaults();
                }
            _settings.AffiliationColors.Remove(ac);
            _settings.Save();
            PawnsChanged();
            ShowPawnManagement();
        }

        if (affected == 0) { doDelete(); return; }

        var popup = new StackPanel { Spacing = 6 };
        popup.Children.Add(new TextBlock
        {
            Text = $"deleting {ac.Name} reassigns {affected} template(s) to Neutral. delete?",
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 240,
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var flyout = new Flyout { Content = popup };
        var yes = new Button { Content = "delete" };
        var no = new Button { Content = "cancel" };
        yes.Click += (_, _) => { flyout.Hide(); doDelete(); };
        no.Click += (_, _) => flyout.Hide();
        row.Children.Add(yes);
        row.Children.Add(no);
        popup.Children.Add(row);
        flyout.ShowAt(anchor);
    }

    // Delete a status style. Templates reference statuses by name (t.Status).
    // Deleting a style resets every template that uses it to "none". "none" itself
    // is the hard default and un-deletable. Confirmation flyout counts affected
    // templates.
    private void DeleteStatusWithWarning(PawnStatus st, Control anchor)
    {
        if (st.Name == "none") return; // hard default, un-deletable

        int affected = 0;
        foreach (var t in ActiveLibrary.Pawns)
            if (t.Status == st.Name) affected++;

        void doDelete()
        {
            foreach (var t in ActiveLibrary.Pawns)
                if (t.Status == st.Name) t.Status = "none";
            _settings.PawnStatuses.Remove(st);
            _settings.Save();
            PawnsChanged();
            ShowPawnManagement();
            RefreshStatusCombo();
        }

        if (affected == 0)
        {
            doDelete();
            return;
        }

        var popup = new StackPanel { Spacing = 6 };
        popup.Children.Add(new TextBlock
        {
            Text = $"deleting resets {affected} pawn template(s) that use this style to \"none\". delete?",
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 240,
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var flyout = new Flyout { Content = popup };
        var yes = new Button { Content = "delete" };
        var no = new Button { Content = "cancel" };
        yes.Click += (_, _) => { flyout.Hide(); doDelete(); };
        no.Click += (_, _) => flyout.Hide();
        row.Children.Add(yes);
        row.Children.Add(no);
        popup.Children.Add(row);
        flyout.ShowAt(anchor);
    }

    // The 10 preset colors for the color picker popup (MS Paint palette style):
    // red, orange, yellow, green, blue, purple, white, black, gray, brown.
    private static List<(int r, int g, int b)> PresetColors() => new()
    {
        (255, 0, 0), (255, 128, 0), (255, 255, 0), (0, 128, 0), (0, 0, 255),
        (128, 0, 128), (255, 255, 255), (0, 0, 0), (128, 128, 128), (128, 64, 0),
    };

    // re-points every pawn in the project through the id remap from a library merge.
    private void RemapPawns(MapProject project, Dictionary<string, string> map)
    {
        foreach (var m in project.Maps)
            foreach (var p in m.Doc.Pawns.Values)
                if (map.TryGetValue(p.TemplateId, out var nid)) p.TemplateId = nid;
    }


    // Rebuild the side panel's Status dropdown options from the current status list
    // and re-select the template's current status. Called whenever statuses change
    // (add, delete, rename) so the editor shows the live list, not a stale snapshot.
    private void RefreshStatusCombo()
    {
        if (_statusCombo is null || _statusComboTemplate is null) return;
        var names = new List<string>();
        foreach (var st in _settings.PawnStatuses) names.Add(st.Name);
        var opts = names.ToArray();
        _statusCombo.ItemsSource = opts;
        _statusCombo.SelectedItem = Array.IndexOf(opts, _statusComboTemplate.Status) >= 0
            ? _statusComboTemplate.Status
            : (opts.Length > 0 ? opts[0] : null);
    }


    private bool IsInvalid(PawnTemplate t)
    {
        var aff = PawnTemplate.NormalizeAffiliation(t.Affiliation);
        if (!_settings.AffiliationColors.Any(a => a.Name == aff)) return true;
        var nm = (t.Name ?? "").Trim().ToLowerInvariant();
        int same = 0;
        foreach (var o in ActiveLibrary.Pawns)
            if ((o.Name ?? "").Trim().ToLowerInvariant() == nm
                && PawnTemplate.NormalizeAffiliation(o.Affiliation) == aff) same++;
        return same > 1;
    }
}
