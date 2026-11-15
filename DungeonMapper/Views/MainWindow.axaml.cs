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
        DoorPresetsClose.Click += (_, _) => { FlushSettings(); DoorPresetsOverlay.IsVisible = false; };

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

        // modal pawn-management popup
        ModalClose.Click += (_, _) => ModalOverlay.IsVisible = false;
        ModalAddPawn.Click += (_, _) =>
        {
            var t = new PawnTemplate { Name = "new pawn", Affiliation = "Neutral", Status = "none" };
            t.ApplyAffiliationDefaults();
            ActiveLibrary.Add(t);
            Canvas.ArmedPawn = t;
            StatusText.Text = $"added {t.Name} - armed for placement";
            ShowPawnManagement();
        };

        Canvas.ActionCompleted += () =>
        {
            ActiveHistory.Commit(_doc);
        };
        Canvas.DoorConfigRequested += d => ShowDoorConfig(d);
        Canvas.StairConfigRequested += s => ShowStairConfig(s);
        Canvas.LabelConfigRequested += l => ShowLabelConfig(l);
        Canvas.PawnConfigRequested += p => ShowPawnInfo(p);
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
            ToolMode.Label => "label: click a cell to place, click a label to edit",
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
                ToolPanel.AddButton("Manage Door Presets", () => ShowDoorPresets());
                break;

            case ToolMode.Pawn:
                ToolPanel.Clear("Pawn");
                ToolPanel.AddHint("pick a pawn below, then click a cell to place it. click a placed pawn to edit its template.");
                // manage pawns FIRST: at small resolutions the pawn display can grow
                // past the panel, so all controls sit above it.
                ToolPanel.AddButton("manage pawns", () => ShowPawnManagement());
                if (ActiveLibrary.Pawns.Count == 0)
                {
                    ToolPanel.AddHint("library is empty - add pawns in the pawn menu.");
                }
                else
                {
                    // sort dropdown for the tool swatch grid: name,
                    // affiliation, instance count. Re-selecting the tool rebuilds
                    // the panel with the new order.
                    ToolPanel.AddChoice("sort", new[] { "insertion", "name", "affiliation", "instances" },
                        _pawnToolSort, mode =>
                        {
                            _pawnToolSort = mode;
                            SetTool(ToolMode.Pawn);
                        });
                    // snapshot the templates so the swatch closure reads a stable
                    // list; reading the live library by index can go out of range
                    // if the library changes after the grid is built (add/delete).
                    var snapshot = SortedPawns(_pawnToolSort);
                    var items = new List<(string, string, string, int, int, int, int, int, int, string)>();
                    foreach (var t in snapshot)
                    {
                        var (eo, ei) = t.Effective(_settings);
                        items.Add((t.Id, string.IsNullOrWhiteSpace(t.Name) ? "pawn" : t.Name,
                                   PawnTemplate.NormalizeAffiliation(t.Affiliation),
                                   eo.r, eo.g, eo.b,            ei.r, ei.g, ei.b, t.Id));
                    }
                    // live count: recomputed on each hover from the live placed pawns
                    Func<string, int> liveCount = id =>
                    {
                        int n = 0;
                        foreach (var p in Canvas.Document.Pawns.Values)
                            if (p.TemplateId == id) n++;
                        return n;
                    };
                    ToolPanel.AddSwatchGrid("pawns", items, liveCount, Canvas.LoadPawnImage, i =>
                    {
                        if (i < 0 || i >= snapshot.Count) return;
                        Canvas.ArmedPawn = snapshot[i];
                        StatusText.Text = $"armed {(string.IsNullOrWhiteSpace(snapshot[i].Name) ? "pawn" : snapshot[i].Name)} - click a cell to place";
                    });
                }
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
        StatusText.Text = "undo";
    }

    private void DoRedo()
    {
        var snap = ActiveHistory.Redo();
        if (snap is null) { StatusText.Text = "nothing to redo"; return; }
        _doc.ApplyFrom(snap);
        Canvas.Refresh();
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

    // Manage Door Presets: modal popup opened from the Door tool. Door categories
    // (name + color) with add/remove. Standard is the undeletable default.
    // Removing a category that is in use warns; on confirm, affected doors reset
    // to Standard in ONE undoable transaction (commit after mutation).
    private void ShowDoorPresets()
    {
        FlushSettings();
        ToolPanel.SetTarget(DoorPresetsSections);
        DoorPresetsSections.Children.Clear();

        ToolPanel.AddHint("door categories (name + color):");
        foreach (var t in _settings.DoorTypes)
        {
            var captured = t;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var nameBox = new TextBox { Text = captured.Name, Width = 90 };
            nameBox.TextChanged += (_, _) =>
            {
                captured.Name = nameBox.Text;
                MarkSetting();
            };
            row.Children.Add(nameBox);
            row.Children.Add(ToolPanel.ColorCell(captured.R, captured.G, captured.B, PresetColors(), c =>
            {
                captured.R = c.r; captured.G = c.g; captured.B = c.b;
                Canvas.Refresh();
                MarkSetting();
            }));
            if (captured.Name != "standard")
            {
                var del = new Button { Content = "remove" };
                del.Click += (_, _) => DeleteDoorTypeWithWarning(captured, del);
                row.Children.Add(del);
            }
            DoorPresetsSections.Children.Add(row);
        }
        ToolPanel.AddButton("+ add door category", () =>
        {
            _settings.DoorTypes.Add(new DoorType("new", 245, 220, 60));
            MarkSetting();
            ShowDoorPresets(); // rebuild for the new row
        });

        DoorPresetsOverlay.IsVisible = true;
        ToolPanel.SetTarget(null);
    }

    // Count doors (axis-aligned + angled) using a door type name.
    private int CountDoorsOfType(string name)
    {
        int n = 0;
        foreach (var d in _doc.Doors.Values)
            if (d.Type == name) n++;
        foreach (var l in _doc.AngledDoors.Values)
            foreach (var d in l)
                if (d.Type == name) n++;
        return n;
    }

    // Delete a door category. Standard is undeletable. If doors of that type
    // exist, a confirmation flyout warns; on confirm they reset to Standard and
    // the mutation is committed as ONE undoable transaction.
    private void DeleteDoorTypeWithWarning(DoorType t, Control anchor)
    {
        if (t.Name == "standard") return; // undeletable default

        int affected = CountDoorsOfType(t.Name);

        void doDelete()
        {
            foreach (var d in _doc.Doors.Values)
                if (d.Type == t.Name) d.Type = "standard";
            foreach (var l in _doc.AngledDoors.Values)
                foreach (var d in l)
                    if (d.Type == t.Name) d.Type = "standard";
            if (_settings.DefaultDoorType == t.Name) _settings.DefaultDoorType = "standard";
            _settings.DoorTypes.Remove(t);
            MarkSetting();
            FlushSettings();
            ActiveHistory.Commit(_doc); // one transaction: undo restores the doors
            Canvas.Refresh();
            ShowDoorPresets();
        }

        if (affected == 0)
        {
            doDelete();
            return;
        }

        var popup = new StackPanel { Spacing = 6 };
        popup.Children.Add(new TextBlock
        {
            Text = $"deleting '{t.Name}' resets {affected} door(s) of that type to Standard. delete?",
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

    private void ShowLabelConfig(MapLabel label)
    {
        ToolPanel.Clear("Label");
        ToolPanel.AddText("Text", label.Text, t =>
        {
            label.Text = t;
            ActiveHistory.Commit(_doc);
            Canvas.Refresh();
        });
        // per-label size + color: size is a font size in points, 0 =
        // auto (zoom-scaled); color uses the standardized color picker, -1 = inherit
        // the settings label color.
        ToolPanel.AddNumber("Font size (0 = auto)", label.FontSize, v =>
        {
            label.FontSize = Math.Max(0, v);
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
        ToolPanel.AddButton("reset to auto size + inherit color", () =>
        {
            label.FontSize = 0; label.R = -1; label.G = -1; label.B = -1;
            ActiveHistory.Commit(_doc);
            Canvas.Refresh();
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

    // Template editor: edits a library template (name, affiliation, custom colors,
    // inner/outer, status). Reached from the management menu. Editing a template
    // propagates to every placed pawn that references it (hard reference).
    private void ShowTemplateEditor(PawnTemplate t)
    {
        ToolPanel.Clear("Edit template");
        var presets = PresetColors();

        // NOTE: do NOT call PopulatePanel(Canvas.Mode) here. In Pawn mode that
        // rebuilds the placement panel and wipes this edit panel (the "Edit"
        // button bounced back to the selection menu). Canvas.Refresh updates the
        // map; BuildLibraryGrid refreshes the management modal's swatches so the
        // edit panel (side) and the modal (library) stay in sync live.
        ToolPanel.AddText("Name", t.Name, name =>
        {
            t.Name = name;
            Canvas.Refresh();
            BuildLibraryGrid();
        });
        var affNames = new List<string>();
        foreach (var ac in _settings.AffiliationColors) affNames.Add(ac.Name);
        ToolPanel.AddChoice("Affiliation", affNames.ToArray(),
            PawnTemplate.NormalizeAffiliation(t.Affiliation), aff =>
            {
                t.Affiliation = aff;
                if (!t.ColorOverride) t.ApplyAffiliationDefaults();
                Canvas.Refresh();
                BuildLibraryGrid();
            });

        // custom colors: build the inner/outer fields once, then the toggle. Toggling
        // inserts/removes the color fields IN PLACE so the edit panel stays open (no
        // PopulatePanel wipe, no re-click). Fields start appended; if override is off
        // they're removed, and the toggle re-inserts them right after the checkbox.
        var innerPanel = ToolPanel.AddColorField("Inner color", t.InnerR, t.InnerG, t.InnerB, presets, c =>
        {
            t.InnerR = c.r; t.InnerG = c.g; t.InnerB = c.b;
            Canvas.Refresh();
            BuildLibraryGrid();
        });
        var outerPanel = ToolPanel.AddColorField("Outer color", t.OuterR, t.OuterG, t.OuterB, presets, c =>
        {
            t.OuterR = c.r; t.OuterG = c.g; t.OuterB = c.b;
            Canvas.Refresh();
            BuildLibraryGrid();
        });
        var check = new CheckBox { Content = "custom colors", IsChecked = t.ColorOverride, Foreground = Brushes.White };
        check.IsCheckedChanged += (_, _) =>
        {
            bool on = check.IsChecked == true;
            t.ColorOverride = on;
            if (!on) t.ApplyAffiliationDefaults();
            Canvas.Refresh();
            BuildLibraryGrid();
            if (on)
            {
                ToolPanel.InsertAfter(check, innerPanel);
                ToolPanel.InsertAfter(innerPanel, outerPanel);
            }
            else
            {
                ToolPanel.RemoveControl(innerPanel);
                ToolPanel.RemoveControl(outerPanel);
            }
        };
        ToolPanel.Target.Children.Add(check);
        if (!t.ColorOverride)
        {
            ToolPanel.RemoveControl(innerPanel);
            ToolPanel.RemoveControl(outerPanel);
        }

        var names = new List<string>();
        foreach (var st in _settings.PawnStatuses) names.Add(st.Name);
        var statusCombo = ToolPanel.AddChoice("Status", names.ToArray(), t.Status, s =>
        {
            t.Status = s;
            Canvas.Refresh();
            BuildLibraryGrid();
        });
        _statusCombo = statusCombo;
        _statusComboTemplate = t;

        // unique pawn image: upload replaces the two-tone circles entirely, drawn
        // square (center-cropped) to fit the cell. PNG preferred, JPEG accepted.
        // every upload is compressed to a 50x50 png (PawnImage.Compress).
        ToolPanel.AddButton("upload image (png/jpg)", async () =>
        {
            var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg" } },
                },
            });
            if (files.Count == 0) return;
            var src = files[0].Path.LocalPath;
            try
            {
                var png = PawnImage.Compress(System.IO.File.ReadAllBytes(src));
                t.ImageB64 = System.Convert.ToBase64String(png);
                t.ImageExt = ".png";
                Canvas.InvalidateImageCache();
                Canvas.Refresh();
                BuildLibraryGrid();
                StatusText.Text = $"image compressed to 50x50 png ({png.Length / 1024} KB)";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"could not read image: {ex.Message}";
            }
        });
        if (!string.IsNullOrEmpty(t.ImageB64))
        {
            ToolPanel.AddButton("remove image", () =>
            {
                t.ImageB64 = "";
                t.ImageExt = "";
                Canvas.InvalidateImageCache();
                Canvas.Refresh();
                BuildLibraryGrid();
            });
        }

        ToolPanel.AddButton("back to library", () => ShowPawnManagement());

        // Delete template. If live placed pawns reference it, warn first (they
        // render as X after deletion - broken reference - and can be replaced via
        // Change Pawn). The warning is a confirmation flyout anchored on the button.
        var delBtn = new Button { Content = "delete template" };
        delBtn.Click += (_, _) => DeleteTemplateWithWarning(t, delBtn);
        ToolPanel.AddButtonRaw(delBtn);
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
            Canvas.Refresh();
            BuildLibraryGrid();
            ShowPawnManagement();
            // the side edit panel is showing the now-deleted template; restore it to
            // the pawn tool panel so the stale editor (whose changes do nothing) is gone.
            PopulatePanel(Canvas.Mode);
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
            Canvas.Refresh();
            BuildLibraryGrid();
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
            Canvas.Refresh();
            BuildLibraryGrid();
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

    // Export: writes the active library to a json file (templates + embedded
    // base64 images), shareable independently of any map.
    private async void ExportLibrary()
    {
        var file = await this.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"PawnLibrary_{Stamp()}.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } } },
        });
        if (file is null) return;
        var path = file.Path.LocalPath;
        System.IO.File.WriteAllText(path, MapSerializer.LibraryToJson(ActiveLibrary));
        StatusText.Text = $"exported {_libraries[_project].Pawns.Count} templates to {path}";
    }

    // Import: reads a library json file, then asks append vs overwrite.
    // Append adds the imported templates to the end; overwrite deletes all
    // current templates first. Colliding ids get a fresh id (imported template
    // becomes a new entry; existing references are untouched).
    private async void ImportLibrary()
    {
        var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } } },
        });
        if (files.Count == 0) return;
        List<PawnTemplate> templates;
        try
        {
            templates = MapSerializer.LibraryFromDtos(MapSerializer.LibraryFromJson(System.IO.File.ReadAllText(files[0].Path.LocalPath)));
        }
        catch (Exception ex) { StatusText.Text = $"import failed: {ex.Message}"; return; }
        if (templates.Count == 0) { StatusText.Text = "nothing to import (empty library file)"; return; }

        var overlay = new StackPanel { Spacing = 8 };
        overlay.Children.Add(new TextBlock { Text = $"import {templates.Count} templates:", Foreground = Brushes.White, FontSize = 14 });
        var appendBtn = new Button { Content = "append (add to the end)" };
        var overBtn = new Button { Content = "overwrite (delete all current ones)" };
        var cancelBtn = new Button { Content = "cancel" };
        void Close() => ModalOverlay.IsVisible = false;
        appendBtn.Click += (_, _) =>
        {
            Close();
            ActiveLibrary.MergeAppend(templates);
            Canvas.InvalidateImageCache();
            Canvas.Refresh();
            BuildLibraryGrid();
            StatusText.Text = $"appended {templates.Count} pawns";
        };
        overBtn.Click += (_, _) =>
        {
            Close();
            ActiveLibrary.MergeOverwrite(templates);
            Canvas.InvalidateImageCache();
            Canvas.Refresh();
            BuildLibraryGrid();
            StatusText.Text = $"replaced library with {templates.Count} pawns";
        };
        cancelBtn.Click += (_, _) => Close();
        overlay.Children.Add(appendBtn);
        overlay.Children.Add(overBtn);
        overlay.Children.Add(cancelBtn);
        ModalOverlay.Child = overlay;
        ModalOverlay.IsVisible = true;
    }

    // Pawn management: a modal popup centered over the map. The library grid is
    // virtualized (VirtualizingStackPanel) so only visible rows render, keeping
    // large libraries fast. Opened from the pawn tool panel's "pawn menu" button.
    private void ShowPawnManagement()
    {
        ModalOverlay.IsVisible = true;
        var presets = PresetColors();

        // sort dropdown for the library grid: name, affiliation,
        // instance count, invalid-first.
        ModalSortRow.Children.Clear();
        ModalSortRow.Children.Add(new TextBlock { Text = "sort:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        var sortCombo = new ComboBox { MinWidth = 150 };
        foreach (var m in new[] { "insertion", "name", "affiliation", "instances", "invalid" })
            sortCombo.Items.Add(m);
        sortCombo.SelectedIndex = Math.Max(0, new[] { "insertion", "name", "affiliation", "instances", "invalid" }.ToList().IndexOf(_pawnSort));
        sortCombo.SelectionChanged += (_, _) =>
        {
            _pawnSort = sortCombo.SelectedItem?.ToString() ?? "insertion";
            BuildLibraryGrid();
        };
        ModalSortRow.Children.Add(sortCombo);

        // dedicated import/export buttons for the pawn library (independent of
        // the map file; the library is still saved inside the project file).
        var importBtn = new Button { Content = "import" };
        importBtn.Click += (_, _) => ImportLibrary();
        ModalSortRow.Children.Add(importBtn);
        var exportBtn = new Button { Content = "export" };
        exportBtn.Click += (_, _) => ExportLibrary();
        ModalSortRow.Children.Add(exportBtn);

        BuildLibraryGrid();

        // Global affiliation coloring: templates inherit these unless ColorOverride.
        // outer + inner on the SAME row (modal is constant width).
        ToolPanel.SetTarget(ModalAffiliations);
        ModalAffiliations.Children.Clear();
        foreach (var ac in _settings.AffiliationColors)
        {
            var captured = ac;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock { Text = captured.Name, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Width = 55 });
            var outerCol = new StackPanel { Spacing = 1 };
            outerCol.Children.Add(new TextBlock { Text = "outer", Foreground = Brushes.White, FontSize = 11 });
            outerCol.Children.Add(ToolPanel.ColorCell(captured.OuterR, captured.OuterG, captured.OuterB, presets, c =>
            {
                captured.OuterR = c.r; captured.OuterG = c.g; captured.OuterB = c.b;
                _settings.Save();
                Canvas.Refresh();
                BuildLibraryGrid();
            }));
            var innerCol = new StackPanel { Spacing = 1 };
            innerCol.Children.Add(new TextBlock { Text = "inner", Foreground = Brushes.White, FontSize = 11 });
            innerCol.Children.Add(ToolPanel.ColorCell(captured.InnerR, captured.InnerG, captured.InnerB, presets, c =>
            {
                captured.InnerR = c.r; captured.InnerG = c.g; captured.InnerB = c.b;
                _settings.Save();
                Canvas.Refresh();
                BuildLibraryGrid();
            }));
            row.Children.Add(outerCol);
            row.Children.Add(innerCol);
            // delete X for non-default affiliations. Player/Ally/Enemy/Neutral are
            // hard-coded defaults and undeletable.
            if (captured.Name != "Player" && captured.Name != "Ally"
                && captured.Name != "Enemy" && captured.Name != "Neutral")
            {
                var delAff = new Button { Content = "X", Padding = new Thickness(4, 0, 4, 0) };
                delAff.Click += (_, _) => DeleteAffiliationWithWarning(captured, delAff);
                row.Children.Add(delAff);
            }
            ModalAffiliations.Children.Add(row);
        }

        ToolPanel.AddButton("+ add affiliation", () =>
        {
            var n = _settings.AffiliationColors.Count;
            var name = $"Affiliation {n + 1}";
            while (_settings.AffiliationColors.Any(a => a.Name == name))
            { n++; name = $"Affiliation {n + 1}"; }
            _settings.AffiliationColors.Add(new AffiliationColor(name, (180, 180, 180), (110, 110, 110)));
            _settings.Save();
            Canvas.Refresh();
            BuildLibraryGrid();
            ShowPawnManagement();
        });

        // Status styles: an actual Grid with grid lines. Columns: Name, color cell
        // ([preview][palette]), Thickness, delete X. Each style on its own row.
        // "none" is the hard default (black, thickness 1) and is un-deletable.
        ToolPanel.SetTarget(ModalStatuses);
        ModalStatuses.Children.Clear();
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); // name + duplicate X, ~2x wider
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });       // color
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });     // thickness
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });        // delete
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
        int rowIdx = 0;
        // header row
        var hName = new TextBlock { Text = "Name", Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(2) };
        var hColor = new TextBlock { Text = "Color", Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(2) };
        var hThick = new TextBlock { Text = "Thickness", Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(2) };
        Grid.SetColumn(hName, 0); Grid.SetRow(hName, rowIdx);
        Grid.SetColumn(hColor, 1); Grid.SetRow(hColor, rowIdx);
        Grid.SetColumn(hThick, 2); Grid.SetRow(hThick, rowIdx);
        grid.Children.Add(hName); grid.Children.Add(hColor); grid.Children.Add(hThick);
        rowIdx++;
        // status rows: name cell = [textbox][red X shown when the name is a duplicate].
        // Track each row so renames can recompute the duplicate set live.
        var rows = new List<(PawnStatus st, TextBox nameTb, TextBlock dupX)>();
        foreach (var st in _settings.PawnStatuses)
        {
            var captured = st;
            bool isNone = captured.Name == "none";
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var nameTb = new TextBox { Text = captured.Name, Width = 124, Margin = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
            var dupX = new TextBlock { Text = "✕", Foreground = Brushes.Red, FontSize = 16, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
            var nameCell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            nameCell.Children.Add(nameTb);
            nameCell.Children.Add(dupX);
            nameTb.TextChanged += (_, _) =>
            {
                captured.Name = nameTb.Text;
                _settings.Save();
                Canvas.Refresh();
                BuildLibraryGrid();
                UpdateDuplicates();
                RefreshStatusCombo();
            };

            var colorCell = ToolPanel.ColorCell(captured.R, captured.G, captured.B, presets, c =>
            {
                captured.R = c.r; captured.G = c.g; captured.B = c.b;
                Canvas.Refresh();
                _settings.Save();
                BuildLibraryGrid();
            });
            colorCell.Margin = new Thickness(2);

            var thickTb = new TextBox { Text = captured.Thickness.ToString(), Width = 50, Margin = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
            thickTb.TextChanged += (_, _) =>
            {
                if (int.TryParse(thickTb.Text, out int v))
                {
                    captured.Thickness = Math.Max(0, v);
                    Canvas.Refresh();
                    _settings.Save();
                    BuildLibraryGrid();
                }
            };

            Grid.SetColumn(nameCell, 0); Grid.SetRow(nameCell, rowIdx);
            Grid.SetColumn(colorCell, 1); Grid.SetRow(colorCell, rowIdx);
            Grid.SetColumn(thickTb, 2); Grid.SetRow(thickTb, rowIdx);
            grid.Children.Add(nameCell); grid.Children.Add(colorCell); grid.Children.Add(thickTb);

            // delete X per row; "none" is un-deletable (no button). Deleting a style
            // resets everything that uses it to "none" (confirmation flyout).
            if (!isNone)
            {
                var delX = new Button { Content = "✕", Foreground = Brushes.Red, Margin = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
                delX.Click += (_, _) => DeleteStatusWithWarning(captured, delX);
                Grid.SetColumn(delX, 3); Grid.SetRow(delX, rowIdx);
                grid.Children.Add(delX);
            }

            rows.Add((captured, nameTb, dupX));
            rowIdx++;
        }

        // recompute which names are duplicated and toggle each row's red X.
        void UpdateDuplicates()
        {
            var counts = new Dictionary<string, int>();
            foreach (var (st, _, _) in rows)
                counts[st.Name] = counts.TryGetValue(st.Name, out int n) ? n + 1 : 1;
            foreach (var (st, _, dupX) in rows)
                dupX.IsVisible = counts[st.Name] > 1;
        }
        UpdateDuplicates();

        // grid lines: a border on each cell via a Grid with ShowGridLines
        grid.ShowGridLines = true;
        ModalStatuses.Children.Add(grid);
        ToolPanel.AddButton("+ add status style", () =>
        {
            // give new statuses indexed unique names ("new", "new 1", "new 2", ...)
            // so two new ones don't collide on "new".
            const string prefix = "new";
            string name = prefix;
            int i = 1;
            while (_settings.PawnStatuses.Any(st => st.Name == name))
                name = prefix + " " + (i++);
            _settings.PawnStatuses.Add(new PawnStatus(name, 245, 220, 60, 2));
            _settings.Save();
            ShowPawnManagement();
            RefreshStatusCombo();
        });

        ToolPanel.SetTarget(null);
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

    // Rebuild the virtualized library grid: chunk templates into rows of 4 swatches.
    // The ItemsControl's VirtualizingStackPanel renders only the rows in view.
    private string _pawnSort = "insertion";
    private string _pawnToolSort = "insertion";

    private void BuildLibraryGrid()
    {
        LibraryGrid.Items.Clear();
        var snapshot = SortedPawns(_pawnSort);
        for (int i = 0; i < snapshot.Count; i += 4)
            LibraryGrid.Items.Add(BuildSwatchRow(snapshot.GetRange(i, Math.Min(4, snapshot.Count - i))));
    }

    // Shared sort for the pawn management modal and the pawn tool swatch grid.
    // Modes: insertion (library order), name, affiliation, instances (live placed
    // count, desc), invalid (invalid templates first). A template is invalid when
    // its affiliation is not in the affiliation table (deleted faction) or it is a
    // duplicate - sharing normalized name + affiliation with another template.
    private List<PawnTemplate> SortedPawns(string mode)
    {
        var list = new List<PawnTemplate>(ActiveLibrary.Pawns);
        switch (mode)
        {
            case "name":
                list.Sort((a, b) => string.Compare((a.Name ?? "").Trim(), (b.Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                break;
            case "affiliation":
                list.Sort((a, b) => string.Compare(PawnTemplate.NormalizeAffiliation(a.Affiliation),
                    PawnTemplate.NormalizeAffiliation(b.Affiliation), StringComparison.OrdinalIgnoreCase));
                break;
            case "instances":
                list.Sort((a, b) => InstanceCount(b).CompareTo(InstanceCount(a)));
                break;
            case "invalid":
                list.Sort((a, b) => (IsInvalid(b) ? 1 : 0).CompareTo(IsInvalid(a) ? 1 : 0));
                break;
            default: // insertion order, no sort
                break;
        }
        return list;
    }

    private int InstanceCount(PawnTemplate t)
    {
        int n = 0;
        foreach (var p in Canvas.Document.Pawns.Values) if (p.TemplateId == t.Id) n++;
        return n;
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

    // One row of clickable pawn swatches (concentric circles + status ring). Clicking
    // a swatch opens its template editor. Hover shows a live instance count.
    private StackPanel BuildSwatchRow(List<PawnTemplate> row)
    {
        var rowPanel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var t in row)
        {
            var captured = t;
            var (effOuter, effInner) = t.Effective(_settings);
            var st = _settings.FindPawnStatus(t.Status);
            var cell = new StackPanel { Spacing = 1, Margin = new Thickness(2) };
            var canvas = new Avalonia.Controls.Canvas { Width = 30, Height = 30 };
            // unique-image templates render the image in the swatch (center-crop
            // square, clipped to the outer circle), same as on the map.
            var bmp = string.IsNullOrEmpty(t.ImageB64) ? null : Canvas.LoadPawnImage(t.Id);
            // non-image pawns keep the affiliation outer color; the image brush
            // replaces it only when a bitmap loads.
            IBrush outerFill = new SolidColorBrush(Color.FromRgb((byte)effOuter.r, (byte)effOuter.g, (byte)effOuter.b));
            if (bmp is not null)
            {
                double side = Math.Min(bmp.PixelSize.Width, bmp.PixelSize.Height);
                // center-crop square in source-pixel coords, clipped to the ellipse
                outerFill = new ImageBrush
                {
                    Source = bmp,
                    Stretch = Stretch.Fill,
                    TileMode = TileMode.None,
                    SourceRect = new RelativeRect((bmp.PixelSize.Width - side) / (2.0 * bmp.PixelSize.Width),
                                                  (bmp.PixelSize.Height - side) / (2.0 * bmp.PixelSize.Height),
                                                  side / (double)bmp.PixelSize.Width,
                                                  side / (double)bmp.PixelSize.Height,
                                                  RelativeUnit.Relative),
                };
            }
            var outer = new Ellipse { Width = 26, Height = 26, Fill = outerFill };
            Avalonia.Controls.Canvas.SetLeft(outer, 2); Avalonia.Controls.Canvas.SetTop(outer, 2);
            var inner = new Ellipse { Width = 13, Height = 13, Fill = new SolidColorBrush(Color.FromRgb((byte)effInner.r, (byte)effInner.g, (byte)effInner.b)), IsVisible = bmp is null };
            Avalonia.Controls.Canvas.SetLeft(inner, 8); Avalonia.Controls.Canvas.SetTop(inner, 8);
            canvas.Children.Add(outer);
            canvas.Children.Add(inner);
            if (st.Thickness > 0)
            {
                var ring = new Ellipse { Width = 26, Height = 26, Stroke = new SolidColorBrush(Color.FromRgb((byte)st.R, (byte)st.G, (byte)st.B)), StrokeThickness = st.Thickness };
                Avalonia.Controls.Canvas.SetLeft(ring, 2); Avalonia.Controls.Canvas.SetTop(ring, 2);
                canvas.Children.Add(ring);
            }
            var hit = new Rectangle { Width = 30, Height = 30, Fill = Brushes.Transparent };
            hit.PointerPressed += (_, _) =>
            {
                // clicking a library swatch arms it for placement AND opens its
                // editor. Without arming here, the modal can never change the
                // armed pawn - the first-armed template keeps getting placed.
                Canvas.ArmedPawn = captured;
                StatusText.Text = $"armed {(string.IsNullOrWhiteSpace(captured.Name) ? "pawn" : captured.Name)} - click a cell to place";
                ShowTemplateEditor(captured);
            };
            canvas.Children.Add(hit);
            var cap = new TextBlock { Text = string.IsNullOrWhiteSpace(t.Name) ? "pawn" : t.Name, FontSize = 10, Foreground = Brushes.White, MaxWidth = 60, TextWrapping = TextWrapping.Wrap };
            cell.Children.Add(canvas);
            cell.Children.Add(cap);
            cell.PointerEntered += (_, _) =>
            {
                int n = 0;
                foreach (var p in Canvas.Document.Pawns.Values) if (p.TemplateId == captured.Id) n++;
                ToolTip.SetTip(cell, $"{captured.Name}, {PawnTemplate.NormalizeAffiliation(captured.Affiliation)} ({n}x) - click to edit");
            };
            rowPanel.Children.Add(cell);
        }
        return rowPanel;
    }
}
