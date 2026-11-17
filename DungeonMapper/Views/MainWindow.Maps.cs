using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// maps panel: a floating, non modal list of the campaign's maps over the
// bottom left of the map. the frame is fixed in MainWindow.axaml and styled
// by Styles/Ui.axaml, only the rows are built here.
public partial class MainWindow
{
    // the map whose name is being edited inline, and its box while it is open
    private MapEntry? _renamingMap;
    private TextBox? _mapRenameBox;

    // wired once from the constructor
    private void InitMaps()
    {
        BtnManageMaps.Click += (_, _) => ToggleMaps();
        MapsClose.Click += (_, _) => CloseMaps();
        MapsAdd.Click += (_, _) => { AddMap(); ShowMaps(); };
        MapsImport.Click += (_, _) => ImportMaps();
    }

    private void ToggleMaps()
    {
        if (MapsOverlay.IsVisible) { CloseMaps(); return; }
        CloseModals();
        ShowMaps();
    }

    private void CloseMaps()
    {
        FinishMapRename(true);
        RefreshMapList();
        MapsOverlay.IsVisible = false;
        FocusCanvas();
    }

    // called after edits, undo and redo so the counts follow. a rename in
    // progress is left alone so the typed name is not lost.
    private void RefreshMaps()
    {
        if (!MapsOverlay.IsVisible || _mapRenameBox is not null) return;
        ShowMaps();
    }

    // applies or drops the inline rename. does not rebuild the rows.
    private void FinishMapRename(bool commit)
    {
        if (_mapRenameBox is not { } box || _renamingMap is not { } entry) { _mapRenameBox = null; _renamingMap = null; return; }
        _mapRenameBox = null;
        _renamingMap = null;
        var v = box.Text?.Trim();
        int idx = _project.Maps.IndexOf(entry);
        if (!commit || idx < 0 || string.IsNullOrEmpty(v) || v == entry.Name) return;
        entry.Name = _project.UniqueName(v, idx);
        StatusText.Text = $"renamed to {entry.Name}";
        RefreshMapList();
    }

    private void StartMapRename(MapEntry entry)
    {
        FinishMapRename(true);
        _renamingMap = entry;
        ShowMaps();
    }

    // rebuilds the rows. a rename still open on another row is applied first.
    private void ShowMaps()
    {
        if (_mapRenameBox is not null) FinishMapRename(true);
        MapsOverlay.IsVisible = true;
        MapsRows.Children.Clear();
        for (int i = 0; i < _project.Maps.Count; i++)
            MapsRows.Children.Add(MapRow(i, _project.Maps[i]));
    }

    // one row: name, pawn and label counts, then rename, up, down, export and
    // delete. clicking the row switches to that map, double clicking the name
    // renames it.
    private Control MapRow(int idx, MapEntry entry)
    {
        bool active = idx == _project.ActiveIndex;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10, MinHeight = 32 };

        // the name cell fills its column so a double click anywhere on it renames
        var nameCell = new Panel { Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch };
        if (ReferenceEquals(_renamingMap, entry))
        {
            var box = new TextBox { Text = entry.Name, Classes = { "field" }, VerticalAlignment = VerticalAlignment.Center };
            _mapRenameBox = box;
            box.KeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Enter or Key.Escape)) return;
                FinishMapRename(e.Key == Key.Enter);
                ShowMaps();
                FocusCanvas();
                e.Handled = true;
            };
            // the rebuild that follows a finish also takes focus from the box,
            // so only the box that is still open reacts
            box.LostFocus += (_, _) =>
            {
                if (!ReferenceEquals(_mapRenameBox, box)) return;
                FinishMapRename(true);
                ShowMaps();
            };
            box.AttachedToVisualTree += (_, _) => { box.Focus(); box.SelectAll(); };
            nameCell.Children.Add(box);
        }
        else
        {
            var name = new TextBlock { Text = entry.Name, Classes = { "body" }, TextTrimming = TextTrimming.CharacterEllipsis };
            if (active) name.Classes.Add("strong");
            nameCell.Children.Add(name);
            ToolTip.SetTip(nameCell, active ? "current map, double click to rename" : "click to switch, double click to rename");
        }
        g.Children.Add(nameCell);

        var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        counts.Children.Add(Count(entry.Doc.Pawns.Count, "pawn"));
        counts.Children.Add(new TextBlock { Text = "·", Classes = { "count", "zero" } });
        counts.Children.Add(Count(entry.Doc.Labels.Count, "label"));
        Grid.SetColumn(counts, 1);
        g.Children.Add(counts);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(Icon("DMRename", "rename", () => StartMapRename(entry)));
        buttons.Children.Add(Icon("DMUp", "move up", () => MoveMapRow(idx, idx - 1), idx > 0));
        buttons.Children.Add(Icon("DMDown", "move down", () => MoveMapRow(idx, idx + 1), idx < _project.Maps.Count - 1));
        buttons.Children.Add(Icon("DMExport", "export this map to a file", () => ExportMap(entry)));
        bool canDelete = _project.Maps.Count > 1;
        Button? del = null;
        del = Icon("DMDelete", canDelete ? "delete" : "a campaign keeps at least one map", () =>
        {
            int pawns = entry.Doc.Pawns.Count, labels = entry.Doc.Labels.Count;
            ConfirmFlyout(del!,
                $"delete {entry.Name}? it has {Plural(pawns, "pawn")} and {Plural(labels, "label")}. this can't be undone.",
                () => DeleteMap(entry));
        }, canDelete);
        del.Classes.Add("danger");
        // a disabled button gets no pointer events, so the tooltip would never
        // show. this keeps it showing for the last map.
        ToolTip.SetShowOnDisabled(del, true);
        buttons.Children.Add(del);
        Grid.SetColumn(buttons, 2);
        g.Children.Add(buttons);

        var row = new Border { Child = g, Classes = { "row" } };
        if (active) row.Classes.Add("selected");
        // buttons and the rename box handle their own presses, so this only
        // sees presses on the name and counts. the rows are rebuilt by the
        // first press, so a double click is read from the click count instead
        // of the double tapped gesture, which needs the same element twice.
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2 && IsInside(e.Source, nameCell)) { StartMapRename(entry); e.Handled = true; return; }
            if (e.ClickCount != 1) return;
            bool wasRenaming = _mapRenameBox is not null;
            FinishMapRename(true);
            if (idx != _project.ActiveIndex) SwitchMap(idx);
            else if (wasRenaming) ShowMaps();
        };
        return row;

        static TextBlock Count(int n, string noun)
        {
            var tb = new TextBlock { Text = Plural(n, noun), Classes = { "count" } };
            if (n == 0) tb.Classes.Add("zero");
            return tb;
        }

        static Button Icon(string icon, string tip, Action click, bool enabled = true)
        {
            var b = new Button
            {
                Content = PngIcon(icon, 14),
                Classes = { "btn" },
                Width = 28, Height = 26, Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsEnabled = enabled,
            };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }

        static bool IsInside(object? source, Visual target)
        {
            for (var v = source as Visual; v is not null; v = Avalonia.VisualTree.VisualExtensions.GetVisualParent(v))
                if (ReferenceEquals(v, target)) return true;
            return false;
        }
    }

    public static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private void MoveMapRow(int from, int to)
    {
        _project.MoveMap(from, to);
        RefreshMapList();
        ShowMaps();
    }

    private void DeleteMap(MapEntry entry)
    {
        int idx = _project.Maps.IndexOf(entry);
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
        PawnViewsChanged();
        RefreshDoorPresets();
        RefreshToolInfo();
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
            MapSerializer.SaveMapFile(entry, path, ActiveLibrary, ActivePresets);
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
                // presets the file carries that this campaign lacks are added,
                // names it already has keep this campaign's look
                ActivePresets.MergeMissing(CampaignPresets.FromWire(imported.Presets));
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
            SyncCampaign();
            Canvas.InvalidateImageCache();
            SwitchMap(firstNew);
        }
        if (MapsOverlay.IsVisible || added > 0) ShowMaps();
        StatusText.Text = failed.Count == 0
            ? $"imported {added} map{(added == 1 ? "" : "s")}"
            : $"imported {added}, failed: {string.Join(", ", failed)}";
    }
}
