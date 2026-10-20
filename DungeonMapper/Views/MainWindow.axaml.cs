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
    private readonly History _history = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly PawnLibrary _pawnLibrary = PawnLibrary.Load();
    // the side panel's Status dropdown, so it can be refreshed when statuses change
    private ComboBox? _statusCombo;
    private PawnTemplate? _statusComboTemplate;

    public MainWindow()
    {
        InitializeComponent();

        Canvas.Document = _doc;
        Canvas.Settings = _settings;
        Canvas.Library = _pawnLibrary;
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

        Canvas.ActionCompleted += () => _history.Commit(_doc);
        Canvas.DoorConfigRequested += d => ShowDoorConfig(d);
        Canvas.StairConfigRequested += s => ShowStairConfig(s);
        Canvas.LabelConfigRequested += l => ShowLabelConfig(l);
        Canvas.PawnConfigRequested += p => ShowPawnInfo(p);
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
                ToolPanel.Clear("Pawn");
                ToolPanel.AddHint("pick a pawn below, then click a cell to place it. click a placed pawn to edit its template.");
                if (_pawnLibrary.Pawns.Count == 0)
                {
                    ToolPanel.AddHint("library is empty - add pawns in the pawn menu.");
                }
                else
                {
                    // snapshot the templates so the swatch closure reads a stable
                    // list; reading the live library by index can go out of range
                    // if the library changes after the grid is built (add/delete).
                    var snapshot = new List<PawnTemplate>(_pawnLibrary.Pawns);
                    var items = new List<(string, string, string, int, int, int, int, int, int)>();
                    foreach (var t in snapshot)
                    {
                        var (eo, ei) = t.Effective(_settings);
                        items.Add((t.Id, string.IsNullOrWhiteSpace(t.Name) ? "pawn" : t.Name,
                                   PawnTemplate.NormalizeAffiliation(t.Affiliation),
                                   eo.r, eo.g, eo.b, ei.r, ei.g, ei.b));
                    }
                    // live count: recomputed on each hover from the live placed pawns
                    Func<string, int> liveCount = id =>
                    {
                        int n = 0;
                        foreach (var p in Canvas.Document.Pawns.Values)
                            if (p.TemplateId == id) n++;
                        return n;
                    };
                    ToolPanel.AddSwatchGrid("pawns", items, liveCount, i =>
                    {
                        if (i < 0 || i >= snapshot.Count) return;
                        Canvas.ArmedPawn = snapshot[i];
                        StatusText.Text = $"armed {(snapshot[i].Name ?? "pawn")} - click a cell to place";
                    });
                }
                ToolPanel.AddButton("manage pawns", () => ShowPawnManagement());
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
                _history.Commit(_doc);
                Canvas.Refresh();
                StatusText.Text = "pawn deleted";
            });
            return;
        }

        ToolPanel.AddHint($"name: {t.Name}");
        ToolPanel.AddHint($"affiliation: {PawnTemplate.NormalizeAffiliation(t.Affiliation)}");
        ToolPanel.AddHint($"instances on map: {count}");
        ToolPanel.AddHint($"status: {t.Status}");
        ToolPanel.AddButton("change pawn", () => ShowChangePawn(pawn));
        ToolPanel.AddButton("delete pawn", () =>
        {
            _doc.RemovePawn(pawn.Cell);
            _history.Commit(_doc);
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
                _history.Commit(_doc);
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
            _pawnLibrary.Save();
            Canvas.Refresh();
            BuildLibraryGrid();
        });
        ToolPanel.AddChoice("Affiliation", new[] { "Player", "Ally", "Enemy", "Neutral" },
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
            ModalAffiliations.Children.Add(row);
        }

        // Status styles: an actual Grid with grid lines. Columns: Name, color cell
        // ([preview][palette]), Thickness, delete X. Each style on its own row.
        // "none" is the hard default (black, thickness 1) and is un-deletable.
        ToolPanel.SetTarget(ModalStatuses);
        ModalStatuses.Children.Clear();
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) }); // name, ~2x wider
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
    private void BuildLibraryGrid()
    {
        LibraryGrid.Items.Clear();
        var snapshot = new List<PawnTemplate>(_pawnLibrary.Pawns);
        for (int i = 0; i < snapshot.Count; i += 4)
            LibraryGrid.Items.Add(BuildSwatchRow(snapshot.GetRange(i, Math.Min(4, snapshot.Count - i))));
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
            var outer = new Ellipse { Width = 26, Height = 26, Fill = new SolidColorBrush(Color.FromRgb((byte)effOuter.r, (byte)effOuter.g, (byte)effOuter.b)) };
            Avalonia.Controls.Canvas.SetLeft(outer, 2); Avalonia.Controls.Canvas.SetTop(outer, 2);
            var inner = new Ellipse { Width = 13, Height = 13, Fill = new SolidColorBrush(Color.FromRgb((byte)effInner.r, (byte)effInner.g, (byte)effInner.b)) };
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
            hit.PointerPressed += (_, _) => ShowTemplateEditor(captured);
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
