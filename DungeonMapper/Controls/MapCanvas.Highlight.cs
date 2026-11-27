using Avalonia;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// the placed item whose view is open gets a dashed ring on the map, so it is
// clear which one the side panel changes (user: a door's view looked like the
// door tool's defaults). objects, labels and images have their handle box
// instead. set by the item views, cleared whenever the panel changes.
public sealed partial class MapCanvas
{
    /// the door being edited (any kind), or null
    public IDoorConfig? HighlightDoor { get; set; }
    /// the stair or pawn cell being edited, or null
    public CellKey? HighlightCell { get; set; }
    /// whether the highlighted cell is a pawn's (a ring) or a stair's (a square)
    public bool HighlightPawn { get; set; }

    public void ClearHighlight()
    {
        if (HighlightDoor is null && HighlightCell is null) return;
        HighlightDoor = null;
        HighlightCell = null;
        InvalidateVisual();
    }

    /// where the highlighted door sits, world units (null when it is gone)
    public Point? HighlightDoorCenter()
    {
        if (HighlightDoor is not { } d) return null;
        switch (Document.DoorSpotOf(d))
        {
            case EdgeKey e:
                var (a, b) = EdgeSegment(e);
                return new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            case VertexDoorKey v:
                return new Point(v.X, v.Y);
            case (AngledWall w, int i) when Document.AngledDoors.TryGetValue(w, out var list) && i < list.Count:
                double t = list[i].T;
                return new Point(w.Ax + (w.Bx - w.Ax) * t, w.Ay + (w.By - w.Ay) * t);
            default:
                return null;
        }
    }

    private void DrawHighlight(DrawingContext ctx)
    {
        var accent = new SolidColorBrush(Color.FromRgb(90, 127, 208));
        var pen = new Pen(accent, 2.5, new DashStyle(new double[] { 3, 2 }, 0));
        if (HighlightDoorCenter() is Point dc)
        {
            double r = _scale * 0.6;
            ctx.DrawEllipse(null, pen, WorldToScreen(dc.X, dc.Y), r, r);
        }
        if (HighlightCell is CellKey c && (HighlightPawn ? Document.Pawns.ContainsKey(c) : Document.Stairs.ContainsKey(c)))
        {
            if (HighlightPawn)
            {
                double r = _scale * 0.52;
                ctx.DrawEllipse(null, pen, WorldToScreen(c.X + 0.5, c.Y + 0.5), r, r);
            }
            else
            {
                var p = WorldToScreen(c.X, c.Y);
                double inset = _scale * 0.04;
                ctx.DrawRectangle(null, pen, new Rect(p.X + inset, p.Y + inset, _scale - 2 * inset, _scale - 2 * inset), 3, 3);
            }
        }
    }
}
