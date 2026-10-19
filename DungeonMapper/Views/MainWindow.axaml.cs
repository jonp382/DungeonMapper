using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
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
    private readonly History _history = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly PawnLibrary _pawnLibrary = PawnLibrary.Load();

    public MainWindow()
    {
        InitializeComponent();

        Canvas.Document = _doc;
        Canvas.Settings = _settings;
        _history.Initialize(_doc);

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

        BtnUndo.Click += (_, _) => DoUndo();
        BtnRedo.Click += (_, _) => DoRedo();
        BtnFit.Click += (_, _) => Canvas.AutoFit();
        BtnSave.Click += (_, _) => SaveMap();
        BtnLoad.Click += (_, _) => LoadMap();
        BtnSettings.Click += (_, _) => ShowSettings();

        Canvas.ActionCompleted += () => _history.Commit(_doc);
        Canvas.DoorConfigRequested += d => ShowDoorConfig(d);
        Canvas.StairConfigRequested += s => ShowStairConfig(s);
        Canvas.LabelConfigRequested += l => ShowLabelConfig(l);
        Canvas.PawnConfigRequested += p => ShowPawnConfig(p);
        Canvas.StatusMessage += msg =>
        {
            StatusText.Text = msg;
            System.IO.File.WriteAllText("/tmp/scratch/status.txt", msg);
        };

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
                case Key.Z: DoUndo(); e.Handled = true; return;
                case Key.Y: DoRedo(); e.Handled = true; return;
                case Key.C: Canvas.CopySelection(); Canvas.Refresh(); e.Handled = true; return;
                case Key.V: Canvas.PasteAtCursor(); Canvas.Refresh(); e.Handled = true; return;
                case Key.X: Canvas.CutSelection(); Canvas.Refresh(); e.Handled = true; return;
            }
        }
        else if (e.Key == Key.Delete)
        {
            // delete = erase the current selection with everything selected
            Canvas.DeleteSelection(); Canvas.Refresh(); e.Handled = true; return;
        }
        else if (e.KeyModifiers == KeyModifiers.None)
        {
            // tool hotkeys from the configurable table (item 7)
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
    }

    private void SetTool(ToolMode mode)
    {
        Canvas.Mode = mode;
        StatusText.Text = mode switch
        {
            ToolMode.Select => "select: click an element to configure, drag a region to select, Ctrl+C copy, Ctrl+V paste",
            ToolMode.Move => "move: drag a region to select, then click the destination to move it",
            ToolMode.Room => "room: click-drag a rectangle. edges get auto-walls",
            ToolMode.Floor => "floor: click-drag a rectangle. fills cells, clears interior walls/doors/stairs/labels, draws no walls",
            ToolMode.Wall => "wall: click an edge to add, click a wall to remove, drag to draw a line",
            ToolMode.Door => "door: click a wall edge to place, click a door to configure",
            ToolMode.Stair => "stair: click a cell to place, click a stair to configure, drag for a region",
            ToolMode.Label => "label: click a cell to place, click a label to edit",
            ToolMode.Erase => "erase: click-drag to remove cells, walls, doors, stairs, labels. delete key deletes the selection",
            ToolMode.Pawn => "pawn: click a cell to place, click a pawn to configure. one pawn per cell",
            _ => "",
        };
        PopulatePanel(mode);
        Canvas.Refresh();
        FocusCanvas(); // restore hotkey focus after the tool button click
        System.IO.File.WriteAllText("/tmp/scratch/status.txt", StatusText.Text);
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
                break;

            case ToolMode.Select:
                ToolPanel.Clear("Select options");
                ToolPanel.AddHint("copy/paste the selection with Ctrl+C / Ctrl+V. the paste ghost follows the cursor.");
                break;

            case ToolMode.Move:
                ToolPanel.Clear("Move options");
                ToolPanel.AddHint("drag a region to select, then click the destination. the source is removed on move. works across maps: select on one map, switch, click the target.");
                ToolPanel.AddCheck("show paste ghost", Canvas.GhostEnabled, v => { Canvas.GhostEnabled = v; Canvas.Refresh(); FocusCanvas(); });
                break;

            case ToolMode.Room:
                ToolPanel.Clear("Room settings");
                ToolPanel.AddHint("normal: drawing over existing rooms merges them (shared walls removed); a room grown out of a room opens a passageway.");
                ToolPanel.AddCheck("strict walls (draw outer walls over existing rooms, keep interior walls)", Canvas.RoomStrict, v => { Canvas.RoomStrict = v; FocusCanvas(); });
                break;

            case ToolMode.Door:
                // global door defaults: shown before placing any door, and every
                // placed door inherits them (MapCanvas reads Settings at placement).
                ToolPanel.Clear("Door settings");
                ToolPanel.AddHint("defaults apply to every door you place next.");
                AddDoorDefaultsSection();
                break;

            case ToolMode.Pawn:
                ToolPanel.Clear("Pawn settings");
                ToolPanel.AddHint("defaults apply to every pawn you place next. click a placed pawn to configure it, or pick a saved pawn from the library.");
                ToolPanel.AddText("default pawn name", _settings.DefaultPawnName, t =>
                {
                    _settings.DefaultPawnName = t;
                    _settings.Save();
                });
                ToolPanel.AddChoice("default pawn class", new[] { "Player", "Friendly", "Hostile", "Neutral" },
                    _settings.DefaultPawnClass, c =>
                    {
                        _settings.DefaultPawnClass = c;
                        _settings.Save();
                    });
                AddPawnStatusEditor();
                AddPawnLibrarySection();
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

    // Switches the canvas to a map by index. History is reset to the new map.
    private void SwitchMap(int index)
    {
        _project.SetActive(index);
        Canvas.Document = _doc;
        _history.Initialize(_doc);
        // keep the armed selection + clipboard so cross-map move and cut/paste
        // work: select on map1, switch to map2, click the destination.
        Canvas.AutoFit();
        RefreshMapList();
        StatusText.Text = $"switched to {_project.ActiveName}";
        System.IO.File.WriteAllText("/tmp/scratch/status.txt", StatusText.Text);
        Canvas.Refresh();
        FocusCanvas(); // combo kept focus; restore it so hotkeys/paste work
    }

    private void AddMap()
    {
        string name = $"Map {_project.Maps.Count + 1}";
        _project.AddMap(name);
        Canvas.Document = _doc;
        _history.Initialize(_doc);
        Canvas.ClearSelection();
        Canvas.ResetClipboard(); // new empty map: a cut from the old map must not paste/delete here
        Canvas.AutoFit();
        RefreshMapList();
        StatusText.Text = $"added {name}";
        System.IO.File.WriteAllText("/tmp/scratch/status.txt", StatusText.Text);
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
        var snap = _history.Undo();
        if (snap is null) { StatusText.Text = "nothing to undo"; return; }
        _doc.ApplyFrom(snap);
        Canvas.Refresh();
        StatusText.Text = "undo";
    }

    private void DoRedo()
    {
        var snap = _history.Redo();
        if (snap is null) { StatusText.Text = "nothing to redo"; return; }
        _doc.ApplyFrom(snap);
        Canvas.Refresh();
        StatusText.Text = "redo";
    }

    private async void SaveMap()
    {
        var file = await this.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "project.json",
            DefaultExtension = "json",
        });
        if (file is null) return;
        MapSerializer.SaveProject(_project, file.Path.LocalPath);
        StatusText.Text = $"saved {file.Path.LocalPath}";
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
            string text = System.IO.File.ReadAllText(path);
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
            Canvas.Document = _doc;
            _history.Initialize(_doc);
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

    // Builds the global door settings fields: default type, default state, and the
    // door category editor. Shared by the Settings menu and the Door tool panel so
    // the door tool shows the same fields before placing, and placed doors inherit
    // them (MapCanvas reads Settings.DefaultDoorType/State at placement time).
    private void AddDoorDefaultsSection()
    {
        // default door properties (item 3)
        var names = new List<string>();
        foreach (var t in _settings.DoorTypes) names.Add(t.Name);
        ToolPanel.AddChoice("default door type", names.ToArray(), _settings.DefaultDoorType, t =>
        {
            _settings.DefaultDoorType = t;
            _settings.Save();
        });
        ToolPanel.AddChoice("default door state", new[] { "open", "closed", "locked" },
            _settings.DefaultDoorState, s =>
            {
                _settings.DefaultDoorState = s;
                _settings.Save();
            });

        // door categories editor (item 4)
        ToolPanel.AddHint("door categories (name + color):");
        foreach (var t in _settings.DoorTypes)
        {
            var captured = t;
            ToolPanel.AddText(captured.Name, $"#{captured.R:X2}{captured.G:X2}{captured.B:X2}", hex =>
            {
                if (hex.Length == 7 && hex[0] == '#')
                {
                    captured.R = Convert.ToInt32(hex.Substring(1, 2), 16);
                    captured.G = Convert.ToInt32(hex.Substring(3, 2), 16);
                    captured.B = Convert.ToInt32(hex.Substring(5, 2), 16);
                    Canvas.Refresh();
                    _settings.Save();
                }
            });
        }
        ToolPanel.AddButton("+ add door category", () =>
        {
            _settings.DoorTypes.Add(new DoorType("new", 245, 220, 60));
            _settings.Save();
            // rebuild for the active tool so the Door tool panel keeps its context
            PopulatePanel(Canvas.Mode);
        });
    }

    private void ShowSettings()
    {
        ToolPanel.Clear("Settings");

        // grid draw spacing (item 6)
        ToolPanel.AddNumber("grid draw spacing (cells between lines)", _settings.GridDrawSpacing, v =>
        {
            _settings.GridDrawSpacing = Math.Max(1, v);
            Canvas.Refresh();
            _settings.Save();
        });

        AddDoorDefaultsSection();

        // default label text (item 1)
        ToolPanel.AddText("default label text", _settings.DefaultLabelText, t =>
        {
            _settings.DefaultLabelText = t;
            _settings.Save();
        });

        // tool hotkeys editor (item 7)
        ToolPanel.AddHint("tool hotkeys (single letter):");
        foreach (var modeName in _settings.ToolHotkeys.Keys)
        {
            var captured = modeName;
            ToolPanel.AddText(captured, _settings.ToolHotkeys[captured], key =>
            {
                var k = key.Trim().ToUpperInvariant();
                if (k.Length == 1)
                {
                    _settings.ToolHotkeys[captured] = k;
                    _settings.Save();
                    RefreshToolLabels();
                }
            });
        }
    }

    private void ShowDoorConfig(Door door)
    {
        ToolPanel.Clear("Door");
        var names = new List<string>();
        foreach (var t in _settings.DoorTypes) names.Add(t.Name);
        ToolPanel.AddChoice("Type", names.ToArray(), door.Type, t =>
        {
            door.Type = t;
            _history.Commit(_doc);
            Canvas.Refresh();
        });
        ToolPanel.AddChoice("State", new[] { "open", "closed", "locked" },
            door.State.ToString().ToLowerInvariant(), s =>
            {
                door.State = Enum.Parse<DoorState>(s, ignoreCase: true);
                _history.Commit(_doc);
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
        ToolPanel.AddChoice("Direction", new[] { "up", "down" },
            stair.Direction.ToString().ToLowerInvariant(), s =>
            {
                var dir = Enum.Parse<StairDirection>(s, ignoreCase: true);
                foreach (var s2 in _doc.ConnectedStairs(cell))
                    s2.Direction = dir;
                _history.Commit(_doc);
                Canvas.Refresh();
            });
    }

    private void ShowLabelConfig(MapLabel label)
    {
        ToolPanel.Clear("Label");
        ToolPanel.AddText("Text", label.Text, t =>
        {
            label.Text = t;
            _history.Commit(_doc);
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

    // Pawn config: name, class, inner/outer colors, status. Fields apply live.
    // "save to library" persists the pawn as a reusable template in pawn_library.json.
    private void ShowPawnConfig(Pawn pawn)
    {
        ToolPanel.Clear("Pawn");
        ToolPanel.AddText("Name", pawn.Name, t =>
        {
            pawn.Name = t;
            _history.Commit(_doc);
        });
        ToolPanel.AddChoice("Class", new[] { "Player", "Friendly", "Hostile", "Neutral" },
            pawn.Class.ToString(), c =>
            {
                pawn.Class = Enum.Parse<PawnClass>(c);
                pawn.ApplyClassDefaults();
                _history.Commit(_doc);
                Canvas.Refresh();
            });
        ToolPanel.AddText("Inner color", $"#{pawn.InnerR:X2}{pawn.InnerG:X2}{pawn.InnerB:X2}", hex =>
        {
            if (TryHex(hex, out var c))
            {
                pawn.InnerR = c.r; pawn.InnerG = c.g; pawn.InnerB = c.b;
                _history.Commit(_doc);
                Canvas.Refresh();
            }
        });
        ToolPanel.AddText("Outer color", $"#{pawn.OuterR:X2}{pawn.OuterG:X2}{pawn.OuterB:X2}", hex =>
        {
            if (TryHex(hex, out var c))
            {
                pawn.OuterR = c.r; pawn.OuterG = c.g; pawn.OuterB = c.b;
                _history.Commit(_doc);
                Canvas.Refresh();
            }
        });
        var names = new List<string>();
        foreach (var st in _settings.PawnStatuses) names.Add(st.Name);
        ToolPanel.AddChoice("Status", names.ToArray(), pawn.Status, s =>
        {
            pawn.Status = s;
            _history.Commit(_doc);
            Canvas.Refresh();
        });
        ToolPanel.AddButton("save to library", () =>
        {
            _pawnLibrary.Add(new PawnTemplate
            {
                Name = string.IsNullOrWhiteSpace(pawn.Name) ? "pawn" : pawn.Name,
                Class = pawn.Class.ToString(),
                InnerR = pawn.InnerR, InnerG = pawn.InnerG, InnerB = pawn.InnerB,
                OuterR = pawn.OuterR, OuterG = pawn.OuterG, OuterB = pawn.OuterB,
                Status = pawn.Status,
            });
            _pawnLibrary.Save();
            StatusText.Text = $"saved {pawn.Name} to pawn library";
        });
        ToolPanel.AddButton("delete pawn", () =>
        {
            _doc.RemovePawn(pawn.Cell);
            _history.Commit(_doc);
            Canvas.Refresh();
            StatusText.Text = "pawn deleted";
        });
    }

    // Pawn status styles editor, same pattern as the door category editor:
    // each row is "name" -> hex color, plus a thickness number, plus + add.
    private void AddPawnStatusEditor()
    {
        ToolPanel.AddHint("status styles (name -> #hex, thickness):");
        foreach (var st in _settings.PawnStatuses)
        {
            var captured = st;
            ToolPanel.AddText(captured.Name, $"#{captured.R:X2}{captured.G:X2}{captured.B:X2}", hex =>
            {
                if (TryHex(hex, out var c))
                {
                    captured.R = c.r; captured.G = c.g; captured.B = c.b;
                    Canvas.Refresh();
                    _settings.Save();
                }
            });
            ToolPanel.AddNumber($"{captured.Name} thickness", captured.Thickness, v =>
            {
                captured.Thickness = Math.Max(0, v);
                Canvas.Refresh();
                _settings.Save();
            });
        }
        ToolPanel.AddButton("+ add status style", () =>
        {
            _settings.PawnStatuses.Add(new PawnStatus("new", 245, 220, 60, 2));
            _settings.Save();
            PopulatePanel(Canvas.Mode);
        });
    }

    // The saved pawn library: picking a name sets the pawn defaults (name, class,
    // colors, status), so the next pawn placed on the map is that saved pawn.
    private void AddPawnLibrarySection()
    {
        var libNames = new List<string>();
        foreach (var t in _pawnLibrary.Pawns) libNames.Add(t.Name);
        if (libNames.Count == 0)
        {
            ToolPanel.AddHint("library: empty. save pawns from the pawn config panel.");
            return;
        }
        ToolPanel.AddHint("library: pick a saved pawn to load its defaults, then click a cell to place it.");
        ToolPanel.AddChoice("saved pawns", libNames.ToArray(), libNames[0], name =>
        {
            var t = _pawnLibrary.Find(name);
            if (t is null) return;
            _settings.DefaultPawnName = t.Name;
            _settings.DefaultPawnClass = t.Class;
            _settings.DefaultPawnStatus = t.Status;
            _settings.DefaultPawnInner = $"{t.InnerR},{t.InnerG},{t.InnerB}";
            _settings.DefaultPawnOuter = $"{t.OuterR},{t.OuterG},{t.OuterB}";
            _settings.Save();
            StatusText.Text = $"loaded {name} from library";
        });
    }
}
