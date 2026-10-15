using DungeonMapper.Models;
using Avalonia.Controls;

namespace DungeonMapper.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Demo content so the canvas shows something on first run.
        var doc = new MapDocument();
        for (int x = 0; x < 4; x++)
            for (int y = 0; y < 2; y++)
                doc.Cells.Add(new CellKey(x, y));
        for (int x = 0; x < 4; x++)
        {
            doc.SetWall(new EdgeKey(new CellKey(x, 0), Side.North), true);
            doc.SetWall(new EdgeKey(new CellKey(x, 1), Side.South), true);
        }
        for (int y = 0; y < 2; y++)
        {
            doc.SetWall(new EdgeKey(new CellKey(3, y), Side.East), true);
            doc.SetWall(new EdgeKey(new CellKey(0, y), Side.West), true);
        }
        doc.SetWall(new EdgeKey(new CellKey(1, 0), Side.East), true);
        doc.TryPlaceDoor(new EdgeKey(new CellKey(1, 0), Side.North), out var d1);
        d1.Type = "portcullis"; d1.State = DoorState.Locked;
        doc.TryPlaceDoor(new EdgeKey(new CellKey(1, 0), Side.East), out var d2);
        doc.Stairs[new CellKey(3, 1)] = new Stair { Direction = StairDirection.Up };
        doc.Labels.Add(new MapLabel { Cell = new CellKey(0, 0), Text = "Entrance" });

        Canvas.Document = doc;
        Canvas.AutoFit();
    }
}
