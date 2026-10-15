using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

/// Custom control that renders a MapDocument. World units = cells (5ft).
/// screen = (world - offset) * scale. Right-drag pans, wheel zooms (cursor-anchored).
/// High-contrast palette for laptop + TV display.
public sealed class MapCanvas : Control
{
    public MapDocument Document { get; set; } = new();

    private Point _offset = new(-1, -1); // world coord at screen origin
    private double _scale = 40;          // pixels per cell

    private const double MinScale = 8, MaxScale = 200;

    // High-contrast palette (dark bg, bright fills)
    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(20, 20, 25));
    private static readonly IBrush CellFill = new SolidColorBrush(Color.FromRgb(60, 75, 105));
    private static readonly IBrush GridLine = new SolidColorBrush(Color.FromRgb(45, 52, 66));
    private static readonly IPen WallPen = new Pen(new SolidColorBrush(Color.FromRgb(235, 235, 240)), 3);
    private static readonly IBrush StairUp = new SolidColorBrush(Color.FromRgb(120, 220, 120));
    private static readonly IBrush StairDown = new SolidColorBrush(Color.FromRgb(230, 160, 80));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(250, 250, 250));

    // Door color per type; unknown types fall back to standard yellow.
    private static IBrush DoorBrush(string type) => type.ToLowerInvariant() switch
    {
        "standard" => new SolidColorBrush(Color.FromRgb(245, 220, 60)),
        "portcullis" => new SolidColorBrush(Color.FromRgb(180, 180, 190)),
        "bricked" => new SolidColorBrush(Color.FromRgb(160, 100, 60)),
        "locked" => new SolidColorBrush(Color.FromRgb(220, 80, 80)),
        "magical" => new SolidColorBrush(Color.FromRgb(180, 120, 240)),
        _ => new SolidColorBrush(Color.FromRgb(245, 220, 60)),
    };

    public void AutoFit()
    {
        var b = Document.ContentBounds();
        if (b is null) { _offset = new Point(-1, -1); _scale = 40; InvalidateVisual(); return; }
        var (minX, minY, maxX, maxY) = b.Value;
        double w = maxX - minX + 1, h = maxY - minY + 1;
        double availW = Bounds.Width > 0 ? Bounds.Width : 800;
        double availH = Bounds.Height > 0 ? Bounds.Height : 600;
        _scale = Math.Clamp(Math.Min(availW / (w + 2), availH / (h + 2)), MinScale, MaxScale);
        _offset = new Point(minX - 1, minY - 1);
        InvalidateVisual();
    }

    private Point WorldToScreen(double wx, double wy)
        => new((wx - _offset.X) * _scale, (wy - _offset.Y) * _scale);

    private Point ScreenToWorld(Point p)
        => new(p.X / _scale + _offset.X, p.Y / _scale + _offset.Y);

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Background, new Rect(Bounds.Size));
        if (Document.Cells.Count == 0) return;

        // Visible world window, for culling
        var topLeft = ScreenToWorld(new Point(0, 0));
        var bottomRight = ScreenToWorld(new Point(Bounds.Width, Bounds.Height));

        // Cell fills + faint grid
        foreach (var c in Document.Cells)
        {
            if (c.X + 1 < topLeft.X || c.X > bottomRight.X || c.Y + 1 < topLeft.Y || c.Y > bottomRight.Y) continue;
            var p0 = WorldToScreen(c.X, c.Y);
            var rect = new Rect(p0.X, p0.Y, _scale, _scale);
            ctx.FillRectangle(CellFill, rect);
            ctx.DrawLine(new Pen(GridLine, 1), rect.TopLeft, rect.BottomLeft);
            ctx.DrawLine(new Pen(GridLine, 1), rect.TopLeft, rect.TopRight);
        }

        // Stairs: fill tint + arrow glyph
        foreach (var (c, s) in Document.Stairs)
        {
            var p0 = WorldToScreen(c.X, c.Y);
            var rect = new Rect(p0.X, p0.Y, _scale, _scale);
            var brush = s.Direction == StairDirection.Up ? StairUp : StairDown;
            var col = ((SolidColorBrush)brush).Color;
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(90, col.R, col.G, col.B)), rect);
            var center = new Point(p0.X + _scale / 2, p0.Y + _scale / 2);
            double a = _scale * 0.28;
            // simple triangle via path
            var tip = s.Direction == StairDirection.Up
                ? new Point(center.X, center.Y - a) : new Point(center.X, center.Y + a);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(tip, isFilled: true);
                g.LineTo(new Point(center.X - a, center.Y + a));
                g.LineTo(new Point(center.X + a, center.Y + a));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(brush, null, geo);
        }

        // Walls, with door symbol where a door exists
        foreach (var (e, has) in Document.Walls)
        {
            if (!has) continue;
            var seg = EdgeSegment(e);
            var a = WorldToScreen(seg.a.X, seg.a.Y);
            var b = WorldToScreen(seg.b.X, seg.b.Y);
            ctx.DrawLine(WallPen, a, b);
            if (Document.Doors.TryGetValue(e, out var door))
                DrawDoor(ctx, e, a, b, door);
        }

        // Labels centered on their cell
        foreach (var l in Document.Labels)
        {
            var p0 = WorldToScreen(l.Cell.X, l.Cell.Y);
            var ft = new FormattedText(l.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Inter"), Math.Max(10, _scale * 0.35), LabelBrush);
            var center = new Point(p0.X + _scale / 2, p0.Y + _scale / 2);
            ctx.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
        }
    }

    /// Edge endpoints in world coords for a normalized EdgeKey.
    private static (Point a, Point b) EdgeSegment(EdgeKey e)
    {
        var (x, y) = (e.Cell.X, e.Cell.Y);
        return e.Side switch
        {
            Side.North => (new Point(x, y), new Point(x + 1, y)),
            Side.East => (new Point(x + 1, y), new Point(x + 1, y + 1)),
            // Normalized keys only store North/East, but accept all sides for safety.
            Side.South => (new Point(x, y + 1), new Point(x + 1, y + 1)),
            Side.West => (new Point(x, y), new Point(x, y + 1)),
            _ => (new Point(x, y), new Point(x, y)),
        };
    }

    private void DrawDoor(DrawingContext ctx, EdgeKey e, Point a, Point b, Door door)
    {
        var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        bool horizontal = Math.Abs(b.Y - a.Y) < 0.001;
        double gap = _scale * 0.45; // wall gap where the door sits
        var gapRect = horizontal
            ? new Rect(mid.X - gap / 2, mid.Y - 1.5, gap, 3)
            : new Rect(mid.X - 1.5, mid.Y - gap / 2, 3, gap);
        ctx.FillRectangle(Background, gapRect); // erase wall segment for the gap
        var brush = DoorBrush(door.Type);
        double s2 = Math.Max(4, _scale * 0.18);
        // symbol: square for closed, diamond for open, square+dot for locked
        if (door.State == DoorState.Open)
        {
            var geo = new StreamGeometry();
            using var g = geo.Open();
            g.BeginFigure(new Point(mid.X, mid.Y - s2), true);
            g.LineTo(new Point(mid.X + s2, mid.Y));
            g.LineTo(new Point(mid.X, mid.Y + s2));
            g.LineTo(new Point(mid.X - s2, mid.Y));
            g.EndFigure(true);
            ctx.DrawGeometry(brush, null, geo);
        }
        else
        {
            ctx.FillRectangle(brush, new Rect(mid.X - s2, mid.Y - s2, s2 * 2, s2 * 2));
            if (door.State == DoorState.Locked)
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    new Rect(mid.X - 2, mid.Y - 2, 4, 4));
        }
    }

    // ---- input: right-drag pan, wheel zoom ----
    private bool _panning;
    private Point _panStartScreen, _panStartOffset;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            _panning = true;
            _panStartScreen = e.GetPosition(this);
            _panStartOffset = _offset;
            e.Pointer.Capture(this);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(this);
        _offset = new Point(_panStartOffset.X - (p.X - _panStartScreen.X) / _scale,
                            _panStartOffset.Y - (p.Y - _panStartScreen.Y) / _scale);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var cursor = e.GetPosition(this);
        var worldCursor = ScreenToWorld(cursor);
        double factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        _scale = Math.Clamp(_scale * factor, MinScale, MaxScale);
        _offset = new Point(worldCursor.X - cursor.X / _scale, worldCursor.Y - cursor.Y / _scale);
        InvalidateVisual();
    }
}
