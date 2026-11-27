using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// the dashed box with resize handles on the open object, label or image (in
// its own tool and the select tool). a handle resizes; the dashed edge
// between the handles moves the item by whole cells, like PowerPoint (user).
// the move is the handle (0, 0), which Handles never gives.
public sealed partial class MapCanvas
{
    /// the handle that moves instead of resizing
    public static readonly (int sx, int sy) MoveHandle = (0, 0);
    /// how close to the dashed edge a press grabs it, screen px
    private const double EdgeGrab = 6;
    /// the world point the move started from
    private Point _boxGrab;

    /// what a press at screen hits on box: a resize handle, the dashed edge
    /// (MoveHandle), or nothing. inside a small box the edge band shrinks so
    /// a click in the middle still reaches the item.
    public static (int sx, int sy)? BoxHit(Rect box, Point screen)
    {
        foreach (var (h, hp) in Handles(box))
            if (Math.Abs(screen.X - hp.X) <= HandleSize && Math.Abs(screen.Y - hp.Y) <= HandleSize)
                return h;
        double inX = Math.Min(EdgeGrab, box.Width / 4), inY = Math.Min(EdgeGrab, box.Height / 4);
        bool alongX = screen.X >= box.Left - EdgeGrab && screen.X <= box.Right + EdgeGrab;
        bool alongY = screen.Y >= box.Top - EdgeGrab && screen.Y <= box.Bottom + EdgeGrab;
        bool onSide = alongY && (Near(screen.X, box.Left, inX, -1) || Near(screen.X, box.Right, inX, 1));
        bool onTop = alongX && (Near(screen.Y, box.Top, inY, -1) || Near(screen.Y, box.Bottom, inY, 1));
        return onSide || onTop ? MoveHandle : null;

        // within EdgeGrab outside the line (dir = which way is outside), inward
        // up to the band
        static bool Near(double v, double line, double inward, int dir)
        {
            double d = (v - line) * dir;
            return d <= EdgeGrab && d >= -inward;
        }
    }

    /// a move: the start rect shifted by the whole cells the pointer went
    /// from where it grabbed
    private (int x, int y, int w, int h) MovedRect((int x, int y, int w, int h) start, Point world) =>
        (start.x + (int)Math.Round(world.X - _boxGrab.X), start.y + (int)Math.Round(world.Y - _boxGrab.Y), start.w, start.h);

    private void DrawBoxHandles(DrawingContext ctx, Rect box)
    {
        var accent = new SolidColorBrush(Color.FromRgb(90, 127, 208));
        ctx.DrawRectangle(new Pen(accent, 2, new DashStyle(new double[] { 3, 2 }, 0)), box);
        foreach (var (_, hp) in Handles(box))
        {
            var hr = new Rect(hp.X - HandleSize / 2, hp.Y - HandleSize / 2, HandleSize, HandleSize);
            ctx.FillRectangle(Brushes.White, hr);
            ctx.DrawRectangle(new Pen(accent, 1.5), hr);
        }
    }

    /// the tools that show the open item's box: its own tool and select
    private bool BoxTool(ToolMode own) => Mode == own || Mode == ToolMode.Select;

    /// the select tool: a press on the open item's handles or edge
    private bool SelectBoxPressed(PointerPressedEventArgs e) =>
        LabelHandlePressed(e) || ObjectHandlePressed(e) || ImageHandlePressed(e);

    /// the select tool opened something other than a boxed item (or nothing):
    /// the box goes
    private void ClearBoxSelection()
    {
        SelectedLabel = null;
        SelectedObjectId = null;
        SelectedImageId = null;
    }

    /// the box under the cursor, for the pointer shape
    private Rect? HoverBox()
    {
        if (HandleLabel is { } l) return LabelBox(l);
        if (BoxTool(ToolMode.Object) && SelectedObject is { } o) return CellBox(o.X, o.Y, o.W, o.H);
        if (BoxTool(ToolMode.Image) && SelectedImage is { } im && (_remote is null || PlayerImages.Editable(im))) return CellBox(im.X, im.Y, im.W, im.H);
        return null;
    }

    private Rect CellBox(int x, int y, int w, int h)
    {
        var p = WorldToScreen(x, y);
        return new Rect(p.X, p.Y, w * _scale, h * _scale);
    }

    /// move arrows over the edge, resize arrows over a handle
    private void UpdateBoxCursor(Point screen)
    {
        var hit = HoverBox() is { } b ? BoxHit(b, screen) : null;
        StandardCursorType? want = hit switch
        {
            null => null,
            (0, 0) => StandardCursorType.SizeAll,
            (-1, -1) => StandardCursorType.TopLeftCorner,
            (1, 1) => StandardCursorType.BottomRightCorner,
            (1, -1) => StandardCursorType.TopRightCorner,
            (-1, 1) => StandardCursorType.BottomLeftCorner,
            (_, 0) => StandardCursorType.SizeWestEast,
            _ => StandardCursorType.SizeNorthSouth,
        };
        if (want == _boxCursor) return;
        _boxCursor = want;
        Cursor = want is { } c ? new Cursor(c) : null;
    }
    private StandardCursorType? _boxCursor;
}
