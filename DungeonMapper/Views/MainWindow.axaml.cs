using System;
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

    public MainWindow()
    {
        InitializeComponent();

        Canvas.Document = _doc;
        _history.Initialize(_doc);

        // Tool buttons
        ToolSelect.Click += (_, _) => SetTool(ToolMode.Select);
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

    private void ShowDoorConfig(Door door)
    {
        ToolPanel.Clear("Door");
        ToolPanel.AddText("Type", door.Type, t =>
        {
            door.Type = string.IsNullOrWhiteSpace(t) ? "standard" : t.Trim();
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
