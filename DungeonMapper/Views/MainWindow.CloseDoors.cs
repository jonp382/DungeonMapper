using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DungeonMapper.Models;

using static DungeonMapper.Views.Ui;

namespace DungeonMapper.Views;

// door tool "close open doors" modal: pick one door type, every open door of
// that type on the current map becomes closed. locked and closed doors are
// left alone. one undo step.
public partial class MainWindow
{
    private void ShowCloseDoors(string type)
    {
        var types = ActivePresets.DoorTypes.GroupBy(t => t.Name).Select(g => g.First()).ToList();
        if (!types.Any(t => t.Name == type) && types.Count > 0) type = types[0].Name;
        int open = _doc.AllDoors().Count(d => d.Type == type && d.State == DoorState.Open);

        var dock = new DockPanel { Margin = new(16, 12, 16, 16) };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        var go = Btn("close all", primary: true);
        go.IsEnabled = open > 0;
        var cancel = Btn("cancel");
        string picked = type;
        go.Click += (_, _) => CloseOpenDoors(picked);
        cancel.Click += (_, _) => { CloseModals(); FocusCanvas(); };
        row.Children.Add(go);
        row.Children.Add(cancel);
        DockPanel.SetDock(row, Dock.Bottom);
        dock.Children.Add(row);

        var info = new Border
        {
            Classes = { "infobox" },
            Margin = new(0, 10, 0, 0),
            Child = new TextBlock
            {
                Text = open == 0 ? $"no open {type} doors on this map." : $"{Plural(open, "open door")} of this type on this map.",
                Classes = { "body" },
                TextWrapping = TextWrapping.Wrap,
            },
        };
        DockPanel.SetDock(info, Dock.Bottom);
        dock.Children.Add(info);

        var top = new StackPanel { Spacing = 4 };
        top.Children.Add(new TextBlock
        {
            Text = "open doors of the chosen type become closed. locked doors are not touched.",
            Classes = { "hint" },
            TextWrapping = TextWrapping.Wrap,
        });
        top.Children.Add(FieldLabel("door type"));
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);

        // many presets wrap onto many rows, so the chips scroll
        var chips = new WrapPanel();
        foreach (var t in types)
        {
            var name = t.Name;
            chips.Children.Add(Chip(name, name == type,
                new Ellipse { Width = 9, Height = 9, Fill = Rgb((t.R, t.G, t.B)) },
                () => ShowCloseDoors(name)));
        }
        dock.Children.Add(new ScrollViewer { Content = chips, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

        ShowModal(440, 340, "Close open doors", "on this map", dock);
        _modalKind = "close-doors";
    }

    private void CloseOpenDoors(string type)
    {
        int n = _doc.CloseOpenDoors(type);
        CloseModals();
        if (n > 0)
        {
            ActiveHistory.Commit(_doc); // one undo step for every door closed
            Canvas.Refresh();
            RefreshMaps();
            RefreshToolInfo();
        }
        SetStatus(n == 0 ? $"no open {type} doors on this map" : $"closed {Plural(n, $"{type} door")}", n == 0 ? StatusKind.Warning : StatusKind.Info);
        FocusCanvas();
    }
}
