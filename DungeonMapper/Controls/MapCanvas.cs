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

public enum ToolMode { Select, Move, Room, Floor, Wall, Door, Stair, Label, Erase }

/// Which element categories the Erase tool removes. Doors follow walls (a door
/// implies a wall), so a door is removed when its wall is erased.
public sealed class EraseFilter
{
    public bool Cells = true;
    public bool Walls = true;
    public bool Stairs = true;
    public bool Labels = true;
}

/// Custom control that renders a MapDocument and hosts the editing tools.
/// World units = cells (5ft). screen = (world - offset) * scale.
/// Right-drag pans, wheel zooms (cursor-anchored). Left-click/drag edits per ToolMode.
/// Raises ActionCompleted after each edit so the owner can commit to history;
/// raises config requests for door/stair/label dialogs.
public sealed class MapCanvas : Control
{
    public MapDocument Document { get; set; } = new();
    public ToolMode Mode { get; set; } = ToolMode.Select;
    public AppSettings Settings { get; set; } = AppSettings.Default();

    public event Action? ActionCompleted;
    public event Action<Door>? DoorConfigRequested;
    public event Action<Stair>? StairConfigRequested;
    public event Action<MapLabel>? LabelConfigRequested;
    public event Action<string>? StatusMessage;

    public void Refresh() => InvalidateVisual();

    // ---- clipboard / selection ----
    private MapDocument.RegionSnapshot? _clipboard;
    private CellKey _selectStart, _selectEnd;
    private bool _selectDragging;
    private Point? _pasteCursor; // world cursor for paste ghost
    private bool _ghostVisible;  // paste ghost shows only on hover, not during click/drag
    public bool GhostEnabled { get; set; } = true; // user toggle for the paste ghost
    public EraseFilter EraseFilter { get; } = new(); // categories the Erase tool removes

    public bool HasClipboard => _clipboard is not null && !_clipboard.Empty;
    public bool HasSelection => _selectDragging || _selectStart != _selectEnd;

    /// Captures the current selection rectangle into the clipboard.
    public void CopySelection()
    {
        var (x0, y0, x1, y1) = NormRect(_selectStart, _selectEnd);
        var snap = Document.CaptureRegion(x0, y0, x1, y1);
        if (snap.Empty) { StatusMessage?.Invoke("nothing to copy - select a region first"); return; }
        _clipboard = snap;
        _cutSource = null; // a plain copy has no source to delete on paste
        StatusMessage?.Invoke($"copied {snap.Cells.Count} cells");
    }

    /// Cut: capture the selection AND remember its source region. The next
    /// paste deletes the source; later pastes act as normal paste.
    public void CutSelection()
    {
        var (x0, y0, x1, y1) = NormRect(_selectStart, _selectEnd);
        var snap = Document.CaptureRegion(x0, y0, x1, y1);
        if (snap.Empty) { StatusMessage?.Invoke("nothing to cut - select a region first"); return; }
        _clipboard = snap;
        _cutSource = (x0, y0, x1, y1);
        _cutSourceDoc = Document; // remember which map the source lives on
        StatusMessage?.Invoke($"cut {snap.Cells.Count} cells");
    }

    private (int x0, int y0, int x1, int y1)? _cutSource; // region to delete on the next paste
    private MapDocument? _cutSourceDoc; // the map the cut source lives on; paste deletes it only on that map

    /// Deletes all elements in a rectangle (cells, walls, doors, stairs, labels)
    /// from the given document.
    private void ClearRegion(MapDocument doc, int x0, int y0, int x1, int y1)
    {
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                var c = new CellKey(x, y);
                doc.Cells.Remove(c);
                doc.Stairs.Remove(c);
                doc.Labels.RemoveAll(l => l.Cell == c);
                foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                    doc.SetWall(new EdgeKey(c, side), false);
            }
    }

    /// Pastes the clipboard at the given world cell (origin maps to that cell).
    public void PasteAt(CellKey target)
    {
        if (_clipboard is null || _clipboard.Empty) { StatusMessage?.Invoke("clipboard empty"); return; }
        Document.PasteRegion(_clipboard, target.X, target.Y);
        // after a cut, the first paste deletes the original source region;
        // subsequent pastes act as normal paste (source cleared).
        if (_cutSource is (int cx0, int cy0, int cx1, int cy1))
        {
            // the cut source lives on a specific map; delete it only when
            // pasting back into that same map, never the current map's cells
            if (_cutSourceDoc == Document)
            {
                ClearRegion(Document, cx0, cy0, cx1, cy1);
                StatusMessage?.Invoke("pasted (cut source removed)");
            }
            _cutSource = null;
            _cutSourceDoc = null;
        }
        Commit();
    }

    /// Move: capture the current selection, paste it at the target cell, and
    /// delete the source region. One action, no clipboard round-trip.
    public void MoveAt(CellKey target)
    {
        var (x0, y0, x1, y1) = NormRect(_selectStart, _selectEnd);
        var snap = Document.CaptureRegion(x0, y0, x1, y1);
        if (snap.Empty) { StatusMessage?.Invoke("nothing to move - select a region first"); return; }
        _clipboard = snap; // armed region shows as the paste ghost at the cursor
        Document.PasteRegion(snap, target.X, target.Y);
        ClearRegion(Document, x0, y0, x1, y1);
        StatusMessage?.Invoke($"moved {snap.Cells.Count} cells");
        Commit();
    }

    /// Pastes at the current cursor cell, if a paste ghost is showing.
    public void PasteAtCursor()
    {
        if (_pasteCursor is Point p)
            PasteAt(new CellKey((int)Math.Floor(p.X), (int)Math.Floor(p.Y)));
    }

    /// Delete hotkey: erase the current selection region with every category
    /// on (cells, walls+doors, stairs, labels), like the eraser tool fully on.
    public void DeleteSelection()
    {
        if (!HasSelection) { StatusMessage?.Invoke("nothing to delete - select a region first"); return; }
        var (x0, y0, x1, y1) = NormRect(_selectStart, _selectEnd);
        ClearRegion(Document, x0, y0, x1, y1);
        StatusMessage?.Invoke($"deleted {(x1 - x0 + 1) * (y1 - y0 + 1)} cells");
        Commit();
    }

    /// Clears the clipboard and any remembered cut source. The clipboard is
    /// per-map: pasting a cut from another map must not delete a region of the
    /// current map, so this fires on every map switch/load.
    public void ResetClipboard()
    {
        _clipboard = null;
        _cutSource = null;
        _pasteCursor = null;
        _ghostVisible = false;
    }

    public void ClearSelection()
    {
        _selectDragging = false;
        _selectStart = _selectEnd = default;
        InvalidateVisual();
    }

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

    // Door color per type, from the configurable DoorTypes list; unknown types
    // fall back to the first category.
    private IBrush DoorBrush(string type)
    {
        var t = Settings.FindDoorType(type);
        return new SolidColorBrush(Color.FromRgb((byte)t.R, (byte)t.G, (byte)t.B));
    }

    public void AutoFit()
    {
        var b = Document.ContentBounds();
        if (b is null) { _offset = new Point(-1, -1); _scale = 40; InvalidateVisual(); return; }
        var (minX, minY, maxX, maxY) = b.Value;
        double w = maxX - minX + 1, h = maxY - minY + 1;
        double availW = Bounds.Width > 0 ? Bounds.Width : 800;
        double availH = Bounds.Height > 0 ? Bounds.Height : 600;
        _scale = Math.Clamp(Math.Min(availW / (w + 2), availH / (h + 2)), MinScale, MaxScale);
        // center content in the viewport, not top-left aligned
        double cx = (minX + maxX + 1) / 2.0, cy = (minY + maxY + 1) / 2.0;
        _offset = new Point(cx - availW / (2 * _scale), cy - availH / (2 * _scale));
        InvalidateVisual();
    }

    private Point WorldToScreen(double wx, double wy)
        => new((wx - _offset.X) * _scale, (wy - _offset.Y) * _scale);

    private Point ScreenToWorld(Point p)
        => new(p.X / _scale + _offset.X, p.Y / _scale + _offset.Y);

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Background, new Rect(Bounds.Size));

        // Visible world window, for culling
        var topLeft = ScreenToWorld(new Point(0, 0));
        var bottomRight = ScreenToWorld(new Point(Bounds.Width, Bounds.Height));

        // Cell fills + grid lines. Grid is always 5ft (1 cell); GridDrawSpacing
        // controls how many cells between drawn lines (1 = every 5ft, 2 = 10ft).
        int sp = Math.Max(1, Settings.GridDrawSpacing);
        foreach (var c in Document.Cells)
        {
            if (c.X + 1 < topLeft.X || c.X > bottomRight.X || c.Y + 1 < topLeft.Y || c.Y > bottomRight.Y) continue;
            var p0 = WorldToScreen(c.X, c.Y);
            var rect = new Rect(p0.X, p0.Y, _scale, _scale);
            ctx.FillRectangle(CellFill, rect);
            // vertical grid line at this cell's left edge if it's a spacing boundary
            if (FloorMod(c.X, sp) == 0)
                ctx.DrawLine(new Pen(GridLine, 1), rect.TopLeft, rect.BottomLeft);
            // horizontal grid line at this cell's top edge if it's a spacing boundary
            if (FloorMod(c.Y, sp) == 0)
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
            // tip points up for Up stairs, down for Down; base is opposite the tip
            double tipY = s.Direction == StairDirection.Up ? center.Y - a : center.Y + a;
            double baseY = s.Direction == StairDirection.Up ? center.Y + a : center.Y - a;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(center.X, tipY), isFilled: true);
                g.LineTo(new Point(center.X - a, baseY));
                g.LineTo(new Point(center.X + a, baseY));
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

        DrawPreview(ctx);
    }

    // ---- drag preview ----
    private bool _dragging;
    private CellKey _dragStart, _dragEnd;
    private EdgeKey? _previewEdge;
    private EdgeKey? _wallDragStart;
    private bool? _wallDragHorizontal; // orientation locked from the first wall edge clicked

    private void DrawPreview(DrawingContext ctx)
    {
        if (_dragging && (Mode == ToolMode.Room || Mode == ToolMode.Floor || Mode == ToolMode.Stair || Mode == ToolMode.Erase))
        {
            var (x0, y0, x1, y1) = NormRect(_dragStart, _dragEnd);
            var tl = WorldToScreen(x0, y0);
            var rect = new Rect(tl.X, tl.Y, (x1 - x0 + 1) * _scale, (y1 - y0 + 1) * _scale);
            var fill = Mode == ToolMode.Room
                ? new SolidColorBrush(Color.FromArgb(70, 60, 75, 105))
                : Mode == ToolMode.Floor
                ? new SolidColorBrush(Color.FromArgb(70, 150, 130, 90)) // floor: muted tan
                : Mode == ToolMode.Stair
                ? new SolidColorBrush(Color.FromArgb(70, 230, 160, 80))
                : new SolidColorBrush(Color.FromArgb(70, 220, 60, 60)); // erase: red = removal
            ctx.FillRectangle(fill, rect);
            var border = Mode == ToolMode.Erase
                ? new Pen(new SolidColorBrush(Color.FromRgb(220, 60, 60)), 2)
                : new Pen(new SolidColorBrush(Color.FromRgb(250, 250, 250)), 2);
            ctx.DrawRectangle(border, rect);
        }
        if (_previewEdge is EdgeKey pe && (Mode == ToolMode.Wall || Mode == ToolMode.Door))
        {
            var seg = EdgeSegment(pe);
            var a = WorldToScreen(seg.a.X, seg.a.Y);
            var b = WorldToScreen(seg.b.X, seg.b.Y);
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(250, 250, 250)), 2), a, b);
        }

        // selection rectangle (cyan)
        if (_selectDragging || (_selectStart != _selectEnd && (Mode == ToolMode.Select || Mode == ToolMode.Move)))
        {
            var (x0, y0, x1, y1) = NormRect(_selectStart, _selectEnd);
            var tl = WorldToScreen(x0, y0);
            var rect = new Rect(tl.X, tl.Y, (x1 - x0 + 1) * _scale, (y1 - y0 + 1) * _scale);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(60, 80, 200, 220)), rect);
            ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(80, 200, 220)), 2), rect);
        }

        // paste ghost: clipboard preview at the cursor cell. Shown in Select
        // mode, and in Move mode when a region is armed (not mid-drag), so the
        // user sees where the move will land.
        bool ghostMode = Mode == ToolMode.Select
            || (Mode == ToolMode.Move && HasSelection && !_dragging);
        if (_clipboard is not null && !_clipboard.Empty && _pasteCursor is Point pc && ghostMode && _ghostVisible && GhostEnabled)
        {
            var origin = new CellKey((int)Math.Floor(pc.X), (int)Math.Floor(pc.Y));
            int dx = origin.X - _clipboard.OriginX, dy = origin.Y - _clipboard.OriginY;
            var ghost = new SolidColorBrush(Color.FromArgb(70, 80, 200, 220));
            foreach (var c in _clipboard.Cells)
            {
                var p0 = WorldToScreen(c.X + dx, c.Y + dy);
                ctx.FillRectangle(ghost, new Rect(p0.X, p0.Y, _scale, _scale));
            }
        }
    }

    private static (int x0, int y0, int x1, int y1) NormRect(CellKey a, CellKey b)
        => (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    // floor-mod for negative cell coords (C# % is truncating, not flooring)
    private static int FloorMod(int a, int m)
    {
        int r = a % m;
        return r < 0 ? r + m : r;
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

    /// Nearest edge to a world point, within tolerance; null if none.
    /// If horizontal is set, only edges of that orientation are considered
    /// (N/S = horizontal, E/W = vertical), so a wall drag stays on its axis.
    private EdgeKey? HitEdge(Point world, bool? horizontal = null)
    {
        int cx = (int)Math.Floor(world.X), cy = (int)Math.Floor(world.Y);
        CellKey cell = new(cx, cy);
        EdgeKey? best = null;
        double bestD = 0.3; // tolerance in cells
        foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
        {
            var e = EdgeKey.Normalize(cell, side);
            if (horizontal is not null)
            {
                bool isHoriz = e.Side == Side.North || e.Side == Side.South;
                if (isHoriz != horizontal.Value) continue;
            }
            var (a, b) = EdgeSegment(e);
            double d = DistPointSeg(world, a, b);
            if (d < bestD) { bestD = d; best = e; }
        }
        return best;
    }

    private static double DistPointSeg(Point p, Point a, Point b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double len2 = abx * abx + aby * aby;
        if (len2 == 0) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        double t = Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2, 0, 1);
        double dx = p.X - (a.X + t * abx), dy = p.Y - (a.Y + t * aby);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void Commit()
    {
        ActionCompleted?.Invoke();
        InvalidateVisual();
    }

    // ---- input ----
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
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        _ghostVisible = false; // hide paste ghost on click/drag

        var world = ScreenToWorld(e.GetPosition(this));
        var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));

        switch (Mode)
        {
            case ToolMode.Select:
            case ToolMode.Move:
                if (Mode == ToolMode.Move && HasSelection)
                {
                    // a region is already armed; this click is the move target
                    MoveAt(cell);
                    ClearSelection();
                    break;
                }
                _selectDragging = true;
                _selectStart = cell;
                _selectEnd = cell;
                e.Pointer.Capture(this);
                break;

            case ToolMode.Room:
            case ToolMode.Floor:
            case ToolMode.Stair:
            case ToolMode.Erase:
                _dragging = true;
                _dragStart = cell;
                _dragEnd = cell;
                e.Pointer.Capture(this);
                break;

            case ToolMode.Wall:
                var we = HitEdge(world);
                if (we is null) break;
                var wallEdge = we.Value;
                if (Document.HasWall(wallEdge))
                {
                    Document.SetWall(wallEdge, false); // toggle off
                    Commit();
                }
                else
                {
                    _dragging = true;
                    _wallDragStart = wallEdge;
                    _wallDragHorizontal = wallEdge.Side == Side.North || wallEdge.Side == Side.South;
                    Document.SetWall(wallEdge, true);
                    Commit();
                    e.Pointer.Capture(this);
                }
                break;

            case ToolMode.Door:
                var de = HitEdge(world);
                if (de is null) break;
                var doorEdge = de.Value;
                var existing = Document.GetDoor(doorEdge);
                if (existing is not null)
                {
                    DoorConfigRequested?.Invoke(existing);
                }
                else
                {
                    // door implies wall: auto-create the wall if missing, then place the door
                    if (!Document.HasWall(doorEdge))
                        Document.SetWall(doorEdge, true);
                    Document.TryPlaceDoor(doorEdge, Settings.DefaultDoorType, Settings.DefaultDoorState, out var d);
                    Commit();
                    DoorConfigRequested?.Invoke(d);
                }
                break;

            case ToolMode.Label:
                var lbl = Document.Labels.FirstOrDefault(l => l.Cell == cell);
                if (lbl is not null)
                {
                    LabelConfigRequested?.Invoke(lbl);
                }
                else
                {
                    lbl = new MapLabel { Cell = cell, Text = Settings.DefaultLabelText };
                    Document.Labels.Add(lbl);
                    Commit();
                    LabelConfigRequested?.Invoke(lbl);
                }
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_panning)
        {
            var p = e.GetPosition(this);
            _offset = new Point(_panStartOffset.X - (p.X - _panStartScreen.X) / _scale,
                                _panStartOffset.Y - (p.Y - _panStartScreen.Y) / _scale);
            InvalidateVisual();
            return;
        }

        var world = ScreenToWorld(e.GetPosition(this));
        _pasteCursor = world; // paste ghost follows the cursor
        if (!_selectDragging && !_dragging && !_panning)
        {
            _ghostVisible = true;
            // redraw so the paste ghost tracks the cursor on hover
            if (_clipboard is not null && !_clipboard.Empty && (Mode == ToolMode.Select || Mode == ToolMode.Move))
                InvalidateVisual();
        }

        if (_selectDragging)
        {
            var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
            if (cell != _selectEnd) { _selectEnd = cell; InvalidateVisual(); }
            return;
        }

        if (_dragging)
        {
            var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
            if (Mode == ToolMode.Room || Mode == ToolMode.Floor || Mode == ToolMode.Stair || Mode == ToolMode.Erase)
            {
                if (cell != _dragEnd) { _dragEnd = cell; InvalidateVisual(); }
            }
            else if (Mode == ToolMode.Wall)
            {
                var edge = HitEdge(world, _wallDragHorizontal);
                if (edge is not null)
                {
                    var e2 = edge.Value;
                    if (e2 != _wallDragStart)
                    {
                        _wallDragStart = e2;
                        if (!Document.HasWall(e2))
                        {
                            Document.SetWall(e2, true);
                            Commit();
                        }
                    }
                }
            }
        }
        else if (Mode == ToolMode.Wall || Mode == ToolMode.Door)
        {
            var edge = HitEdge(world);
            if (edge != _previewEdge) { _previewEdge = edge; InvalidateVisual(); }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
            return;
        }

        if (_selectDragging)
        {
            _selectDragging = false;
            _ghostVisible = false;
            e.Pointer.Capture(null);
            var world = ScreenToWorld(e.GetPosition(this));
            var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
            _selectEnd = cell;
            if (Mode == ToolMode.Move && _selectStart != _selectEnd)
            {
                // arm the move: capture the region so the paste ghost previews
                // it at the cursor until the destination click
                var (x0, y0, x1, y1) = NormRect(_selectStart, _selectEnd);
                _clipboard = Document.CaptureRegion(x0, y0, x1, y1);
                _cutSource = null;
            }
            if (_selectStart == _selectEnd)
            {
                // single cell: manage the element under the cursor
                var edge = HitEdge(world);
                var door = edge is not null ? Document.GetDoor(edge.Value) : null;
                if (door is not null) DoorConfigRequested?.Invoke(door);
                else if (Document.Stairs.TryGetValue(cell, out var s)) StairConfigRequested?.Invoke(s);
                else
                {
                    var lbl = Document.Labels.FirstOrDefault(l => l.Cell == cell);
                    if (lbl is not null) LabelConfigRequested?.Invoke(lbl);
                }
            }
            InvalidateVisual();
            return;
        }

        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);

        switch (Mode)
        {
            case ToolMode.Room:
            {
                var (x0, y0, x1, y1) = NormRect(_dragStart, _dragEnd);
                // passageway: if the drag started inside an existing room, the
                // shared boundary stays open so the new region connects as a
                // passageway instead of being sealed off.
                bool passageway = Document.Cells.Contains(_dragStart);
                Document.AddRoom(x0, y0, x1, y1, passageway);
                Commit();
                break;
            }
            case ToolMode.Floor:
            {
                var (x0, y0, x1, y1) = NormRect(_dragStart, _dragEnd);
                Document.AddFloor(x0, y0, x1, y1);
                Commit();
                break;
            }
            case ToolMode.Stair:
            {
                var (x0, y0, x1, y1) = NormRect(_dragStart, _dragEnd);
                bool single = x0 == x1 && y0 == y1;
                if (single && Document.Stairs.TryGetValue(_dragStart, out var existingStair))
                {
                    StairConfigRequested?.Invoke(existingStair);
                }
                else
                {
                    Stair first = null!;
                    for (int x = x0; x <= x1; x++)
                        for (int y = y0; y <= y1; y++)
                        {
                            var c = new CellKey(x, y);
                            if (!Document.Stairs.TryGetValue(c, out var s))
                            {
                                s = new Stair { Direction = StairDirection.Down };
                                Document.Stairs[c] = s;
                            }
                            if (ReferenceEquals(s, first)) { }
                            if (first is null) first = s;
                        }
                    Commit();
                    StairConfigRequested?.Invoke(first);
                }
                break;
            }
            case ToolMode.Wall:
                _wallDragStart = null;
                break;
            case ToolMode.Erase:
            {
                var (x0, y0, x1, y1) = NormRect(_dragStart, _dragEnd);
                var f = EraseFilter;
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                    {
                        var c = new CellKey(x, y);
                        if (f.Cells) Document.Cells.Remove(c);
                        if (f.Stairs) Document.Stairs.Remove(c);
                        if (f.Labels) Document.Labels.RemoveAll(l => l.Cell == c);
                        // remove walls on all four edges of the cell (doors auto-removed)
                        if (f.Walls)
                            foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                                Document.SetWall(new EdgeKey(c, side), false);
                    }
                Commit();
                break;
            }
        }
        InvalidateVisual();
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
        // symbol: solid square for closed, hollow square (outline only,
        // transparent core) for open, square + big inner square for locked.
        // The inner square is 30% of the door side, so it scales with zoom.
        if (door.State == DoorState.Open)
        {
            var pen = new Pen(brush, Math.Max(1.5, _scale * 0.06));
            ctx.DrawRectangle(pen, new Rect(mid.X - s2, mid.Y - s2, s2 * 2, s2 * 2));
        }
        else
        {
            ctx.FillRectangle(brush, new Rect(mid.X - s2, mid.Y - s2, s2 * 2, s2 * 2));
            if (door.State == DoorState.Locked)
            {
                double inner = s2 * 2 * 0.3;
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    new Rect(mid.X - inner / 2, mid.Y - inner / 2, inner, inner));
            }
        }
    }
}
