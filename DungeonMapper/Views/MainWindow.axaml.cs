using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

using static DungeonMapper.Views.Ui;

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
    // the object types of each campaign, keyed like the pawn libraries
    private readonly Dictionary<MapProject, ObjectLibrary> _objectLibraries = new();
    private ObjectLibrary ActiveObjects => ObjectsFor(_project);
    private ObjectLibrary ObjectsFor(MapProject p)
    {
        if (!_objectLibraries.TryGetValue(p, out var lib)) _objectLibraries[p] = lib = new ObjectLibrary();
        return lib;
    }
    private PawnLibrary LibraryFor(MapProject p)
    {
        if (!_libraries.TryGetValue(p, out var lib))
        {
            lib = new PawnLibrary();
            _libraries[p] = lib;
        }
        return lib;
    }
    // door presets, pawn statuses and affiliation colors of each campaign,
    // keyed like the libraries. they persist with the campaign file. a
    // campaign without its own (a new one, or a file from before they moved
    // into the campaign) gets the built-in starters.
    private readonly Dictionary<MapProject, CampaignPresets> _presets = new();
    private CampaignPresets ActivePresets => PresetsFor(_project);
    private CampaignPresets PresetsFor(MapProject p)
    {
        if (!_presets.TryGetValue(p, out var pr))
        {
            pr = CampaignPresets.Default();
            _presets[p] = pr;
        }
        return pr;
    }
    // hands the open campaign's library and presets to the canvas. a default
    // door type the campaign lacks goes back to standard.
    private void SyncCampaign()
    {
        Canvas.Library = ActiveLibrary;
        Canvas.Presets = ActivePresets;
        Canvas.ObjectTypes = ActiveObjects;
        if (Canvas.ArmedObject is { } armed && ActiveObjects.Find(armed.Id) != armed) Canvas.ArmedObject = null;
        if (!ActivePresets.DoorTypes.Any(t => t.Name == Canvas.DefaultDoorType))
            Canvas.DefaultDoorType = CampaignPresets.StandardDoor;
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

        Canvas.Document = _doc;
        Canvas.Settings = _settings;
        // the door presets, statuses and affiliations used to live in
        // settings.json. the first start after they moved into the campaign
        // seeds the campaign it opens with them. settings.json drops them the
        // next time it is written.
        if (AppSettings.LegacyPresets() is { } legacy)
            _presets[_project] = CampaignPresets.ForCampaign(legacy);
        SyncCampaign();
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
        // no png for objects yet: a drawn rounded block
        SetIcon(ToolObject, new Border
        {
            Width = 18, Height = 14, CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromRgb(140, 140, 150)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(216, 216, 224)), BorderThickness = new Thickness(1.5),
        });
        SetIcon(ToolErase, PngIcon("DMErase"));
        // no png for measure yet: a drawn ruler line with end ticks
        SetIcon(ToolMeasure, new Avalonia.Controls.Shapes.Path
        {
            Width = 20, Height = 20, Stretch = Stretch.None,
            Data = Geometry.Parse("M4,16 L16,4 M2,14 L6,18 M14,2 L18,6 M8,10 L10,12 M11,7 L13,9"),
            Stroke = new SolidColorBrush(Color.FromRgb(216, 216, 224)), StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
        });
        // no png for fog yet: a drawn eye
        SetIcon(ToolFog, new Avalonia.Controls.Shapes.Path
        {
            Width = 20, Height = 20, Stretch = Stretch.None,
            Data = Geometry.Parse("M2,10 Q10,2 18,10 Q10,18 2,10 Z M10,7.5 A2.5,2.5 0 1 1 9.99,7.5 Z"),
            Stroke = new SolidColorBrush(Color.FromRgb(216, 216, 224)), StrokeThickness = 2,
            StrokeLineCap = PenLineCap.Round,
        });
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
        ToolObject.Click += (_, _) => SetTool(ToolMode.Object);
        ToolPolygon.Click += (_, _) => SetTool(ToolMode.Polygon);
        ToolMeasure.Click += (_, _) => SetTool(ToolMode.Measure);
        ToolFog.Click += (_, _) => SetTool(ToolMode.Fog);


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
        MenuObjects.Click += (_, _) => { CloseModals(); ShowObjectManagement(); };
        MenuDoorPresets.Click += (_, _) => { CloseModals(); ShowDoorPresets(); };
        MenuHotkeys.Click += (_, _) => { CloseModals(); ShowHotkeys(); };

        // settings, door presets and hotkeys modal popups
        InitSettings();
        InitMeasure();
        InitFog();
        InitDoorPresets();
        InitHotkeys();
        InitMaps();
        InitModal();

        // autosave: ticks every minute; writes to maps/autosaves when the
        // configured interval has elapsed (0 = off). Flushes pending settings.
        _autosaveTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autosaveTimer.Tick += (_, _) =>
        {
            FlushSettings();
            if (_settings.AutosaveMinutes <= 0) return;
            if ((DateTime.Now - _lastAutosave).TotalMinutes < _settings.AutosaveMinutes) return;
            RunAutosave(); // skips when nothing changed since the last autosave or save
        };
        _autosaveTimer.Start();

        Canvas.ActionCompleted += () =>
        {
            ActiveHistory.Commit(_doc);
            RefreshMaps();
            RefreshToolInfo();
        };
        Canvas.DoorConfigRequested += d => ShowDoorConfig(d);
        Canvas.StairConfigRequested += s => ShowStairConfig(s);
        Canvas.LabelConfigRequested += l => ShowLabelConfig(l);
        // rebuild so the size box and the reopen spot follow the new range
        Canvas.LabelResized += l => { if (Canvas.SelectedLabel == l) ShowLabelConfig(l); };
        Canvas.LabelEditRequested += l => BeginLabelEdit(l);
        Canvas.ViewChanged += PlaceLabelEditor;
        Canvas.PawnConfigRequested += p => ShowPawnInfo(p);
        Canvas.ObjectConfigRequested += o => ShowObjectInfo(o);
        Canvas.FormationDeployed += () => { PawnViewsChanged(); RefreshMaps(); };
        Canvas.ObjectsChanged += () => { RefreshObjectPanel(); RefreshObjectManager(); };
        Canvas.PawnPlaced += id =>
        {
            ActiveLibrary.NoteRecent(id);
            PawnViewsChanged();
            RefreshDoorPresets();
            RefreshMaps();
            RefreshToolInfo();
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
        MarkSaved(); // the empty starting campaign has nothing to save
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
            // esc on the exit prompt is cancel, never no
            if (ModalOverlay.IsVisible && _modalKind == "exit") { CloseExitPrompt(); e.Handled = true; return; }
            if (ModalOverlay.IsVisible && _modalKind == "close-doors") { CloseModals(); FocusCanvas(); e.Handled = true; return; }
            if (ModalOverlay.IsVisible && _modalKind == "objects") { CloseManage(); e.Handled = true; return; }
            if (DoorPresetsOverlay.IsVisible) { CloseDoorPresets(); e.Handled = true; return; }
            if (ModalOverlay.IsVisible && _modalKind == "preset-import" && _presetImportBack is { } back) { back(); e.Handled = true; return; }
            if (SettingsOverlay.IsVisible) { CloseSettings(); e.Handled = true; return; }
            if (HotkeysOverlay.IsVisible) { CloseHotkeys(); e.Handled = true; return; }
            // a formation capture or an armed formation goes first
            if (Canvas.PickingArea) { Canvas.CancelAreaPick(); StatusText.Text = "formation capture cancelled"; e.Handled = true; return; }
            if (Canvas.FormationArmed) { Canvas.CancelFormation(); StatusText.Text = "deploy cancelled"; e.Handled = true; return; }
            // Escape cancels an armed move (disarm ghost, reset rotation, restore
            // hotkeys) and discards polygon vertices.
            if (Canvas.MoveArmed) { Canvas.CancelMove(); Canvas.Refresh(); e.Handled = true; return; }
            // the maps panel is not modal, so an armed move is cancelled first
            if (MapsOverlay.IsVisible) { CloseMaps(); e.Handled = true; return; }
            if (Canvas.Mode == ToolMode.Polygon && !IsTyping())
            { Canvas.CancelPolygon(); Canvas.Refresh(); e.Handled = true; return; }
            if (Canvas.Mode == ToolMode.Wall && !IsTyping())
            { Canvas.CancelLine(); Canvas.Refresh(); e.Handled = true; return; }
            if (Canvas.Mode == ToolMode.Measure && Canvas.HasMeasure && !IsTyping())
            { Canvas.ClearMeasure(); e.Handled = true; return; }
            if (Canvas.Mode == ToolMode.Fog && Canvas.FogAnchored && !IsTyping())
            { Canvas.ReleaseFogAnchor(); e.Handled = true; return; }
        }
        else if (e.Key == Key.R && Canvas.FormationArmed && !IsTyping())
        {
            // R turns an armed formation clockwise, Shift+R counterclockwise
            Canvas.RotateFormation(e.KeyModifiers == KeyModifiers.Shift ? 1 : -1);
            e.Handled = true; return;
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
            // zoom to fit only changes the view, so it works while a move is armed.
            // ctrl+z never gets here, the ctrl branch above handles it.
            if (Enum.TryParse<Key>(_settings.ToolHotkeys.GetValueOrDefault(AppSettings.FitHotkey, ""), out var fit) && fit == e.Key)
            { Canvas.AutoFit(); e.Handled = true; return; }
            if (!Canvas.HotkeysDisabled
                && Enum.TryParse<Key>(_settings.ToolHotkeys.GetValueOrDefault(AppSettings.FormationHotkey, ""), out var form) && form == e.Key)
            { DeployLastFormation(); e.Handled = true; return; }
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
        LabelTool(ToolObject, "Object");
        LabelTool(ToolMeasure, "Measure");
        LabelTool(ToolFog, "Fog");
        // the view menu shows the zoom to fit letter, display only
        MenuFit.InputGesture = Enum.TryParse<Key>(_settings.ToolHotkeys.GetValueOrDefault(AppSettings.FitHotkey, ""), out var fit)
            ? new KeyGesture(fit) : null;
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
        ToolMode.Object => ToolObject,
        ToolMode.Measure => ToolMeasure,
        ToolMode.Fog => ToolFog,
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
        _presetImportBack = null;
        SettingsOverlay.IsVisible = false;
        DoorPresetsOverlay.IsVisible = false;
        HotkeysOverlay.IsVisible = false;
        FinishMapRename(true);
        MapsOverlay.IsVisible = false;
    }

    private void SetTool(ToolMode mode)
    {
        FlushSettings(); // switching tools flushes pending settings changes
        // switching tools counts as a cancel of an armed move: the ghost is
        // disarmed, rotation resets, and hotkeys are restored.
        Canvas.CancelMove();
        Canvas.CancelFormation();
        Canvas.CancelAreaPick();
        Canvas.Mode = mode;
        foreach (var m in Enum.GetValues<ToolMode>())
            ToolButton(m).Classes.Set("active", m == mode);
        // re-selecting a tool resets the armed pawn template: the user expects
        // clicking the pawn tool again to clear a stale selection.
        Canvas.ArmedPawn = null;
        Canvas.ArmedObject = null;
        Canvas.CancelLine(); // a half-dragged line ghost must not survive a tool switch
        Canvas.ClearMeasure(); // a measurement lasts until a tool switch
        Canvas.ResetFog(); // fog is back on and unanchored each time the tool is picked
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
            ToolMode.Object => "object: pick a type, drag to place. click an object to configure, drag its handles to resize",
            ToolMode.Wall => "wall: click on your starting grid point and release on your end point to draw a line",
            ToolMode.Measure => "measure: drag for a distance, ctrl+click adds a leg. drag out from a pawn (or shift+drag) for a radius. esc clears",
            ToolMode.Fog => "fog: the vision radius follows the cursor, click to anchor it. shift+scroll changes the radius, esc releases the anchor",
            _ => "",
        };
        PopulatePanel(mode);
        Canvas.Refresh();
        FocusCanvas(); // restore hotkey focus after the tool button click
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
        RefreshToolInfo();
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
        RefreshToolInfo();
        StatusText.Text = $"added {name}";
        Canvas.Refresh();
        FocusCanvas();
    }

    private void DoUndo()
    {
        FlushPendingEdits();
        var snap = ActiveHistory.Undo();
        if (snap is null) { StatusText.Text = "nothing to undo"; return; }
        _doc.ApplyFrom(snap);
        Canvas.Refresh();
        PawnViewsChanged();
        RefreshDoorPresets();
        RefreshMaps();
        RefreshToolInfo();
        ReopenConfigView();
        StatusText.Text = "undo";
    }

    private void DoRedo()
    {
        FlushPendingEdits();
        var snap = ActiveHistory.Redo();
        if (snap is null) { StatusText.Text = "nothing to redo"; return; }
        _doc.ApplyFrom(snap);
        Canvas.Refresh();
        PawnViewsChanged();
        RefreshDoorPresets();
        RefreshMaps();
        RefreshToolInfo();
        ReopenConfigView();
        StatusText.Text = "redo";
    }

    private async void SaveMap() => await SaveMapAsync();

    // false when the picker was cancelled or the write failed
    private async Task<bool> SaveMapAsync()
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
        if (file is null) return false;
        var path = file.Path.LocalPath;
        try { MapSerializer.SaveProject(_project, path, ActiveLibrary, ActivePresets, ActiveObjects); }
        catch (Exception ex) { StatusText.Text = $"save failed: {ex.Message}"; return false; }
        _currentMapPath = path; // Ctrl+S saves over this file from now on
        MarkSaved();
        StatusText.Text = $"saved {path}";
        return true;
    }

    // Ctrl+S: save over the map the user loaded (the user-selected json, NOT the
    // backup). A brand-new map with no load/save history opens the picker once,
    // then Ctrl+S saves over that file.
    private async void SaveCurrent() => await SaveCurrentAsync();

    private async Task<bool> SaveCurrentAsync()
    {
        FlushPendingEdits();
        if (_currentMapPath is null) return await SaveMapAsync();
        try { MapSerializer.SaveProject(_project, _currentMapPath, ActiveLibrary, ActivePresets, ActiveObjects); }
        catch (Exception ex) { StatusText.Text = $"save failed: {ex.Message}"; return false; }
        MarkSaved();
        StatusText.Text = $"saved {_currentMapPath}";
        return true;
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
            // a campaign file keeps its own presets. a map file opened as a new
            // campaign gets the ones it carries plus the built-in starters.
            _presets[project] = kind == SaveFileKind.Campaign
                ? CampaignPresets.ForCampaign(imported.Presets)
                : CampaignPresets.ForMapFile(imported.Presets);
            // the embedded library goes into the new campaign's (empty) library,
            // pawn references remapped through the id map. images travel as
            // base64 on the templates, so nothing is written to disk.
            var templates = MapSerializer.LibraryFromDtos(imported.Library);
            var idMap = ActiveLibrary.MergeAppend(templates);
            RemapPawns(_project, idMap);
            // formations come with a campaign file, members re-pointed like pawns
            foreach (var f in imported.Formations)
            {
                foreach (var m in f.Members)
                    if (idMap.TryGetValue(m.TemplateId, out var nid)) m.TemplateId = nid;
                ActiveLibrary.Formations.Add(f);
            }
            MergeObjects(imported, _project.Maps);
            SyncCampaign();
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
            RefreshToolInfo();
            // a map file opened as a new campaign has no campaign file yet, so
            // it counts as unsaved
            if (kind == SaveFileKind.Campaign) MarkSaved(); else _autosavedContent = null;
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

    // ---- in place label editing: a text box laid over the label on the map,
    // like editing a cell in a spreadsheet. enter keeps the text, shift+enter
    // or alt+enter starts a new line, escape puts the old text back, and
    // clicking away keeps it.

    private TextBox? _labelEditor;
    private MapLabel? _editingLabel;
    private bool _labelBoxWrap;
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
                InsertNewline(tb);
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

    // replaces the selection with a line break and puts the caret after it
    private static void InsertNewline(TextBox tb)
    {
        var t = tb.Text ?? "";
        int a = Math.Min(tb.SelectionStart, tb.SelectionEnd), b = Math.Max(tb.SelectionStart, tb.SelectionEnd);
        a = Math.Clamp(a, 0, t.Length); b = Math.Clamp(b, a, t.Length);
        tb.Text = t[..a] + "\n" + t[b..];
        tb.SelectionStart = tb.SelectionEnd = tb.CaretIndex = a + 1;
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
            ActivePresets.AffiliationColors.Remove(ac);
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
            ActivePresets.PawnStatuses.Remove(st);
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

    // a file's object pictures and types into this campaign, its placed objects
    // re-pointed at the merged types. pictures inline in an older file's looks
    // move into the pool first, so the look compare sees picture keys
    private void MergeObjects(ImportedMaps imported, IEnumerable<MapEntry> maps)
    {
        var lib = ActiveObjects;
        lib.AddPictures(imported.ObjectPictures);
        var types = MapSerializer.TidyObjectTypes(imported.ObjectTypes);
        lib.AdoptLegacy(types.Select(t => t.Look)
            .Concat(maps.SelectMany(m => m.Doc.Objects).Where(o => o.Look is not null).Select(o => o.Look!)));
        RemapObjects(maps, lib.MergeAppend(types));
    }

    // re-points placed objects through the id remap from an object library merge
    private static void RemapObjects(IEnumerable<MapEntry> maps, Dictionary<string, string> map)
    {
        foreach (var m in maps)
            foreach (var o in m.Doc.Objects)
                if (map.TryGetValue(o.TypeId, out var nid)) o.TypeId = nid;
    }

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
        foreach (var st in ActivePresets.PawnStatuses) names.Add(st.Name);
        var opts = names.ToArray();
        _statusCombo.ItemsSource = opts;
        _statusCombo.SelectedItem = Array.IndexOf(opts, _statusComboTemplate.Status) >= 0
            ? _statusComboTemplate.Status
            : (opts.Length > 0 ? opts[0] : null);
    }


    private bool IsInvalid(PawnTemplate t)
    {
        var aff = PawnTemplate.NormalizeAffiliation(t.Affiliation);
        if (!ActivePresets.AffiliationColors.Any(a => a.Name == aff)) return true;
        var nm = (t.Name ?? "").Trim().ToLowerInvariant();
        int same = 0;
        foreach (var o in ActiveLibrary.Pawns)
            if ((o.Name ?? "").Trim().ToLowerInvariant() == nm
                && PawnTemplate.NormalizeAffiliation(o.Affiliation) == aff) same++;
        return same > 1;
    }
}
