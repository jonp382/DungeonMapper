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
    private readonly MapDocument _doc = new();
    private readonly History _history = new();
    private readonly AppSettings _settings = AppSettings.Load();

    public MainWindow()
    {
        InitializeComponent();

        Canvas.Document = _doc;
        Canvas.Settings = _settings;
        _history.Initialize(_doc);

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

            default:
                ToolPanel.Clear("Tool options");
                ToolPanel.AddHint("no options for this tool.");
                break;
        }
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
            SuggestedFileName = "map.json",
            DefaultExtension = "json",
        });
        if (file is null) return;
        MapSerializer.Save(_doc, file.Path.LocalPath);
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
            var loaded = MapSerializer.Load(files[0].Path.LocalPath);
            _doc.ApplyFrom(loaded);
            _history.Initialize(_doc);
            Canvas.AutoFit();
            StatusText.Text = $"loaded {files[0].Path.LocalPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"load failed: {ex.Message}";
        }
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

        // default label text (item 1)
        ToolPanel.AddText("default label text", _settings.DefaultLabelText, t =>
        {
            _settings.DefaultLabelText = t;
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
            ShowSettings();
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
