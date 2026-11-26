using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using DungeonMapper.Controls;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// door presets modal. the frame and the editor are fixed in MainWindow.axaml
// and styled by Styles/Ui.axaml. only the rows are built here, and they use
// the style classes (row, count, tag) instead of colors.
public partial class MainWindow
{
    private const string StandardDoor = "standard";
    private DoorType? _doorSel;

    // wired once from the constructor
    private void InitDoorPresets()
    {
        DoorPresetsClose.Click += (_, _) => CloseDoorPresets();
        DoorAdd.Click += (_, _) => AddDoorType(new DoorType(UniqueDoorName("new type"), 245, 220, 60));
        DoorDuplicate.Click += (_, _) =>
        {
            if (_doorSel is { } t) AddDoorType(new DoorType(UniqueDoorName(t.Name + " copy"), t.R, t.G, t.B));
        };
        DoorMakeDefault.Click += (_, _) =>
        {
            if (_doorSel is null) return;
            Canvas.DefaultDoorType = _doorSel.Name;
            DoorSettingsChanged();
        };
        DoorDelete.Click += (_, _) => DeleteSelectedDoorType();
        DoorImport.Click += (_, _) => ShowPresetImport(CampaignPresets.Part.Doors);
        DoorExport.Click += (_, _) => ExportPresets(CampaignPresets.Part.Doors);

        // rename is applied on enter or when the box loses focus, not per key,
        // so half typed names never reach the doors
        DoorName.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { ApplyDoorRename(); e.Handled = true; }
            else if (e.Key == Key.Escape) { RefreshDoorEditor(); FocusCanvas(); e.Handled = true; }
        };
        DoorName.LostFocus += (_, _) => ApplyDoorRename();
    }

    private void ShowDoorPresets()
    {
        FlushSettings();
        if (_doorSel is null || !ActivePresets.DoorTypes.Contains(_doorSel))
            _doorSel = ActivePresets.DoorTypes.FirstOrDefault();
        DoorPresetsOverlay.IsVisible = true;
        RefreshDoorPresets();
    }

    private void CloseDoorPresets()
    {
        ApplyDoorRename();
        FlushSettings();
        DoorPresetsOverlay.IsVisible = false;
        FocusCanvas();
    }

    // called after undo, redo, map switches and door edits so the counts follow
    private void RefreshDoorPresets()
    {
        if (!DoorPresetsOverlay.IsVisible) return;
        RefreshDoorRows();
        RefreshDoorEditor();
    }

    private (int map, int campaign) DoorCounts(string name)
    {
        int map = _doc.AllDoors().Count(d => d.Type == name);
        int campaign = _project.Maps.Sum(m => m.Doc.AllDoors().Count(d => d.Type == name));
        return (map, campaign);
    }

    private void RefreshDoorRows()
    {
        DoorRows.Children.Clear();
        int totalMap = 0, totalCampaign = 0;
        foreach (var t in ActivePresets.DoorTypes)
        {
            var (map, campaign) = DoorCounts(t.Name);
            totalMap += map;
            totalCampaign += campaign;
            DoorRows.Children.Add(DoorRow(t, map, campaign));
        }
        int n = ActivePresets.DoorTypes.Count;
        DoorFooter.Text = $"{n} door type{(n == 1 ? "" : "s")} · {totalMap} doors on this map · {totalCampaign} in the campaign";
    }

    private Control DoorRow(DoorType t, int map, int campaign)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("30,*,60,70") };
        var swatch = new Border
        {
            Width = 22, Height = 8, CornerRadius = new CornerRadius(2),
            Background = Rgb((t.R, t.G, t.B)),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        };
        g.Children.Add(swatch);

        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        name.Children.Add(new TextBlock { Text = t.Name, Classes = { "body" }, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });
        if (t.Name == Canvas.DefaultDoorType) name.Children.Add(new TextBlock { Text = "DEFAULT", Classes = { "tag" } });
        Grid.SetColumn(name, 1);
        g.Children.Add(name);

        g.Children.Add(Count(map, 2));
        g.Children.Add(Count(campaign, 3));

        var row = new Border { Child = g, Classes = { "row" } };
        if (t == _doorSel) row.Classes.Add("selected");
        row.PointerPressed += (_, _) =>
        {
            ApplyDoorRename();
            _doorSel = t;
            RefreshDoorPresets();
        };
        return row;

        static TextBlock Count(int n, int col)
        {
            var tb = new TextBlock { Text = $"×{n}", Classes = { "count" } };
            if (n == 0) tb.Classes.Add("zero");
            Grid.SetColumn(tb, col);
            return tb;
        }
    }

    private void RefreshDoorEditor()
    {
        var t = _doorSel;
        DoorName.Classes.Remove("invalid");
        DoorNameError.IsVisible = false;
        if (t is null) return;

        DoorPreview.Background = Rgb((t.R, t.G, t.B));
        DoorPreviewName.Text = t.Name;
        DoorName.Text = t.Name;
        bool standard = t.Name == StandardDoor;
        DoorName.IsEnabled = !standard;

        DoorColorHost.Content = ColorButton((t.R, t.G, t.B), c =>
        {
            t.R = c.r; t.G = c.g; t.B = c.b;
            DoorPreview.Background = Rgb(c);
            Canvas.Refresh();
            RefreshDoorRows();
        });

        bool isDefault = t.Name == Canvas.DefaultDoorType;
        DoorMakeDefault.IsEnabled = !isDefault;
        DoorMakeDefault.Content = isDefault ? "this is the default" : "set as default";
        DoorDefaultNote.IsVisible = isDefault;

        var (map, campaign) = DoorCounts(t.Name);
        DoorUsage.Text = campaign == 0
            ? "no doors use this type yet."
            : $"{map} door{(map == 1 ? "" : "s")} on this map, {campaign} across the campaign.";

        DoorDelete.IsEnabled = !standard;
        DoorDeleteNote.Text = standard
            ? "standard is the fallback type and cannot be renamed or deleted."
            : campaign > 0 ? $"deleting turns its {campaign} door{(campaign == 1 ? "" : "s")} back into standard." : "";
        DoorDeleteNote.IsVisible = DoorDeleteNote.Text != "";
    }

    private string UniqueDoorName(string baseName)
    {
        string name = baseName;
        for (int i = 2; ActivePresets.DoorTypes.Any(t => t.Name == name); i++) name = $"{baseName} {i}";
        return name;
    }

    private void AddDoorType(DoorType t)
    {
        ApplyDoorRename();
        ActivePresets.DoorTypes.Add(t);
        _doorSel = t;
        DoorSettingsChanged();
        DoorName.Focus();
        DoorName.SelectAll();
    }

    // door types changed: redraw and rebuild the door tool panel whose default
    // type dropdown lists the names. the types save with the campaign
    private void DoorSettingsChanged()
    {
        Canvas.Refresh();
        if (Canvas.Mode == ToolMode.Door) PopulatePanel(ToolMode.Door);
        RefreshDoorPresets();
    }

    // renames the selected type and every door that uses it on every map,
    // including the undo snapshots, so undo never brings back the old name
    private void ApplyDoorRename()
    {
        var t = _doorSel;
        if (t is null || !DoorPresetsOverlay.IsVisible) return;
        string from = t.Name, to = (DoorName.Text ?? "").Trim();
        if (to == from) return;

        string? error = to.Length == 0 ? "a door type needs a name."
            : ActivePresets.DoorTypes.Any(o => o != t && o.Name == to) ? $"there is already a type called '{to}'."
            : null;
        if (error is not null)
        {
            DoorName.Classes.Add("invalid");
            DoorNameError.Text = error;
            DoorNameError.IsVisible = true;
            return;
        }

        foreach (var m in _project.Maps)
        {
            m.Doc.RetypeDoors(from, to);
            HistoryFor(m).Rewrite(d => d.RetypeDoors(from, to));
        }
        t.Name = to;
        if (Canvas.DefaultDoorType == from) Canvas.DefaultDoorType = to;
        SetStatus($"renamed door type '{from}' to '{to}'");
        DoorSettingsChanged();
    }

    // standard cannot go. doors of the deleted type become standard on every
    // map, one undo step per affected map
    private void DeleteSelectedDoorType()
    {
        var t = _doorSel;
        if (t is null || t.Name == StandardDoor) return;
        var (_, campaign) = DoorCounts(t.Name);

        void doDelete()
        {
            foreach (var m in _project.Maps)
                if (m.Doc.RetypeDoors(t.Name, StandardDoor) > 0)
                    HistoryFor(m).Commit(m.Doc);
            if (Canvas.DefaultDoorType == t.Name) Canvas.DefaultDoorType = StandardDoor;
            int i = ActivePresets.DoorTypes.IndexOf(t);
            ActivePresets.DoorTypes.Remove(t);
            _doorSel = ActivePresets.DoorTypes.Count == 0 ? null : ActivePresets.DoorTypes[Math.Clamp(i, 0, ActivePresets.DoorTypes.Count - 1)];
            SetStatus($"deleted door type '{t.Name}'");
            DoorSettingsChanged();
        }

        if (campaign == 0) doDelete();
        else ConfirmFlyout(DoorDelete, $"delete '{t.Name}'? {campaign} door{(campaign == 1 ? "" : "s")} across the campaign become standard.", doDelete);
    }
}
