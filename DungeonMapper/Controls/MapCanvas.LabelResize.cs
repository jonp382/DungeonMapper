using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// resize handles on the label open in the label (or select) tool's config
// view, like the object tool's: the dragged sides snap to grid lines, at
// least one cell; the dashed edge moves it (MapCanvas.BoxHandles.cs). the
// label changes live while dragging (an auto size font refits as it goes) and
// one undo step is taken on release when the range changed.
public sealed partial class MapCanvas
{
    /// the label whose config view is open, it gets the handles (label and
    /// select tools)
    public MapLabel? SelectedLabel { get; set; }

    /// a label was resized by its handles (the config view rebuilds)
    public event Action<MapLabel>? LabelResized;

    private bool _labelResizing;
    private (int sx, int sy) _labelHandle;
    private (int x, int y, int w, int h) _labelStart;

    private MapLabel? HandleLabel =>
        BoxTool(ToolMode.Label) && SelectedLabel is { } l && Document.Labels.Contains(l) ? l : null;

    private Rect LabelBox(MapLabel l)
    {
        var p = WorldToScreen(l.Cell.X, l.Cell.Y);
        return new Rect(p.X, p.Y, l.W * _scale, l.H * _scale);
    }

    private bool LabelHandlePressed(PointerPressedEventArgs e)
    {
        if (HandleLabel is not { } l) return false;
        if (BoxHit(LabelBox(l), e.GetPosition(this)) is not { } h) return false;
        _labelResizing = true;
        _labelHandle = h;
        _labelStart = (l.Cell.X, l.Cell.Y, l.W, l.H);
        _boxGrab = ScreenToWorld(e.GetPosition(this));
        e.Pointer.Capture(this);
        InvalidateVisual();
        return true;
    }

    private bool LabelResizeMoved(Point world)
    {
        if (!_labelResizing || HandleLabel is not { } l) return false;
        var (sx, sy, sw, sh) = _labelStart;
        if (_labelHandle == MoveHandle)
        {
            var (mx, my, _, _) = MovedRect(_labelStart, world);
            if (l.Cell != new CellKey(mx, my)) { l.Cell = new CellKey(mx, my); InvalidateVisual(); }
            return true;
        }
        int left = sx, top = sy, right = sx + sw, bottom = sy + sh;
        int gx = (int)Math.Round(world.X), gy = (int)Math.Round(world.Y);
        if (_labelHandle.sx < 0) left = Math.Min(gx, right - 1);
        if (_labelHandle.sx > 0) right = Math.Max(gx, left + 1);
        if (_labelHandle.sy < 0) top = Math.Min(gy, bottom - 1);
        if (_labelHandle.sy > 0) bottom = Math.Max(gy, top + 1);
        if ((l.Cell.X, l.Cell.Y, l.W, l.H) != (left, top, right - left, bottom - top))
        {
            l.Cell = new CellKey(left, top);
            l.W = right - left;
            l.H = bottom - top;
            InvalidateVisual();
        }
        return true;
    }

    private bool LabelResizeReleased(PointerReleasedEventArgs e)
    {
        if (!_labelResizing) return false;
        _labelResizing = false;
        e.Pointer.Capture(null);
        if (HandleLabel is { } l && (l.Cell.X, l.Cell.Y, l.W, l.H) != _labelStart)
        {
            _bodyClick = null;
            Status(_labelHandle == MoveHandle ? $"label moved to {l.Cell.X},{l.Cell.Y}" : $"label resized to {l.W * 5}x{l.H * 5}ft");
            Commit();
            LabelResized?.Invoke(l);
        }
        else RunBodyClick();
        InvalidateVisual();
        return true;
    }

    private void DrawLabelHandles(DrawingContext ctx)
    {
        if (HandleLabel is not { } l) return;
        var box = LabelBox(l);
        if (_labelResizing && _labelHandle != MoveHandle) DrawSizeRuler(ctx, l.W, l.H, new Point(box.Center.X, box.Top));
        DrawBoxHandles(ctx, box);
    }
}
