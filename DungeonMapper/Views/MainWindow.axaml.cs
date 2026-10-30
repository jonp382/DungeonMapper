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
    private readonly PawnLibrary _pawnLibrary = PawnLibrary.Load();
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
        // unique-pawn images live in a pawn_images subfolder next to the exe.
        Canvas.PawnImagesDir = System.IO.Path.Combine(AppContext.BaseDirectory, "pawn_images");
        System.IO.Directory.CreateDirectory(Canvas.PawnImagesDir);
        Canvas.Settings = _settings;
        Canvas.Library = _pawnLibrary;
        HistoryFor(_project.Maps[0]); // snapshot the starting map at startup

        // hotkey labels on tool buttons (item: show hotkeys in tool text)
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
        LabelTool(ToolLine, "Line");

        // map list (item 8)
        RefreshMapList();
        MapList.SelectionChanged += (_, _) =>
        {
            if (_suppressMapSelect) return;
            int idx = MapList.SelectedIndex;
            if (idx >= 0 && idx != _project.ActiveIndex) SwitchMap(idx);
        };
        BtnAddMap.Click += (_, _) => AddMap();
        BtnRenameMap.Click += (_, _) => RenameActiveMap();

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
        ToolLine.Click += (_, _) => SetTool(ToolMode.Line);

        BtnUndo.Click += (_, _) => DoUndo();
        BtnRedo.Click += (_, _) => DoRedo();
        BtnFit.Click += (_, _) => Canvas.AutoFit();
        BtnSave.Click += (_, _) => SaveMap();
        BtnLoad.Click += (_, _) => LoadMap();
        BtnSettings.Click += (_, _) => ShowSettings();

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
            MapSerializer.SaveProject(_project, System.IO.Path.Combine(dir, name));
            StatusText.Text = $"autosaved {name}";
        };
        _autosaveTimer.Start();

        // modal pawn-management popup
        ModalClose.Click += (_, _) => ModalOverlay.IsVisible = false;
        ModalAddPawn.Click += (_, _) =>
        {
            var t = new PawnTemplate { Name = "new pawn", Affiliation = "Neutral", Status = "none" };
            t.ApplyAffiliationDefaults();
            _pawnLibrary.Add(t);
            _pawnLibrary.Save();
            Canvas.ArmedPawn = t;
            StatusText.Text = $"added {t.Name} - armed for placement";
            ShowPawnManagement();
        };

        Canvas.ActionCompleted += () =>
        {
            ActiveHistory.Commit(_doc);
            CleanupUnreferencedImages();
        };
        Canvas.DoorConfigRequested += d => ShowDoorConfig(d);
        Canvas.StairConfigRequested += s => ShowStairConfig(s);
        Canvas.LabelConfigRequested += l => ShowLabelConfig(l);
        Canvas.PawnConfigRequested += p => ShowPawnInfo(p);
        Canvas.StatusMessage += msg =>
        {
            StatusText.Text = msg;
            // System.IO.File.WriteAllText("/tmp/scratch/status.txt", msg);
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
            if (Canvas.Mode == ToolMode.Line && !IsTyping())
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

    // "Tool (K)" button label from the configurable hotkey table.
    private void LabelTool(Button b, string modeName)
    {
        string key = _settings.ToolHotkeys.TryGetValue(modeName, out var k) ? k : "";
        b.Content = string.IsNullOrEmpty(key) ? modeName : $"{modeName} ({key})";
    }

    // Re-label all tool buttons after a hotkey edit.
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
        LabelTool(ToolLine, "Line");
    }

    private void SetTool(ToolMode mode)
    {
        FlushSettings(); // switching tools flushes pending settings changes
        // switching tools counts as a cancel of an armed move: the ghost is
        // disarmed, rotation resets, and hotkeys are restored.
        Canvas.CancelMove();
        Canvas.Mode = mode;
        // re-selecting a tool resets the armed pawn template: the user expects
        // clicking the pawn tool again to clear a stale selection.
        Canvas.ArmedPawn = null;
        Canvas.CancelLine(); // a half-dragged line ghost must not survive a tool switch
        StatusText.Text = mode switch
        {
            ToolMode.Select => "select: click an element to configure, drag a region to select, Ctrl+C copy, Ctrl+V paste, shift+scroll / R / Shift+R rotates the clipboard",
            ToolMode.Move => "move: drag a region to select, then click the destination to move it. shift+scroll / R / Shift+R rotates the armed region, Escape cancels",
            ToolMode.Room => "room: click-drag a rectangle. edges get auto-walls",
            ToolMode.Floor => "floor: click-drag a rectangle. fills cells, clears interior walls/doors/stairs/labels, draws no walls",
            ToolMode.Wall => "wall: click an edge to add, click a wall to remove, drag to draw a line",
            ToolMode.Door => "door: click a wall edge to place, click a door to configure",
            ToolMode.Stair => "stair: click a cell to place, click a stair to configure, drag for a region",
            ToolMode.Label => "label: click a cell to place, click a label to edit",
            ToolMode.Erase => "erase: click-drag to remove cells, walls, doors, stairs, labels. delete key deletes the selection",
            ToolMode.Pawn => "pawn: click a cell to place, click a pawn to configure. one pawn per cell",
            ToolMode.Line => "line: press at a grid vertex, drag to the end vertex, release. orthogonal lines create walls along the path, angled lines create one angled wall",
            _ => "",
        };
        PopulatePanel(mode);
        Canvas.Refresh();
        FocusCanvas(); // restore hotkey focus after the tool button click
        // System.IO.File.WriteAllText("/tmp/scratch/status.txt", StatusText.Text);
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
                if (_pawnLibrary.Pawns.Count == 0)
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
                                   eo.r, eo.g, eo.b, ei.r, ei.g, ei.b, t.Image));
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

            case ToolMode.Line:
                ToolPanel.Clear("Line");
                ToolPanel.AddHint("press at a grid vertex, drag to the end vertex, release. orthogonal lines create a series of walls along the path; angled lines create one angled wall. walls merge with existing walls, doors survive; erase removes them");
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
        StatusText.Text = $"switched to {_project.ActiveName}";
        // System.IO.File.WriteAllText("/tmp/scratch/status.txt", StatusText.Text);
        Canvas.Refresh();
        FocusCanvas(); // combo kept focus; restore it so hotkeys/paste work
    }

    private void AddMap()
    {
        string name = $"Map {_project.Maps.Count + 1}";
        _project.AddMap(name);
        Canvas.Document = _doc;
        HistoryFor(_project.Maps[^1]); // snapshot the new map at add time
        Canvas.ClearSelection();
        Canvas.ResetClipboard(); // new empty map: a cut from the old map must not paste/delete here
        Canvas.AutoFit();
        RefreshMapList();
        StatusText.Text = $"added {name}";
        // System.IO.File.WriteAllText("/tmp/scratch/status.txt", StatusText.Text);
        Canvas.Refresh();
        FocusCanvas();
    }

    private void RenameActiveMap()
    {
        var active = _project.Active;
        int idx = _project.ActiveIndex;
        string baseName = _project.Maps[idx].Name;
        // rename via the floating panel: a text field prefilled with the current name
        ToolPanel.Clear("Rename map");
        ToolPanel.AddText("map name", baseName, v =>
        {
            _project.Maps[idx].Name = string.IsNullOrWhiteSpace(v) ? baseName : v;
            RefreshMapList();
            StatusText.Text = $"renamed to {_project.Maps[idx].Name}";
        });
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
        MapSerializer.SaveProject(_project, path);
        _currentMapPath = path; // Ctrl+S saves over this file from now on
        StatusText.Text = $"saved {path}";
    }

    // Ctrl+S: save over the map the user loaded (the user-selected json, NOT the
    // backup). A brand-new map with no load/save history opens the picker once,
    // then Ctrl+S saves over that file.
    private void SaveCurrent()
    {
        if (_currentMapPath is null) { SaveMap(); return; }
        MapSerializer.SaveProject(_project, _currentMapPath);
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

            // accept both single-map files and multi-map projects
            if (text.Contains("\"maps\""))
            {
                _project = MapSerializer.LoadProject(path);
            }
            else
            {
                _project = new MapProject();
                _project.Maps[0].Doc = MapSerializer.Load(path);
            }
            _currentMapPath = path; // Ctrl+S saves over the user-selected json
            Canvas.Document = _doc;
            _histories.Clear(); // fresh project = fresh entries
            HistoryFor(_project.Maps[_project.ActiveIndex]); // snapshot loaded state
            Canvas.ClearSelection();
            Canvas.ResetClipboard(); // loaded project replaces all maps; old cut sources are stale
            Canvas.AutoFit();
            RefreshMapList();
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

        // tool hotkeys editor (item 7): grid of small single-letter boxes with a
        // label above each, like the pawn status styles. Reset restores the
        // hardcoded defaults.
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
            ShowSettings(); // rebuild the grid with the restored defaults
            RefreshToolLabels();
        });

        SettingsOverlay.IsVisible = true;
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
        var t = _pawnLibrary.Find(pawn.TemplateId);
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
        foreach (var t in _pawnLibrary.Pawns)
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

    /// Deletes image files in pawn_images that no placed pawn references any more.
    /// A template's image is referenced while at least one placed pawn on the
    /// active map points at that template. Runs after every committed action.
    private void CleanupUnreferencedImages()
    {
        var referenced = new HashSet<string>();
        foreach (var p in _doc.Pawns.Values)
        {
            var t = _pawnLibrary.Find(p.TemplateId);
            if (t is not null && !string.IsNullOrEmpty(t.Image)) referenced.Add(t.Image);
        }
        foreach (var f in System.IO.Directory.GetFiles(Canvas.PawnImagesDir))
        {
            var name = System.IO.Path.GetFileName(f);
            if (!referenced.Contains(name))
            {
                try { System.IO.File.Delete(f); } catch { }
            }
        }
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
            _pawnLibrary.Save();
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
                _pawnLibrary.Save();
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
            _pawnLibrary.Save();
            Canvas.Refresh();
            BuildLibraryGrid();
        });
        var outerPanel = ToolPanel.AddColorField("Outer color", t.OuterR, t.OuterG, t.OuterB, presets, c =>
        {
            t.OuterR = c.r; t.OuterG = c.g; t.OuterB = c.b;
            _pawnLibrary.Save();
            Canvas.Refresh();
            BuildLibraryGrid();
        });
        var check = new CheckBox { Content = "custom colors", IsChecked = t.ColorOverride, Foreground = Brushes.White };
        check.IsCheckedChanged += (_, _) =>
        {
            bool on = check.IsChecked == true;
            t.ColorOverride = on;
            if (!on) t.ApplyAffiliationDefaults();
            _pawnLibrary.Save();
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
            _pawnLibrary.Save();
            Canvas.Refresh();
            BuildLibraryGrid();
        });
        _statusCombo = statusCombo;
        _statusComboTemplate = t;

        // unique pawn image: upload replaces the two-tone circles entirely, drawn
        // square (center-cropped) to fit the cell. PNG preferred, JPEG accepted.
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
            var ext = System.IO.Path.GetExtension(src).ToLowerInvariant();
            var dest = System.IO.Path.Combine(Canvas.PawnImagesDir, t.Id + ext);
            System.IO.File.Copy(src, dest, overwrite: true);
            t.Image = System.IO.Path.GetFileName(dest);
            _pawnLibrary.Save();
            Canvas.InvalidateImageCache();
            Canvas.Refresh();
            BuildLibraryGrid();
        });
        if (!string.IsNullOrEmpty(t.Image))
        {
            ToolPanel.AddButton("remove image", () =>
            {
                var path = System.IO.Path.Combine(Canvas.PawnImagesDir, t.Image);
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                t.Image = "";
                _pawnLibrary.Save();
                Canvas.InvalidateImageCache();
                Canvas.Refresh();
                BuildLibraryGrid();
                CleanupUnreferencedImages();
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
            _pawnLibrary.Remove(t.Id);
            _pawnLibrary.Save();
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
        foreach (var t in _pawnLibrary.Pawns)
            if (PawnTemplate.NormalizeAffiliation(t.Affiliation) == ac.Name) affected++;

        void doDelete()
        {
            foreach (var t in _pawnLibrary.Pawns)
                if (PawnTemplate.NormalizeAffiliation(t.Affiliation) == ac.Name)
                {
                    t.Affiliation = "Neutral";
                    if (!t.ColorOverride) t.ApplyAffiliationDefaults();
                }
            _settings.AffiliationColors.Remove(ac);
            _settings.Save();
            _pawnLibrary.Save();
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
        foreach (var t in _pawnLibrary.Pawns)
            if (t.Status == st.Name) affected++;

        void doDelete()
        {
            foreach (var t in _pawnLibrary.Pawns)
                if (t.Status == st.Name) t.Status = "none";
            _settings.PawnStatuses.Remove(st);
            _settings.Save();
            _pawnLibrary.Save();
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
        var list = new List<PawnTemplate>(_pawnLibrary.Pawns);
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
        foreach (var o in _pawnLibrary.Pawns)
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
            var bmp = string.IsNullOrEmpty(t.Image) ? null : Canvas.LoadPawnImage(t.Image);
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
