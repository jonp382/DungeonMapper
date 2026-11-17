using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DungeonMapper.Controls;
using DungeonMapper.Models;
using Part = DungeonMapper.Models.CampaignPresets.Part;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// import and export for the campaign presets: door types (door presets
// modal), pawn statuses and affiliation colors (their pawn management tabs).
// each list has its own file. import is a checklist that only adds names the
// campaign lacks, it never changes one it already has.
public partial class MainWindow
{
    // where cancel and esc go from an open preset import picker
    private Action? _presetImportBack;

    private static string PartLabel(Part part) => part switch
    {
        Part.Doors => "door types",
        Part.Statuses => "status styles",
        _ => "affiliations",
    };

    private static string PartFile(Part part) => part switch
    {
        Part.Doors => "Door_presets",
        Part.Statuses => "Pawn_statuses",
        _ => "Affiliations",
    };

    private async void ExportPresets(Part part)
    {
        if (part == Part.Doors) ApplyDoorRename();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"{PartFile(part)}_{Stamp()}.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } } },
        });
        if (file is null) return;
        var path = file.Path.LocalPath;
        int n = part switch
        {
            Part.Doors => ActivePresets.DoorTypes.Count,
            Part.Statuses => ActivePresets.PawnStatuses.Count,
            _ => ActivePresets.AffiliationColors.Count,
        };
        try
        {
            System.IO.File.WriteAllText(path, ActivePresets.ExportJson(part));
            StatusText.Text = $"exported {n} {PartLabel(part)} to {path}";
        }
        catch (Exception ex) { StatusText.Text = $"export failed: {ex.Message}"; }
    }

    // import and export all under the affiliations and status styles tabs
    private Control PresetTabFooter(Part part)
    {
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var imp = Btn("import...");
        ToolTip.SetTip(imp, $"add {PartLabel(part)} from a presets, campaign or map file. names you already have are never changed.");
        imp.Click += (_, _) => ShowPresetImport(part);
        foot.Children.Add(imp);
        var exp = Btn("export all");
        ToolTip.SetTip(exp, $"save this campaign's {PartLabel(part)} to a file");
        exp.Click += (_, _) => ExportPresets(part);
        foot.Children.Add(exp);
        return new Border
        {
            Child = foot,
            Classes = { "footer" },
            Padding = new Thickness(14, 8, 14, 12),
        };
    }

    private sealed record PresetItem(string Name, PawnLibrary.ImportKind Kind, Func<Control> Look, object Value);

    private static Control DoorLook(DoorType t) =>
        new Border { Width = 30, Height = 8, CornerRadius = new CornerRadius(2), Background = Rgb((t.R, t.G, t.B)), VerticalAlignment = VerticalAlignment.Center };

    private static Control StatusLook(PawnStatus s)
    {
        var g = new Grid { Width = 18, Height = 18 };
        g.Children.Add(new Ellipse { Width = 18, Height = 18, Classes = { "look" } });
        if (s.Thickness > 0) g.Children.Add(new Ellipse { Width = 18, Height = 18, Stroke = Rgb((s.R, s.G, s.B)), StrokeThickness = Math.Min(s.Thickness, 4) });
        return g;
    }

    // back to the modal or tab the picker was opened from
    private void PresetImportReturn(Part part)
    {
        _presetImportBack = null;
        if (part == Part.Doors)
        {
            ModalOverlay.IsVisible = false;
            _modalKind = null;
            ShowDoorPresets();
        }
        else
        {
            _manageTab = part == Part.Statuses ? "statuses" : "affiliations";
            ShowPawnManagement();
        }
    }

    private async void ShowPresetImport(Part part)
    {
        if (part == Part.Doors) ApplyDoorRename();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("JSON files") { Patterns = new[] { "*.json" } } },
        });
        if (files.Count == 0) return;
        var path = files[0].Path.LocalPath;
        CampaignPresets incoming;
        try { incoming = CampaignPresets.ReadImport(System.IO.File.ReadAllText(path)); }
        catch (Exception ex) { StatusText.Text = $"import failed: {ex.Message}"; return; }

        var p = ActivePresets;
        List<PresetItem> items = part switch
        {
            Part.Doors => incoming.DoorTypes.Select(t => new PresetItem(t.Name, p.Classify(t), () => DoorLook(t), t)).ToList(),
            Part.Statuses => incoming.PawnStatuses.Select(s => new PresetItem(s.Name, p.Classify(s), () => StatusLook(s), s)).ToList(),
            _ => incoming.AffiliationColors.Select(a => new PresetItem(a.Name, p.Classify(a), () => AffDot(a, 18), a)).ToList(),
        };
        if (items.Count == 0) { StatusText.Text = $"that file has no {PartLabel(part)} in it"; return; }

        // only names the campaign lacks can be imported
        var fresh = items.Where(i => i.Kind == PawnLibrary.ImportKind.New).ToList();
        var picked = new HashSet<PresetItem>(fresh);

        if (part == Part.Doors) DoorPresetsOverlay.IsVisible = false;
        _modalKind = "preset-import";
        _presetImportBack = () => PresetImportReturn(part);
        var root = new DockPanel { Margin = new Thickness(12) };

        Grid RowGrid() => new() { ColumnDefinitions = new ColumnDefinitions("22,36,*,140"), ColumnSpacing = 8 };
        var colHead = RowGrid();
        var all = new CheckBox { MinWidth = 0, Padding = new Thickness(0), IsEnabled = fresh.Count > 0 };
        ToolTip.SetTip(all, "select all new");
        colHead.Children.Add(all);
        AddCell(colHead, Tx("look", "colhead"), 1);
        AddCell(colHead, Tx("name", "colhead"), 2);
        var headB = new Border { Child = colHead, Classes = { "colhead" }, Padding = new Thickness(6, 0, 6, 4), Margin = new Thickness(0) };
        DockPanel.SetDock(headB, Dock.Top);
        root.Children.Add(headB);

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 6 };
        var cancel = Btn("cancel");
        cancel.Background = Brushes.Transparent;
        buttons.Children.Add(cancel);
        var importAll = Btn($"import all new ({fresh.Count})");
        importAll.IsEnabled = fresh.Count > 0;
        AddCell(buttons, importAll, 2);
        var importSel = Btn("", primary: true);
        AddCell(buttons, importSel, 3);
        var btnBorder = new Border { Child = buttons, Classes = { "footer" }, Padding = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(btnBorder, Dock.Bottom);
        root.Children.Add(btnBorder);
        var note = new TextBlock
        {
            Text = "only names this campaign does not have yet can be imported. a name it already has keeps the campaign's look, even when the file's look is different.",
            Classes = { "small" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8),
        };
        DockPanel.SetDock(note, Dock.Bottom);
        root.Children.Add(note);

        var list = new StackPanel { Spacing = 1, Margin = new Thickness(0, 2, 0, 0) };
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        void Rebuild()
        {
            list.Children.Clear();
            foreach (var it in items)
            {
                bool can = it.Kind == PawnLibrary.ImportKind.New;
                var g = RowGrid();
                var cb = new CheckBox { IsChecked = picked.Contains(it), IsEnabled = can, MinWidth = 0, Padding = new Thickness(0) };
                cb.Click += (_, _) => { if (cb.IsChecked == true) picked.Add(it); else picked.Remove(it); Rebuild(); };
                g.Children.Add(cb);
                var look = it.Look();
                look.Opacity = can ? 1 : 0.45;
                look.HorizontalAlignment = HorizontalAlignment.Left;
                AddCell(g, look, 1);
                var nameTb = Tx(it.Name, "body");
                nameTb.Classes.Set("faint", !can);
                nameTb.TextTrimming = TextTrimming.CharacterEllipsis;
                AddCell(g, nameTb, 2);
                var (nt, nc) = it.Kind switch
                {
                    PawnLibrary.ImportKind.Have => ("already in campaign", "faint"),
                    PawnLibrary.ImportKind.NewLook => ("same name, new look", "warn"),
                    _ => ("new", "ok"),
                };
                var tag = Tx(nt, nc);
                tag.FontSize = 10;
                tag.HorizontalAlignment = HorizontalAlignment.Right;
                if (it.Kind == PawnLibrary.ImportKind.NewLook)
                    ToolTip.SetTip(tag, "skipped. the campaign keeps its own look for this name. rename one of them to bring it in.");
                AddCell(g, tag, 3);
                var row = new Border { Child = g, Classes = { "row", "dense" } };
                row.Classes.Set("inert", !can);
                if (can) row.PointerPressed += (_, _) => { if (!picked.Add(it)) picked.Remove(it); Rebuild(); };
                list.Children.Add(row);
            }
            all.IsChecked = fresh.Count > 0 && fresh.All(picked.Contains);
            importSel.Content = $"import selected ({picked.Count})";
            importSel.IsEnabled = picked.Count > 0;
        }

        void DoImport(List<PresetItem> chosen)
        {
            int added = part switch
            {
                Part.Doors => ActivePresets.AddMissing(chosen.Select(i => (DoorType)i.Value)),
                Part.Statuses => ActivePresets.AddMissing(chosen.Select(i => (PawnStatus)i.Value)),
                _ => ActivePresets.AddMissing(chosen.Select(i => (AffiliationColor)i.Value)),
            };
            Canvas.Refresh();
            if (part == Part.Doors && Canvas.Mode == ToolMode.Door) PopulatePanel(ToolMode.Door);
            if (part != Part.Doors) PawnsChanged();
            PresetImportReturn(part);
            StatusText.Text = $"imported {added} {PartLabel(part)}";
        }

        all.Click += (_, _) =>
        {
            foreach (var it in fresh) { if (all.IsChecked == true) picked.Add(it); else picked.Remove(it); }
            Rebuild();
        };
        cancel.Click += (_, _) => PresetImportReturn(part);
        importAll.Click += (_, _) => DoImport(fresh);
        importSel.Click += (_, _) => DoImport(fresh.Where(picked.Contains).ToList());
        Rebuild();

        ShowModal(560, 600, $"Import {PartLabel(part)}", $"from {System.IO.Path.GetFileName(path)} · {items.Count} in file · {fresh.Count} new", root);
    }
}
