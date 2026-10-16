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
        Canvas.DoorConfigRequested += d => ShowDoorDialog(d);
        Canvas.StairConfigRequested += s => ShowStairDialog(s);
        Canvas.LabelConfigRequested += l => ShowLabelDialog(l);
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
            ToolMode.Wall => "wall: click an edge to add, click a wall to remove, drag to draw a line",
            ToolMode.Door => "door: click a wall edge to place, click a door to configure",
            ToolMode.Stair => "stair: click a cell to place, click a stair to configure, drag for a region",
            ToolMode.Label => "label: click a cell to place, click a label to edit",
            ToolMode.Erase => "erase: click-drag to remove cells, walls, doors, stairs, labels",
            _ => "",
        };
        Canvas.Refresh();
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

    private void ShowDoorDialog(Door door)
    {
        var dlg = new ConfigDialog("Door");
        dlg.AddText("Type", door.Type, t => door.Type = string.IsNullOrWhiteSpace(t) ? "standard" : t.Trim());
        dlg.AddChoice("State", new[] { "open", "closed", "locked" },
            door.State.ToString().ToLowerInvariant(), s =>
                door.State = Enum.Parse<DoorState>(s, ignoreCase: true));
        dlg.ShowDialog(this);
        _history.Commit(_doc);
        Canvas.Refresh();
    }

    private void ShowStairDialog(Stair stair)
    {
        var dlg = new ConfigDialog("Stair");
        dlg.AddChoice("Direction", new[] { "up", "down" },
            stair.Direction.ToString().ToLowerInvariant(), s =>
                stair.Direction = Enum.Parse<StairDirection>(s, ignoreCase: true));
        dlg.ShowDialog(this);
        _history.Commit(_doc);
        Canvas.Refresh();
    }

    private void ShowLabelDialog(MapLabel label)
    {
        var dlg = new ConfigDialog("Label");
        dlg.AddText("Text", label.Text, t => label.Text = t);
        dlg.ShowDialog(this);
        _history.Commit(_doc);
        Canvas.Refresh();
    }
}
