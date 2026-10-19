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

    public MainWindow()
    {
        InitializeComponent();

        Canvas.Document = _doc;
        Canvas.Settings = _settings;
        _history.Initialize(_doc);

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
            ToolMode.Erase => "erase: click-drag to remove cells, walls, doors, stairs, labels",
            _ => "",
        };
        PopulatePanel(mode);
        Canvas.Refresh();
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
                break;

            case ToolMode.Select:
                ToolPanel.Clear("Select options");
                ToolPanel.AddHint("copy/paste the selection with Ctrl+C / Ctrl+V.");
                ToolPanel.AddCheck("show paste ghost on hover", Canvas.GhostEnabled, v => { Canvas.GhostEnabled = v; Canvas.Refresh(); });
                break;

            case ToolMode.Move:
                ToolPanel.Clear("Move options");
                ToolPanel.AddHint("drag a region to select, then click the destination. the source is removed on move.");
                break;

            case ToolMode.Door:
                // global door defaults: shown before placing any door, and every
                // placed door inherits them (MapCanvas reads Settings at placement).
                ToolPanel.Clear("Door settings");
                ToolPanel.AddHint("defaults apply to every door you place next.");
                AddDoorDefaultsSection();
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
        Canvas.ClearSelection();
        Canvas.AutoFit();
        RefreshMapList();
        StatusText.Text = $"switched to {_project.ActiveName}";
        System.IO.File.WriteAllText("/tmp/scratch/status.txt", StatusText.Text);
        Canvas.Refresh();
    }

    private void AddMap()
    {
        string name = $"Map {_project.Maps.Count + 1}";
        _project.AddMap(name);
        Canvas.Document = _doc;
        _history.Initialize(_doc);
        Canvas.ClearSelection();
        Canvas.AutoFit();
        RefreshMapList();
        StatusText.Text = $"added {name}";
        System.IO.File.WriteAllText("/tmp/scratch/status.txt", StatusText.Text);
        Canvas.Refresh();
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
}
