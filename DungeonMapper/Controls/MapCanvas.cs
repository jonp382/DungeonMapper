using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

public enum ToolMode { Select, Room, Floor, Wall, Door, Stair, Label, Erase, Pawn, Polygon, Object, Measure, Reveal, Image, Sight }

/// Which element categories the Erase tool removes. Doors follow walls (a door
/// implies a wall), so a door is removed when its wall is erased.
public sealed class EraseFilter
{
    public bool Cells = true;
    public bool Walls = true;
    public bool Stairs = true;
    public bool Labels = true;
    public bool Pawns = true;
    public bool Objects = true;
    public bool Images = true;
}

/// Custom control that renders a MapDocument and hosts the editing tools.
/// World units = cells (5ft). screen = (world - offset) * scale.
/// Right- or middle-drag pans, wheel zooms (cursor-anchored). Left-click/drag edits per ToolMode.
/// Raises ActionCompleted after each edit so the owner can commit to history;
/// raises config requests for door/stair/label dialogs.
public sealed partial class MapCanvas : Control
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

    private MapDocument _document = new();
    /// the map drawn and edited. during the player view preview this is the
    /// players' copy (read only, rebuilt on every Refresh); setting it always
    /// sets the real map
    public MapDocument Document
    {
        get => _remote ?? _preview ?? _document;
        set { _document = value; if (_preview is not null) _preview = PlayerView.Build(_document); }
    }
    public ToolMode Mode { get; set; } = ToolMode.Select;
    public AppSettings Settings { get; set; } = AppSettings.Default();
    public PawnLibrary Library { get; set; } = new();
    // door presets, pawn statuses and affiliation colors of the open campaign
    public CampaignPresets Presets { get; set; } = CampaignPresets.Default();
    // what a new door gets. session only: every start begins with a standard
    // closed door, and a campaign switch or preset delete that leaves the type
    // missing puts it back to standard.
    public string DefaultDoorType { get; set; } = CampaignPresets.StandardDoor;
    public string DefaultDoorState { get; set; } = "closed";
    /// Template armed for placement from the floating submenu. Clicking a cell
    /// places a pawn referencing this template. Null = no pawn armed.
    public PawnTemplate? ArmedPawn { get; set; }
    // bitmap cache keyed by template Id; images are base64 on the template
    // (PawnTemplate.ImageB64), decoded in memory - no pawn_images folder.
    private readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap> _bitmapCache = new();

    /// Drops every cached bitmap. Call when a template's image is uploaded,
    /// removed, the template is deleted, or a project is loaded, so the next
    /// render re-decodes.
    public void InvalidateImageCache()
    {
        foreach (var b in _bitmapCache.Values) b.Dispose();
        _bitmapCache.Clear();
    }

    /// Decodes (and caches) a template's base64 image, keyed by template Id.
    /// Public so the pawn management modal and the pawn tool swatch grids can
    /// render template images in their swatches, not just on the map.
    public Avalonia.Media.Imaging.Bitmap? LoadPawnImage(string templateId)
    {
        if (_bitmapCache.TryGetValue(templateId, out var cached)) return cached;
        var t = Library.Find(templateId);
        if (t is null || string.IsNullOrEmpty(t.ImageB64)) return null;
        try
        {
            using var ms = new System.IO.MemoryStream(System.Convert.FromBase64String(t.ImageB64));
            var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
            _bitmapCache[templateId] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    /// Pawns only moves carry just the pawns, with the origin at their bounding
    /// box so the ghost and the landing spot fit the pawns, not the dragged
    /// rectangle. Returns an empty snapshot when the region has no pawns.
    private static MapDocument.RegionSnapshot PawnsOnlySnapshot(MapDocument.RegionSnapshot snap, out (int x0, int y0, int x1, int y1) box)
    {
        box = default;
        var s = new MapDocument.RegionSnapshot();
        if (snap.Pawns.Count == 0) return s;
        box = (snap.Pawns.Min(p => p.Cell.X), snap.Pawns.Min(p => p.Cell.Y),
               snap.Pawns.Max(p => p.Cell.X), snap.Pawns.Max(p => p.Cell.Y));
        s.OriginX = box.x0;
        s.OriginY = box.y0;
        s.Pawns.AddRange(snap.Pawns);
        return s;
    }

    public event Action? ActionCompleted;
    public event Action<IDoorConfig>? DoorConfigRequested;
    public event Action<Stair>? StairConfigRequested;
    public event Action<MapLabel>? LabelConfigRequested;
    /// a label should be edited in place on the map (double click, or just placed)
    public event Action<MapLabel>? LabelEditRequested;
    /// the pan or zoom changed, so anything placed over the map must follow
    public event Action? ViewChanged;
    public event Action<Pawn>? PawnConfigRequested;
    public event Action<string>? StatusMessage;
    /// the kind of the last status message (its color in the status bar)
    public StatusKind LastStatusKind { get; private set; }
    private void Status(string text, StatusKind kind = StatusKind.Info)
    {
        LastStatusKind = kind;
        StatusMessage?.Invoke(text);
    }
    // a pawn was placed with the pawn tool (template id), for the recent strip
    public event Action<string>? PawnPlaced;
    /// hero pawns are one per campaign. called for each hero placed on this map:
    /// the window removes that hero from every other map and returns the pawn it
    /// removed (null when the hero was not on another map).
    public Func<string, Pawn?>? TakeHeroFromOtherMaps;

    public void Refresh()
    {
        if (_preview is not null) _preview = PlayerView.Build(_document);
        InvalidateVisual();
    }

    // ---- player view preview and joined sessions (multiplayer) ----
    private MapDocument? _preview;
    private MapDocument? _remote;

    /// nothing can be edited: the preview or a joined session. the measure
    /// tool still works, it only reads
    public bool ReadOnlyView => _preview is not null || _remote is not null;
    /// shows exactly what players would be sent. editing is off: left clicks
    /// do nothing, panning and zooming still work
    public bool PlayerPreview
    {
        get => _preview is not null;
        set
        {
            if (value == PlayerPreview) return;
            _preview = value ? PlayerView.Build(_document) : null;
            InvalidateVisual();
        }
    }

    private void DrawPreviewBanner(DrawingContext ctx)
    {
        if (_preview is null) return;
        var ft = new FormattedText("player view preview: what players are sent. esc or a tool returns to editing",
            System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Inter", FontStyle.Normal, FontWeight.Bold), 14, Brushes.White);
        double w = ft.Width + 20, h = ft.Height + 10;
        var box = new Rect(Math.Max(4, (Bounds.Width - w) / 2), 8, w, h);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 120, 40, 110)), new Pen(new SolidColorBrush(Color.FromRgb(236, 90, 220)), 1.5), box, 6, 6);
        ctx.DrawText(ft, new Point(box.X + 10, box.Y + 5));
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(236, 90, 220)), 3), new Rect(Bounds.Size).Deflate(1.5));
    }

    // ---- clipboard / selection ----
    private MapDocument.RegionSnapshot? _clipboard;
    private CellKey _selectStart, _selectEnd;
    private bool _selectDragging;
    // per-pawn drag in the select and pawn tools: pressing directly on a pawn drags that
    // single pawn to the release cell; a press without drag shows its config.
    private Pawn? _pawnDragPawn;
    private CellKey _pawnDragStart, _pawnDragEnd;
    private bool _pawnDragging;
    private Point? _pasteCursor; // world cursor for paste ghost
    public bool GhostEnabled { get; set; } = true; // user toggle for the paste ghost
    // armed move + rotation: after a move region is dragged, the snapshot stays
    // armed so the destination ghost persists (and can be re-placed). _rotTurns
    // is the counterclockwise quarter-turns applied to the armed snapshot.
    private bool _moveArmed;
    // the armed move carries only pawns: "pawns only" in the select tool,
    // or a shift+drag box in the pawn tool
    private bool _armedPawnsOnly;
    // the box being dragged is a shift+drag
    private bool _shiftBox;
    private int _rotTurns;
    private (int x0, int y0, int x1, int y1)? _moveSourceRect; // source region to clear on first placement
    private bool _moveSourceCleared; // source cleared on the first placement; a placement disarms, so it never re-clears
    private bool _movePlaced; // a placement happened; hotkeys restored
    public bool MoveArmed => _moveArmed;
    // rotation is available when a snapshot is armed in Move mode, or a clipboard
    // exists in Select mode (copy/cut paste). In Move mode a stale clipboard from
    // an earlier copy must NOT be rotatable - only the armed snapshot is.
    public bool CanRotate => _clipboard is not null && !_clipboard.Empty
        && (Mode == ToolMode.Select || (Mode == ToolMode.Pawn && _moveArmed));
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
        if (snap.Empty) { Status("nothing to copy - select a region first", StatusKind.Warning); return; }
        _clipboard = snap;
        _cutSource = null; // a plain copy has no source to delete on paste
        Status($"copied {snap.Cells.Count} cells");
    }

    /// Cut: capture the selection AND remember its source region. The next
    /// paste deletes the source; later pastes act as normal paste.
    public void CutSelection()
    {
        var (x0, y0, x1, y1) = NormRect(_selectStart, _selectEnd);
        var snap = Document.CaptureRegion(x0, y0, x1, y1);
        if (snap.Empty) { Status("nothing to cut - select a region first", StatusKind.Warning); return; }
        _clipboard = snap;
        _cutSource = (x0, y0, x1, y1);
        _cutSourceDoc = Document; // remember which map the source lives on
        Status($"cut {snap.Cells.Count} cells");
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
        if (_clipboard is null || _clipboard.Empty) { Status("clipboard empty", StatusKind.Warning); return; }
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
            Status("pasted (cut source removed from its map)");
        }
        else
        {
            Document.PasteRegion(_clipboard, target.X, target.Y);
        }
        KeepHeroesUnique(_clipboard, target);
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
        { Status("nothing armed to move", StatusKind.Warning); return; }
        // a joined player's box goes to the DM
        if (_remote is not null) { RemoteMoveAt(target); return; }

        // first placement clears the source on the map it was selected on.
        // move-pawns-only keeps the old contract: only the pawns leave the source,
        // cells/walls/stairs/labels stay put.
        if (!_moveSourceCleared)
        {
            var src = _moveSourceDoc ?? Document;
            var (sx0, sy0, sx1, sy1) = _moveSourceRect!.Value;
            // braces matter here: without them the else binds to the inner if inside
            // the foreach, so the normal (non pawns-only) path never clears the source.
            if (_armedPawnsOnly)
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
        if (_armedPawnsOnly)
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
        KeepHeroesUnique(_clipboard, target);
        _movePlaced = true; // successful placement restores hotkeys
        Status(_rotTurns != 0
            ? $"placed {what} ({_rotTurns * 90}° ccw)"
            : $"moved {what}");
        Commit();
        // one placement per arm: a successful placement disarms, so the ghost, the
        // rotation, and the hotkey lock all end with the move. The original contract
        // was one placement too - the cleared source left nothing to place again.
        CancelMove();
    }

    /// Removes every other copy of a hero template, on this map and the others,
    /// leaving only the one at keep. Returns one removed copy, or null if none.
    public Pawn? TakeHeroCopies(CellKey keep, string templateId)
    {
        Pawn? taken = null;
        foreach (var (c, p) in Document.Pawns.ToList())
            if (c != keep && p.TemplateId == templateId) { taken ??= p; Document.Pawns.Remove(c); }
        var other = TakeHeroFromOtherMaps?.Invoke(templateId);
        return taken ?? other;
    }

    /// After a snapshot lands at target, keeps any heroes in it unique: the
    /// pasted copy stays and older copies elsewhere are removed.
    private void KeepHeroesUnique(MapDocument.RegionSnapshot snap, CellKey target)
    {
        int dx = target.X - snap.OriginX, dy = target.Y - snap.OriginY;
        foreach (var p in snap.Pawns)
            if (Library.Find(p.TemplateId)?.Hero == true)
                TakeHeroCopies(new CellKey(p.Cell.X + dx, p.Cell.Y + dy), p.TemplateId);
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
            Document.Pawns[nc] = new Pawn { Cell = nc, TemplateId = p.TemplateId, Statuses = p.CloneStatuses(), DmOnly = p.DmOnly };
        }
        foreach (var (dest, occ) in swaps)
        {
            var moverSrc = new CellKey(dest.X - dx, dest.Y - dy);
            Document.Pawns[moverSrc] = new Pawn { Cell = moverSrc, TemplateId = occ.TemplateId, Statuses = occ.CloneStatuses(), DmOnly = occ.DmOnly };
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
        _dragMoving = false;
        // a drag move borrowed the clipboard: what was copied comes back
        if (_clipboardSaved) { _clipboard = _clipboardBeforeMove; _clipboardBeforeMove = null; _clipboardSaved = false; }
        _armedPawnsOnly = false;
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

    /// Pastes at the current cursor cell, if a paste ghost is showing.
    public void PasteAtCursor()
    {
        if (_pasteCursor is Point p)
            PasteAt(new CellKey((int)Math.Floor(p.X), (int)Math.Floor(p.Y)));
    }

    /// Delete hotkey: erase the current selection region, honoring the Erase
    /// tool's category filters (cells, walls+doors, stairs, labels).
    /// the selected region (select tool box), null when there is none
    public (int x0, int y0, int x1, int y1)? SelectionRect => HasSelection ? NormRect(_selectStart, _selectEnd) : null;

    /// what a region delete would take, per erase filter kind
    public sealed record RegionCounts(int Cells, int Walls, int Stairs, int Labels, int Pawns, int Objects, int Images)
    {
        public int Total(EraseFilter f) =>
            (f.Cells ? Cells : 0) + (f.Walls ? Walls : 0) + (f.Stairs ? Stairs : 0) + (f.Labels ? Labels : 0)
            + (f.Pawns ? Pawns : 0) + (f.Objects ? Objects : 0) + (f.Images ? Images : 0);
    }

    public RegionCounts CountRegion(int x0, int y0, int x1, int y1)
    {
        int cells = 0, stairs = 0, pawns = 0;
        var walls = new HashSet<EdgeKey>();
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                var c = new CellKey(x, y);
                if (Document.Cells.Contains(c)) cells++;
                if (Document.Stairs.ContainsKey(c)) stairs++;
                if (Document.Pawns.ContainsKey(c)) pawns++;
                foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                {
                    var k = EdgeKey.Normalize(c, side);
                    if (Document.Walls.ContainsKey(k)) walls.Add(k);
                }
            }
        int angled = Document.AngledWalls.Count(w => MapDocument.AngledWallIntersectsRect(w, x0, y0, x1, y1));
        int labels = Document.Labels.Count(l => Enumerable.Range(x0, x1 - x0 + 1).Any(x => Enumerable.Range(y0, y1 - y0 + 1).Any(y => l.Covers(new CellKey(x, y)))));
        return new RegionCounts(cells, walls.Count + angled, stairs, labels, pawns,
            Document.Objects.Count(o => o.Touches(x0, y0, x1, y1)), Document.Images.Count(o => o.Touches(x0, y0, x1, y1)));
    }

    /// "3 pawns, 14 walls, 1 object": what a delete with filter f takes
    public static string DescribeCounts(RegionCounts n, EraseFilter f)
    {
        var parts = new List<string>();
        void Add(bool on, int count, string one, string many) { if (on && count > 0) parts.Add($"{count} {(count == 1 ? one : many)}"); }
        Add(f.Pawns, n.Pawns, "pawn", "pawns");
        Add(f.Cells, n.Cells, "floor cell", "floor cells");
        Add(f.Walls, n.Walls, "wall", "walls");
        Add(f.Stairs, n.Stairs, "stair", "stairs");
        Add(f.Labels, n.Labels, "label", "labels");
        Add(f.Objects, n.Objects, "object", "objects");
        Add(f.Images, n.Images, "image", "images");
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    /// deletes what filter f picks in the selected region, one undo step.
    /// adjustWalls re-seals rooms on the new outer edge like the eraser's
    /// option. the delete key's modal and shift+delete (everything) use it.
    public void DeleteSelection(EraseFilter f, bool adjustWalls)
    {
        if (SelectionRect is not var (x0, y0, x1, y1)) { Status("nothing to delete - select a region first", StatusKind.Warning); return; }
        var counts = CountRegion(x0, y0, x1, y1);
        if (counts.Total(f) == 0) { Status("nothing to delete in that region", StatusKind.Warning); return; }
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                var c = new CellKey(x, y);
                if (f.Cells) Document.Cells.Remove(c);
                if (f.Stairs) Document.Stairs.Remove(c);
                if (f.Pawns) Document.Pawns.Remove(c);
                if (f.Labels) Document.Labels.RemoveAll(l => l.Covers(c));
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
        if (f.Objects && EraseObjects(x0, y0, x1, y1) > 0) ObjectsChanged?.Invoke();
        if (f.Images && EraseImages(x0, y0, x1, y1) > 0) ImagesChanged?.Invoke();
        if (adjustWalls && f.Cells) Document.RebuildRoomBoundaryWalls(x0, y0, x1, y1);
        Status($"deleted {DescribeCounts(counts, f)} (Ctrl+Z undoes)");
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
        _moveArmed = false;
        _armedPawnsOnly = false;
        _rotTurns = 0;
        _moveSourceRect = null;
        _moveSourceCleared = false;
    }

    /// Select tool "clear clipboard" button: drops the clipboard, the paste
    /// ghost and the selection rectangle.
    public void ClearClipboard()
    {
        ResetClipboard();
        ClearSelection();
        Status("clipboard cleared");
    }

    /// Rotates the armed snapshot by turns counterclockwise quarter-turns (R =
    /// clockwise = -1, Shift+R / shift+scroll up = counterclockwise = +1). The
    /// rotation is applied to the stored snapshot so the destination ghost and
    /// the placement both reflect it. In Move mode the source region is untouched
    /// until the first placement, so re-rotating before placing is free.
    public void RotateRegion(int turns)
    {
        if (_clipboard is null || _clipboard.Empty) { Status("nothing armed to rotate", StatusKind.Warning); return; }
        _rotTurns = (((_rotTurns + turns) % 4) + 4) % 4;
        _clipboard = MapDocument.Rotate(_clipboard, turns);
        Status($"rotated {(_rotTurns * 90)}° ccw");
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
        Document.AddPolygon(_polyVerts, passageway, strict, PolygonMergeNearby, new CellKey(first.x, first.y));
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
    /// the zoom, in pixels per cell (kept within the zoom limits)
    public double ViewScale
    {
        get => _scale;
        set { _scale = Math.Clamp(value, MinScale, MaxScale); InvalidateVisual(); ViewChanged?.Invoke(); }
    }

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
    /// wall line thickness in screen pixels from the global setting, clamped
    private double WallWidth => Math.Clamp(Settings.WallThickness, AppSettings.MinWallThickness, AppSettings.MaxWallThickness);

    private Pen? _wallPenField; private int _wpR, _wpG, _wpB; private double _wpW;
    private IPen WallPen
    {
        get
        {
            if (_wallPenField is null || _wpR != Settings.WallR || _wpG != Settings.WallG || _wpB != Settings.WallB || _wpW != WallWidth)
            {
                _wpR = Settings.WallR; _wpG = Settings.WallG; _wpB = Settings.WallB; _wpW = WallWidth;
                // square caps close the notch where two thick walls meet at a corner
                _wallPenField = new Pen(new SolidColorBrush(Color.FromRgb((byte)_wpR, (byte)_wpG, (byte)_wpB)), _wpW,
                    lineCap: PenLineCap.Square);
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

    // Door color per type, from the campaign's door presets; unknown types
    // fall back to standard.
    private IBrush DoorBrush(string type)
    {
        var t = Presets.FindDoorType(type);
        return new SolidColorBrush(Color.FromRgb((byte)t.R, (byte)t.G, (byte)t.B));
    }

    // a door is 75% of one cell of wall long, on every wall kind, so a door
    // across two walls at a vertex is the same size as one on a single wall
    private const double DoorLength = 0.75;
    // open door outline, a fixed pixel width like the walls so it holds up when
    // zoomed out. a bit thinner than a wall so the two read apart. capped at half
    // the glyph's short side so thick walls zoomed far out still leave it hollow.
    private double OpenDoorWidth(double s2) => Math.Min(s2, Math.Max(1.5, WallWidth * 0.75));

    // Door rectangle glyph centered at mid, long side along ang (radians). Open =
    // hollow outline, closed = solid, locked = solid + red core. Shared by placed
    // doors, angled doors, and the door tool placement ghost.
    private void DrawDoorGlyph(DrawingContext ctx, Point mid, double ang, IBrush brush, DoorState state)
    {
        double s2 = Math.Max(4, _scale * 0.18);
        double l2 = Math.Max(s2, _scale * DoorLength / 2);
        var rect = new Rect(mid.X - l2, mid.Y - s2, l2 * 2, s2 * 2);
        void core()
        {
            if (state == DoorState.Open)
            {
                var pen = new Pen(brush, OpenDoorWidth(s2));
                ctx.DrawRectangle(pen, rect);
            }
            else
            {
                ctx.FillRectangle(brush, rect);
                if (state == DoorState.Locked)
                {
                    // locked inner core is always RED, independent of door type.
                    ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(220, 40, 40)),
                        new Rect(mid.X - l2 / 2, mid.Y - s2 / 2, l2, s2));
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

    // locate: the cell being highlighted and when, the ring fades out
    private CellKey? _locateCell;
    private DateTime _locateAt;
    private Avalonia.Threading.DispatcherTimer? _locateTimer;
    private const double LocateSeconds = 1.6;

    /// Centers the view on a cell without changing zoom (zooms in first if the
    /// map is zoomed far out) and flashes a ring around it.
    public void CenterOn(CellKey c)
    {
        if (_scale < 24) _scale = Math.Min(40, MaxScale);
        double availW = Bounds.Width > 0 ? Bounds.Width : 800;
        double availH = Bounds.Height > 0 ? Bounds.Height : 600;
        _offset = new Point(c.X + 0.5 - availW / (2 * _scale), c.Y + 0.5 - availH / (2 * _scale));
        _locateCell = c;
        _locateAt = DateTime.Now;
        if (_locateTimer is null)
        {
            _locateTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _locateTimer.Tick += (_, _) =>
            {
                if ((DateTime.Now - _locateAt).TotalSeconds > LocateSeconds) { _locateCell = null; _locateTimer!.Stop(); }
                InvalidateVisual();
            };
        }
        _locateTimer.Start();
        InvalidateVisual();
        ViewChanged?.Invoke();
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

    /// Shared floor-ring painter: ring 0 outer, rings 1+ holes (EvenOdd fill),
    /// stroked edge-by-edge with the floor color to close anti-aliasing seams
    /// where an edge meets a room rect. polygon floors are flood regions too,
    /// so this is the one painter for both. culled by the ring bounds.
    private (StreamGeometry geo, double x0, double y0, double x1, double y1)? DrawFloorRings(DrawingContext ctx, FloodRegion? fr, Point topLeft, Point bottomRight)
    {
        if (fr is null || fr.Rings.Count == 0) return null;
        double fminX = double.MaxValue, fminY = double.MaxValue, fmaxX = double.MinValue, fmaxY = double.MinValue;
        foreach (var ring in fr.Rings)
            foreach (var (rx, ry) in ring)
            {
                if (rx < fminX) fminX = rx; if (rx > fmaxX) fmaxX = rx;
                if (ry < fminY) fminY = ry; if (ry > fmaxY) fmaxY = ry;
            }
        if (fmaxX < topLeft.X || fminX > bottomRight.X + 1 || fmaxY < topLeft.Y || fminY > bottomRight.Y + 1) return null;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            foreach (var ring in fr.Rings)
            {
                g.BeginFigure(WorldToScreen(ring[0].Item1, ring[0].Item2), true);
                for (int i = 1; i < ring.Count; i++)
                    g.LineTo(WorldToScreen(ring[i].Item1, ring[i].Item2), true);
            }
        }
        ctx.DrawGeometry(CellFill, null, geo);
        var pen = new Pen(CellFill, 1.2);
        foreach (var ring in fr.Rings)
            for (int i = 0; i < ring.Count; i++)
            {
                var (ax, ay) = ring[i];
                var (bx, by) = ring[(i + 1) % ring.Count];
                ctx.DrawLine(pen, WorldToScreen(ax, ay), WorldToScreen(bx, by));
            }
        return (geo, fminX, fminY, fmaxX, fmaxY);
    }

    // grid lines over the world box x0..x1, y0..y1. the caller clips them to
    // a floor.
    private void DrawGridLines(DrawingContext ctx, double x0, double y0, double x1, double y1, int sp, Pen majorPen, Pen minorPen)
    {
        if (x1 < x0 || y1 < y0) return;
        double top = WorldToScreen(0, y0).Y, bottom = WorldToScreen(0, y1).Y;
        double left = WorldToScreen(x0, 0).X, right = WorldToScreen(x1, 0).X;
        for (int x = (int)Math.Floor(x0); x <= (int)Math.Ceiling(x1); x++)
        {
            bool major = sp == 1 || FloorMod(x, sp) == 0;
            if (!major && !Settings.ShowMinorGrid) continue;
            double sx = WorldToScreen(x, 0).X;
            ctx.DrawLine(major ? majorPen : minorPen, new Point(sx, top), new Point(sx, bottom));
        }
        for (int y = (int)Math.Floor(y0); y <= (int)Math.Ceiling(y1); y++)
        {
            bool major = sp == 1 || FloorMod(y, sp) == 0;
            if (!major && !Settings.ShowMinorGrid) continue;
            double sy = WorldToScreen(0, y).Y;
            ctx.DrawLine(major ? majorPen : minorPen, new Point(left, sy), new Point(right, sy));
        }
    }

    // a dot on each grid vertex in the visible window. major vertices (both
    // coordinates on the spacing) get a bigger dot, minor ones a smaller dot
    // when minor grid is on. when zoomed out far enough that dots would crowd
    // together they are skipped, minor first.
    // one sp x sp block of dots is drawn into a small bitmap and the screen is
    // filled with it as a tiled brush. drawing thousands of dots one by one,
    // or as one big path, ran at a few frames a second with minor dots on.
    private RenderTargetBitmap? _dotTile;
    private ImageBrush? _dotBrush;
    private (int px, int sp, bool minor, double rMajor, double rMinor, Color col) _dotTileKey;
    private void DrawGridDots(DrawingContext ctx, Point topLeft, Point bottomRight, int sp)
    {
        bool minor = sp > 1 && Settings.ShowMinorGrid && _scale >= 8;
        int step = minor ? 1 : sp;
        if (_scale * step < 8) return;
        double rMajor = Math.Clamp(_scale * 0.04, 1.1, 2.0);
        double rMinor = sp > 1 ? rMajor * 0.7 : rMajor;
        var col = Color.FromRgb((byte)Settings.GridDotR, (byte)Settings.GridDotG, (byte)Settings.GridDotB);
        double tile = sp * _scale;
        int px = (int)Math.Round(tile);
        if (px > 1024)
        {
            // zoomed in this far there are only a few dots on screen, draw
            // them directly instead of building a huge tile
            var solid = new SolidColorBrush(col);
            int xs = (int)Math.Floor(topLeft.X), ys = (int)Math.Floor(topLeft.Y);
            xs -= FloorMod(xs, step); ys -= FloorMod(ys, step);
            int xe = (int)Math.Ceiling(bottomRight.X), ye = (int)Math.Ceiling(bottomRight.Y);
            for (int y = ys; y <= ye; y += step)
                for (int x = xs; x <= xe; x += step)
                {
                    double r = FloorMod(x, sp) == 0 && FloorMod(y, sp) == 0 ? rMajor : rMinor;
                    ctx.DrawEllipse(solid, null, WorldToScreen(x, y), r, r);
                }
            return;
        }
        var key = (px, sp, minor, rMajor, rMinor, col);
        if (_dotTile is null || _dotTileKey != key)
        {
            // the tile covers world vertex - 0.5 cell to + sp - 0.5 cells, so
            // every dot sits inside it and none is cut at the tile edge
            _dotTile?.Dispose();
            _dotTile = new RenderTargetBitmap(new PixelSize(px, px), new Vector(96, 96));
            double cell = (double)px / sp;
            var solid = new SolidColorBrush(col);
            using (var tctx = _dotTile.CreateDrawingContext())
                for (int j = 0; j < sp; j++)
                    for (int i = 0; i < sp; i++)
                    {
                        bool major = i == 0 && j == 0;
                        if (!major && !minor) continue;
                        double r = major ? rMajor : rMinor;
                        tctx.DrawEllipse(solid, null, new Point((i + 0.5) * cell, (j + 0.5) * cell), r, r);
                    }
            _dotBrush = new ImageBrush(_dotTile) { TileMode = TileMode.Tile, Stretch = Stretch.Fill };
            _dotTileKey = key;
        }
        // line the tile up with world vertex 0,0 (a major vertex). the brush
        // offset did not place the tile where asked, so the tile stays at 0,0
        // and the drawing is shifted by a transform instead. only the offset
        // within one tile matters.
        var origin = WorldToScreen(-0.5, -0.5);
        double ox = origin.X - Math.Floor(origin.X / tile) * tile;
        double oy = origin.Y - Math.Floor(origin.Y / tile) * tile;
        _dotBrush!.DestinationRect = new RelativeRect(0, 0, tile, tile, RelativeUnit.Absolute);
        using (ctx.PushTransform(Matrix.CreateTranslation(ox, oy)))
            ctx.FillRectangle(_dotBrush, new Rect(-ox, -oy, Bounds.Width, Bounds.Height));
    }

    private Point _lastViewOffset; private double _lastViewScale;
    public override void Render(DrawingContext ctx)
    {
        if (_offset != _lastViewOffset || _scale != _lastViewScale)
        {
            _lastViewOffset = _offset; _lastViewScale = _scale;
            if (ViewChanged is { } vc) Avalonia.Threading.Dispatcher.UIThread.Post(vc);
        }
        ctx.FillRectangle(Background, new Rect(Bounds.Size));

        // Visible world window, for culling
        var topLeft = ScreenToWorld(new Point(0, 0));
        var bottomRight = ScreenToWorld(new Point(Bounds.Width, Bounds.Height));

        // Cell fills. Grid is always 5ft (1 cell); GridDrawSpacing is the MAJOR
        // spacing. Colors come from settings.
        int sp = Math.Max(1, Settings.GridDrawSpacing);
        // these lines are only drawn inside floors, so the floor grid opacity
        // setting goes straight into their alpha
        byte gridAlpha = (byte)Math.Round(255 * Math.Clamp(Settings.FloorGridOpacity, 0, 100) / 100.0);
        var majorPen = new Pen(new SolidColorBrush(Color.FromArgb(gridAlpha, (byte)Settings.MajorGridR, (byte)Settings.MajorGridG, (byte)Settings.MajorGridB)), 1);
        var minorPen = new Pen(new SolidColorBrush(Color.FromArgb(gridAlpha, (byte)Settings.MinorGridR, (byte)Settings.MinorGridG, (byte)Settings.MinorGridB)), 1);
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
        // cells cut by a diagonal are never in Cells, the flood region rings
        // below paint their floor, so every cell here is a full square.
        // empty space shows a dot on each grid vertex instead of lines. drawn
        // before the floors, so a floor covers any dot under it and the dots
        // only show outside rooms.
        DrawGridDots(ctx, topLeft, bottomRight, sp);

        int cx0 = (int)Math.Floor(topLeft.X), cx1 = (int)Math.Ceiling(bottomRight.X);
        int cy0 = (int)Math.Floor(topLeft.Y), cy1 = (int)Math.Ceiling(bottomRight.Y);

        // Visible cells only. The greedy pass below needs top-left-first order;
        // iterating the viewport area row-by-row gives that for free (no LINQ
        // sort) and off-screen cells never enter the scan. When the viewport is
        // larger than the document (zoomed way out), iterate the document and
        // cull instead - O(min(window area, document size)) per frame.
        var visibleCells = new List<CellKey>();
        var floorClip = new GeometryGroup { FillRule = FillRule.NonZero };
        var remaining = new HashSet<CellKey>();
        long windowArea = (long)(cx1 - cx0 + 1) * (cy1 - cy0 + 1);
        if (windowArea <= Document.Cells.Count)
        {
            for (int y = cy0; y <= cy1; y++)
                for (int x = cx0; x <= cx1; x++)
                {
                    var k = new CellKey(x, y);
                    if (Document.Cells.Contains(k))
                    { visibleCells.Add(k); remaining.Add(k); }
                }
        }
        else
        {
            foreach (var c in Document.Cells.Where(c => c.X >= cx0 && c.X <= cx1 && c.Y >= cy0 && c.Y <= cy1)
                                             .OrderBy(c => c.Y).ThenBy(c => c.X))
            { visibleCells.Add(c); remaining.Add(c); }
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
            double rw = w * _scale + (Document.Cells.Contains(new CellKey(c.X + w, c.Y)) ? pad : 0);
            double rh = h * _scale + (Document.Cells.Contains(new CellKey(c.X, c.Y + h)) ? pad : 0);
            var p0 = WorldToScreen(c.X, c.Y);
            ctx.FillRectangle(CellFill, new Rect(p0.X, p0.Y, rw, rh));
            floorClip.Children.Add(new RectangleGeometry(new Rect(p0.X, p0.Y, rw, rh)));
        }

        // flood region floors, polygon floors included
        var floodFloors = new List<(StreamGeometry geo, double x0, double y0, double x1, double y1)>();
        foreach (var fr in Document.FloodRegions)
            if (DrawFloorRings(ctx, fr, topLeft, bottomRight) is { } ff) floodFloors.Add(ff);

        // grid lines only inside rooms, clipped to the floor. drawn after the
        // fills so the lines show on top. major at spacing boundaries, minor at
        // every 5ft cell edge; at sp==1 minor coincides with major so all lines
        // are major.
        if (gridAlpha == 0) floodFloors.Clear(); // nothing to draw, skip the clipped passes
        if (floorClip.Children.Count > 0 && gridAlpha > 0)
            using (ctx.PushGeometryClip(floorClip))
                DrawGridLines(ctx, topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y, sp, majorPen, minorPen);
        // flood floors clip one at a time, each keeps its own even-odd holes.
        // lines are limited to the region's box so many regions stay cheap.
        foreach (var (geo, fx0, fy0, fx1, fy1) in floodFloors)
            using (ctx.PushGeometryClip(geo))
                DrawGridLines(ctx, Math.Max(fx0, topLeft.X), Math.Max(fy0, topLeft.Y),
                              Math.Min(fx1, bottomRight.X), Math.Min(fy1, bottomRight.Y), sp, majorPen, minorPen);

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

        // objects sit on the floor, under walls, doors, labels and pawns
        DrawObjects(ctx, topLeft, bottomRight);

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
        }
        // doors after every wall: the door wall gap setting can clear part of
        // the next wall along, which a later wall would otherwise draw over
        foreach (var (e, door) in Document.Doors)
        {
            if (!Document.HasWall(e)) continue;
            var seg = EdgeSegment(e);
            if (Math.Max(seg.a.X, seg.b.X) + 1 < topLeft.X || Math.Min(seg.a.X, seg.b.X) - 1 > bottomRight.X
                || Math.Max(seg.a.Y, seg.b.Y) + 1 < topLeft.Y || Math.Min(seg.a.Y, seg.b.Y) - 1 > bottomRight.Y) continue;
            DrawDoor(ctx, e, WorldToScreen(seg.a.X, seg.a.Y), WorldToScreen(seg.b.X, seg.b.Y), door);
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

        // Vertex doors: centered on the lattice vertex, straddling the two
        // collinear walls, the same length as a single wall door. Punch a gap
        // along the orientation, then draw the glyph rotated to it.
        foreach (var (k, d) in Document.VertexDoors)
        {
            if (k.X + 1 < topLeft.X || k.X - 1 > bottomRight.X || k.Y + 1 < topLeft.Y || k.Y - 1 > bottomRight.Y) continue;
            var v = WorldToScreen(k.X, k.Y);
            double gw = WallWidth + 1; // a pixel over the wall to cover its anti-aliased edge
            // a vertex door spans two edges, so the door wall gap stays on them
            double reach = _scale * (DoorLength / 2 + DoorWallGap);
            ctx.FillRectangle(CellFill, k.Horizontal
                ? new Rect(v.X - reach, v.Y - gw / 2, reach * 2, gw)
                : new Rect(v.X - gw / 2, v.Y - reach, gw, reach * 2));
            double gap = _scale * DoorLength;
            var gapRect = k.Horizontal
                ? new Rect(v.X - gap / 2, v.Y - gw / 2, gap, gw)
                : new Rect(v.X - gw / 2, v.Y - gap / 2, gw, gap);
            ctx.FillRectangle(GapFill(d.State), gapRect);
            DrawDoorGlyph(ctx, v, k.Horizontal ? 0 : Math.PI / 2, DoorBrush(d.Type), d.State);
        }

        // images, like labels: over doors, under labels and pawns
        DrawImages(ctx, topLeft, bottomRight);

        // Labels: centered vertically in their range, aligned across it
        foreach (var l in Document.Labels)
        {
            if (l.Cell.X + l.W < topLeft.X || l.Cell.X > bottomRight.X || l.Cell.Y + l.H < topLeft.Y || l.Cell.Y > bottomRight.Y) continue;
            // the label being edited in place is drawn by the edit box instead
            if (ReferenceEquals(l, EditingLabel)) continue;
            var p0 = WorldToScreen(l.Cell.X, l.Cell.Y);
            double fs = LabelRenderSize(l);
            var brush = LabelTextBrush(l);
            // one line at a time so each line is aligned on its own
            var lines = LabelLines(l.Text)
                .Select(t => (blank: t.Length == 0, ft: new FormattedText(t.Length == 0 ? " " : t, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Inter"), fs, brush)))
                .ToList();
            double w = l.W * _scale, h = l.H * _scale, pad = LabelPad * _scale;
            double y = p0.Y + (h - lines.Sum(x => x.ft.Height)) / 2;
            foreach (var (blank, ft) in lines)
            {
                double lw = blank ? 0 : ft.Width;
                double tx = l.Align switch
                {
                    LabelAlign.Left => p0.X + pad,
                    LabelAlign.Right => p0.X + w - pad - lw,
                    _ => p0.X + (w - lw) / 2,
                };
                if (lw > 0) ctx.DrawText(ft, new Point(tx, y));
                y += ft.Height;
            }
        }

        DrawSightTint(ctx); // under the pawns, over the floor and walls

        // Pawns: concentric circles (outer + inner) + outer status border, scaled
        // by zoom. Visual data resolves from the library template (hard reference);
        // a missing template renders an X (broken reference). Drawn after labels.
        foreach (var (c, p) in Document.Pawns)
        {
            if (c.X + 1 < topLeft.X || c.X > bottomRight.X || c.Y + 1 < topLeft.Y || c.Y > bottomRight.Y) continue;
            DrawPawnGlyph(ctx, c, p.TemplateId, p.Statuses);
        }

        // per-pawn drag ghost: the dragged pawn follows the cursor as a
        // semi-transparent glyph at the cell it will land on.
        if (_pawnDragging && _pawnDragPawn is not null && _pawnDragEnd != _pawnDragStart)
            DrawPawnGlyph(ctx, _pawnDragEnd, _pawnDragPawn.TemplateId, _pawnDragPawn.Statuses, 0.45);

        DrawStatusTimers(ctx, topLeft, bottomRight);

        // names on map: a dark pill in each pawn, low down. skipped when zoomed far
        // out, where the text would only be noise.
        if (Settings.ShowPawnNames && _scale >= 14)
        {
            double fs = Math.Clamp(_scale * 0.28, 9, 14);
            foreach (var (c, p) in Document.Pawns)
            {
                if (c.X + 1 < topLeft.X || c.X > bottomRight.X || c.Y + 1 < topLeft.Y || c.Y > bottomRight.Y) continue;
                var t = Library.Find(p.TemplateId);
                if (t is null || string.IsNullOrWhiteSpace(t.Name)) continue;
                DrawNamePill(ctx, c, t.Name, fs);
            }
        }

        // locate ring: a yellow ring that shrinks onto the located cell and fades
        if (_locateCell is CellKey lc)
        {
            double k = Math.Clamp((DateTime.Now - _locateAt).TotalSeconds / LocateSeconds, 0, 1);
            var p0 = WorldToScreen(lc.X, lc.Y);
            var center = new Point(p0.X + _scale / 2, p0.Y + _scale / 2);
            double r = _scale * (0.6 + 0.9 * (1 - Math.Min(1, k * 3)));
            byte a = (byte)(255 * (1 - k * k));
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(a, 255, 204, 51)), 3), center, r, r);
        }

        DrawSightDim(ctx); // under the tool overlays, over the whole map
        DrawRevealOverlay(ctx);
        DrawPreview(ctx);
        DrawObjectOverlay(ctx);
        DrawImageOverlay(ctx);
        DrawLabelHandles(ctx);
        DrawHighlight(ctx);
        DrawFormationOverlay(ctx);
        DrawMeasureOverlay(ctx);
        DrawSightLine(ctx);
        DrawPlayerPawnDrag(ctx);
        DrawPings(ctx);
        DrawFloating(ctx);
        DrawPreviewBanner(ctx);
    }

    // ---- drag preview ----
    private bool _dragging;
    private CellKey _dragStart, _dragEnd;
    private EdgeKey? _previewEdge;
    private (AngledWall wall, double t)? _previewAngled; // door ghost on hovered angled segment (t in cell units)
    private (int x, int y, bool horizontal)? _previewVertex; // door ghost snapped to a lattice vertex

    // polygon tool: vertices are LATTICE points (integer grid intersections).
    // Click adds a snapped vertex; clicking the first vertex closes the polygon;
    // Enter closes by appending the first vertex (ClosePolygon).
    private readonly List<(int x, int y)> _polyVerts = new();
    private Point _polyCursor; // world cursor, for the closing preview segment

    // wall tool: press snaps the start LATTICE vertex (same snap rule as the
    // polygon tool), the ghost line follows the snapped cursor, and release
    // snaps the end vertex and commits ONCE. Additive: existing walls merge,
    // doors on them survive; removal goes through the Erase tool.
    private (int x, int y)? _lineStart;
    private (int x, int y)? _lineEnd; // snapped end vertex during the drag
    private bool _lineDragging;

    // Floor tool sub-mode: false = manual rectangle drag (the original),
    // true = flood fill, click a cell to fill the room it is in. The popup
    // panel buttons flip this; the status bar shows which mode is active.
    public bool FloorFloodFill;
    private List<List<(double, double)>>? _floodPreview;
    private CellKey? _floodPreviewCell;

    /// Discards an in-progress line (Escape / tool switch): the ghost is
    /// dropped and nothing is committed.
    public void CancelLine()
    {
        _lineStart = null;
        _lineEnd = null;
        _lineDragging = false;
    }

    // gap between the range edge and left or right aligned text, in cells
    private const double LabelPad = 0.15;
    private int _pressClicks;

    /// The size a label draws at, at MapLabel.RefScale: its own size when set,
    /// otherwise its default.
    public static double LabelSize(MapLabel l) => l.FontSize > 0 ? l.FontSize : LabelDefaultSize(l);

    // range labels default a bit under the size that exactly fills the range
    private const double RangeFill = 0.8;

    /// The default size of a label. a single cell label uses the global
    /// default. a range label gets the largest size that fits all its lines
    /// inside the range padding, capped at half the range height, then
    /// scaled down by RangeFill so it does not crowd the edges.
    public static double LabelDefaultSize(MapLabel l)
    {
        if (!l.IsRange) return MapLabel.DefaultSize;
        double cap = l.H * MapLabel.RefScale * 0.5;
        double size = cap;
        if (!string.IsNullOrEmpty(l.Text))
        {
            // text grows linearly with the font size, so measure once at 100
            double widest = 0, tall = 0;
            foreach (var t in LabelLines(l.Text))
            {
                var ft = new FormattedText(t.Length == 0 ? " " : t, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Inter"), 100, Brushes.White);
                if (t.Length > 0) widest = Math.Max(widest, ft.Width);
                tall += ft.Height;
            }
            double roomW = (l.W - 2 * LabelPad) * MapLabel.RefScale;
            double roomH = (l.H - 2 * LabelPad) * MapLabel.RefScale;
            if (widest > 0) size = Math.Min(size, roomW / (widest / 100));
            if (tall > 0) size = Math.Min(size, roomH / (tall / 100));
        }
        return Math.Max(4, size * RangeFill);
    }

    /// a label's text split into lines
    public static string[] LabelLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// the font size a label draws at on screen at the current zoom. sizes are
    /// stored at RefScale and follow the zoom; the floor keeps small text
    /// readable when zoomed far out.
    public double LabelRenderSize(MapLabel l)
    {
        double size = LabelSize(l);
        return Math.Max(Math.Min(10, size), size * _scale / MapLabel.RefScale);
    }

    public IBrush LabelTextBrush(MapLabel l) => l.HasOwnColor
        ? new SolidColorBrush(Color.FromRgb((byte)l.R, (byte)l.G, (byte)l.B))
        : LabelBrush;

    /// the screen rectangle a label covers
    public Rect LabelScreenRect(MapLabel l)
    {
        var p0 = WorldToScreen(l.Cell.X, l.Cell.Y);
        return new Rect(p0.X, p0.Y, l.W * _scale, l.H * _scale);
    }

    /// the label being edited in place. the canvas skips drawing it.
    public MapLabel? EditingLabel { get; set; }

    /// the topmost label covering a cell
    private MapLabel? LabelAt(CellKey c) => Document.Labels.LastOrDefault(l => l.Covers(c));

    private void DrawPreview(DrawingContext ctx)
    {
        if (_dragging && (Mode == ToolMode.Room || Mode == ToolMode.Floor || Mode == ToolMode.Stair || Mode == ToolMode.Erase || Mode == ToolMode.Label))
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
                : Mode == ToolMode.Label
                ? new SolidColorBrush(Color.FromArgb(50, 250, 250, 250))
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
        if (_previewEdge is EdgeKey pe && Mode == ToolMode.Door)
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
                var st = Enum.TryParse<DoorState>(DefaultDoorState, true, out var s)
                    ? s : DoorState.Closed;
                DrawDoorGlyph(ctx, mid, Math.Abs(b.Y - a.Y) < 0.001 ? 0 : Math.PI / 2,
                    GhostBrush((SolidColorBrush)DoorBrush(DefaultDoorType)), st);
            }
        }
        // angled door ghost: hover near an angled segment shows the rotated door
        // square at the projected t, same priority as the click handler.
        if (Mode == ToolMode.Door && _previewAngled is not null)
        {
            var (wall, t) = _previewAngled.Value;
            var doors = Document.GetAngledDoors(wall);
            if (doors.FirstOrDefault(d => Math.Abs(d.T - t) < DoorLength) is null)
            {
                var wa = WorldToScreen(wall.Ax, wall.Ay);
                var wb = WorldToScreen(wall.Bx, wall.By);
                double len = Math.Sqrt((wb.X - wa.X) * (wb.X - wa.X) + (wb.Y - wa.Y) * (wb.Y - wa.Y));
                if (len > 0.0001)
                {
                    double ux = (wb.X - wa.X) / len, uy = (wb.Y - wa.Y) / len;
                    var mid = new Point(wa.X + ux * t * _scale, wa.Y + uy * t * _scale);
                    var st = Enum.TryParse<DoorState>(DefaultDoorState, true, out var s)
                        ? s : DoorState.Closed;
                    DrawDoorGlyph(ctx, mid, Math.Atan2(uy, ux),
                        GhostBrush((SolidColorBrush)DoorBrush(DefaultDoorType)), st);
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
                var st = Enum.TryParse<DoorState>(DefaultDoorState, true, out var s)
                    ? s : DoorState.Closed;
                DrawDoorGlyph(ctx, v, hor ? 0 : Math.PI / 2,
                    GhostBrush((SolidColorBrush)DoorBrush(DefaultDoorType)), st);
            }
        }

        // selection rectangle (cyan)
        if (_selectDragging || (_selectStart != _selectEnd && (Mode is ToolMode.Select or ToolMode.Pawn))
            || (Mode == ToolMode.Pawn && _moveArmed))
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

        // wall tool ghost: snapped start vertex -> snapped end vertex, fully
        // grid-aligned, with dots on both endpoints.
        if (Mode == ToolMode.Wall && _lineStart is not null && _lineEnd is not null)
        {
            var (sx, sy) = _lineStart.Value;
            var (ex, ey) = _lineEnd.Value;
            var la = WorldToScreen(sx, sy);
            var lb = WorldToScreen(ex, ey);
            var linePen = new Pen(new SolidColorBrush(Color.FromRgb(250, 250, 250)), 2);
            ctx.DrawLine(linePen, la, lb);
            var ldot = new SolidColorBrush(Color.FromRgb(250, 250, 250));
            ctx.DrawEllipse(ldot, null, la, 3, 3);
            ctx.DrawEllipse(ldot, null, lb, 3, 3);
        }

        // flood fill preview: the room the cursor is in, painted in the same
        // muted tan as the floor drag ghost. Skipped for huge regions (the
        // unclosed/cap case) - too many rects to paint per frame.
        if (Mode == ToolMode.Floor && FloorFloodFill && _floodPreview is not null)
        {
            var floodFill = new SolidColorBrush(Color.FromArgb(70, 150, 130, 90));
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                foreach (var ring in _floodPreview)
                {
                    g.BeginFigure(WorldToScreen(ring[0].Item1, ring[0].Item2), true);
                    for (int i = 1; i < ring.Count; i++)
                        g.LineTo(WorldToScreen(ring[i].Item1, ring[i].Item2), true);
                }
            }
            ctx.DrawGeometry(floodFill, null, geo);
        }

        // paste ghost: clipboard preview at the cursor cell. In Select mode it
        // shows all the time (follows the cursor); in Move mode it shows while a
        // move is ARMED (the armed snapshot persists after a placement, so the
        // destination ghost keeps showing for rotation and repeat placement -
        // the old HasSelection check killed the ghost after the first move).
        bool ghostMode = Mode == ToolMode.Select
            || (Mode == ToolMode.Pawn && _moveArmed && !_dragging);
        if (_clipboard is not null && !_clipboard.Empty && _pasteCursor is Point pc && ghostMode && (GhostEnabled || _dragMoving))
        {
            var origin = new CellKey((int)Math.Floor(pc.X), (int)Math.Floor(pc.Y));
            int dx = origin.X - _clipboard.OriginX, dy = origin.Y - _clipboard.OriginY;
            var ghost = new SolidColorBrush(Color.FromArgb(70, 80, 200, 220));
            foreach (var c in _clipboard.Cells)
            {
                var p0 = WorldToScreen(c.X + dx, c.Y + dy);
                ctx.FillRectangle(ghost, new Rect(p0.X, p0.Y, _scale, _scale));
            }
            // objects in the clipboard show faded in their own look
            foreach (var o in _clipboard.Objects)
            {
                var p0 = WorldToScreen(o.X + dx, o.Y + dy);
                var look = o.EffectiveLook(ObjectTypes);
                DrawObjectShape(ctx, new Rect(p0.X, p0.Y, o.W * _scale, o.H * _scale), look, "", 0.5);
            }
            foreach (var im in _clipboard.Images)
            {
                var p0 = WorldToScreen(im.X + dx, im.Y + dy);
                DrawImageIn(ctx, im, new Rect(p0.X, p0.Y, im.W * _scale, im.H * _scale), 0.5);
            }
            // a pawns only move has no cells, the ghost is the pawns themselves
            if (_moveArmed && _armedPawnsOnly)
                foreach (var p in _clipboard.Pawns)
                {
                    var nc = new CellKey(p.Cell.X + dx, p.Cell.Y + dy);
                    var p0 = WorldToScreen(nc.X, nc.Y);
                    ctx.FillRectangle(ghost, new Rect(p0.X, p0.Y, _scale, _scale));
                    DrawPawnGlyph(ctx, nc, p.TemplateId, p.Statuses, 0.45);
                }
        }
    }

    /// Draws one pawn glyph (two-tone circles, unique image, broken-reference X,
    /// status ring) at a cell. Shared by the placed-pawn pass and the per-pawn
    /// drag ghost. alpha < 1 renders it semi-transparent (ghost).
    private static readonly IBrush NamePillBg = new SolidColorBrush(Color.FromArgb(215, 16, 16, 22));
    private static readonly IBrush NamePillText = new SolidColorBrush(Color.FromRgb(240, 240, 245));

    // the pawn name centered under its cell, clipped to about three cells wide
    // inside the pawn circle, low down: the status timers sit around the
    // ring, so the name keeps out of their way (user)
    private void DrawNamePill(DrawingContext ctx, CellKey c, string name, double fs)
    {
        var ft = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Inter"), fs, NamePillText)
        { MaxTextWidth = Math.Max(_scale * 0.9, 44), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        var center = WorldToScreen(c.X + 0.5, c.Y + 0.5);
        double w = ft.Width + 8, h = ft.Height + 2;
        double x = center.X - w / 2, y = center.Y + _scale * 0.26 - h / 2;
        ctx.DrawRectangle(NamePillBg, null, new Rect(x, y, w, h), 3, 3);
        ctx.DrawText(ft, new Point(x + 4, y + 1));
    }

    /// the status presets for a pawn's statuses, in the presets' order (names
    /// no preset has are left out)
    public static List<PawnStatus> RingStatuses(CampaignPresets presets, IReadOnlyList<PawnStatusEntry>? statuses)
    {
        if (statuses is null || statuses.Count == 0) return new();
        return presets.PawnStatuses.Where(s => s.Name != CampaignPresets.NoStatus && statuses.Any(e => e.Name == s.Name)).ToList();
    }

    /// the arcs of a pawn's status ring: one equal slice per status from the
    /// top, clockwise, with gapDeg between them (shrunk to at most half a
    /// slice). one status is the whole circle. two are turned a quarter so
    /// they sit top and bottom with the gaps at the sides (user). with the
    /// pawn's entries, a timed status's arc keeps its start and loses a sixth
    /// of its length per turn under 6 (ArcFraction)
    private static double ArcStart(int n) => n == 2 ? -180 : -90;

    /// the angle (degrees, 0 = right, clockwise) at the middle of arc i of n:
    /// where that status's turn timer sits. one status: the top right
    public static double ArcMiddle(int i, int n) => n <= 1 ? -45 : ArcStart(n) + (i + 0.5) * 360.0 / n;

    /// how much of its slice a status's arc fills: whole for 6 or more turns
    /// left or permanent (-1), then a sixth less per turn, nothing at 0
    public const int ArcFullTurns = 6;
    public static double ArcFraction(int turns) =>
        turns < 0 || turns >= ArcFullTurns ? 1 : turns / (double)ArcFullTurns;

    public static List<(Geometry geo, PawnStatus st)> StatusArcs(Point center, double r, IReadOnlyList<PawnStatus> statuses, double gapDeg,
        IReadOnlyList<PawnStatusEntry>? entries = null)
    {
        var res = new List<(Geometry, PawnStatus)>();
        int n = statuses.Count;
        if (n == 0) return res;
        double Fraction(PawnStatus st) =>
            entries?.FirstOrDefault(e => e.Name == st.Name) is { } e ? ArcFraction(e.Turns) : 1;
        if (n == 1 && Fraction(statuses[0]) >= 1) { res.Add((new EllipseGeometry(new Rect(center.X - r, center.Y - r, r * 2, r * 2)), statuses[0])); return res; }
        double slice = 360.0 / n, gap = n == 1 ? 0 : Math.Min(Math.Max(0, gapDeg), slice * 0.5);
        double start = ArcStart(n);
        Point At(double deg) => new(center.X + r * Math.Cos(deg * Math.PI / 180), center.Y + r * Math.Sin(deg * Math.PI / 180));
        for (int i = 0; i < n; i++)
        {
            double a0 = start + i * slice + gap / 2, sweep = (slice - gap) * Fraction(statuses[i]);
            if (sweep <= 0.01) continue;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(At(a0), false);
                g.ArcTo(At(a0 + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            res.Add((geo, statuses[i]));
        }
        return res;
    }

    private void DrawPawnGlyph(DrawingContext ctx, CellKey c, string templateId, IReadOnlyList<PawnStatusEntry>? statuses, double alpha = 1.0)
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
            if (!string.IsNullOrEmpty(t.ImageB64))
            {
                // unique pawn: the image replaces the two-tone circles entirely.
                // center-crop to a square, then clip to the pawn circle so the
                // corners never stick out past the status ring (the pawn menus
                // show images as circles the same way)
                var bmp = LoadPawnImage(t.Id);
                if (bmp is not null)
                {
                    double side = Math.Min(bmp.PixelSize.Width, bmp.PixelSize.Height);
                    var srcRect = new Rect((bmp.PixelSize.Width - side) / 2, (bmp.PixelSize.Height - side) / 2, side, side);
                    var dest = new Rect(center.X - outerR, center.Y - outerR, outerR * 2, outerR * 2);
                    using (ctx.PushGeometryClip(new EllipseGeometry(dest)))
                        ctx.DrawImage(bmp, srcRect, dest);
                }
            }
            else
            {
                var (effOuter, effInner) = t.Effective(Presets);
                var outerBrush = new SolidColorBrush(Color.FromRgb((byte)effOuter.r, (byte)effOuter.g, (byte)effOuter.b));
                var innerBrush = new SolidColorBrush(Color.FromRgb((byte)effInner.r, (byte)effInner.g, (byte)effInner.b));
                ctx.DrawEllipse(outerBrush, null, center, outerR, outerR);
                ctx.DrawEllipse(innerBrush, null, center, innerR, innerR);
            }
            // status ring on the outer edge: the "none" ring always, then each of
            // the placed pawn's own statuses as an arc over it
            var none = Presets.FindPawnStatus(CampaignPresets.NoStatus);
            if (none.Thickness > 0)
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb((byte)none.R, (byte)none.G, (byte)none.B)), none.Thickness), center, outerR, outerR);
            foreach (var (geo, st) in StatusArcs(center, outerR, RingStatuses(Presets, statuses), Settings.PawnStatusGap, statuses))
                if (st.Thickness > 0)
                    ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb((byte)st.R, (byte)st.G, (byte)st.B)), st.Thickness), geo);
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
        // the preview is a throwaway copy: nothing done to it is an edit
        if (_remote is not null)
        {
            // a joined player's label edits (resize) go to the DM
            if (Mode == ToolMode.Label && SelectedLabel is { } l && PlayerLabels.Editable(l)) RemoteLabelChanged?.Invoke(l);
            if (Mode == ToolMode.Image && SelectedImage is { } im && PlayerImages.Editable(im)) RemoteImageChanged?.Invoke(im);
            InvalidateVisual();
            return;
        }
        if (_preview is not null) { _preview = PlayerView.Build(_document); InvalidateVisual(); return; }
        ActionCompleted?.Invoke();
        InvalidateVisual();
    }

    /// Starts a per-pawn drag when the press is on a pawn: the pawn follows the
    /// cursor as a ghost and lands on the release cell (swapping with a pawn
    /// there). Releasing on the same cell shows the pawn's config instead.
    /// Used by the move, select and pawn tools.
    private bool BeginPawnDrag(PointerPressedEventArgs e, CellKey cell)
    {
        if (!Document.Pawns.TryGetValue(cell, out var pawn)) return false;
        _pawnDragging = true;
        _pawnDragPawn = pawn;
        _pawnDragStart = cell;
        _pawnDragEnd = cell;
        e.Pointer.Capture(this);
        return true;
    }

    /// per-pawn move: lift the pawn from its source cell and place it on the
    /// landing cell. one undoable commit. landing on another pawn SWAPS the
    /// two pawns' positions. nothing but pawns changes.
    private void LandPawn(Pawn pawn, CellKey from, CellKey target)
    {
        var occupant = Document.Pawns.TryGetValue(target, out var occ) ? occ : null;
        Document.Pawns.Remove(from);
        Document.Pawns[target] = new Pawn { Cell = target, TemplateId = pawn.TemplateId, Statuses = pawn.CloneStatuses(), DmOnly = pawn.DmOnly };
        if (occupant is not null)
        {
            Document.Pawns[from] = new Pawn { Cell = from, TemplateId = occupant.TemplateId, Statuses = occupant.CloneStatuses(), DmOnly = occupant.DmOnly };
            Status("swapped pawns");
        }
        else
        {
            Status($"moved pawn to ({target.X}, {target.Y})");
        }
        Commit();
    }

    private static bool Shifted(PointerPressedEventArgs e) => e.KeyModifiers.HasFlag(KeyModifiers.Shift);

    /// Starts a selection box (select, move and pawn tools). A shift box in the
    /// move or pawn tool carries only pawns.
    private void BeginBox(PointerPressedEventArgs e, CellKey cell)
    {
        _selectDragging = true;
        _shiftBox = Shifted(e);
        _selectStart = cell;
        _selectEnd = cell;
        e.Pointer.Capture(this);
    }

    /// whether a new pawn starts DM only: Player pawns start visible to the
    /// players (user), every other new pawn hidden
    public bool NewPawnHidden(string templateId) => !(Library.Find(templateId) is { } t && PlayerPawns.Controlled(t));

    /// the select tool's plain click (or a press on an item without a drag):
    /// opens the element under the cursor. a pawn here means a shift+click
    /// (a plain press on a pawn drags it), which opens it like a plain click.
    /// the boxed item's handles go unless the click opens one again
    private void SelectClick(Point world, CellKey cell)
    {
        ClearBoxSelection();
        if (Document.Pawns.TryGetValue(cell, out var clicked))
        {
            PawnConfigRequested?.Invoke(clicked);
            return;
        }
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
            var lbl = LabelAt(cell);
            if (lbl is not null)
            {
                if (_pressClicks >= 2) LabelEditRequested?.Invoke(lbl);
                else LabelConfigRequested?.Invoke(lbl);
            }
            else if (Document.ImageAt(cell) is { } img)
            {
                SelectedImageId = img.Id;
                ImageConfigRequested?.Invoke(img);
            }
            else if (Document.ObjectAt(cell) is { } obj)
            {
                SelectedObjectId = obj.Id;
                ObjectConfigRequested?.Invoke(obj);
            }
            else NothingClicked?.Invoke();
        }
    }

    /// a right click without a drag (world point and its cell): the window
    /// opens the context menu for what is there
    public event Action<Point, CellKey>? ContextRequested;
    private Point? _rightPress;

    /// copies just the pawn on a cell (the pawn's context menu)
    public void CopyPawn(CellKey cell)
    {
        if (!Document.Pawns.ContainsKey(cell)) return;
        _clipboard = PawnsOnlySnapshot(Document.CaptureRegion(cell.X, cell.Y, cell.X, cell.Y), out _);
        _cutSource = null;
        _rotTurns = 0;
        Status("pawn copied - ctrl+v pastes it in the select tool");
        InvalidateVisual();
    }

    /// a click that hit nothing to open (select tool, a door tool click off
    /// the walls, a pawn tool click with nothing armed): an open item's view
    /// goes back to the tool's view
    public event Action? NothingClicked;

    /// The pawn tool's plain click on a cell with no pawn: places the armed pawn.
    private void PawnToolClick(CellKey cell)
    {
        if (Document.Pawns.TryGetValue(cell, out var existing)) { PawnConfigRequested?.Invoke(existing); return; }
        if (ArmedPawn is not null)
        {
            // place a pawn that hard-references the armed library template.
            // a hero moves instead: the old copy goes and keeps its status.
            var placed = new Pawn { Cell = cell, TemplateId = ArmedPawn.Id, DmOnly = NewPawnHidden(ArmedPawn.Id) };
            Document.PlacePawn(placed);
            if (ArmedPawn.Hero && TakeHeroCopies(cell, ArmedPawn.Id) is { } old)
            {
                placed.Statuses = old.CloneStatuses();
                placed.DmOnly = old.DmOnly;
                Status($"moved hero {(string.IsNullOrWhiteSpace(ArmedPawn.Name) ? "pawn" : ArmedPawn.Name)}");
            }
            Commit();
            PawnPlaced?.Invoke(ArmedPawn.Id);
        }
        else
        {
            NothingClicked?.Invoke();
            Status("select a pawn in the pawn menu first", StatusKind.Warning);
        }
    }

    // ---- input ----
    private bool _panning;
    private Point _panStartScreen, _panStartOffset;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsRightButtonPressed || props.IsMiddleButtonPressed)
        {
            // a right click that does not drag opens the context menu
            _rightPress = props.IsRightButtonPressed ? e.GetPosition(this) : null;
            _panning = true;
            _panStartScreen = e.GetPosition(this);
            _panStartOffset = _offset;
            e.Pointer.Capture(this);
            return;
        }

        if (!props.IsLeftButtonPressed) return;
        if (ReadOnlyView && Mode != ToolMode.Measure && !JoinedTool) return;
        _pressClicks = e.ClickCount;

        var world = ScreenToWorld(e.GetPosition(this));
        var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
        // an area pick or an armed formation takes the click in any tool
        if (FormationPressed(e, cell)) return;
        if (MeasurePressed(e, world, cell)) return;
        if (SightPressed(e, cell)) return;
        if (RevealPressed(e, world, cell)) return;
        if (PlayerPawnPressed(e, cell)) return;

        switch (Mode)
        {
            case ToolMode.Select:
                // inside the selected region: drag to move what is in it
                if (!Shifted(e) && BeginDragMove(e, cell)) break;
                // the open object, label or image: its handles resize, its
                // dashed edge moves it
                if (SelectBoxPressed(e)) break;
                // pressing directly on a pawn: per-pawn drag (select and move
                // tools). a click without a drag shows the pawn's info. shift
                // starts the box even on a pawn.
                if (!Shifted(e) && BeginPawnDrag(e, cell)) break;
                // a label, image or object: drag it to move it, a click opens it
                if (!Shifted(e) && SelectBodyPressed(e, world, cell)) break;
                BeginBox(e, cell);
                break;

            case ToolMode.Room:
                _dragging = true;
                _dragStart = cell;
                _dragEnd = cell;
                e.Pointer.Capture(this);
                break;

            case ToolMode.Floor:
                if (FloorFloodFill)
                {
                    // flood fill: click commits. The region is the room the
                    // clicked cell is in, bounded by walls, doors, stairs and
                    // angled walls. Floors only - nothing is cleared.
                    var region = Document.FloodFillRegion(cell);
                    _floodPreview = null;
                    _floodPreviewCell = null;
                    if (region is null)
                        Status("floor fill: region not enclosed (cap 10000 cells), nothing filled", StatusKind.Error);
                    else
                    {
                        // exact floor geometry: full cells plus the triangular
                        // sections left by diagonal walls, same as polygon floors
                        Document.AddFloodRegion(new FloodRegion { Rings = Document.BuildFloodRings(region) });
                        Commit();
                        Status($"floor fill: {region.Count} cells");
                    }
                    InvalidateVisual();
                    break;
                }
                _dragging = true;
                _dragStart = cell;
                _dragEnd = cell;
                e.Pointer.Capture(this);
                break;
            case ToolMode.Stair:
            case ToolMode.Erase:
                _dragging = true;
                _dragStart = cell;
                _dragEnd = cell;
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
                    var near = doors.FirstOrDefault(d => Math.Abs(d.T - t) < DoorLength);
                    if (near is not null)
                    {
                        DoorConfigRequested?.Invoke(near);
                    }
                    else if (Document.TryPlaceAngledDoor(wall, t, DefaultDoorType, DefaultDoorState, out var ad))
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
                        Status("can't place a door here: a wall at the vertex would bisect the door", StatusKind.Error);
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
                        Document.TryPlaceVertexDoor(vdxx, vdy, orient.Value, DefaultDoorType, DefaultDoorState, out var vdoor);
                        Commit();
                        DoorConfigRequested?.Invoke(vdoor);
                    }
                    break;
                }
                if (de is null) { NothingClicked?.Invoke(); break; }
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
                    Document.TryPlaceDoor(doorEdge, DefaultDoorType, DefaultDoorState, out var d);
                    Commit();
                    DoorConfigRequested?.Invoke(d);
                }
                break;

            case ToolMode.Label:
                // the open label's resize handles come first
                if (LabelHandlePressed(e)) break;
                // clicking a label edits it. otherwise drag out a range (a
                // click is a 1x1 range) and the label is placed on release.
                var lbl = LabelAt(cell);
                if (lbl is not null && _remote is not null && !PlayerLabels.Editable(lbl))
                {
                    Status("only the DM edits that label", StatusKind.Error);
                    break;
                }
                if (lbl is not null)
                {
                    // a double click edits the text on the map; otherwise drag
                    // it to move it, a click opens it
                    if (e.ClickCount >= 2) LabelEditRequested?.Invoke(lbl);
                    else { var open = lbl; BeginBodyMove(e, world, open, null, null, () => LabelConfigRequested?.Invoke(open)); }
                    break;
                }
                _dragging = true;
                _dragStart = cell;
                _dragEnd = cell;
                e.Pointer.Capture(this);
                break;

            case ToolMode.Object:
                ObjectPressed(e, cell);
                break;

            case ToolMode.Image:
                ImagePressed(e, cell);
                break;

            case ToolMode.Pawn:
                // inside a shift box's pawns: drag to move them
                if (!Shifted(e) && BeginDragMove(e, cell)) break;
                // shift+drag: a box that picks pawns
                if (Shifted(e)) { BeginBox(e, cell); break; }
                if (HasSelection) ClearSelection();
                // a placed pawn drags like in the select tool; a click opens it
                if (BeginPawnDrag(e, cell)) break;
                PawnToolClick(cell);
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
                    Document.AddPolygon(_polyVerts, passageway, strict, PolygonMergeNearby, new CellKey(vx, vy));
                    _polyVerts.Clear();
                    Commit();
                    Status("polygon closed");
                }
                else
                {
                    _polyVerts.Add((vx, vy));
                    Status($"vertex ({vx},{vy}) added - click first vertex or press Enter to close");
                }
                break;

            case ToolMode.Wall:
                // snap the press to the nearest LATTICE vertex, same rule as the
                // polygon tool. the drag previews the wall line; release commits.
                int lsx = (int)Math.Round(world.X), lsy = (int)Math.Round(world.Y);
                _lineStart = (lsx, lsy);
                _lineEnd = (lsx, lsy);
                _lineDragging = true;
                e.Pointer.Capture(this);
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
        var hoverBefore = CursorCell;
        CursorCell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
        HoverTimers(hoverBefore);
        if (!_objResizing && !_imgResizing && !_labelResizing) UpdateBoxCursor(e.GetPosition(this));
        if (FormationMoved(world)) return;
        if (ObjectMoved(world)) return;
        if (ImageMoved(world)) return;
        if (MeasureMoved(world)) return;
        if (SightMoved(world)) return;
        if (RevealMoved(world)) return;
        if (PlayerPawnMoved(world)) return;
        if (LabelResizeMoved(world)) return;
        if (DragMoveMoved(world)) return;
        _polyCursor = world; // polygon closing preview follows the cursor
        _pasteCursor = world; // paste ghost follows the cursor
        if (!_selectDragging && !_dragging && !_panning)
        {
            // redraw so the paste ghost tracks the cursor on hover
            if (_clipboard is not null && !_clipboard.Empty && (Mode == ToolMode.Select || (Mode == ToolMode.Pawn && _moveArmed)))
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

        if (_lineDragging)
        {
            // the ghost end stays grid-aligned: snap the cursor to the nearest
            // lattice vertex, same rule as the press.
            var end = ((int)Math.Round(world.X), (int)Math.Round(world.Y));
            if (end != _lineEnd) { _lineEnd = end; InvalidateVisual(); }
            return;
        }

        // flood fill preview: hovering a cell highlights the whole region a
        // click would fill. Recomputed only when the cursor cell changes.
        if (Mode == ToolMode.Floor && FloorFloodFill && !_dragging && !_panning)
        {
            var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
            if (cell != _floodPreviewCell)
            {
                _floodPreviewCell = cell;
                var region = Document.FloodFillRegion(cell);
                _floodPreview = region is not null && region.Count <= 2000
                    ? Document.BuildFloodRings(region) : null;
                InvalidateVisual();
            }
            return;
        }

        if (_dragging)
        {
            var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
            if (Mode == ToolMode.Room || Mode == ToolMode.Floor || Mode == ToolMode.Stair || Mode == ToolMode.Erase || Mode == ToolMode.Label)
            {
                if (cell != _dragEnd) { _dragEnd = cell; InvalidateVisual(); }
            }
        }
        else if (Mode == ToolMode.Door)
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
            var at = e.GetPosition(this);
            if (_rightPress is Point rp && Dist(rp, at) < 4)
            {
                var w = ScreenToWorld(at);
                ContextRequested?.Invoke(w, new CellKey((int)Math.Floor(w.X), (int)Math.Floor(w.Y)));
            }
            _rightPress = null;
            return;
        }
        if (FormationReleased(e)) return;
        if (ObjectReleased(e)) return;
        if (ImageReleased(e)) return;
        if (MeasureReleased(e)) return;
        if (SightReleased(e)) return;
        if (RevealReleased(e)) return;
        if (PlayerPawnReleased(e)) return;
        if (LabelResizeReleased(e)) return;
        if (DragMoveReleased(e)) return;

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
                // (and the select tool's boxed item lets go)
                if (Mode == ToolMode.Select) ClearBoxSelection();
                PawnConfigRequested?.Invoke(pawn);
            }
            else LandPawn(pawn, _pawnDragStart, target);
            InvalidateVisual();
            return;
        }

        if (_selectDragging)
        {
            _selectDragging = false;
            e.Pointer.Capture(null);
            var world = ScreenToWorld(e.GetPosition(this));
            var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
            _selectEnd = cell;
            // decided before a pawns only shrink can turn the box into one cell
            bool singleCell = _selectStart == _selectEnd;
            _shiftBox = false;
            if (_remote is not null)
            {
                RemoteBoxReleased(cell, singleCell);
                InvalidateVisual();
                return;
            }
            if (Mode == ToolMode.Pawn && singleCell)
            {
                // a shift+click without a drag is a plain pawn tool click
                ClearSelection();
                PawnToolClick(cell);
                InvalidateVisual();
                return;
            }
            if (Mode == ToolMode.Pawn && !singleCell)
            {
                // a shift box picks the pawns: the selection shrinks to them,
                // then a drag inside it moves them
                var (bx0, by0, bx1, by1) = NormRect(_selectStart, _selectEnd);
                PawnsOnlySnapshot(Document.CaptureRegion(bx0, by0, bx1, by1), out var pbox);
                if (!Document.Pawns.Keys.Any(k => k.X >= bx0 && k.X <= bx1 && k.Y >= by0 && k.Y <= by1))
                {
                    ClearSelection();
                    Status("no pawns in that region", StatusKind.Warning);
                }
                else { _selectStart = new CellKey(pbox.x0, pbox.y0); _selectEnd = new CellKey(pbox.x1, pbox.y1); }
                InvalidateVisual();
                return;
            }
            // a region drag lets go of the boxed object, label or image
            if (!singleCell && Mode == ToolMode.Select) ClearBoxSelection();
            if (singleCell) SelectClick(world, cell);
            InvalidateVisual();
            return;
        }

        if (_lineDragging)
        {
            _lineDragging = false;
            e.Pointer.Capture(null);
            var world = ScreenToWorld(e.GetPosition(this));
            (int x, int y) lend = ((int)Math.Round(world.X), (int)Math.Round(world.Y));
            var lstart = _lineStart!.Value;
            _lineStart = null;
            _lineEnd = null;
            if (lend == lstart)
            {
                // zero-length line: press and release on the same vertex
                Status("wall line cancelled: start and end are the same vertex", StatusKind.Warning);
            }
            else
            {
                Document.AddLine(lstart.x, lstart.y, lend.x, lend.y);
                Commit();
                Status($"wall ({lstart.x},{lstart.y}) -> ({lend.x},{lend.y})");
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
                // boundary shared with that room stays open so the new region
                // connects as a passageway. other rooms it touches stay walled.
                bool strict = RoomStrict;
                bool passageway = !strict && Document.Cells.Contains(_dragStart);
                Document.AddRoom(x0, y0, x1, y1, passageway, strict, RoomMergeNearby, _dragStart);
                Commit();
                break;
            }
            case ToolMode.Label:
            {
                var (x0, y0, x1, y1) = NormRect(_dragStart, _dragEnd);
                var lbl = new MapLabel
                {
                    Cell = new CellKey(x0, y0), W = x1 - x0 + 1, H = y1 - y0 + 1,
                    Text = Settings.DefaultLabelText,
                };
                Document.Labels.Add(lbl);
                if (_remote is not null)
                {
                    // a player's label: shown here at once, the DM's app keeps it
                    lbl.Id = MapLabel.NewId();
                    lbl.ClientEditable = true;
                    lbl.DmOnly = false;
                    RemoteLabelChanged?.Invoke(lbl);
                    InvalidateVisual();
                }
                else Commit();
                LabelConfigRequested?.Invoke(lbl);
                LabelEditRequested?.Invoke(lbl);
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
                        if (f.Labels) Document.Labels.RemoveAll(l => l.Covers(c));
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
                // flood region floors (polygon and floor tool fills) lose the
                // erased rect, the rest of each floor stays
                if (f.Cells) Document.SubtractFloorRect(x0, y0, x1, y1);
                // objects go when the erase rect touches any part of them
                if (f.Objects && EraseObjects(x0, y0, x1, y1) > 0) ObjectsChanged?.Invoke();
                if (f.Images && EraseImages(x0, y0, x1, y1) > 0) ImagesChanged?.Invoke();
                // "adjust walls": the erase may have removed outer walls (or the
                // room cells they sat on) - rebuild the room boundary so the new
                // outer edge gets fresh walls, including along a clipped floor.
                // scoped to the erase region.
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
        if (e.KeyModifiers == KeyModifiers.Shift && FormationArmed)
        {
            RotateFormation(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers == KeyModifiers.Shift && Mode == ToolMode.Sight)
        {
            StepSight(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }
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

    // an open door shows floor through its hollow glyph, so its wall gap is
    // filled with the floor color. the background color read as a line
    // across the doorway and made the door look sealed when zoomed out.
    private IBrush GapFill(DoorState state) => state == DoorState.Open ? CellFill : Background;

    // door wall gap setting in cells, past each end of the glyph
    private double DoorWallGap => Math.Clamp(Settings.DoorWallGap, 0, AppSettings.MaxDoorWallGap) / 100.0;

    // how far the floor colored wall gap reaches from the middle of the door
    // on edge e toward its end vertex (vx, vy), in cells. it reaches the glyph
    // end plus the door wall gap setting, but stops at the vertex when the
    // wall ends there, and short of the vertex by half a wall when another
    // wall meets it at a right angle, so corners and T joins stay closed.
    private double GapReach(EdgeKey e, int vx, int vy, bool towardLow)
    {
        double want = DoorLength / 2 + DoorWallGap;
        if (want <= DoorLength / 2) return DoorLength / 2;
        bool horizontal = e.Side == Side.North;
        bool perpendicular = horizontal
            ? Document.HasWall(new EdgeKey(new CellKey(vx, vy - 1), Side.West)) || Document.HasWall(new EdgeKey(new CellKey(vx, vy), Side.West))
            : Document.HasWall(new EdgeKey(new CellKey(vx - 1, vy), Side.North)) || Document.HasWall(new EdgeKey(new CellKey(vx, vy), Side.North));
        bool continues = horizontal
            ? Document.HasWall(new EdgeKey(new CellKey(towardLow ? vx - 1 : vx, vy), Side.North))
            : Document.HasWall(new EdgeKey(new CellKey(vx, towardLow ? vy - 1 : vy), Side.West));
        double limit = perpendicular ? 0.5 - WallWidth / 2 / _scale : continues ? 1.0 : 0.5;
        return Math.Max(DoorLength / 2, Math.Min(want, limit));
    }

    private void DrawDoor(DrawingContext ctx, EdgeKey e, Point a, Point b, Door door)
    {
        var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        bool horizontal = Math.Abs(b.Y - a.Y) < 0.001;
        double gap = _scale * DoorLength; // wall gap where the door sits
        double gw = WallWidth + 1; // a pixel over the wall to cover its anti-aliased edge
        if (DoorWallGap > 0)
        {
            // the extra gap past the glyph shows floor whatever the door state
            int x = e.Cell.X, y = e.Cell.Y;
            double lo = _scale * (horizontal ? GapReach(e, x, y, true) : GapReach(e, x + 1, y, true));
            double hi = _scale * (horizontal ? GapReach(e, x + 1, y, false) : GapReach(e, x + 1, y + 1, false));
            ctx.FillRectangle(CellFill, horizontal
                ? new Rect(mid.X - lo, mid.Y - gw / 2, lo + hi, gw)
                : new Rect(mid.X - gw / 2, mid.Y - lo, gw, lo + hi));
        }
        var gapRect = horizontal
            ? new Rect(mid.X - gap / 2, mid.Y - gw / 2, gap, gw)
            : new Rect(mid.X - gw / 2, mid.Y - gap / 2, gw, gap);
        ctx.FillRectangle(GapFill(door.State), gapRect); // erase wall segment for the gap
        DrawDoorGlyph(ctx, mid, horizontal ? 0 : Math.PI / 2, DoorBrush(door.Type), door.State);
    }

    /// Draws an angled wall segment with door gaps. The wall is drawn as the
    /// sub-segments between the door gaps (the same door length as
    /// axis-aligned doors), then each door glyph is drawn centered at its T along
    /// the segment, rotated to the segment angle. Doors are sorted by T so gaps
    /// tile the segment left-to-right. Door T is stored in CELL units; screen
    /// positions are in pixels, so T is multiplied by _scale to convert.
    private void DrawAngledWall(DrawingContext ctx, AngledWall w, Point a, Point b, List<AngledDoor> doors)
    {
        double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        if (len < 0.0001) return;
        double ux = (b.X - a.X) / len, uy = (b.Y - a.Y) / len; // unit direction
        double gapHalf = _scale * (DoorLength / 2 + DoorWallGap); // door gap half-width in screen px, door wall gap setting included
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
