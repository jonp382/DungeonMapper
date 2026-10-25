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

public enum ToolMode { Select, Move, Room, Floor, Wall, Door, Stair, Label, Erase, Pawn, Polygon }

/// Which element categories the Erase tool removes. Doors follow walls (a door
/// implies a wall), so a door is removed when its wall is erased.
public sealed class EraseFilter
{
    public bool Cells = true;
    public bool Walls = true;
    public bool Stairs = true;
    public bool Labels = true;
    public bool Pawns = true;
}

/// Custom control that renders a MapDocument and hosts the editing tools.
/// World units = cells (5ft). screen = (world - offset) * scale.
/// Right-drag pans, wheel zooms (cursor-anchored). Left-click/drag edits per ToolMode.
/// Raises ActionCompleted after each edit so the owner can commit to history;
/// raises config requests for door/stair/label dialogs.
public sealed class MapCanvas : Control
{
    public MapCanvas()
    {
        // focusable so keyboard hotkeys (Ctrl+C/V/X, tool letters, Delete) reach
        // the window: we return focus here after toolbar/panel interactions that
        // would otherwise leave focus on a ComboBox/ScrollBar/CheckBox, which
        // consume those keys (type-ahead) and break the hotkeys.
        Focusable = true;
        // clip drawing to the canvas bounds: panning the map upward must not let
        // walls/pawns/labels render over the top toolbar. The canvas sits below the
        // toolbar in the layout, so clipping to its bounds keeps the toolbar clear.
        ClipToBounds = true;
    }

    public MapDocument Document { get; set; } = new();
    public ToolMode Mode { get; set; } = ToolMode.Select;
    public AppSettings Settings { get; set; } = AppSettings.Default();
    public PawnLibrary Library { get; set; } = new();
    /// Template armed for placement from the floating submenu. Clicking a cell
    /// places a pawn referencing this template. Null = no pawn armed.
    public PawnTemplate? ArmedPawn { get; set; }
    // folder of unique-pawn images (filenames stored on templates as PawnTemplate.Image)
    public string PawnImagesDir { get; set; } = "";
    private readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap> _bitmapCache = new();

    /// Drops every cached bitmap. Call when a template's image is uploaded,
    /// removed, or the template is deleted, so the next render reloads.
    public void InvalidateImageCache()
    {
        foreach (var b in _bitmapCache.Values) b.Dispose();
        _bitmapCache.Clear();
    }

    private Avalonia.Media.Imaging.Bitmap? LoadPawnImage(string fileName)
    {
        if (_bitmapCache.TryGetValue(fileName, out var cached)) return cached;
        var path = System.IO.Path.Combine(PawnImagesDir, fileName);
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            var bmp = new Avalonia.Media.Imaging.Bitmap(path);
            _bitmapCache[fileName] = bmp;
            return bmp;
        }
        catch { return null; }
    }
    // special move mode: selection captures and moves ONLY pawns; other elements
    // are untouched at the source, and incompatible elements at the destination
    // are removed so the pawn lands clean.
    public bool MovePawnsOnly { get; set; }

    public event Action? ActionCompleted;
    public event Action<IDoorConfig>? DoorConfigRequested;
    public event Action<Stair>? StairConfigRequested;
    public event Action<MapLabel>? LabelConfigRequested;
    public event Action<Pawn>? PawnConfigRequested;
    public event Action<string>? StatusMessage;

    public void Refresh() => InvalidateVisual();

    // ---- clipboard / selection ----
    private MapDocument.RegionSnapshot? _clipboard;
    private CellKey _selectStart, _selectEnd;
    private bool _selectDragging;
    // per-pawn drag in the Move tool: pressing directly on a pawn drags that
    // single pawn to the release cell; a press without drag shows its config.
    private Pawn? _pawnDragPawn;
    private CellKey _pawnDragStart, _pawnDragEnd;
    private bool _pawnDragging;
    private Point? _pasteCursor; // world cursor for paste ghost
    private bool _ghostVisible;  // paste ghost shows only on hover, not during click/drag
    public bool GhostEnabled { get; set; } = true; // user toggle for the paste ghost
    public EraseFilter EraseFilter { get; } = new(); // categories the Erase tool removes
    public bool RoomStrict { get; set; } = false; // Room tool: draw walls on the outer border regardless of existing cells, keep interior walls
    public bool PolygonStrict { get; set; } = false; // Polygon tool: same as RoomStrict - outer walls always, interior walls kept

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
    private MapDocument? _cutSourceDoc; // the map the cut source lives on; paste deletes it on that map
    private MapDocument? _moveSourceDoc; // the map the armed move region was selected on

    /// Deletes all elements in a rectangle (cells, walls, doors, stairs, labels)
    /// from the given document.
    private void ClearRegion(MapDocument doc, int x0, int y0, int x1, int y1)
        => doc.ClearRegion(x0, y0, x1, y1);

    /// Pastes the clipboard at the given world cell (origin maps to that cell).
    public void PasteAt(CellKey target)
    {
        if (_clipboard is null || _clipboard.Empty) { StatusMessage?.Invoke("clipboard empty"); return; }
        Document.PasteRegion(_clipboard, target.X, target.Y);
        // after a cut, the first paste deletes the source region from the map
        // it was cut from - works cross-map (cut on map1, paste on map2).
        if (_cutSource is (int cx0, int cy0, int cx1, int cy1))
        {
            // clear the cut source BEFORE pasting so a paste overlapping the
            // source region survives (Excel-style shift); the snapshot is a
            // deep clone, so clearing the source does not affect it.
            ClearRegion(_cutSourceDoc ?? Document, cx0, cy0, cx1, cy1);
            _cutSource = null;
            _cutSourceDoc = null;
            Document.PasteRegion(_clipboard, target.X, target.Y);
            StatusMessage?.Invoke("pasted (cut source removed from its map)");
        }
        else
        {
            Document.PasteRegion(_clipboard, target.X, target.Y);
        }
        Commit();
    }

    /// Move: capture the armed selection from the map it was selected on,
    /// paste it at the target cell of the CURRENT map, and delete the source
    /// region. Works cross-map: select on map1, switch to map2, click target.
    public void MoveAt(CellKey target)
    {
        var (x0, y0, x1, y1) = NormRect(_selectStart, _selectEnd);
        var src = _moveSourceDoc ?? Document;

        if (MovePawnsOnly)
        {
            // special move: capture ONLY pawns in the region. other elements stay
            // at the source; at the destination, incompatible elements (walls,
            // cells, stairs, labels) are removed so the pawn lands clean.
            var pawns = new List<Pawn>();
            foreach (var (c, p) in src.Pawns)
                if (c.X >= x0 && c.X <= x1 && c.Y >= y0 && c.Y <= y1) pawns.Add(p);
            if (pawns.Count == 0) { StatusMessage?.Invoke("no pawns in the selection"); return; }
            int dx = target.X - x0, dy = target.Y - y0;
            // landing on a pawn outside the moving set SWAPS positions (unique to
            // pawns). capture occupants before removing movers so swaps pair the
            // occupant with the mover's source cell.
            var swaps = new List<(CellKey dest, Pawn occupant)>();
            foreach (var p in pawns)
            {
                var nc = new CellKey(p.Cell.X + dx, p.Cell.Y + dy);
                if (Document.Pawns.TryGetValue(nc, out var occ) && !pawns.Contains(occ))
                    swaps.Add((nc, occ));
            }
            foreach (var p in pawns) src.Pawns.Remove(p.Cell);
            foreach (var p in pawns)
            {
                var nc = new CellKey(p.Cell.X + dx, p.Cell.Y + dy);
                ClearCellObstacles(Document, nc);
                Document.Pawns[nc] = new Pawn { Cell = nc, TemplateId = p.TemplateId, Status = p.Status };
            }
            foreach (var (dest, occ) in swaps)
            {
                // the mover's source cell is where the swapped occupant lands
                var moverSrc = new CellKey(dest.X - dx, dest.Y - dy);
                Document.Pawns[moverSrc] = new Pawn { Cell = moverSrc, TemplateId = occ.TemplateId, Status = occ.Status };
            }
            StatusMessage?.Invoke(swaps.Count > 0
                ? $"moved {pawns.Count} pawns, swapped {swaps.Count}"
                : $"moved {pawns.Count} pawns");
            Commit();
            return;
        }

        var snap = src.CaptureRegion(x0, y0, x1, y1);
        if (snap.Empty) { StatusMessage?.Invoke("nothing to move - select a region first"); return; }
        _clipboard = snap; // armed region shows as the paste ghost at the cursor
        // clear the source BEFORE pasting so a move overlapping its own source
        // survives (Excel-style shift); the snapshot is a deep clone.
        if (ReferenceEquals(src, Document))
            Document.MoveRegion(x0, y0, x1, y1, target.X, target.Y);
        else
        {
            Document.PasteRegion(snap, target.X, target.Y);
            src.ClearRegion(x0, y0, x1, y1);
        }
        StatusMessage?.Invoke($"moved {snap.Cells.Count} cells");
        Commit();
    }

    /// Removes everything incompatible with a pawn landing on a cell: stairs,
    /// labels, angled walls touching the cell (their doors go with the wall),
    /// and straight walls on the cell's edges - but ONLY when the cell is not a
    /// room/floor tile. A pawn landing on a room floor keeps the room's walls:
    /// the walls sit on the room's boundary, not inside the pawn's cell, so
    /// landing on the edge of a room must not erase them. Room/floor cells
    /// themselves are never removed - a pawn sits on top of a floor tile.
    private static void ClearCellObstacles(MapDocument doc, CellKey c)
    {
        doc.Stairs.Remove(c);
        doc.Labels.RemoveAll(l => l.Cell == c);
        bool onFloor = doc.Cells.Contains(c);
        if (!onFloor)
            foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                doc.SetWall(new EdgeKey(c, side), false);
        foreach (var w in doc.AngledWalls)
            if (MapDocument.AngledWallIntersectsRect(w, c.X, c.Y, c.X, c.Y))
                doc.AngledDoors.Remove(w);
        doc.AngledWalls.RemoveAll(w => MapDocument.AngledWallIntersectsRect(w, c.X, c.Y, c.X, c.Y));
    }

    /// Pastes at the current cursor cell, if a paste ghost is showing.
    public void PasteAtCursor()
    {
        if (_pasteCursor is Point p)
            PasteAt(new CellKey((int)Math.Floor(p.X), (int)Math.Floor(p.Y)));
    }

    /// Delete hotkey: erase the current selection region, honoring the Erase
    /// tool's category filters (cells, walls+doors, stairs, labels).
    public void DeleteSelection()
    {
        if (!HasSelection) { StatusMessage?.Invoke("nothing to delete - select a region first"); return; }
        var (x0, y0, x1, y1) = NormRect(_selectStart, _selectEnd);
        var f = EraseFilter;
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                var c = new CellKey(x, y);
                if (f.Cells) Document.Cells.Remove(c);
                if (f.Stairs) Document.Stairs.Remove(c);
                if (f.Pawns) Document.Pawns.Remove(c);
                if (f.Labels) Document.Labels.RemoveAll(l => l.Cell == c);
                if (f.Walls)
                    foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                        Document.SetWall(new EdgeKey(c, side), false);
            }
        if (f.Walls)
        {
            foreach (var w in Document.AngledWalls
                         .Where(w => MapDocument.AngledWallIntersectsRect(w, x0, y0, x1, y1)).ToList())
                Document.AngledDoors.Remove(w);
            Document.AngledWalls.RemoveAll(w => MapDocument.AngledWallIntersectsRect(w, x0, y0, x1, y1));
        }
        StatusMessage?.Invoke($"deleted {(x1 - x0 + 1) * (y1 - y0 + 1)} cells (erase filters applied)");
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

    /// Polygon tool: close the polygon by appending the first vertex (Enter key).
    /// No-op when fewer than 3 vertices are armed.
    public void ClosePolygon()
    {
        if (_polyVerts.Count < 3) return;
        var first = _polyVerts[0];
        if (_polyVerts[^1] != first) _polyVerts.Add(first);
        // passageway: if the closing vertex sits inside an existing room cell, the
        // shared boundary stays open so the polygon connects as a passageway.
        bool strict = PolygonStrict;
        bool passageway = !strict && Document.Cells.Contains(new CellKey(first.x, first.y));
        Document.AddPolygon(_polyVerts, passageway, strict);
        _polyVerts.Clear();
        Commit();
    }

    /// Polygon tool: discard the vertices armed so far (cancel button / Escape).
    public void CancelPolygon()
    {
        _polyVerts.Clear();
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
    // Canvas element colors come from Settings (settings menu). The old static
    // GridLine/FaintGridLine brushes were dead code - grid rendering uses the
    // MajorGrid/MinorGrid settings colors - and were removed.
    private IBrush Background => new SolidColorBrush(Color.FromRgb((byte)Settings.BackgroundR, (byte)Settings.BackgroundG, (byte)Settings.BackgroundB));
    private IBrush CellFill => new SolidColorBrush(Color.FromRgb((byte)Settings.CellFillR, (byte)Settings.CellFillG, (byte)Settings.CellFillB));
    private IPen WallPen => new Pen(new SolidColorBrush(Color.FromRgb((byte)Settings.WallR, (byte)Settings.WallG, (byte)Settings.WallB)), 3);
    private IBrush StairUp => new SolidColorBrush(Color.FromRgb((byte)Settings.StairUpR, (byte)Settings.StairUpG, (byte)Settings.StairUpB));
    private IBrush StairDown => new SolidColorBrush(Color.FromRgb((byte)Settings.StairDownR, (byte)Settings.StairDownG, (byte)Settings.StairDownB));
    private IBrush LabelBrush => new SolidColorBrush(Color.FromRgb((byte)Settings.LabelR, (byte)Settings.LabelG, (byte)Settings.LabelB));

    // Door color per type, from the configurable DoorTypes list; unknown types
    // fall back to the first category.
    private IBrush DoorBrush(string type)
    {
        var t = Settings.FindDoorType(type);
        return new SolidColorBrush(Color.FromRgb((byte)t.R, (byte)t.G, (byte)t.B));
    }

    // Door square glyph centered at mid, rotated by ang (radians). Open = hollow
    // outline, closed = solid, locked = solid + red core. Shared by placed doors,
    // angled doors, and the door tool placement ghost.
    private void DrawDoorGlyph(DrawingContext ctx, Point mid, double ang, IBrush brush, DoorState state)
    {
        double s2 = Math.Max(4, _scale * 0.18);
        var rect = new Rect(mid.X - s2, mid.Y - s2, s2 * 2, s2 * 2);
        void core()
        {
            if (state == DoorState.Open)
            {
                var pen = new Pen(brush, Math.Max(1.5, _scale * 0.06));
                ctx.DrawRectangle(pen, rect);
            }
            else
            {
                ctx.FillRectangle(brush, rect);
                if (state == DoorState.Locked)
                {
                    double inner = s2 * 2 * 0.5;
                    // locked inner core is always RED, independent of door type.
                    ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(220, 40, 40)),
                        new Rect(mid.X - inner / 2, mid.Y - inner / 2, inner, inner));
                }
            }
        }
        if (Math.Abs(ang) < 0.0001) { core(); return; }
        // Avalonia transforms points as point * matrix (row-vector), so the
        // composition applies right-to-left: shift mid to origin, rotate, shift back.
        using (ctx.PushTransform(Matrix.CreateTranslation(-mid.X, -mid.Y)
                                 * Matrix.CreateRotation(ang)
                                 * Matrix.CreateTranslation(mid.X, mid.Y)))
            core();
    }

    // Semi-transparent version of a door type color, for the placement ghost.
    private SolidColorBrush GhostBrush(SolidColorBrush src)
        => new SolidColorBrush(Color.FromArgb(120, src.Color.R, src.Color.G, src.Color.B));

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

        // Cell fills. Grid is always 5ft (1 cell); GridDrawSpacing is the MAJOR
        // spacing. Colors come from settings.
        int sp = Math.Max(1, Settings.GridDrawSpacing);
        var majorPen = new Pen(new SolidColorBrush(Color.FromRgb((byte)Settings.MajorGridR, (byte)Settings.MajorGridG, (byte)Settings.MajorGridB)), 1);
        var minorPen = new Pen(new SolidColorBrush(Color.FromRgb((byte)Settings.MinorGridR, (byte)Settings.MinorGridG, (byte)Settings.MinorGridB)), 1);
        // Fill CONTIGUOUS CELLS AS ONE MERGED RECTANGLE per maximal block, so a
        // room built from adjacent cells (e.g. a 10x10 room = 4 connected 5x5
        // tiles) renders as one contiguous square. Filling each cell as its own
        // _scale rect leaves sub-pixel seams between adjacent rects that read as
        // grid lines inside rooms even when minor gridlines are disabled.
        // Greedy maximal rectangles: extend right along the row, extend down
        // while every column of the row is filled, mark cells consumed.
        // Two fixes for the seam lines the user sees inside rooms:
        // 1. iterate top-left-first (hashset order is arbitrary, so the greedy
        //    split a room into two rects at an arbitrary column);
        // 2. overlap each rect by half a pixel into any edge shared with a
        //    neighboring filled cell. The rects meet at fractional pixel
        //    coordinates under pan/zoom, and anti-aliasing at the shared edge
        //    lets the background bleed through as a faint line. Same-color
        //    overlap is invisible; overlap only happens where the neighbor is
        //    filled, so rooms never overhang their boundary.
        var remaining = new HashSet<CellKey>(Document.Cells);
        foreach (var c in Document.Cells.OrderBy(c => c.Y).ThenBy(c => c.X))
        {
            if (!remaining.Contains(c)) continue;
            int w = 1;
            while (remaining.Contains(new CellKey(c.X + w, c.Y))) w++;
            int h = 1;
            while (remaining.Contains(new CellKey(c.X, c.Y + h)))
            {
                bool full = true;
                for (int i = 1; i < w; i++)
                    if (!remaining.Contains(new CellKey(c.X + i, c.Y + h))) { full = false; break; }
                if (!full) break;
                h++;
            }
            for (int dx = 0; dx < w; dx++)
                for (int dy = 0; dy < h; dy++)
                    remaining.Remove(new CellKey(c.X + dx, c.Y + dy));
            if (c.X + w < topLeft.X || c.X > bottomRight.X || c.Y + h < topLeft.Y || c.Y > bottomRight.Y) continue;
            double pad = 0.6;
            double rw = w * _scale + (Document.Cells.Contains(new CellKey(c.X + w, c.Y)) ? pad : 0);
            double rh = h * _scale + (Document.Cells.Contains(new CellKey(c.X, c.Y + h)) ? pad : 0);
            var p0 = WorldToScreen(c.X, c.Y);
            ctx.FillRectangle(CellFill, new Rect(p0.X, p0.Y, rw, rh));
        }

        // Grid lines across the WHOLE visible viewport, not just on drawn flooring,
        // so the grid is visible everywhere. Drawn after cell fills so lines show
        // on top of flooring. Major at spacing boundaries, minor at every 5ft cell
        // edge; at sp==1 minor coincides with major so all lines are major.
        int xStart = (int)Math.Floor(topLeft.X), xEnd = (int)Math.Ceiling(bottomRight.X);
        int yStart = (int)Math.Floor(topLeft.Y), yEnd = (int)Math.Ceiling(bottomRight.Y);
        for (int x = xStart; x <= xEnd; x++)
        {
            double sx = WorldToScreen(x, 0).X;
            bool major = sp == 1 || FloorMod(x, sp) == 0;
            if (!major && !Settings.ShowMinorGrid) continue;
            ctx.DrawLine(major ? majorPen : minorPen, new Point(sx, 0), new Point(sx, Bounds.Height));
        }
        for (int y = yStart; y <= yEnd; y++)
        {
            double sy = WorldToScreen(0, y).Y;
            bool major = sp == 1 || FloorMod(y, sp) == 0;
            if (!major && !Settings.ShowMinorGrid) continue;
            ctx.DrawLine(major ? majorPen : minorPen, new Point(0, sy), new Point(Bounds.Width, sy));
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

        // Angled walls: line segments between lattice vertices, drawn with the
        // same wall pen. Doors on angled walls render as a gap in the segment
        // (the wall is drawn as the sub-segments between door gaps) plus a door
        // glyph rotated to the segment angle.
        foreach (var w in Document.AngledWalls)
        {
            var a = WorldToScreen(w.Ax, w.Ay);
            var b = WorldToScreen(w.Bx, w.By);
            var doors = Document.GetAngledDoors(w);
            DrawAngledWall(ctx, w, a, b, doors);
        }

        // Labels centered on their cell
        foreach (var l in Document.Labels)
        {
            var p0 = WorldToScreen(l.Cell.X, l.Cell.Y);
            // per-label overrides: FontSize 0 = zoom-scaled auto, color -1 = inherit
            double fs = l.FontSize > 0 ? l.FontSize : Math.Max(10, _scale * 0.35);
            var brush = (l.R >= 0 && l.G >= 0 && l.B >= 0)
                ? new SolidColorBrush(Color.FromRgb((byte)l.R, (byte)l.G, (byte)l.B))
                : LabelBrush;
            var ft = new FormattedText(l.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Inter"), fs, brush);
            var center = new Point(p0.X + _scale / 2, p0.Y + _scale / 2);
            ctx.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
        }

        // Pawns: concentric circles (outer + inner) + outer status border, scaled
        // by zoom. Visual data resolves from the library template (hard reference);
        // a missing template renders an X (broken reference). Drawn after labels.
        foreach (var (c, p) in Document.Pawns)
            DrawPawnGlyph(ctx, c, p.TemplateId, p.Status);

        // per-pawn drag ghost: the dragged pawn follows the cursor as a
        // semi-transparent glyph at the cell it will land on.
        if (_pawnDragging && _pawnDragPawn is not null && _pawnDragEnd != _pawnDragStart)
            DrawPawnGlyph(ctx, _pawnDragEnd, _pawnDragPawn.TemplateId, _pawnDragPawn.Status, 0.45);

        DrawPreview(ctx);
    }

    // ---- drag preview ----
    private bool _dragging;
    private CellKey _dragStart, _dragEnd;
    private EdgeKey? _previewEdge;
    private (AngledWall wall, double t)? _previewAngled; // door ghost on hovered angled segment (t in cell units)
    private EdgeKey? _wallDragStart;
    private bool? _wallDragHorizontal; // orientation locked from the first wall edge clicked
    private bool _wallDragDelete;      // true = dragging removes walls, false = dragging adds walls

    // polygon tool: vertices are LATTICE points (integer grid intersections).
    // Click adds a snapped vertex; clicking the first vertex closes the polygon;
    // Enter closes by appending the first vertex (ClosePolygon).
    private readonly List<(int x, int y)> _polyVerts = new();
    private Point _polyCursor; // world cursor, for the closing preview segment

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

            // ruler: current size in feet above the cursor. 1 grid cell = 5ft, so
            // a 2-displayed-cell room at 10ft spacing = 4 cells = 20ft.
            int wCells = x1 - x0 + 1, hCells = y1 - y0 + 1;
            var rulerText = $"{wCells * 5}x{hCells * 5}ft";
            var rulerFt = new FormattedText(rulerText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Inter"), Math.Max(11, _scale * 0.4), LabelBrush);
            var cursor = WorldToScreen(_dragEnd.X + 0.5, _dragEnd.Y);
            var tx = cursor.X - rulerFt.Width / 2;
            var ty = cursor.Y - rulerFt.Height - 4;
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(180, 20, 20, 25)),
                new Rect(tx - 3, ty - 1, rulerFt.Width + 6, rulerFt.Height + 2));
            ctx.DrawText(rulerFt, new Point(tx, ty));
        }
        if (_previewEdge is EdgeKey pe && (Mode == ToolMode.Wall || Mode == ToolMode.Door))
        {
            var seg = EdgeSegment(pe);
            var a = WorldToScreen(seg.a.X, seg.a.Y);
            var b = WorldToScreen(seg.b.X, seg.b.Y);
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(250, 250, 250)), 2), a, b);
            // door tool ghost: the wall-line preview is invisible over an existing
            // wall, so show the door square icon at the edge midpoint instead
            // Ghost uses the armed default type/state, semi-transparent.
            if (Mode == ToolMode.Door && Document.GetDoor(pe) is null)
            {
                var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                var st = Enum.TryParse<DoorState>(Settings.DefaultDoorState, true, out var s)
                    ? s : DoorState.Closed;
                DrawDoorGlyph(ctx, mid, 0, GhostBrush((SolidColorBrush)DoorBrush(Settings.DefaultDoorType)), st);
            }
        }
        // angled door ghost: hover near an angled segment shows the rotated door
        // square at the projected t, same priority as the click handler.
        if (Mode == ToolMode.Door && _previewAngled is not null)
        {
            var (wall, t) = _previewAngled.Value;
            var doors = Document.GetAngledDoors(wall);
            if (doors.FirstOrDefault(d => Math.Abs(d.T - t) < 0.5) is null)
            {
                var wa = WorldToScreen(wall.Ax, wall.Ay);
                var wb = WorldToScreen(wall.Bx, wall.By);
                double len = Math.Sqrt((wb.X - wa.X) * (wb.X - wa.X) + (wb.Y - wa.Y) * (wb.Y - wa.Y));
                if (len > 0.0001)
                {
                    double ux = (wb.X - wa.X) / len, uy = (wb.Y - wa.Y) / len;
                    var mid = new Point(wa.X + ux * t * _scale, wa.Y + uy * t * _scale);
                    var st = Enum.TryParse<DoorState>(Settings.DefaultDoorState, true, out var s)
                        ? s : DoorState.Closed;
                    DrawDoorGlyph(ctx, mid, Math.Atan2(uy, ux),
                        GhostBrush((SolidColorBrush)DoorBrush(Settings.DefaultDoorType)), st);
                }
            }
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

        // polygon preview: outline of the armed vertices + the closing preview
        // segment (cursor to first vertex) + the cells that would fill.
        if (Mode == ToolMode.Polygon && _polyVerts.Count > 0)
        {
            var polyPen = new Pen(new SolidColorBrush(Color.FromRgb(250, 250, 250)), 2);
            // fill preview: cells whose center is inside the polygon (closed, with
            // the cursor as the provisional last vertex when >=2 vertices armed)
            var previewVerts = new List<(int x, int y)>(_polyVerts);
            if (_polyVerts.Count >= 2)
                previewVerts.Add(((int)Math.Round(_polyCursor.X), (int)Math.Round(_polyCursor.Y)));
            if (previewVerts.Count >= 3)
            {
                var fillCells = Document.PolygonFillCells(previewVerts);
                var fill = new SolidColorBrush(Color.FromArgb(70, 60, 75, 105));
                foreach (var c in fillCells)
                {
                    var p0 = WorldToScreen(c.X, c.Y);
                    ctx.FillRectangle(fill, new Rect(p0.X, p0.Y, _scale, _scale));
                }
            }
            // outline
            for (int i = 0; i < _polyVerts.Count - 1; i++)
            {
                var a = WorldToScreen(_polyVerts[i].x, _polyVerts[i].y);
                var b = WorldToScreen(_polyVerts[i + 1].x, _polyVerts[i + 1].y);
                ctx.DrawLine(polyPen, a, b);
            }
            // closing preview segment: last vertex -> cursor (and cursor -> first
            // vertex when >=2 armed, so the loop shape is visible)
            var last = WorldToScreen(_polyVerts[^1].x, _polyVerts[^1].y);
            var cur = WorldToScreen(_polyCursor.X, _polyCursor.Y);
            ctx.DrawLine(polyPen, last, cur);
            if (_polyVerts.Count >= 2)
                ctx.DrawLine(polyPen, cur, WorldToScreen(_polyVerts[0].x, _polyVerts[0].y));
            // vertex dots
            var dot = new SolidColorBrush(Color.FromRgb(250, 250, 250));
            foreach (var (vx, vy) in _polyVerts)
            {
                var p = WorldToScreen(vx, vy);
                ctx.DrawEllipse(dot, null, p, 3, 3);
            }
        }

        // paste ghost: clipboard preview at the cursor cell. In Select mode it
        // shows all the time (follows the cursor); in Move mode only when a
        // region is armed (not mid-drag), so the user sees where the move lands.
        bool ghostMode = Mode == ToolMode.Select
            || (Mode == ToolMode.Move && HasSelection && !_dragging);
        if (_clipboard is not null && !_clipboard.Empty && _pasteCursor is Point pc && ghostMode && GhostEnabled)
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

    /// Draws one pawn glyph (two-tone circles, unique image, broken-reference X,
    /// status ring) at a cell. Shared by the placed-pawn pass and the per-pawn
    /// drag ghost. alpha < 1 renders it semi-transparent (ghost).
    private void DrawPawnGlyph(DrawingContext ctx, CellKey c, string templateId, string? instanceStatus, double alpha = 1.0)
    {
        var p0 = WorldToScreen(c.X, c.Y);
        var center = new Point(p0.X + _scale / 2, p0.Y + _scale / 2);
        var t = Library.Find(templateId);
        using (ctx.PushOpacity(alpha))
        {
            if (t is null)
            {
                // broken reference: X symbol
                var xPen = new Pen(Brushes.Red, Math.Max(2, _scale * 0.08));
                double r = _scale * 0.3;
                ctx.DrawLine(xPen, new Point(center.X - r, center.Y - r), new Point(center.X + r, center.Y + r));
                ctx.DrawLine(xPen, new Point(center.X - r, center.Y + r), new Point(center.X + r, center.Y - r));
                return;
            }
            double outerR = _scale * 0.42;
            double innerR = _scale * 0.22;
            if (!string.IsNullOrEmpty(t.Image))
            {
                // unique pawn: the image replaces the two-tone circles entirely.
                // center-crop to a square so any aspect ratio fits the cell.
                var bmp = LoadPawnImage(t.Image);
                if (bmp is not null)
                {
                    double side = Math.Min(bmp.PixelSize.Width, bmp.PixelSize.Height);
                    var srcRect = new Rect((bmp.PixelSize.Width - side) / 2, (bmp.PixelSize.Height - side) / 2, side, side);
                    var dest = new Rect(p0.X + _scale * 0.08, p0.Y + _scale * 0.08, _scale * 0.84, _scale * 0.84);
                    ctx.DrawImage(bmp, srcRect, dest);
                }
            }
            else
            {
                var (effOuter, effInner) = t.Effective(Settings);
                var outerBrush = new SolidColorBrush(Color.FromRgb((byte)effOuter.r, (byte)effOuter.g, (byte)effOuter.b));
                var innerBrush = new SolidColorBrush(Color.FromRgb((byte)effInner.r, (byte)effInner.g, (byte)effInner.b));
                ctx.DrawEllipse(outerBrush, null, center, outerR, outerR);
                ctx.DrawEllipse(innerBrush, null, center, innerR, innerR);
            }
            // status border ring on the outer edge. per-instance override wins,
            // null inherits the template's status.
            var st = Settings.FindPawnStatus(instanceStatus ?? t.Status);
            if (st.Thickness > 0)
            {
                var ringPen = new Pen(new SolidColorBrush(Color.FromRgb((byte)st.R, (byte)st.G, (byte)st.B)), st.Thickness);
                ctx.DrawEllipse(null, ringPen, center, outerR, outerR);
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

    /// Nearest angled wall to a world point, within tolerance, plus the projected
    /// parameter t (in world units along the segment) of the click. Null if none.
    private (AngledWall wall, double t)? HitAngledWall(Point world)
    {
        AngledWall? best = null;
        double bestT = 0, bestD = 0.3; // tolerance in cells
        foreach (var w in Document.AngledWalls)
        {
            var a = new Point(w.Ax, w.Ay);
            var b = new Point(w.Bx, w.By);
            double abx = b.X - a.X, aby = b.Y - a.Y;
            double len2 = abx * abx + aby * aby;
            if (len2 == 0) continue;
            double t = Math.Clamp(((world.X - a.X) * abx + (world.Y - a.Y) * aby) / len2, 0, 1);
            double dx = world.X - (a.X + t * abx), dy = world.Y - (a.Y + t * aby);
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < bestD) { bestD = d; best = w; bestT = t * Math.Sqrt(len2); }
        }
        return best is null ? null : (best.Value, bestT);
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
                // Select tool: clicking a cell that has a pawn shows its read-only
                // info (name, affiliation, instances) + Delete + Change Pawn. The
                // select tool must not modify the pawn, so no editing here.
                if (Mode == ToolMode.Select && Document.Pawns.TryGetValue(cell, out var selPawn))
                {
                    PawnConfigRequested?.Invoke(selPawn);
                    break;
                }
                if (Mode == ToolMode.Move && Document.Pawns.TryGetValue(cell, out var hitPawn))
                {
                    // pressing directly on a pawn: per-pawn drag. the pawn follows
                    // the cursor as a ghost and lands on the release cell. a press
                    // without drag (release on the same cell) shows its config.
                    _pawnDragging = true;
                    _pawnDragPawn = hitPawn;
                    _pawnDragStart = cell;
                    _pawnDragEnd = cell;
                    e.Pointer.Capture(this);
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
                _wallDragStart = wallEdge;
                _wallDragHorizontal = wallEdge.Side == Side.North || wallEdge.Side == Side.South;
                if (Document.HasWall(wallEdge))
                {
                    // pressing an existing wall: start a DELETE drag so dragging
                    // over existing walls removes them (Excel-style erase drag).
                    _wallDragDelete = true;
                    Document.SetWall(wallEdge, false);
                    Commit();
                }
                else
                {
                    // pressing empty: start an ADD drag.
                    _wallDragDelete = false;
                    Document.SetWall(wallEdge, true);
                    Commit();
                }
                _dragging = true;
                e.Pointer.Capture(this);
                break;

            case ToolMode.Door:
                // angled walls take priority: a click near an angled wall places a
                // door on it (or opens config for a door already near that spot).
                var aw = HitAngledWall(world);
                if (aw is not null)
                {
                    var (wall, t) = aw.Value;
                    var doors = Document.GetAngledDoors(wall);
                    var near = doors.FirstOrDefault(d => Math.Abs(d.T - t) < 0.5);
                    if (near is not null)
                    {
                        DoorConfigRequested?.Invoke(near);
                    }
                    else if (Document.TryPlaceAngledDoor(wall, t, Settings.DefaultDoorType, Settings.DefaultDoorState, out var ad))
                    {
                        Commit();
                        DoorConfigRequested?.Invoke(ad);
                    }
                    break;
                }
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

            case ToolMode.Pawn:
                if (Document.Pawns.TryGetValue(cell, out var existingPawn))
                {
                    PawnConfigRequested?.Invoke(existingPawn);
                }
                else if (ArmedPawn is not null)
                {
                    // place a pawn that hard-references the armed library template
                    Document.PlacePawn(new Pawn { Cell = cell, TemplateId = ArmedPawn.Id });
                    Commit();
                }
                else
                {
                    StatusMessage?.Invoke("select a pawn in the pawn menu first");
                }
                break;

            case ToolMode.Polygon:
                // snap the click to the nearest LATTICE vertex (grid intersection)
                int vx = (int)Math.Round(world.X), vy = (int)Math.Round(world.Y);
                if (_polyVerts.Count >= 3 && vx == _polyVerts[0].x && vy == _polyVerts[0].y)
                {
                    // clicking the first vertex closes the polygon
                    if (_polyVerts[^1] != (vx, vy)) _polyVerts.Add((vx, vy));
                    bool strict = PolygonStrict;
                    bool passageway = !strict && Document.Cells.Contains(new CellKey(vx, vy));
                    Document.AddPolygon(_polyVerts, passageway, strict);
                    _polyVerts.Clear();
                    Commit();
                    StatusMessage?.Invoke("polygon closed");
                }
                else
                {
                    _polyVerts.Add((vx, vy));
                    StatusMessage?.Invoke($"vertex ({vx},{vy}) added - click first vertex or press Enter to close");
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
        _polyCursor = world; // polygon closing preview follows the cursor
        _pasteCursor = world; // paste ghost follows the cursor
        if (!_selectDragging && !_dragging && !_panning)
        {
            _ghostVisible = true;
            // redraw so the paste ghost tracks the cursor on hover
            if (_clipboard is not null && !_clipboard.Empty && (Mode == ToolMode.Select || Mode == ToolMode.Move))
                InvalidateVisual();
        }

        if (_pawnDragging)
        {
            var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
            if (cell != _pawnDragEnd) { _pawnDragEnd = cell; InvalidateVisual(); }
            return;
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
                        if (_wallDragDelete)
                        {
                            if (Document.HasWall(e2))
                            {
                                Document.SetWall(e2, false);
                                Commit();
                            }
                        }
                        else if (!Document.HasWall(e2))
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
            if (Mode == ToolMode.Door)
            {
                // door ghost priority: a hover near an angled segment shows the
                // door square on that segment (the wall-line preview is invisible
                // over an existing wall).
                var aw = HitAngledWall(world);
                if (aw?.wall != _previewAngled?.wall || aw?.t != _previewAngled?.t)
                { _previewAngled = aw; InvalidateVisual(); }
            }
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

        if (_pawnDragging)
        {
            _pawnDragging = false;
            e.Pointer.Capture(null);
            var world = ScreenToWorld(e.GetPosition(this));
            var target = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
            var pawn = _pawnDragPawn!;
            _pawnDragPawn = null;
            if (target == _pawnDragStart)
            {
                // no drag: release on the same cell shows the pawn's config
                PawnConfigRequested?.Invoke(pawn);
            }
            else
            {
                // per-pawn move: lift the pawn from its source cell, clear
                // obstacles at the landing cell, place it there. one undoable commit.
                // landing on another pawn SWAPS the two pawns' positions (unique
                // to pawns - other elements are overwritten, not swapped).
                var occupant = Document.Pawns.TryGetValue(target, out var occ) ? occ : null;
                ClearCellObstacles(Document, target);
                Document.Pawns.Remove(_pawnDragStart);
                Document.Pawns[target] = new Pawn { Cell = target, TemplateId = pawn.TemplateId, Status = pawn.Status };
                if (occupant is not null)
                {
                    Document.Pawns[_pawnDragStart] = new Pawn { Cell = _pawnDragStart, TemplateId = occupant.TemplateId, Status = occupant.Status };
                    StatusMessage?.Invoke("swapped pawns");
                }
                else
                {
                    StatusMessage?.Invoke($"moved pawn to ({target.X}, {target.Y})");
                }
                Commit();
            }
            InvalidateVisual();
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
                // it at the cursor until the destination click. remember the map
                // it was selected on so the move works after a map switch.
                var (x0, y0, x1, y1) = NormRect(_selectStart, _selectEnd);
                _clipboard = Document.CaptureRegion(x0, y0, x1, y1);
                _moveSourceDoc = Document;
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
                // strict: draw walls on the outer border regardless of existing
                // cells, keep interior walls (sub-room inside a room gets walls).
                // passageway: if the drag started inside an existing room, the
                // shared boundary stays open so the new region connects as a
                // passageway instead of being sealed off.
                bool strict = RoomStrict;
                bool passageway = !strict && Document.Cells.Contains(_dragStart);
                Document.AddRoom(x0, y0, x1, y1, passageway, strict);
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
                        if (f.Pawns) Document.Pawns.Remove(c);
                        if (f.Labels) Document.Labels.RemoveAll(l => l.Cell == c);
                        // remove walls on all four edges of the cell (doors auto-removed)
                        if (f.Walls)
                            foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                                Document.SetWall(new EdgeKey(c, side), false);
                    }
                // angled walls touching the erase rect are removed (Walls filter),
                // doors on them go with the wall (door implies wall)
                if (f.Walls)
                {
                    foreach (var w in Document.AngledWalls
                                 .Where(w => MapDocument.AngledWallIntersectsRect(w, x0, y0, x1, y1)).ToList())
                        Document.AngledDoors.Remove(w);
                    Document.AngledWalls.RemoveAll(w => MapDocument.AngledWallIntersectsRect(w, x0, y0, x1, y1));
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
        DrawDoorGlyph(ctx, mid, 0, DoorBrush(door.Type), door.State);
    }

    /// Draws an angled wall segment with door gaps. The wall is drawn as the
    /// sub-segments between the door gaps (gap half-width = 0.45*scale, matching
    /// axis-aligned doors), then each door glyph is drawn centered at its T along
    /// the segment, rotated to the segment angle. Doors are sorted by T so gaps
    /// tile the segment left-to-right. Door T is stored in CELL units; screen
    /// positions are in pixels, so T is multiplied by _scale to convert.
    private void DrawAngledWall(DrawingContext ctx, AngledWall w, Point a, Point b, List<AngledDoor> doors)
    {
        double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        if (len < 0.0001) return;
        double ux = (b.X - a.X) / len, uy = (b.Y - a.Y) / len; // unit direction
        double gapHalf = _scale * 0.45; // door gap half-width in screen px
        Point At(double tPx) => new Point(a.X + ux * tPx, a.Y + uy * tPx);

        // draw wall sub-segments between gaps (door T in cells -> px via _scale)
        double cursor = 0;
        foreach (var d in doors.OrderBy(d => d.T))
        {
            double center = d.T * _scale;
            double gapStart = center - gapHalf, gapEnd = center + gapHalf;
            if (gapStart > cursor) ctx.DrawLine(WallPen, At(cursor), At(gapStart));
            cursor = Math.Max(cursor, gapEnd);
        }
        if (cursor < len) ctx.DrawLine(WallPen, At(cursor), At(len));

        // draw door glyphs rotated to the segment angle
        foreach (var d in doors)
        {
            var mid = At(d.T * _scale);
            double ang = Math.Atan2(uy, ux);
            DrawDoorGlyph(ctx, mid, ang, DoorBrush(d.Type), d.State);
        }
    }
}
