using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// resize handles on the label open in the label tool's config view, like the
// object tool's: the dragged sides snap to grid lines, at least one cell. the
// label changes live while dragging (an auto size font refits as it goes) and
// one undo step is taken on release when the range changed.
public sealed partial class MapCanvas
{
    /// the label whose config view is open, it gets the handles (label tool only)
    public MapLabel? SelectedLabel { get; set; }

    /// a label was resized by its handles (the config view rebuilds)
    public event Action<MapLabel>? LabelResized;

    private bool _labelResizing;
    private (int sx, int sy) _labelHandle;
    private (int x, int y, int w, int h) _labelStart;

    private MapLabel? HandleLabel =>
        Mode == ToolMode.Label && SelectedLabel is { } l && Document.Labels.Contains(l) ? l : null;

    private Rect LabelBox(MapLabel l)
    {
        var p = WorldToScreen(l.Cell.X, l.Cell.Y);
        return new Rect(p.X, p.Y, l.W * _scale, l.H * _scale);
    }

    private bool LabelHandlePressed(PointerPressedEventArgs e)
    {
        if (HandleLabel is not { } l) return false;
        var screen = e.GetPosition(this);
        foreach (var (h, hp) in Handles(LabelBox(l)))
            if (Math.Abs(screen.X - hp.X) <= HandleSize && Math.Abs(screen.Y - hp.Y) <= HandleSize)
            {
                _labelResizing = true;
                _labelHandle = h;
                _labelStart = (l.Cell.X, l.Cell.Y, l.W, l.H);
                e.Pointer.Capture(this);
                InvalidateVisual();
                return true;
            }
        return false;
    }

    private bool LabelResizeMoved(Point world)
    {
        if (!_labelResizing || HandleLabel is not { } l) return false;
        var (sx, sy, sw, sh) = _labelStart;
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
            Status($"label resized to {l.W * 5}x{l.H * 5}ft");
            Commit();
            LabelResized?.Invoke(l);
        }
        InvalidateVisual();
        return true;
    }

    private void DrawLabelHandles(DrawingContext ctx)
    {
        if (HandleLabel is not { } l) return;
        var box = LabelBox(l);
        if (_labelResizing) DrawSizeRuler(ctx, l.W, l.H, new Point(box.Center.X, box.Top));
        var accent = new SolidColorBrush(Color.FromRgb(90, 127, 208));
        ctx.DrawRectangle(new Pen(accent, 2, new DashStyle(new double[] { 3, 2 }, 0)), box);
        foreach (var (_, hp) in Handles(box))
        {
            var hr = new Rect(hp.X - HandleSize / 2, hp.Y - HandleSize / 2, HandleSize, HandleSize);
            ctx.FillRectangle(Brushes.White, hr);
            ctx.DrawRectangle(new Pen(accent, 1.5), hr);
        }
    }
}
