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

    /// Loads (and caches) a pawn image bitmap from the pawn_images folder.
    /// Public so the pawn management modal and the pawn tool swatch grids can
    /// render template images in their swatches, not just on the map.
    public Avalonia.Media.Imaging.Bitmap? LoadPawnImage(string fileName)
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
    // armed move + rotation: after a move region is dragged, the snapshot stays
    // armed so the destination ghost persists (and can be re-placed). _rotTurns
    // is the counterclockwise quarter-turns applied to the armed snapshot.
    private bool _moveArmed;
    private int _rotTurns;
    private (int x0, int y0, int x1, int y1)? _moveSourceRect; // source region to clear on first placement
    private bool _moveSourceCleared; // source cleared on the first placement; a placement disarms, so it never re-clears
    private bool _movePlaced; // a placement happened; hotkeys restored
    public bool MoveArmed => _moveArmed;
    // rotation is available when a snapshot is armed in Move mode, or a clipboard
    // exists in Select mode (copy/cut paste). In Move mode a stale clipboard from
    // an earlier copy must NOT be rotatable - only the armed snapshot is.
    public bool CanRotate => _clipboard is not null && !_clipboard.Empty
        && (Mode == ToolMode.Select || (Mode == ToolMode.Move && _moveArmed));
    public EraseFilter EraseFilter { get; } = new(); // categories the Erase tool removes
    public bool EraseAdjustWalls { get; set; } = false; // Erase tool: after erasing, rebuild room boundary walls so the new outer edge gets walls
    public bool RoomStrict { get; set; } = false; // Room tool: draw walls on the outer border regardless of existing cells, keep interior walls
    public bool RoomMergeNearby { get; set; } = false; // Room tool: a room drawn adjacent to (touching, not overlapping) an existing room connects to it - shared boundary walls stay open
    public bool PolygonStrict { get; set; } = false; // Polygon tool: same as RoomStrict - outer walls always, interior walls kept
    public bool PolygonMergeNearby { get; set; } = false; // Polygon tool: a polygon drawn adjacent to (touching, not overlapping) an existing room connects to it - shared boundary walls stay open

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

    /// Places the armed (possibly rotated) snapshot at the target cell. The
    /// source region is cleared on the FIRST placement only (Excel-style shift,
    /// so a move that overlaps its own source survives); later placements paste
    /// again without re-clearing, so the ghost can be re-placed after rotation.
    /// The armed state persists after placement so the destination ghost keeps
    /// showing for rotation and repeat placement.
    public void MoveAt(CellKey target)
    {
        if (!_moveArmed || _clipboard is null || _clipboard.Empty)
        { StatusMessage?.Invoke("nothing armed to move"); return; }

        // first placement clears the source on the map it was selected on.
        // move-pawns-only keeps the old contract: only the pawns leave the source,
        // cells/walls/stairs/labels stay put.
        if (!_moveSourceCleared)
        {
            var src = _moveSourceDoc ?? Document;
            var (sx0, sy0, sx1, sy1) = _moveSourceRect!.Value;
            // braces matter here: without them the else binds to the inner if inside
            // the foreach, so the normal (non pawns-only) path never clears the source.
            if (MovePawnsOnly)
            {
                foreach (var c in src.Pawns.Keys.ToList())
                    if (c.X >= sx0 && c.X <= sx1 && c.Y >= sy0 && c.Y <= sy1) src.Pawns.Remove(c);
            }
            else
            {
                src.ClearRegion(sx0, sy0, sx1, sy1);
            }
            _moveSourceCleared = true;
        }

        int count; string what;
        if (MovePawnsOnly)
        {
            count = PlacePawns(_clipboard, target);
            what = $"{count} pawns";
        }
        else
        {
            Document.PasteRegion(_clipboard, target.X, target.Y);
            count = _clipboard.Cells.Count;
            what = $"{count} cells";
        }
        _movePlaced = true; // successful placement restores hotkeys
        StatusMessage?.Invoke(_rotTurns != 0
            ? $"placed {what} ({_rotTurns * 90}° ccw)"
            : $"moved {what}");
        Commit();
        // one placement per arm: a successful placement disarms, so the ghost, the
        // rotation, and the hotkey lock all end with the move. The original contract
        // was one placement too - the cleared source left nothing to place again.
        CancelMove();
    }

    /// Places the pawns in a snapshot at the target, swapping with any occupant
    /// pawn at a landing cell (unique to pawns). Returns the pawn count.
    private int PlacePawns(MapDocument.RegionSnapshot snap, CellKey target)
    {
        int dx = target.X - snap.OriginX, dy = target.Y - snap.OriginY;
        var swaps = new List<(CellKey dest, Pawn occupant)>();
        foreach (var p in snap.Pawns)
        {
            var nc = new CellKey(p.Cell.X + dx, p.Cell.Y + dy);
            bool moverLandsHere = snap.Pawns.Any(q => q.Cell.X + dx == nc.X && q.Cell.Y + dy == nc.Y);
            if (!moverLandsHere && Document.Pawns.TryGetValue(nc, out var occ))
                swaps.Add((nc, occ));
        }
        foreach (var p in snap.Pawns)
        {
            var nc = new CellKey(p.Cell.X + dx, p.Cell.Y + dy);
            ClearCellObstacles(Document, nc);
            Document.Pawns[nc] = new Pawn { Cell = nc, TemplateId = p.TemplateId, Status = p.Status };
        }
        foreach (var (dest, occ) in swaps)
        {
            var moverSrc = new CellKey(dest.X - dx, dest.Y - dy);
            Document.Pawns[moverSrc] = new Pawn { Cell = moverSrc, TemplateId = occ.TemplateId, Status = occ.Status };
        }
        return snap.Pawns.Count;
    }

    /// Cancels an armed move: disarms the ghost, resets rotation, restores
    /// hotkeys. Fires on Escape, tool switch, and panel button click. Map switch
    /// deliberately KEEPS the armed move so cross-map placement works.
    public void CancelMove()
    {
        if (!_moveArmed) return;
        _moveArmed = false;
        _movePlaced = false;
        _rotTurns = 0;
        _moveSourceRect = null;
        _moveSourceCleared = false;
        ClearSelection();
        InvalidateVisual();
    }

    /// Hotkeys (tool-switch letters, Delete) are disabled while a move is armed
    /// and not yet placed. A successful placement, a cancel, a tool switch, or a
    /// panel button click restores them.
    public bool HotkeysDisabled => _moveArmed && !_movePlaced;

    /// Removes everything incompatible with a pawn landing on a cell: stairs,
    /// labels, and straight walls on the cell's edges - straight walls are
    /// cleared ONLY when the cell is not a room/floor tile (a pawn landing on a
    /// room edge keeps the room's walls). Angled walls and angled doors are
    /// NEVER removed by a pawn landing - the intersection rules are too complex,
    /// so angled geometry is left alone in every case. Room/floor cells
    /// themselves are never removed - a pawn sits on top of a floor tile.
    private static void ClearCellObstacles(MapDocument doc, CellKey c)
    {
        doc.Stairs.Remove(c);
        doc.Labels.RemoveAll(l => l.Cell == c);
        bool onFloor = doc.Cells.Contains(c);
        if (!onFloor)
            foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                doc.SetWall(new EdgeKey(c, side), false);
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
        if (EraseAdjustWalls && f.Cells) Document.RebuildRoomBoundaryWalls(x0, y0, x1, y1);
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
        _moveArmed = false;
        _rotTurns = 0;
        _moveSourceRect = null;
        _moveSourceCleared = false;
    }

    /// Rotates the armed snapshot by turns counterclockwise quarter-turns (R =
    /// clockwise = -1, Shift+R / shift+scroll up = counterclockwise = +1). The
    /// rotation is applied to the stored snapshot so the destination ghost and
    /// the placement both reflect it. In Move mode the source region is untouched
    /// until the first placement, so re-rotating before placing is free.
    public void RotateRegion(int turns)
    {
        if (_clipboard is null || _clipboard.Empty) { StatusMessage?.Invoke("nothing armed to rotate"); return; }
        _rotTurns = (((_rotTurns + turns) % 4) + 4) % 4;
        _clipboard = MapDocument.Rotate(_clipboard, turns);
        StatusMessage?.Invoke($"rotated {(_rotTurns * 90)}° ccw");
        InvalidateVisual();
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
        Document.AddPolygon(_polyVerts, passageway, strict, PolygonMergeNearby);
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

    private const double MinScale = 2, MaxScale = 200;

    // High-contrast palette (dark bg, bright fills)
    // Canvas element colors come from Settings (settings menu). The old static
    // GridLine/FaintGridLine brushes were dead code - grid rendering uses the
    // MajorGrid/MinorGrid settings colors - and were removed.
    // Pen/Brush caching: the old properties allocated a fresh object on EVERY
    // access, so a 100x100 room (400 wall lines) cost 400 Pen allocations per
    // frame. Each is cached and rebuilt only when its settings color changes.
    private IBrush? _bgBrush; private int _bgR, _bgG, _bgB;
    private IBrush Background
    {
        get
        {
            if (_bgBrush is null || _bgR != Settings.BackgroundR || _bgG != Settings.BackgroundG || _bgB != Settings.BackgroundB)
            {
                _bgR = Settings.BackgroundR; _bgG = Settings.BackgroundG; _bgB = Settings.BackgroundB;
                _bgBrush = new SolidColorBrush(Color.FromRgb((byte)_bgR, (byte)_bgG, (byte)_bgB));
            }
            return _bgBrush;
        }
    }
    private IBrush? _cellFillBrush; private int _cfR, _cfG, _cfB;
    private IBrush CellFill
    {
        get
        {
            if (_cellFillBrush is null || _cfR != Settings.CellFillR || _cfG != Settings.CellFillG || _cfB != Settings.CellFillB)
            {
                _cfR = Settings.CellFillR; _cfG = Settings.CellFillG; _cfB = Settings.CellFillB;
                _cellFillBrush = new SolidColorBrush(Color.FromRgb((byte)_cfR, (byte)_cfG, (byte)_cfB));
            }
            return _cellFillBrush;
        }
    }
    private Pen? _wallPenField; private int _wpR, _wpG, _wpB;
    private IPen WallPen
    {
        get
        {
            if (_wallPenField is null || _wpR != Settings.WallR || _wpG != Settings.WallG || _wpB != Settings.WallB)
            {
                _wpR = Settings.WallR; _wpG = Settings.WallG; _wpB = Settings.WallB;
                _wallPenField = new Pen(new SolidColorBrush(Color.FromRgb((byte)_wpR, (byte)_wpG, (byte)_wpB)), 3);
            }
            return _wallPenField;
        }
    }
    private IBrush? _stairBrush; private int _scR, _scG, _scB;
    private IBrush StairColor
    {
        get
        {
            if (_stairBrush is null || _scR != Settings.StairColorR || _scG != Settings.StairColorG || _scB != Settings.StairColorB)
            {
                _scR = Settings.StairColorR; _scG = Settings.StairColorG; _scB = Settings.StairColorB;
                var col = Color.FromRgb((byte)_scR, (byte)_scG, (byte)_scB);
                _stairBrush = new SolidColorBrush(col);
                _stairTint = new SolidColorBrush(Color.FromArgb(90, col.R, col.G, col.B));
                _stairTintCol = col;
            }
            return _stairBrush;
        }
    }
    // the 90-alpha stair fill tint, cached alongside StairColor (the stair loop
    // used to allocate a tint brush per stair)
    private IBrush? _stairTint; private Color _stairTintCol;
    private IBrush StairTint(Color col)
    {
        if (_stairTint is null || _stairTintCol != col)
        {
            _stairTintCol = col;
            _stairTint = new SolidColorBrush(Color.FromArgb(90, col.R, col.G, col.B));
        }
        return _stairTint;
    }
    private IBrush? _labelBrush; private int _lbR, _lbG, _lbB;
    private IBrush LabelBrush
    {
        get
        {
            if (_labelBrush is null || _lbR != Settings.LabelR || _lbG != Settings.LabelG || _lbB != Settings.LabelB)
            {
                _lbR = Settings.LabelR; _lbG = Settings.LabelG; _lbB = Settings.LabelB;
                _labelBrush = new SolidColorBrush(Color.FromRgb((byte)_lbR, (byte)_lbG, (byte)_lbB));
            }
            return _labelBrush;
        }
    }

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

    /// True when polygon edge i is shared with an existing room (merged): the
    /// cells on both sides of the edge are room cells. The outline stroke skips
    /// merged edges so the boundary disappears where the polygon meets a room.
    private bool IsPolygonEdgeMerged(List<(int x, int y)> verts, int i)
    {
        var (ax, ay) = verts[i];
        var (bx, by) = verts[(i + 1) % verts.Count];
        if (ax == bx)
        {
            int y0 = Math.Min(ay, by), y1 = Math.Max(ay, by);
            for (int y = y0; y < y1; y++)
                if (Document.Cells.Contains(new CellKey(ax - 1, y)) && Document.Cells.Contains(new CellKey(ax, y)))
                    return true;
            return false;
        }
        if (ay == by)
        {
            int x0 = Math.Min(ax, bx), x1 = Math.Max(ax, bx);
            for (int x = x0; x < x1; x++)
                if (Document.Cells.Contains(new CellKey(x, ay - 1)) && Document.Cells.Contains(new CellKey(x, ay)))
                    return true;
            return false;
        }
        var w = new AngledWall(ax, ay, bx, by);
        var (c1, c2) = MapDocument.DiagonalSideCells(w);
        return Document.Cells.Contains(c1) && Document.Cells.Contains(c2);
    }

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
        // polygon-covered cells get their floor from the polygon geometry path
        // below, NOT from cell rectangles. A cell whose center is inside the
        // polygon is a fill cell, and drawing it as a full-cell rect would
        // paint the whole square even where a polygon edge cuts the cell (the
        // base-corner cells of a triangle). Remove fill cells from the
        // merged-rect pass, and keep the half-pixel pad from bleeding room
        // floor into a polygon cell across the polygon edge.
        // culled to the visible window: off-screen polygons cost nothing. Fill
        // cells are computed only for polygons whose bounds intersect the
        // window, clamped to the window range, so a huge polygon enclosing the
        // viewport costs O(window area), not O(polygon area).
        int cx0 = (int)Math.Floor(topLeft.X), cx1 = (int)Math.Ceiling(bottomRight.X);
        int cy0 = (int)Math.Floor(topLeft.Y), cy1 = (int)Math.Ceiling(bottomRight.Y);
        var polyCells = new HashSet<CellKey>();
        foreach (var p in Document.Polygons)
        {
            if (p.Verts.Count < 3) continue;
            int pminX = p.Verts.Min(v => v.x), pmaxX = p.Verts.Max(v => v.x);
            int pminY = p.Verts.Min(v => v.y), pmaxY = p.Verts.Max(v => v.y);
            if (pmaxX < topLeft.X || pminX > bottomRight.X + 1 || pmaxY < topLeft.Y || pminY > bottomRight.Y + 1) continue;
            polyCells.UnionWith(Document.PolygonFillCells(p.Verts, cx0, cx1, cy0, cy1));
        }

        // Visible cells only. The greedy pass below needs top-left-first order;
        // iterating the viewport area row-by-row gives that for free (no LINQ
        // sort) and off-screen cells never enter the scan. When the viewport is
        // larger than the document (zoomed way out), iterate the document and
        // cull instead - O(min(window area, document size)) per frame.
        var visibleCells = new List<CellKey>();
        var remaining = new HashSet<CellKey>();
        long windowArea = (long)(cx1 - cx0 + 1) * (cy1 - cy0 + 1);
        if (windowArea <= Document.Cells.Count)
        {
            for (int y = cy0; y <= cy1; y++)
                for (int x = cx0; x <= cx1; x++)
                {
                    var k = new CellKey(x, y);
                    if (Document.Cells.Contains(k) && !polyCells.Contains(k))
                    { visibleCells.Add(k); remaining.Add(k); }
                }
        }
        else
        {
            foreach (var c in Document.Cells.Where(c => c.X >= cx0 && c.X <= cx1 && c.Y >= cy0 && c.Y <= cy1)
                                             .OrderBy(c => c.Y).ThenBy(c => c.X))
            {
                if (!polyCells.Contains(c))
                { visibleCells.Add(c); remaining.Add(c); }
            }
        }
        foreach (var c in visibleCells)
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
            double rw = w * _scale + (Document.Cells.Contains(new CellKey(c.X + w, c.Y)) && (!polyCells.Contains(new CellKey(c.X + w, c.Y)) || !Document.HasWall(new EdgeKey(new CellKey(c.X + w - 1, c.Y), Side.East))) ? pad : 0);
            double rh = h * _scale + (Document.Cells.Contains(new CellKey(c.X, c.Y + h)) && (!polyCells.Contains(new CellKey(c.X, c.Y + h)) || !Document.HasWall(new EdgeKey(new CellKey(c.X, c.Y + h - 1), Side.South))) ? pad : 0);
            var p0 = WorldToScreen(c.X, c.Y);
            ctx.FillRectangle(CellFill, new Rect(p0.X, p0.Y, rw, rh));
        }

        // polygon floors: the exact polygon shape, not the cell squares. The
        // loop is filled as geometry, then stroked with the same brush: half the
        // stroke width expands the fill outward, closing the anti-aliasing seam
        // where a polygon edge meets a neighboring room rectangle (the same
        // trick as the 0.6px rect overlap above). Grid lines draw on top.
        foreach (var p in Document.Polygons)
        {
            if (p.Verts.Count < 3) continue;
            int pminX = p.Verts.Min(v => v.x), pmaxX = p.Verts.Max(v => v.x);
            int pminY = p.Verts.Min(v => v.y), pmaxY = p.Verts.Max(v => v.y);
            if (pmaxX < topLeft.X || pminX > bottomRight.X + 1 || pmaxY < topLeft.Y || pminY > bottomRight.Y + 1) continue;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(WorldToScreen(p.Verts[0].x, p.Verts[0].y), true);
                for (int i = 1; i < p.Verts.Count; i++)
                    g.LineTo(WorldToScreen(p.Verts[i].x, p.Verts[i].y), true);
            }
            // fill the full geometry: the floor stays continuous even where an
            // edge is merged. The outline is stroked edge-by-edge below, skipping
            // merged edges (shared with a room) so the boundary disappears.
            ctx.DrawGeometry(CellFill, null, geo);
            var strokePen = new Pen(CellFill, 1.2);
            for (int i = 0; i < p.Verts.Count; i++)
            {
                var (ax, ay) = p.Verts[i];
                var (bx, by) = p.Verts[(i + 1) % p.Verts.Count];
                if (IsPolygonEdgeMerged(p.Verts, i)) continue; // shared with a room
                ctx.DrawLine(strokePen, WorldToScreen(ax, ay), WorldToScreen(bx, by));
            }
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

        // Stairs: fill tint + axis-aligned triangle. Up = tip up, Down = tip
        // down, Left = tip left, Right = tip right. No diagonal glyphs: the
        // triangle's tip is the direction the stairs face.
        foreach (var (c, s) in Document.Stairs)
        {
            if (c.X + 1 < topLeft.X || c.X > bottomRight.X || c.Y + 1 < topLeft.Y || c.Y > bottomRight.Y) continue;
            var p0 = WorldToScreen(c.X, c.Y);
            var rect = new Rect(p0.X, p0.Y, _scale, _scale);
            // single stair color: every direction renders in the same color.
            var brush = StairColor;
            var col = ((SolidColorBrush)brush).Color;
            ctx.FillRectangle(StairTint(col), rect);
            var center = new Point(p0.X + _scale / 2, p0.Y + _scale / 2);
            double a = _scale * 0.28;
            // tip points along the direction, base sits opposite the tip
            double tipX = center.X, tipY = center.Y;
            double b1x = center.X, b1y = center.Y, b2x = center.X, b2y = center.Y;
            switch (s.Direction)
            {
                case StairDirection.Up:
                    tipY = center.Y - a; b1x = center.X - a; b1y = center.Y + a; b2x = center.X + a; b2y = center.Y + a;
                    break;
                case StairDirection.Down:
                    tipY = center.Y + a; b1x = center.X - a; b1y = center.Y - a; b2x = center.X + a; b2y = center.Y - a;
                    break;
                case StairDirection.Left:
                    tipX = center.X - a; b1x = center.X + a; b1y = center.Y - a; b2x = center.X + a; b2y = center.Y + a;
                    break;
                case StairDirection.Right:
                    tipX = center.X + a; b1x = center.X - a; b1y = center.Y - a; b2x = center.X - a; b2y = center.Y + a;
                    break;
            }
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(tipX, tipY), isFilled: true);
                g.LineTo(new Point(b1x, b1y));
                g.LineTo(new Point(b2x, b2y));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(brush, null, geo);
        }

        // Walls, with door symbol where a door exists. Culled to the visible
        // window: a 100x100 room adds 400 perimeter walls, and the old loop
        // iterated every wall in the document on every frame, on-screen or not.
        foreach (var (e, has) in Document.Walls)
        {
            if (!has) continue;
            var seg = EdgeSegment(e);
            if (Math.Max(seg.a.X, seg.b.X) < topLeft.X || Math.Min(seg.a.X, seg.b.X) > bottomRight.X
                || Math.Max(seg.a.Y, seg.b.Y) < topLeft.Y || Math.Min(seg.a.Y, seg.b.Y) > bottomRight.Y) continue;
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
            if (Math.Max(w.Ax, w.Bx) + 1 < topLeft.X || Math.Min(w.Ax, w.Bx) - 1 > bottomRight.X
                || Math.Max(w.Ay, w.By) + 1 < topLeft.Y || Math.Min(w.Ay, w.By) - 1 > bottomRight.Y) continue;
            var a = WorldToScreen(w.Ax, w.Ay);
            var b = WorldToScreen(w.Bx, w.By);
            var doors = Document.GetAngledDoors(w);
            DrawAngledWall(ctx, w, a, b, doors);
        }

        // Vertex doors: centered on the lattice vertex, fixed 5ft (one cell)
        // width, straddling the two collinear walls. Punch a full-cell gap along
        // the orientation, then draw the glyph rotated to it.
        foreach (var (k, d) in Document.VertexDoors)
        {
            if (k.X + 1 < topLeft.X || k.X - 1 > bottomRight.X || k.Y + 1 < topLeft.Y || k.Y - 1 > bottomRight.Y) continue;
            var v = WorldToScreen(k.X, k.Y);
            double gap = _scale; // 5ft door = one cell of wall width
            var gapRect = k.Horizontal
                ? new Rect(v.X - gap / 2, v.Y - 1.5, gap, 3)
                : new Rect(v.X - 1.5, v.Y - gap / 2, 3, gap);
            ctx.FillRectangle(Background, gapRect);
            DrawDoorGlyph(ctx, v, k.Horizontal ? 0 : Math.PI / 2, DoorBrush(d.Type), d.State);
        }

        // Labels centered on their cell
        foreach (var l in Document.Labels)
        {
            if (l.Cell.X + 1 < topLeft.X || l.Cell.X > bottomRight.X || l.Cell.Y + 1 < topLeft.Y || l.Cell.Y > bottomRight.Y) continue;
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
        {
            if (c.X + 1 < topLeft.X || c.X > bottomRight.X || c.Y + 1 < topLeft.Y || c.Y > bottomRight.Y) continue;
            DrawPawnGlyph(ctx, c, p.TemplateId, p.Status);
        }

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
    private (int x, int y, bool horizontal)? _previewVertex; // door ghost snapped to a lattice vertex
    private EdgeKey? _wallDragStart;
    private bool? _wallDragHorizontal; // orientation locked from the first wall edge clicked
    private bool _wallDragDelete;      // true = dragging removes walls, false = dragging adds walls
    // wall drag commits are batched: the press commits the first wall, drag
    // moves mutate the document with repaint-only invalidation, and the release
    // commits once. Per-edge commits serialized the WHOLE document per pointer
    // move (3.6 ms/commit on a 20k-cell doc), so a long wall-line drag froze the
    // UI and burned 100 history snapshots. One commit per drag = one undo step.
    private bool _wallDragDirty;

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
            if (Mode == ToolMode.Door && Document.GetDoor(pe) is null && _previewVertex is null)
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
        // vertex door ghost: door square centered on the lattice vertex, rotated
        // to its orientation; suppressed when a door already sits at the vertex
        if (Mode == ToolMode.Door && _previewVertex is not null)
        {
            var (vx, vy, hor) = _previewVertex.Value;
            if (Document.GetVertexDoor(new VertexDoorKey(vx, vy, hor)) is null)
            {
                var v = WorldToScreen(vx, vy);
                var st = Enum.TryParse<DoorState>(Settings.DefaultDoorState, true, out var s)
                    ? s : DoorState.Closed;
                DrawDoorGlyph(ctx, v, hor ? 0 : Math.PI / 2,
                    GhostBrush((SolidColorBrush)DoorBrush(Settings.DefaultDoorType)), st);
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
                // the preview shows the EXACT polygon shape, same as the
                // committed floor render: fill the loop as geometry and stroke
                // with the same brush to expand the fill outward.
                var fill = new SolidColorBrush(Color.FromArgb(70, 60, 75, 105));
                var previewGeo = new StreamGeometry();
                using (var g = previewGeo.Open())
                {
                    g.BeginFigure(WorldToScreen(previewVerts[0].x, previewVerts[0].y), true);
                    for (int i = 1; i < previewVerts.Count; i++)
                        g.LineTo(WorldToScreen(previewVerts[i].x, previewVerts[i].y), true);
                }
                ctx.DrawGeometry(fill, new Pen(fill, 1.2), previewGeo);
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
        // shows all the time (follows the cursor); in Move mode it shows while a
        // move is ARMED (the armed snapshot persists after a placement, so the
        // destination ghost keeps showing for rotation and repeat placement -
        // the old HasSelection check killed the ghost after the first move).
        bool ghostMode = Mode == ToolMode.Select
            || (Mode == ToolMode.Move && _moveArmed && !_dragging);
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

    /// Nearest lattice vertex (grid intersection) to a world point, within the
    /// same tolerance as HitEdge; null if none.
    private (int x, int y, double dist)? HitVertex(Point world)
    {
        int vx = (int)Math.Round(world.X), vy = (int)Math.Round(world.Y);
        double dx = world.X - vx, dy = world.Y - vy;
        double d = Math.Sqrt(dx * dx + dy * dy);
        return d <= 0.3 ? (vx, vy, d) : null;
    }

    /// Distance from a world point to the midpoint of an edge (where the edge
    /// door ghost sits), used for the vertex-vs-edge-midpoint snap comparison.
    private static double MidDist(Point p, EdgeKey e)
    {
        var (a, b) = EdgeSegment(e);
        double mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2;
        return Math.Sqrt((p.X - mx) * (p.X - mx) + (p.Y - my) * (p.Y - my));
    }

    /// Orientation for a vertex door at lattice vertex (vx,vy): the orientation
    /// whose perpendicular pair is empty (no wall bisecting the door). If both
    /// orientations are valid, align to the nearest of the four edges touching
    /// the vertex. Null = crossroads/corner: no valid orientation exists.
    private bool? VertexDoorOrientation(Point world, int vx, int vy)
    {
        bool h = Document.VertexDoorValid(vx, vy, true);
        bool v = Document.VertexDoorValid(vx, vy, false);
        if (!h && !v) return null;
        if (h && v)
        {
            var edges = new[]
            {
                new EdgeKey(new CellKey(vx - 1, vy), Side.North),
                new EdgeKey(new CellKey(vx, vy), Side.North),
                new EdgeKey(new CellKey(vx - 1, vy - 1), Side.East),
                new EdgeKey(new CellKey(vx - 1, vy), Side.East),
            };
            EdgeKey best = edges[0]; double bestD = double.MaxValue;
            foreach (var e in edges) { double d = MidDist(world, e); if (d < bestD) { bestD = d; best = e; } }
            return best.Side == Side.North;
        }
        return h;
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
                    // a region is already armed; this click is the move target.
                    // the armed snapshot persists after placement so the ghost
                    // keeps showing for rotation and repeat placement.
                    MoveAt(cell);
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
                // vertex doors: the ghost snaps to the nearest of edge midpoint /
                // grid vertex, vertex wins ties. A vertex door is centered on the
                // lattice point, straddling two collinear walls; crossroads and
                // corners are rejected (a wall at the vertex bisects the door).
                var vt = HitVertex(world);
                if (vt is not null && (de is null || MidDist(world, de.Value) >= vt.Value.dist - 1e-9))
                {
                    var (vdxx, vdy, _) = vt.Value;
                    var orient = VertexDoorOrientation(world, vdxx, vdy);
                    if (orient is null)
                    {
                        StatusMessage?.Invoke("can't place a door here: a wall at the vertex would bisect the door");
                        break;
                    }
                    var vkey = new VertexDoorKey(vdxx, vdy, orient.Value);
                    var existingVd = Document.GetVertexDoor(vkey);
                    if (existingVd is not null)
                    {
                        DoorConfigRequested?.Invoke(existingVd);
                    }
                    else
                    {
                        // door implies wall: TryPlaceVertexDoor auto-creates both straddled walls
                        Document.TryPlaceVertexDoor(vdxx, vdy, orient.Value, Settings.DefaultDoorType, Settings.DefaultDoorState, out var vdoor);
                        Commit();
                        DoorConfigRequested?.Invoke(vdoor);
                    }
                    break;
                }
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
                    Document.AddPolygon(_polyVerts, passageway, strict, PolygonMergeNearby);
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
                                _wallDragDirty = true;
                                InvalidateVisual();
                            }
                        }
                        else if (!Document.HasWall(e2))
                        {
                            Document.SetWall(e2, true);
                            _wallDragDirty = true;
                            InvalidateVisual();
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
                // vertex ghost: vertex wins ties over the edge midpoint
                var vt = HitVertex(world);
                (int x, int y, bool horizontal)? pv = null;
                if (vt is not null && (edge is null || MidDist(world, edge.Value) >= vt.Value.dist - 1e-9))
                {
                    var o = VertexDoorOrientation(world, vt.Value.x, vt.Value.y);
                    if (o is not null) pv = (vt.Value.x, vt.Value.y, o.Value);
                }
                if (pv != _previewVertex) { _previewVertex = pv; InvalidateVisual(); }
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
                var snap = Document.CaptureRegion(x0, y0, x1, y1);
                if (!snap.Empty)
                {
                    _clipboard = snap;
                    _moveSourceDoc = Document;
                    _cutSource = null;
                    _moveArmed = true;
                    _moveSourceRect = (x0, y0, x1, y1);
                    _moveSourceCleared = false;
                    _movePlaced = false;
                    _rotTurns = 0;
                }
            }
            if (_selectStart == _selectEnd)
            {
                // single cell: manage the element under the cursor
                var edge = HitEdge(world);
                var vt = HitVertex(world);
                VertexDoor? vd = null;
                if (vt is not null && (edge is null || MidDist(world, edge.Value) >= vt.Value.dist - 1e-9))
                    vd = Document.GetVertexDoor(new VertexDoorKey(vt.Value.x, vt.Value.y, true))
                      ?? Document.GetVertexDoor(new VertexDoorKey(vt.Value.x, vt.Value.y, false));
                var door = edge is not null ? Document.GetDoor(edge.Value) : null;
                if (vd is not null) DoorConfigRequested?.Invoke(vd);
                else if (door is not null) DoorConfigRequested?.Invoke(door);
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
                Document.AddRoom(x0, y0, x1, y1, passageway, strict, RoomMergeNearby);
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
                    // direction from the drag (StairDrag.FromDrag): horizontal
                    // L-to-R = Right, R-to-L = Left; vertical bottom-to-top =
                    // Up, top-to-bottom = Down; single click defaults to Down.
                    // The config dropdown overrides.
                    var dir = StairDrag.FromDrag(_dragEnd.X - _dragStart.X, _dragEnd.Y - _dragStart.Y);
                    Stair first = null!;
                    for (int x = x0; x <= x1; x++)
                        for (int y = y0; y <= y1; y++)
                        {
                            var c = new CellKey(x, y);
                            if (!Document.Stairs.TryGetValue(c, out var s))
                            {
                                s = new Stair { Direction = dir };
                                Document.Stairs[c] = s;
                            }
                            if (first is null) first = s;
                        }
                    Commit();
                    StairConfigRequested?.Invoke(first);
                }
                break;
            }
            case ToolMode.Wall:
                _wallDragStart = null;
                if (_wallDragDirty)
                {
                    _wallDragDirty = false;
                    Commit();
                }
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
                // polygons touching the erase rect are removed as whole objects
                // (loop, fill cells, boundary walls, angled walls, doors). The
                // Cells filter gates them: a polygon's floor is cell geometry,
                // and its walls go with the object - the rebuild below re-derives
                // the surviving rooms' boundaries, so no wall lines are left
                // floating over empty space.
                if (f.Cells)
                    foreach (var p in Document.Polygons
                                 .Where(p => MapDocument.PolygonTouchesRect(p, x0, y0, x1, y1)).ToList())
                        Document.RemovePolygon(p);
                // "adjust walls": the erase may have removed outer walls (or the
                // room cells they sat on) - rebuild the room boundary so the new
                // outer edge gets fresh walls. Scoped to the erase region: a
                // global pass repaints polygon diagonals into staircases.
                if (EraseAdjustWalls && f.Cells) Document.RebuildRoomBoundaryWalls(x0, y0, x1, y1);
                Commit();
                break;
            }
        }
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        // shift + scroll rotates the armed snapshot instead of zooming: scroll up
        // = counterclockwise, scroll down = clockwise. Only when a snapshot is
        // armed in Move/Select mode; otherwise shift+scroll falls through to zoom.
        if (e.KeyModifiers == KeyModifiers.Shift && CanRotate)
        {
            RotateRegion(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }
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
