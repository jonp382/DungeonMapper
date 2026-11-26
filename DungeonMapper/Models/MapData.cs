using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonMapper.Models;

/// A cell coordinate. 1 unit = 5ft. Y increases downward (screen space).
public readonly record struct CellKey(int X, int Y);

/// An angled wall: a line segment between two LATTICE vertices (integer grid
/// points = cell corners). Endpoints are grid-anchored (the "never free-floating"
/// rule); the segment itself may cut diagonally through cells. Axis-aligned edges
/// are stored as EdgeKey walls, so this type only holds diagonal segments.
public readonly record struct AngledWall(int Ax, int Ay, int Bx, int By);

public sealed class FloodRegion
{
    // closed rings of world points (cell corners and diagonal cut points),
    // filled even-odd so holes come out right. the floor is this exact
    // geometry, so a room bounded by diagonals fills its triangular sections
    // too, not just the full cells. polygon floors and floor tool fills are
    // both stored this way.
    public List<List<(double x, double y)>> Rings = new();
    public FloodRegion Clone()
    {
        var r = new FloodRegion();
        foreach (var ring in Rings) r.Rings.Add(new List<(double, double)>(ring));
        return r;
    }
    public (double minX, double minY, double maxX, double maxY) Bounds()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var ring in Rings)
            foreach (var (x, y) in ring)
            {
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        return (minX, minY, maxX, maxY);
    }
}

/// A door on an angled wall. The door sits at parameter T (0..1) along its wall
/// segment, centered, with a fixed projected width of 1 cell (matching axis-aligned
/// doors). Type/State mirror the edge Door. Multiple doors may sit on one segment.
public sealed class AngledDoor : IDoorConfig
{
    public double T { get; set; } = 0.5; // door center along the segment, 0..1
    public string Type { get; set; } = "standard";
    public DoorState State { get; set; } = DoorState.Closed;
    /// hidden from players even on revealed cells (multiplayer). new items
    /// start hidden, files from before the flag load as visible
    public bool DmOnly { get; set; } = true;
}

/// A grid vertex (lattice point = cell corner) plus the door orientation. A
/// vertex door is fixed size (5ft = one cell wall width), centered on the
/// vertex, straddling the two collinear unit edges meeting at that vertex
/// (2.5ft on each side). Horizontal doors sit on the left+right edges,
/// vertical doors on the top+bottom edges.
public readonly record struct VertexDoorKey(int X, int Y, bool Horizontal);

/// A door centered on a grid vertex. Implements IDoorConfig so the existing
/// door config dialog serves it. Valid only on straight walls: the two
/// straddled edges are collinear unit edges; the perpendicular pair must be
/// empty, so no remaining wall bisects the door (crossroads and corners are
/// rejected at placement).
public sealed class VertexDoor : IDoorConfig
{
    public string Type { get; set; } = "standard";
    public DoorState State { get; set; } = DoorState.Closed;
    /// hidden from players even on revealed cells (multiplayer). new items
    /// start hidden, files from before the flag load as visible
    public bool DmOnly { get; set; } = true;
}

public enum Side { North, East, South, West }

/// A cell edge, keyed by cell + side. Normalized so shared edges have one key:
/// South edges fold to the North edge of the cell above; West edges fold to the
/// East edge of the cell to the left.
public readonly record struct EdgeKey(CellKey Cell, Side Side)
{
    public static EdgeKey Normalize(CellKey cell, Side side)
    {
        switch (side)
        {
            // South edge of (x,y) is the segment at world y=y+1, shared with the
            // cell below: North edge of (x, y+1).
            case Side.South: return new EdgeKey(new CellKey(cell.X, cell.Y + 1), Side.North);
            case Side.West: return new EdgeKey(new CellKey(cell.X - 1, cell.Y), Side.East);
            default: return new EdgeKey(cell, side);
        }
    }

    /// The cell on the opposite side of this edge (the neighbor sharing it).
    public CellKey NeighborCell()
    {
        var (x, y) = (Cell.X, Cell.Y);
        return Side switch
        {
            Side.North => new CellKey(x, y - 1),
            Side.South => new CellKey(x, y + 1),
            Side.East => new CellKey(x + 1, y),
            Side.West => new CellKey(x - 1, y),
            _ => Cell,
        };
    }
}

public enum DoorState { Open, Closed, Locked }

/// Shared surface for doors on axis-aligned edges (Door) and doors on angled
/// walls (AngledDoor), so the door config UI serves both.
public interface IDoorConfig
{
    string Type { get; set; }
    DoorState State { get; set; }
    bool DmOnly { get; set; }
}

/// A door on an edge. A door implies a wall with a gap: the edge must have a wall.
/// Type is a free-form string (e.g. "standard", "portcullis", "bricked", "magical")
/// so custom doors stay modular; the renderer picks color/symbol per type.
public sealed class Door : IDoorConfig
{
    public string Type { get; set; } = "standard";
    public DoorState State { get; set; } = DoorState.Closed;
    /// hidden from players even on revealed cells (multiplayer). new items
    /// start hidden, files from before the flag load as visible
    public bool DmOnly { get; set; } = true;
}

public enum StairDirection { Up, Down, Left, Right }

/// Direction from a stair drag: horizontal drags (|dx| >= |dy|) give Right for
/// L-to-R, Left for R-to-L; vertical drags give Down for top-to-bottom (y
/// grows downward), Up for bottom-to-top. A zero drag (single click) defaults
/// to Down. Diagonal drags resolve to the dominant axis - no diagonal direction.
public static class StairDrag
{
    public static StairDirection FromDrag(int dx, int dy)
        => dx == 0 && dy == 0
            ? StairDirection.Down
            : Math.Abs(dx) >= Math.Abs(dy)
                ? (dx >= 0 ? StairDirection.Right : StairDirection.Left)
                : (dy >= 0 ? StairDirection.Down : StairDirection.Up);
}

/// A stair is a cell feature, not an edge. TargetMap is reserved for future
/// multi-map tracking (stairs connecting maps); null = same map.
public sealed class Stair
{
    /// Axis-aligned only: Up/Down render as vertical triangles, Left/Right as
    /// horizontal. Dragging sets the default (bottom-to-top = Up, top-to-bottom
    /// = Down, left-to-right = Right, right-to-left = Left); the config dropdown
    /// overrides it. No diagonal directions.
    public StairDirection Direction { get; set; } = StairDirection.Down;
    public string? TargetMap { get; set; }
}

public enum LabelAlign { Left, Center, Right }

/// Text placed over a range of cells. Cell is the top-left cell, W and H the
/// range in cells (1x1 for a single cell label). The text is centered
/// vertically in the range and aligned left, center or right across it.
public sealed class MapLabel
{
    /// font sizes are stored at this many pixels per cell and scale with zoom
    public const double RefScale = 40;
    /// the default size of a single cell label
    public const double DefaultSize = 14;

    public CellKey Cell { get; set; }
    public int W { get; set; } = 1;
    public int H { get; set; } = 1;
    public LabelAlign Align { get; set; } = LabelAlign.Center;
    public string Text { get; set; } = "";
    // Per-label overrides. FontSize 0 = auto: the default size for a single
    // cell, or the size that fits the text in the range. Color -1 = inherit
    // the settings label color. oversized text may leak into neighboring
    // cells, which is allowed. clamped to 0..MaxFontSize so a silly typed or
    // loaded value cannot break text rendering.
    public const int MaxFontSize = 500;
    private int _fontSize;
    public int FontSize { get => _fontSize; set => _fontSize = Math.Clamp(value, 0, MaxFontSize); }
    public int R { get; set; } = -1;
    public int G { get; set; } = -1;
    public int B { get; set; } = -1;
    /// hidden from players even on revealed cells (multiplayer). new items
    /// start hidden, files from before the flag load as visible
    public bool DmOnly { get; set; } = true;
    /// multiplayer: players may edit and delete it (players' own labels start
    /// that way, the DM can hand over any label). needs an Id
    public bool ClientEditable { get; set; }
    /// multiplayer: the joined name of the player who made it, null = the DM
    public string? CreatedBy { get; set; }
    /// stable id so players can address the label; "" until it needs one
    public string Id { get; set; } = "";

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// true when the label has its own color, false when it follows the
    /// settings label color
    public bool HasOwnColor => R >= 0 && G >= 0 && B >= 0;

    public bool IsRange => W > 1 || H > 1;

    public bool Covers(CellKey c) =>
        c.X >= Cell.X && c.X < Cell.X + W && c.Y >= Cell.Y && c.Y < Cell.Y + H;

    /// a copy of this label anchored at another cell
    public MapLabel CopyAt(CellKey cell) => new()
    {
        Cell = cell, W = W, H = H, Align = Align, Text = Text,
        FontSize = FontSize, R = R, G = G, B = B, DmOnly = DmOnly,
        ClientEditable = ClientEditable, CreatedBy = CreatedBy, Id = Id,
    };
}

public enum Affiliation { Player, Ally, Enemy, Neutral }

/// A pawn placed on the map. It is a HARD REFERENCE to a library template by Id:
/// all visual data (name, affiliation, colors, status) is resolved from the
/// PawnLibrary at render time. TemplateId null/missing = broken reference,
/// rendered as an X. No stats/health - position only.
public sealed class Pawn
{
    public CellKey Cell { get; set; }
    public string TemplateId { get; set; } = "";
    // the pawn's own statuses (pawn types have none). empty = none. kept in
    // the order they were ticked; drawn in the status presets' order
    public List<PawnStatusEntry> Statuses { get; set; } = new();
    /// a deep copy of the statuses, for a pawn copied, moved or swapped
    public List<PawnStatusEntry> CloneStatuses() => Statuses.Select(s => s.Clone()).ToList();
    public bool HasStatus(string name) => Statuses.Any(s => s.Name == name);
    /// hidden from players even on revealed cells (multiplayer). new items
    /// start hidden, files from before the flag load as visible
    public bool DmOnly { get; set; } = true;
}

/// one status on a pawn: the status preset's name and the turns it has left
/// (turn tracking). -1 = permanent
public sealed class PawnStatusEntry
{
    public string Name { get; set; } = "";
    public int Turns { get; set; } = -1;
    public PawnStatusEntry Clone() => new() { Name = Name, Turns = Turns };
}

/// The whole map. Rooms are not a data structure - a room is just a set of cells.
public sealed partial class MapDocument
{
    public HashSet<CellKey> Cells { get; } = new();
    public Dictionary<EdgeKey, bool> Walls { get; } = new();
    public Dictionary<EdgeKey, Door> Doors { get; } = new();
    public Dictionary<CellKey, Stair> Stairs { get; } = new();
    public List<MapLabel> Labels { get; } = new();
    public Dictionary<CellKey, Pawn> Pawns { get; } = new();
    public List<AngledWall> AngledWalls { get; } = new();
    public Dictionary<AngledWall, List<AngledDoor>> AngledDoors { get; } = new();
    public List<FloodRegion> FloodRegions { get; } = new();
    public Dictionary<VertexDoorKey, VertexDoor> VertexDoors { get; } = new();
    // placed objects in placement order: later ones draw on top
    public List<MapObject> Objects { get; } = new();
    // placed images (Models/MapImages.cs) in placement order: later on top
    public List<MapImage> Images { get; } = new();
    // multiplayer: cells the DM has revealed to players. a mask over cell
    // coordinates, independent of the floor (copy, move and erase leave it)
    public HashSet<CellKey> Revealed { get; } = new();
    /// multiplayer: the area lit for players (the DM's anchored fog circle):
    /// center in world units and radius in cells. null = all revealed floor is
    /// lit. session only: not saved, not part of undo
    public (double x, double y, int radius)? PlayerLight { get; set; }
    /// turn tracking (Models/TurnTracker.cs): the turn of the combat on this
    /// map, 0 = no combat. saved and undone with the map
    public int CombatTurn { get; set; }

    /// the topmost object covering the cell, null when none
    public MapObject? ObjectAt(CellKey c)
    {
        for (int i = Objects.Count - 1; i >= 0; i--)
            if (Objects[i].Contains(c)) return Objects[i];
        return null;
    }

    public MapObject? FindObject(string id) => Objects.Find(o => o.Id == id);

    /// the topmost image covering the cell, null when none
    public MapImage? ImageAt(CellKey c)
    {
        for (int i = Images.Count - 1; i >= 0; i--)
            if (Images[i].Contains(c)) return Images[i];
        return null;
    }

    public MapImage? FindImage(string id) => Images.Find(o => o.Id == id);

    /// Door tool "close open doors": every open door of the type becomes
    /// closed. Locked and closed doors are left alone. Returns how many closed.
    public int CloseOpenDoors(string type)
    {
        int n = 0;
        foreach (var d in AllDoors())
            if (d.Type == type && d.State == DoorState.Open) { d.State = DoorState.Closed; n++; }
        return n;
    }

    /// every door on the map: edge, angled and vertex doors
    public IEnumerable<IDoorConfig> AllDoors()
    {
        foreach (var d in Doors.Values) yield return d;
        foreach (var l in AngledDoors.Values)
            foreach (var d in l) yield return d;
        foreach (var d in VertexDoors.Values) yield return d;
    }

    /// points every door of type `from` at type `to`. returns how many changed
    public int RetypeDoors(string from, string to)
    {
        int n = 0;
        foreach (var d in AllDoors())
            if (d.Type == from) { d.Type = to; n++; }
        return n;
    }

    /// The two collinear unit edges a vertex door straddles (2.5ft of the door
    /// on each). Keys are normalized (North/East). Horizontal: left+right edges
    /// meeting at the vertex; vertical: top+bottom edges.
    public static List<EdgeKey> VertexStraddles(VertexDoorKey v) => v.Horizontal
        ? new List<EdgeKey> { new EdgeKey(new CellKey(v.X - 1, v.Y), Side.North),
                              new EdgeKey(new CellKey(v.X, v.Y), Side.North) }
        : new List<EdgeKey> { new EdgeKey(new CellKey(v.X - 1, v.Y - 1), Side.East),
                              new EdgeKey(new CellKey(v.X - 1, v.Y), Side.East) };

    /// The perpendicular edge pair at a vertex: for a horizontal door, the
    /// top+bottom edges; for a vertical door, the left+right edges. A wall on
    /// any of these bisects the door, so placement is rejected when one exists.
    public static List<EdgeKey> VertexPerpendiculars(VertexDoorKey v) => v.Horizontal
        ? new List<EdgeKey> { new EdgeKey(new CellKey(v.X - 1, v.Y - 1), Side.East),
                              new EdgeKey(new CellKey(v.X - 1, v.Y), Side.East) }
        : new List<EdgeKey> { new EdgeKey(new CellKey(v.X - 1, v.Y), Side.North),
                              new EdgeKey(new CellKey(v.X, v.Y), Side.North) };

    /// The vertex-door keys whose straddled pair contains this normalized edge.
    /// Removing the wall removes those doors (door implies wall).
    private static List<VertexDoorKey> VertexDoorsOnEdge(EdgeKey e) => e.Side switch
    {
        Side.North => new List<VertexDoorKey> { new VertexDoorKey(e.Cell.X, e.Cell.Y, true),
                                                new VertexDoorKey(e.Cell.X + 1, e.Cell.Y, true) },
        Side.East => new List<VertexDoorKey> { new VertexDoorKey(e.Cell.X + 1, e.Cell.Y, false),
                                               new VertexDoorKey(e.Cell.X + 1, e.Cell.Y + 1, false) },
        _ => new List<VertexDoorKey>(),
    };

    public VertexDoor? GetVertexDoor(VertexDoorKey key)
        => VertexDoors.TryGetValue(key, out var d) ? d : null;

    /// True when a vertex door of this orientation can sit at (x,y): the
    /// perpendicular pair must be empty (no wall bisecting the door). The
    /// straddled walls are auto-created at placement, so they don't gate it.
    public bool VertexDoorValid(int x, int y, bool horizontal)
    {
        foreach (var e in VertexPerpendiculars(new VertexDoorKey(x, y, horizontal)))
            if (HasWall(e)) return false;
        return true;
    }

    /// Places a vertex door at lattice point (x,y) with the given orientation.
    /// Rejects crossroads/corners: a wall on the perpendicular pair bisects the
    /// door. Auto-creates the two straddled walls (door implies wall). Applies
    /// default type/state to a newly created door.
    public bool TryPlaceVertexDoor(int x, int y, bool horizontal, string defaultType, string defaultState, out VertexDoor door)
    {
        var key = new VertexDoorKey(x, y, horizontal);
        if (!VertexDoorValid(x, y, horizontal)) { door = null!; return false; }
        bool isNew = !VertexDoors.ContainsKey(key);
        door = isNew ? new VertexDoor() : VertexDoors[key];
        if (isNew)
        {
            door.Type = defaultType;
            if (Enum.TryParse<DoorState>(defaultState, ignoreCase: true, out var st)) door.State = st;
            VertexDoors[key] = door;
        }
        foreach (var e in VertexStraddles(key)) SetWall(e, true);
        return true;
    }

    /// Places a pawn on a cell. One pawn per cell (no stacking): placing on an
    /// occupied cell replaces the existing pawn.
    public void PlacePawn(Pawn pawn)
    {
        Pawns[pawn.Cell] = pawn;
    }

    public void RemovePawn(CellKey cell) => Pawns.Remove(cell);

    /// Doors sitting on an angled wall segment (empty list if none).
    public List<AngledDoor> GetAngledDoors(AngledWall wall)
        => AngledDoors.TryGetValue(wall, out var l) ? l : new List<AngledDoor>();

    /// Places a door on an angled wall at parameter t (0..1, door center along the
    /// segment). The door has a fixed projected width of 1 cell, so the segment
    /// must be at least 1 unit long and t must leave room for the gap
    /// (t in [0.5, len-0.5]). Returns false when the wall is too short or t
    /// doesn't fit. Multiple doors may sit on one segment.
    public bool TryPlaceAngledDoor(AngledWall wall, double t, string defaultType, string defaultState, out AngledDoor door)
    {
        door = null!;
        double len = Math.Sqrt((wall.Bx - wall.Ax) * (wall.Bx - wall.Ax) + (wall.By - wall.Ay) * (wall.By - wall.Ay));
        if (len < 1.0) return false; // wall too short to host a 1-cell door
        if (t < 0.5 || t > len - 0.5) return false; // door gap must fit inside the segment
        var list = AngledDoors.TryGetValue(wall, out var l) ? l : (AngledDoors[wall] = new List<AngledDoor>());
        door = new AngledDoor { T = t, Type = defaultType };
        if (Enum.TryParse<DoorState>(defaultState, ignoreCase: true, out var st)) door.State = st;
        list.Add(door);
        return true;
    }

    public bool HasWall(EdgeKey edge) => Walls.TryGetValue(EdgeKey.Normalize(edge.Cell, edge.Side), out var w) && w;

    public void SetWall(EdgeKey edge, bool exists)
    {
        var key = EdgeKey.Normalize(edge.Cell, edge.Side);
        if (exists) Walls[key] = true;
        else Walls.Remove(key);
        // Removing a wall removes any door on it (door implies wall).
        if (!exists && Doors.ContainsKey(key)) Doors.Remove(key);
        // A vertex door straddles two walls; removing either one removes the
        // door. This hook covers erase, wall-drag-delete, ClearRegion, and
        // room-merge interior removal in one place.
        if (!exists)
            foreach (var vk in VertexDoorsOnEdge(key))
                if (VertexDoors.ContainsKey(vk)) VertexDoors.Remove(vk);
    }

    /// rebuilds the room boundary walls in the region [x0,y0]-[x1,y1] expanded
    /// by one cell: a cell edge with floor on one side and none on the other
    /// gets a wall. floor means a room cell or a flood region's rings, so a
    /// cell cut by a diagonal counts by its floor side. edges an angled wall
    /// crosses are skipped, the diagonal is the boundary there and walls on
    /// those edges would draw a staircase. only adds walls, interior walls are
    /// never touched. scoped on purpose, a global pass repaints distant
    /// features. used by the erase tool's adjust walls mode.
    public void RebuildRoomBoundaryWalls(int x0, int y0, int x1, int y1)
    {
        var crossed = BuildCrossedEdges();
        const double e = 1e-3;
        for (int x = x0 - 1; x <= x1 + 1; x++)
            for (int y = y0 - 1; y <= y1 + 1; y++)
            {
                var c = new CellKey(x, y);
                foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                {
                    var key = EdgeKey.Normalize(c, side);
                    if (crossed.Contains(key)) continue;
                    // the edge midpoint pushed just inside this cell and just across
                    var (ix, iy, ox, oy) = side switch
                    {
                        Side.North => (x + 0.5, y + e, x + 0.5, y - e),
                        Side.South => (x + 0.5, y + 1 - e, x + 0.5, y + 1 + e),
                        Side.East => (x + 1 - e, y + 0.5, x + 1 + e, y + 0.5),
                        _ => (x + e, y + 0.5, x - e, y + 0.5),
                    };
                    if (FloorAt(ix, iy) && !FloorAt(ox, oy)) SetWall(key, true);
                }
            }
    }

    /// true when (px,py) is on floor: inside a room cell or inside the rings
    /// of any flood region.
    public bool FloorAt(double px, double py)
    {
        if (Cells.Contains(new CellKey((int)Math.Floor(px), (int)Math.Floor(py)))) return true;
        foreach (var fr in FloodRegions)
        {
            var (minX, minY, maxX, maxY) = fr.Bounds();
            if (px < minX || px > maxX || py < minY || py > maxY) continue;
            if (PointInRings(px, py, fr.Rings)) return true;
        }
        return false;
    }

    public Door? GetDoor(EdgeKey edge)
    {
        var key = EdgeKey.Normalize(edge.Cell, edge.Side);
        return Doors.TryGetValue(key, out var d) ? d : null;
    }

    /// Places a door on an edge. Only valid if the edge has a wall.
    public bool TryPlaceDoor(EdgeKey edge, out Door door)
    {
        var key = EdgeKey.Normalize(edge.Cell, edge.Side);
        if (!HasWall(key))
        {
            door = null!;
            return false;
        }
        door = Doors.TryGetValue(key, out var existing) ? existing : new Door();
        Doors[key] = door;
        return true;
    }

    /// Places a door, applying default type/state to a newly created door.
    public bool TryPlaceDoor(EdgeKey edge, string defaultType, string defaultState, out Door door)
    {
        var key = EdgeKey.Normalize(edge.Cell, edge.Side);
        if (!HasWall(key)) { door = null!; return false; }
        bool isNew = !Doors.ContainsKey(key);
        door = isNew ? new Door() : Doors[key];
        if (isNew)
        {
            door.Type = defaultType;
            if (Enum.TryParse<DoorState>(defaultState, ignoreCase: true, out var st))
                door.State = st;
        }
        Doors[key] = door;
        return true;
    }

    public void RemoveDoor(EdgeKey edge)
    {
        Doors.Remove(EdgeKey.Normalize(edge.Cell, edge.Side));
    }

    // where a door sits: an EdgeKey, a VertexDoorKey or an (AngledWall, index)
    // pair. undo swaps in new door objects, the spot finds the same door again
    public object? DoorSpotOf(IDoorConfig door)
    {
        foreach (var (k, d) in Doors) if (ReferenceEquals(d, door)) return k;
        foreach (var (k, d) in VertexDoors) if (ReferenceEquals(d, door)) return k;
        foreach (var (w, list) in AngledDoors)
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], door)) return (w, i);
        return null;
    }

    public IDoorConfig? DoorAtSpot(object? spot) => spot switch
    {
        EdgeKey e => Doors.GetValueOrDefault(e),
        VertexDoorKey v => VertexDoors.GetValueOrDefault(v),
        (AngledWall w, int i) => AngledDoors.TryGetValue(w, out var l) && i < l.Count ? l[i] : null,
        _ => null,
    };

    // removes just the door, its wall stays. returns false when the door is not on this map
    public bool RemoveDoorConfig(IDoorConfig door)
    {
        switch (DoorSpotOf(door))
        {
            case EdgeKey e: Doors.Remove(e); return true;
            case VertexDoorKey v: VertexDoors.Remove(v); return true;
            case (AngledWall w, int i):
                AngledDoors[w].RemoveAt(i);
                if (AngledDoors[w].Count == 0) AngledDoors.Remove(w);
                return true;
            default: return false;
        }
    }

    /// Adds a rectangular room: fills cells, sets walls on the outer boundary,
    /// and removes walls strictly inside the region. Drawing a room over adjacent
    /// rooms merges them (boolean union) - the shared interior walls disappear.
    /// When passageway is true, a boundary edge is left OPEN (no wall) if the cell
    /// across that edge is an existing room cell, so a room grown out of an existing
    /// room connects as a passageway rather than being sealed off.
    /// When strict is true, every outer boundary edge gets a wall regardless of
    /// existing cells, and interior walls are NOT removed - so a sub-room drawn
    /// inside an existing room gets its own visible walls.
    /// passageFrom is the cell the drag started in. when given, passageway only
    /// opens to the room around that cell, so a hallway grown out of one room
    /// stays walled off from other rooms it happens to touch. mergeNearby still
    /// opens to every adjacent room.
    public void AddRoom(int x0, int y0, int x1, int y1, bool passageway = false, bool strict = false, bool mergeNearby = false,
        CellKey? passageFrom = null)
    {
        // existing cells outside the new region, captured before we add new cells
        var existing = new HashSet<CellKey>();
        if ((passageway || mergeNearby) && !strict)
        {
            var reach = !mergeNearby && passageFrom is { } from ? RoomAround(from) : null;
            foreach (var c in Cells)
                if ((c.X < x0 || c.X > x1 || c.Y < y0 || c.Y > y1) && (reach is null || reach.Contains(c)))
                    existing.Add(c);
        }

        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                Cells.Add(new CellKey(x, y));

        bool Open(int cx, int cy, Side side)
        {
            if (strict) return false; // strict: every outer edge is walled
            if (!(passageway || mergeNearby)) return false;
            var (nx, ny) = side switch
            {
                Side.North => (cx, cy - 1),
                Side.South => (cx, cy + 1),
                Side.East => (cx + 1, cy),
                Side.West => (cx - 1, cy),
            };
            return existing.Contains(new CellKey(nx, ny));
        }

        // outer boundary walls
        for (int x = x0; x <= x1; x++)
        {
            if (Open(x, y0, Side.North)) SetWall(new EdgeKey(new CellKey(x, y0), Side.North), false);
            else SetWall(new EdgeKey(new CellKey(x, y0), Side.North), true);
            if (Open(x, y1, Side.South)) SetWall(new EdgeKey(new CellKey(x, y1), Side.South), false);
            else SetWall(new EdgeKey(new CellKey(x, y1), Side.South), true);
        }
        for (int y = y0; y <= y1; y++)
        {
            if (Open(x0, y, Side.West)) SetWall(new EdgeKey(new CellKey(x0, y), Side.West), false);
            else SetWall(new EdgeKey(new CellKey(x0, y), Side.West), true);
            if (Open(x1, y, Side.East)) SetWall(new EdgeKey(new CellKey(x1, y), Side.East), false);
            else SetWall(new EdgeKey(new CellKey(x1, y), Side.East), true);
        }
        // interior walls removed so adjacent rooms merge (skipped in strict mode)
        if (!strict)
        {
            for (int x = x0 + 1; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    SetWall(new EdgeKey(new CellKey(x - 1, y), Side.East), false);
            for (int y = y0 + 1; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    SetWall(new EdgeKey(new CellKey(x, y), Side.North), false);

            // angled walls that become interior to the merged region are removed
            // too, so a room grown over a diagonal merges cleanly. two scopes,
            // both required:
            //  1. the wall must pass through the new room rect, so a room drawn
            //     far away or only touching an endpoint never deletes it.
            //  2. the wall is interior when there is floor on both sides of the
            //     segment (sampled just off its midpoint). an outer diagonal has
            //     empty space on one side, so it is a boundary wall and stays.
            // doors on removed segments go with the wall (door implies wall).
            var removed = AngledWalls
                .Where(w => AngledWallIntersectsRect(w, x0, y0, x1, y1, true) && DiagonalInterior(w))
                .ToList();
            foreach (var w in removed) AngledDoors.Remove(w);
            AngledWalls.RemoveAll(w => AngledWallIntersectsRect(w, x0, y0, x1, y1, true) && DiagonalInterior(w));
        }
    }

    /// The two cells on either side of a diagonal segment: the midpoint pushed a
    /// tiny distance along the normal each way. A fixed 0.25 offset jumped past
    /// the neighbouring cell on shallow slopes, a tiny one lands in the cell the
    /// segment actually touches at any slope.
    public static (CellKey, CellKey) DiagonalSideCells(AngledWall w)
    {
        var (o1, o2) = SidePoints(w.Ax, w.Ay, w.Bx, w.By);
        return (new CellKey((int)Math.Floor(o1.x), (int)Math.Floor(o1.y)),
                new CellKey((int)Math.Floor(o2.x), (int)Math.Floor(o2.y)));
    }

    /// the segment midpoint pushed 1e-3 along the normal, one point each side
    private static ((double x, double y), (double x, double y)) SidePoints(int ax, int ay, int bx, int by)
    {
        double dx = bx - ax, dy = by - ay;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double mx = (ax + bx) / 2.0, my = (ay + by) / 2.0;
        double nx = -dy / len * 1e-3, ny = dx / len * 1e-3;
        return ((mx + nx, my + ny), (mx - nx, my - ny));
    }

    /// true when the diagonal segment is interior to the floor: there is floor
    /// on both sides of it, just off its midpoint. an outer diagonal has empty
    /// space on one side, so it is a boundary wall and must survive.
    public bool DiagonalInterior(AngledWall w)
    {
        if (w.Ax == w.Bx && w.Ay == w.By) return false;
        var (o1, o2) = SidePoints(w.Ax, w.Ay, w.Bx, w.By);
        return FloorAt(o1.x, o1.y) && FloorAt(o2.x, o2.y);
    }

    /// Splits the diagonal a-b at the end points of every collinear angled wall
    /// that lies on it, so each piece is either fully shared with a neighbour
    /// wall or not shared at all.
    private List<(int ax, int ay, int bx, int by)> DiagonalPieces(int ax, int ay, int bx, int by)
    {
        long dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        var stops = new SortedDictionary<long, (int x, int y)> { [0] = (ax, ay), [len2] = (bx, by) };
        foreach (var w in AngledWalls)
            foreach (var (px, py) in new[] { (w.Ax, w.Ay), (w.Bx, w.By) })
            {
                long rx = px - ax, ry = py - ay;
                if (rx * dy - ry * dx != 0) continue; // not on the line
                long s = rx * dx + ry * dy;
                if (s > 0 && s < len2) stops[s] = (px, py);
            }
        var pieces = new List<(int, int, int, int)>();
        (int x, int y)? prev = null;
        foreach (var p in stops.Values)
        {
            if (prev is { } q) pieces.Add((q.x, q.y, p.x, p.y));
            prev = p;
        }
        return pieces;
    }

    /// True when there is floor on the side of segment a-b facing away from
    /// the polygon `verts`: inside a flood region (another polygon's floor or
    /// a floor tool fill), or in a room cell whose center is on that side (a
    /// cell cut by the segment with its center on the polygon side belongs to
    /// the polygon).
    private bool FloorAcross(int ax, int ay, int bx, int by, List<(int x, int y)> verts)
    {
        var (o1, o2) = SidePoints(ax, ay, bx, by);
        bool in1 = PointInPolygon(o1.x, o1.y, verts), in2 = PointInPolygon(o2.x, o2.y, verts);
        if (in1 == in2) return false;
        var far = in1 ? o2 : o1;
        foreach (var fr in FloodRegions)
            if (PointInRings(far.x, far.y, fr.Rings)) return true;
        var cell = new CellKey((int)Math.Floor(far.x), (int)Math.Floor(far.y));
        if (!Cells.Contains(cell)) return false;
        double dx = bx - ax, dy = by - ay;
        double sideFar = dx * (far.y - ay) - dy * (far.x - ax);
        double sideCenter = dx * (cell.Y + 0.5 - ay) - dy * (cell.X + 0.5 - ax);
        return sideFar * sideCenter > 0;
    }

    /// even-odd point test against floor rings (outer ring plus holes)
    private static bool PointInRings(double px, double py, List<List<(double x, double y)>> rings)
    {
        bool inside = false;
        foreach (var ring in rings)
        {
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var (xi, yi) = ring[i];
                var (xj, yj) = ring[j];
                if (((yi > py) != (yj > py)) &&
                    (px < (xj - xi) * (py - yi) / (yj - yi) + xi))
                    inside = !inside;
            }
        }
        return inside;
    }

    /// Removes the span a-b from every angled wall collinear with it, in either
    /// direction. A wall only partly inside the span is cut back to the part
    /// outside it. Doors inside the span go with it, doors on a kept part move
    /// onto that part.
    public void RemoveAngledSpan(int ax, int ay, int bx, int by)
    {
        long dx = bx - ax, dy = by - ay;
        foreach (var w in AngledWalls.ToList())
        {
            long wx = w.Bx - w.Ax, wy = w.By - w.Ay, wl2 = wx * wx + wy * wy;
            if ((w.Ax - ax) * dy - (w.Ay - ay) * dx != 0) continue;
            if ((w.Bx - ax) * dy - (w.By - ay) * dx != 0) continue;
            // span ends along the wall, in units of the wall's squared length
            long sa = (ax - w.Ax) * wx + (ay - w.Ay) * wy;
            long sb = (bx - w.Ax) * wx + (by - w.Ay) * wy;
            var (lo, loPt) = sa < sb ? (sa, (ax, ay)) : (sb, (bx, by));
            var (hi, hiPt) = sa < sb ? (sb, (bx, by)) : (sa, (ax, ay));
            long u0 = Math.Max(0, lo), u1 = Math.Min(wl2, hi);
            if (u1 <= u0) continue; // no shared length
            double wlen = Math.Sqrt(wl2);
            AngledWalls.Remove(w);
            AngledDoors.Remove(w, out var doors);
            if (u0 > 0) // kept part from the wall's start to the span
            {
                var piece = new AngledWall(w.Ax, w.Ay, loPt.Item1, loPt.Item2);
                AngledWalls.Add(piece);
                double end = wlen * u0 / wl2;
                var keep = doors?.Where(d => d.T + 0.5 <= end + 1e-9).ToList();
                if (keep is { Count: > 0 }) AngledDoors[piece] = keep;
            }
            if (u1 < wl2) // kept part from the span to the wall's end
            {
                var piece = new AngledWall(hiPt.Item1, hiPt.Item2, w.Bx, w.By);
                AngledWalls.Add(piece);
                double start = wlen * u1 / wl2;
                var keep = doors?.Where(d => d.T - 0.5 >= start - 1e-9)
                    .Select(d => new AngledDoor { T = d.T - start, Type = d.Type, State = d.State, DmOnly = d.DmOnly }).ToList();
                if (keep is { Count: > 0 }) AngledDoors[piece] = keep;
            }
        }
    }

    /// Enumerates the cells whose INTERIOR a lattice segment passes through, via
    /// a DDA grid walk from (ax,ay) to (bx,by). Axis-aligned segments lie on grid
    /// lines (cell boundaries) and cross no cell interior, so they return empty
    /// (their walls are EdgeKey edges, handled separately). Diagonal segments
    /// return the sequence of cells the line cuts through.
    public static HashSet<CellKey> CellsCrossed(int ax, int ay, int bx, int by)
    {
        var cells = new HashSet<CellKey>();
        double dx = bx - ax, dy = by - ay;
        if (dx == 0 || dy == 0) return cells; // axis-aligned: on a grid line
        int sx = dx > 0 ? 1 : -1, sy = dy > 0 ? 1 : -1;
        int cx = ax + (sx > 0 ? 0 : -1), cy = ay + (sy > 0 ? 0 : -1);
        double tMaxX = sx > 0 ? 1.0 / dx : -1.0 / dx;
        double tMaxY = sy > 0 ? 1.0 / dy : -1.0 / dy;
        double tDeltaX = Math.Abs(1.0 / dx), tDeltaY = Math.Abs(1.0 / dy);
        double t = 0;
        while (t < 1.0)
        {
            cells.Add(new CellKey(cx, cy));
            if (tMaxX < tMaxY) { cx += sx; tMaxX += tDeltaX; t = tMaxX - tDeltaX; }
            else { cy += sy; tMaxY += tDeltaY; t = tMaxY - tDeltaY; }
        }
        return cells;
    }

    /// Adds a polygon room from a closed loop of LATTICE vertices (integer grid
    /// points = cell corners). Cells whose CENTER (x+0.5, y+0.5) is inside the
    /// polygon are filled (cell-center point-in-polygon, preserving the 5x5
    /// cell/formation model). Each edge is decomposed: axis-aligned edges become
    /// unit EdgeKey walls on the boundary; diagonal edges become AngledWall
    /// segments. Behaves like AddRoom: UNION by default (interior walls among the
    /// filled cells are removed so adjacent rooms merge), passageway opens a
    /// boundary edge when the cell across it is an existing room, strict keeps
    /// every outer wall and interior walls, and mergeNearby opens shared walls
    /// between the polygon and existing rooms. Default draws walls as-is.
    /// the polygon leaves no object behind: it becomes plain walls, room cells
    /// and a flood region floor. cells cut by its own diagonals stay out of
    /// Cells, the floor rings cover them, the same as the floor tool.
    public void AddPolygon(List<(int x, int y)> verts, bool passageway = false, bool strict = false, bool mergeNearby = false,
        CellKey? passageFrom = null)
    {
        if (verts.Count < 3) return;
        var fill = PolygonFillCells(verts);
        if (fill.Count == 0) return;

        // existing cells outside the fill, captured before we add new cells.
        // with passageFrom only the room around that cell counts, like AddRoom.
        var existing = new HashSet<CellKey>();
        if (passageway && !strict)
        {
            var reach = passageFrom is { } from ? RoomAround(from) : null;
            foreach (var c in Cells)
                if (!fill.Contains(c) && (reach is null || reach.Contains(c))) existing.Add(c);
        }

        var ownWalls = new List<AngledWall>();
        for (int i = 0; i < verts.Count; i++)
        {
            var (ax, ay) = verts[i];
            var (bx, by) = verts[(i + 1) % verts.Count];
            if (ax != bx && ay != by) ownWalls.Add(new AngledWall(ax, ay, bx, by));
        }
        var cutOwn = CutCellWalls(ownWalls);
        foreach (var c in fill)
            if (!cutOwn.ContainsKey(c)) Cells.Add(c);

        ApplyPolygonBoundary(verts, fill, existing, passageway, strict);

        // the floor: the polygon itself, as full fill cells plus each cell a
        // diagonal crosses clipped to the polygon. it is added after the
        // merge pass so FloorAcross never finds the polygon's own floor.
        var floor = new FloodRegion { Rings = BuildPolygonFloorRings(verts) };
        var opened = new List<(int ax, int ay, int bx, int by)>();

        // interior walls removed so adjacent rooms merge, ONLY when the merge
        // checkbox is on (skipped in strict mode). like AddRoom, only walls
        // with a fill cell on at least one side are opened: the polygon's
        // inside and its border with neighbouring room cells, so a polygon
        // grown out of a room opens the shared wall. walls between two cells
        // elsewhere on the map, and their doors, are left alone.
        if (mergeNearby && !strict)
        {
            var merged = new HashSet<CellKey>(fill);
            foreach (var c in Cells) merged.Add(c);
            foreach (var c in fill)
                foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                {
                    var (nx, ny) = side switch
                    {
                        Side.North => (c.X, c.Y - 1),
                        Side.South => (c.X, c.Y + 1),
                        Side.East => (c.X + 1, c.Y),
                        Side.West => (c.X - 1, c.Y),
                    };
                    if (merged.Contains(new CellKey(nx, ny)))
                        SetWall(new EdgeKey(c, side), false);
                }
            // polygon edges shared with room cells open the full face, not just
            // the rows that pass the center-point fill test. The merged-cell pass
            // above only opens walls between merged cells, so a sliver cell on the
            // polygon side (center outside, so not a fill cell) leaves its wall.
            // Walk each edge: the polygon interior is the side the fill cells sit
            // on; the room side is the opposite. Open the wall for every cell the
            // edge spans when the room-side cell is a room.
            for (int i = 0; i < verts.Count; i++)
            {
                var (ax, ay) = verts[i];
                var (bx, by) = verts[(i + 1) % verts.Count];
                if (ax == bx)
                {
                    int y0 = Math.Min(ay, by), y1 = Math.Max(ay, by);
                    bool interiorRight = fill.Any(c => c.X == ax);
                    for (int y = y0; y < y1; y++)
                    {
                        var roomCell = interiorRight ? new CellKey(ax - 1, y) : new CellKey(ax, y);
                        if (Cells.Contains(roomCell))
                            SetWall(new EdgeKey(roomCell, interiorRight ? Side.East : Side.West), false);
                    }
                }
                else if (ay == by)
                {
                    int x0 = Math.Min(ax, bx), x1 = Math.Max(ax, bx);
                    bool interiorDown = fill.Any(c => c.Y == ay);
                    for (int x = x0; x < x1; x++)
                    {
                        var roomCell = interiorDown ? new CellKey(x, ay - 1) : new CellKey(x, ay);
                        if (Cells.Contains(roomCell))
                            SetWall(new EdgeKey(roomCell, interiorDown ? Side.South : Side.North), false);
                    }
                }
                else
                {
                    // a diagonal edge opens where there is floor on the far side.
                    // it is split at the ends of any collinear wall on it first,
                    // so a neighbour sharing only part of the edge opens only
                    // that part. walls are matched in either direction.
                    foreach (var (px, py, qx, qy) in DiagonalPieces(ax, ay, bx, by))
                        if (FloorAcross(px, py, qx, qy, verts))
                        {
                            RemoveAngledSpan(px, py, qx, qy);
                            opened.Add((px, py, qx, qy));
                        }
                }
            }
        }
        FloodRegions.Add(floor);

        // a cell that only an opened diagonal cut has floor all over it now,
        // so it becomes a plain room cell again
        if (opened.Count > 0)
        {
            var cut = CutCellWalls(AngledWalls);
            foreach (var (ax, ay, bx, by) in opened)
                foreach (var c in CellsCrossed(ax, ay, bx, by))
                {
                    if (cut.ContainsKey(c)) continue;
                    double cx = c.X + 0.5, cy = c.Y + 0.5;
                    if (FloorAt(cx - 0.25, cy - 0.25) && FloorAt(cx + 0.25, cy - 0.25)
                        && FloorAt(cx - 0.25, cy + 0.25) && FloorAt(cx + 0.25, cy + 0.25))
                        Cells.Add(c);
                }
        }
    }

    /// v3 files stored polygons as vertex loops. their walls and cells are
    /// already loaded, so this adds the floor and drops the fill cells the
    /// polygon's own diagonals cut, the same shape AddPolygon leaves now.
    public void AddLegacyPolygon(List<(int x, int y)> verts)
    {
        if (verts.Count < 3) return;
        var fill = PolygonFillCells(verts);
        if (fill.Count == 0) return;
        var own = new List<AngledWall>();
        for (int i = 0; i < verts.Count; i++)
        {
            var (ax, ay) = verts[i];
            var (bx, by) = verts[(i + 1) % verts.Count];
            if (ax == bx || ay == by) continue;
            foreach (var w in AngledWalls)
                if ((w.Ax == ax && w.Ay == ay && w.Bx == bx && w.By == by) ||
                    (w.Ax == bx && w.Ay == by && w.Bx == ax && w.By == ay))
                { own.Add(w); break; }
        }
        foreach (var c in CutCellWalls(own).Keys) Cells.Remove(c);
        FloodRegions.Add(new FloodRegion { Rings = BuildPolygonFloorRings(verts) });
    }

    /// The cells whose CENTER (x+0.5, y+0.5) lies inside the closed polygon.
    /// Shared by AddPolygon and the Polygon tool's live fill preview.
    /// The scan is clamped to the optional window (cx0..cx1, cy0..cy1): the
    /// render pass passes the visible viewport so a huge polygon enclosing the
    /// view costs O(window area), not O(polygon area). AddPolygon/preview pass
    /// the defaults (int.MinValue/MaxValue) and get the full fill.
    public HashSet<CellKey> PolygonFillCells(List<(int x, int y)> verts,
        int cx0 = int.MinValue, int cx1 = int.MaxValue,
        int cy0 = int.MinValue, int cy1 = int.MaxValue)
    {
        var cells = new HashSet<CellKey>();
        if (verts.Count < 3) return cells;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var (vx, vy) in verts)
        {
            if (vx < minX) minX = vx; if (vx > maxX) maxX = vx;
            if (vy < minY) minY = vy; if (vy > maxY) maxY = vy;
        }
        if (minX < cx0) minX = cx0; if (maxX > cx1) maxX = cx1;
        if (minY < cy0) minY = cy0; if (maxY > cy1) maxY = cy1;
        if (minX >= maxX || minY >= maxY) return cells;
        for (int x = minX; x < maxX; x++)
            for (int y = minY; y < maxY; y++)
                if (PointInPolygon(x + 0.5, y + 0.5, verts))
                    cells.Add(new CellKey(x, y));
        return cells;
    }

    /// Decomposes a polygon loop's edges through AddLine (the Wall tool's
    /// engine): axis-aligned unit segments get walls, diagonal edges become one
    /// deduped AngledWall. The passageway/strict checks stay here: an axis edge
    /// facing an existing room is skipped when passageway is on. Shared by
    /// AddPolygon.
    private void ApplyPolygonBoundary(List<(int x, int y)> verts, HashSet<CellKey> fill,
        HashSet<CellKey> existing, bool passageway, bool strict)
    {
        for (int i = 0; i < verts.Count; i++)
        {
            var (ax, ay) = verts[i];
            var (bx, by) = verts[(i + 1) % verts.Count];
            if (ax == bx) // vertical edge at x=ax
            {
                int y0 = Math.Min(ay, by), y1 = Math.Max(ay, by);
                for (int y = y0; y < y1; y++)
                {
                    var left = new CellKey(ax - 1, y);
                    var right = new CellKey(ax, y);
                    bool leftIn = fill.Contains(left), rightIn = fill.Contains(right);
                    if (leftIn && rightIn) continue; // true interior edge, no boundary wall
                    var exterior = leftIn ? right : left; // side not in fill (or left when both out)
                    if (!strict && passageway && existing.Contains(exterior)) continue; // open passage
                    AddLine(ax, y, ax, y + 1); // East wall of the left cell
                }
            }
            else if (ay == by) // horizontal edge at y=ay
            {
                int x0 = Math.Min(ax, bx), x1 = Math.Max(ax, bx);
                for (int x = x0; x < x1; x++)
                {
                    var top = new CellKey(x, ay - 1);
                    var bottom = new CellKey(x, ay);
                    bool topIn = fill.Contains(top), bottomIn = fill.Contains(bottom);
                    if (topIn && bottomIn) continue; // true interior edge
                    var exterior = topIn ? bottom : top;
                    if (!strict && passageway && existing.Contains(exterior)) continue;
                    AddLine(x, ay, x + 1, ay); // North wall of the bottom cell
                }
            }
            else
            {
                AddLine(ax, ay, bx, by); // one deduped AngledWall
            }
        }
    }

    /// True when the angled wall segment touches ANY PART of the rectangle
    /// [x0,y0]-[x1,y1] (world bounds x in [x0, x1+1], y in [y0, y1+1]).
    /// Liang-Barsky segment-rectangle clipping: the segment intersects the rect
    /// when the clipped parameter range is non-empty. Used by erase, capture,
    /// clear, move, copy - matches the user's expectation that a selection grabs
    /// any edge that overlaps it, not only edges fully inside.
    public static bool AngledWallIntersectsRect(AngledWall w, int x0, int y0, int x1, int y1, bool throughInterior = false)
    {
        double rx0 = x0, ry0 = y0, rx1 = x1 + 1, ry1 = y1 + 1;
        double dx = w.Bx - w.Ax, dy = w.By - w.Ay;
        double t0 = 0, t1 = 1;
        double[] p = { -dx, dx, -dy, dy };
        double[] q = { w.Ax - rx0, rx1 - w.Ax, w.Ay - ry0, ry1 - w.Ay };
        for (int i = 0; i < 4; i++)
        {
            if (p[i] == 0) { if (q[i] < 0) return false; }
            else
            {
                double t = q[i] / p[i];
                if (p[i] < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
                else { if (t < t0) return false; if (t < t1) t1 = t; }
            }
        }
        // throughInterior: a diagonal that only meets the rect at a corner or
        // an endpoint on its edge does not count
        return throughInterior ? t1 - t0 > 1e-9 : t0 <= t1;
    }

    /// Ray-casting point-in-polygon. True when (px,py) is inside the closed
    /// polygon `verts`.
    private static bool PointInPolygon(double px, double py, List<(int x, int y)> verts)
    {
        bool inside = false;
        int n = verts.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var (xi, yi) = verts[i];
            var (xj, yj) = verts[j];
            if (((yi > py) != (yj > py)) &&
                (px < (xj - xi) * (py - yi) / (yj - yi) + xi))
                inside = !inside;
        }
        return inside;
    }

    /// Adds a rectangular floor region: fills cells, removes walls strictly inside
    /// the region (and any doors on them), and removes stairs/labels in the cells.
    /// Unlike AddRoom it draws NO walls, and it leaves the boundary walls of the
    /// region untouched - only interior elements are cleared, leaving plain flooring.
    public void AddFloor(int x0, int y0, int x1, int y1)
    {
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                Cells.Add(new CellKey(x, y));
        // interior walls removed (doors on them auto-removed by SetWall)
        for (int x = x0 + 1; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                SetWall(new EdgeKey(new CellKey(x - 1, y), Side.East), false);
        for (int y = y0 + 1; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                SetWall(new EdgeKey(new CellKey(x, y), Side.North), false);
        // stairs, labels, and pawns inside the region are cleared
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                Stairs.Remove(new CellKey(x, y));
                Pawns.Remove(new CellKey(x, y));
            }
        Labels.RemoveAll(l => l.Cell.X >= x0 && l.Cell.X <= x1 && l.Cell.Y >= y0 && l.Cell.Y <= y1);
    }

    /// Adds a straight wall line between two LATTICE vertices (integer grid
    /// points = cell corners), using the same edge decomposition as a polygon
    /// edge. Axis-aligned segments become the series of unit EdgeKey walls
    /// along the span (horizontal at y=ay: North edges of cells (x, ay) for
    /// x in [min..max-1]; vertical at x=ax: East edges of cells (ax-1, y) for
    /// y in [min..max-1]). Any other lattice segment becomes ONE AngledWall
    /// from start to end, so a 5x3 slope is a single angled wall, matching the
    /// polygon tool. Additive: walls that already exist are left untouched
    /// (doors on them preserved), a diagonal that already exists in either
    /// direction is not duplicated, and a zero-length line does nothing.
    public void AddLine(int ax, int ay, int bx, int by)
    {
        if (ax == bx && ay == by) return;
        if (ay == by) // horizontal segment along the grid line y = ay
        {
            int x0 = Math.Min(ax, bx), x1 = Math.Max(ax, bx);
            for (int x = x0; x < x1; x++)
                SetWall(new EdgeKey(new CellKey(x, ay), Side.North), true);
        }
        else if (ax == bx) // vertical segment along the grid line x = ax
        {
            int y0 = Math.Min(ay, by), y1 = Math.Max(ay, by);
            for (int y = y0; y < y1; y++)
                SetWall(new EdgeKey(new CellKey(ax - 1, y), Side.East), true);
        }
        else
        {
            var w = new AngledWall(ax, ay, bx, by);
            var rev = new AngledWall(bx, by, ax, ay);
            foreach (var e in AngledWalls)
                if (e == w || e == rev) return; // already present, no duplicate
            AngledWalls.Add(w);
        }
    }

    /// Wall-aware flood fill: the set of cells reachable from START through
    /// open cell edges, the same 4-adjacency BFS as ConnectedStairs, but the
    /// walk is the fill itself. A move is blocked by: a unit wall on the
    /// shared edge (a door implies a wall, so doors block), an angled wall
    /// crossing the shared edge, an angled wall cutting through the neighbour
    /// cell's interior (a diagonal boundary owns that cell, the fill stops
    /// before it), and a stair in the neighbour cell (fill stays on this
    /// level). Returns null when the region exceeds MAX_CELLS - the clicked
    /// region is not enclosed (open field, gap in the walls), so the caller
    /// fills nothing.
    /// The room cells reachable from start without crossing a wall: the room
    /// the cell belongs to. empty when start is not a room cell.
    public HashSet<CellKey> RoomAround(CellKey start)
    {
        var room = new HashSet<CellKey>();
        if (!Cells.Contains(start)) return room;
        var queue = new Queue<CellKey>();
        room.Add(start);
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var (dx, dy, side) in new[] { (1, 0, Side.East), (-1, 0, Side.West), (0, 1, Side.South), (0, -1, Side.North) })
            {
                var n = new CellKey(c.X + dx, c.Y + dy);
                if (room.Contains(n) || !Cells.Contains(n)) continue;
                if (HasWall(new EdgeKey(c, side))) continue;
                room.Add(n);
                queue.Enqueue(n);
            }
        }
        return room;
    }

    public HashSet<CellKey>? FloodFillRegion(CellKey start, int maxCells = 10000)
    {
        var cut = BuildCutCells();
        var crossed = BuildCrossedEdges();
        var region = new HashSet<CellKey>();
        if (Stairs.ContainsKey(start) || cut.Contains(start)) return region;
        var seen = new HashSet<CellKey> { start };
        var queue = new Queue<CellKey>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            region.Add(c);
            if (region.Count > maxCells) return null;
            foreach (var (dx, dy, side) in new[] { (1, 0, Side.East), (-1, 0, Side.West), (0, 1, Side.South), (0, -1, Side.North) })
            {
                var n = new CellKey(c.X + dx, c.Y + dy);
                if (seen.Contains(n)) continue;
                var edge = EdgeKey.Normalize(c, side);
                if (HasWall(edge) || crossed.Contains(edge)) continue;
                if (cut.Contains(n) || Stairs.ContainsKey(n)) continue;
                seen.Add(n);
                queue.Enqueue(n);
            }
        }
        return region;
    }

    /// Adds floor cells ONLY - walls, doors, stairs, labels untouched. Floors
    /// are independent of the lines that bound them, so the flood fill never
    /// clears anything. (The manual rectangle AddFloor does clear interiors.)
    public void AddFloorCells(HashSet<CellKey> cells)
    {
        foreach (var c in cells) Cells.Add(c);
    }

    public void AddFloodRegion(FloodRegion fr) => FloodRegions.Add(fr);

    /// Builds the exact floor geometry of a flood region: the union of every
    /// region cell's full square and every cut cell's region-side part (the
    /// triangle a diagonal leaves behind), clipped by each wall's half-plane.
    /// The union's boundary is walked into closed rings: ring 0 outer, rings
    /// 1+ holes (interior wall islands). Same non-full-cell logic as the
    /// polygon tool's floor.
    public List<List<(double x, double y)>> BuildFloodRings(HashSet<CellKey> region, List<AngledWall>? ownWalls = null)
    {
        var polys = new List<List<(double, double)>>();
        // ownWalls restricts the cut-wall set to the polygon's own diagonals:
        // an unrelated diagonal adjacent to the fill can never pull its
        // triangle into this floor. The flood tool path passes null and gets
        // every wall, exactly as before.
        var cutWalls = ownWalls is null ? CutCellWalls(AngledWalls) : CutCellWalls(ownWalls);
        foreach (var c in region)
        {
            var poly = new List<(double, double)> { (c.X, c.Y), (c.X + 1, c.Y), (c.X + 1, c.Y + 1), (c.X, c.Y + 1) };
            // a polygon fill cell can be cut by its own diagonal (center inside,
            // the edge passes through the square): clip the square to the center
            // side so the ring never extends past the polygon boundary. The flood
            // BFS never puts a cut cell in the region, so this is polygon-only.
            if (cutWalls.TryGetValue(c, out var cw))
                foreach (var w in cw)
                    poly = ClipHalfPlane(poly, w.Ax, w.Ay, w.Bx, w.By, c.X + 0.5, c.Y + 0.5);
            if (poly.Count >= 3) polys.Add(poly);
        }
        // bounding walls: the angled walls cutting the cut cells the region BFS
        // stopped at (cut cells adjacent to a region cell). A cut cell in a
        // disconnected feature is not adjacent to this region, so its wall is
        // not a bounding wall and its triangle must not join this ring.
        var boundWalls = new HashSet<AngledWall>();
        foreach (var c in region)
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                if (cutWalls.TryGetValue(new CellKey(c.X + dx, c.Y + dy), out var ws))
                    foreach (var w in ws) boundWalls.Add(w);
        foreach (var (cell, walls) in cutWalls)
        {
            if (region.Contains(cell)) continue; // cut cells are never region cells
            if (region.Count == 0) continue;
            // include a cut cell only when one of its cutting walls bounds this
            // region; the region lies entirely on one side of every bounding
            // wall, so any region cell defines the region side
            if (!walls.Any(w => boundWalls.Contains(w))) continue;
            var sample = region.First();
            var poly = new List<(double, double)> { (cell.X, cell.Y), (cell.X + 1, cell.Y), (cell.X + 1, cell.Y + 1), (cell.X, cell.Y + 1) };
            foreach (var w in walls)
                poly = ClipHalfPlane(poly, w.Ax, w.Ay, w.Bx, w.By, sample.X + 0.5, sample.Y + 0.5);
            if (poly.Count >= 3) polys.Add(poly);
        }

        return UnionRings(polys);
    }

    /// the exact floor of a polygon: full squares for the fill cells no
    /// diagonal crosses, and for every cell a diagonal edge crosses, the part
    /// of that square inside the polygon. axis edges run along grid lines, so
    /// any cell the polygon only partly covers is crossed by a diagonal.
    /// clipping the polygon to the square handles every case the per-wall
    /// half-plane clip got wrong: a center sitting exactly on a diagonal, a
    /// concave corner, and a cell crossed by two edges.
    public List<List<(double x, double y)>> BuildPolygonFloorRings(List<(int x, int y)> verts)
    {
        var polys = new List<List<(double, double)>>();
        if (verts.Count < 3) return polys;
        // the pieces have to wind the same way as the full squares or their
        // shared edges would not cancel in the union
        var shape = verts.Select(v => ((double)v.x, (double)v.y)).ToList();
        if (SignedArea(shape) < 0) shape.Reverse();

        var diag = new List<AngledWall>();
        for (int i = 0; i < verts.Count; i++)
        {
            var (ax, ay) = verts[i];
            var (bx, by) = verts[(i + 1) % verts.Count];
            if (ax != bx && ay != by) diag.Add(new AngledWall(ax, ay, bx, by));
        }
        var cut = CutCellWalls(diag);
        foreach (var c in PolygonFillCells(verts))
            if (!cut.ContainsKey(c))
                polys.Add(new List<(double, double)> { (c.X, c.Y), (c.X + 1, c.Y), (c.X + 1, c.Y + 1), (c.X, c.Y + 1) });
        foreach (var c in cut.Keys)
        {
            double cx = c.X + 0.5, cy = c.Y + 0.5;
            var piece = ClipHalfPlane(shape, c.X, c.Y, c.X, c.Y + 1, cx, cy);
            piece = ClipHalfPlane(piece, c.X + 1, c.Y, c.X + 1, c.Y + 1, cx, cy);
            piece = ClipHalfPlane(piece, c.X, c.Y, c.X + 1, c.Y, cx, cy);
            piece = ClipHalfPlane(piece, c.X, c.Y + 1, c.X + 1, c.Y + 1, cx, cy);
            if (piece.Count >= 3 && Math.Abs(SignedArea(piece)) > 1e-9) polys.Add(piece);
        }
        return UnionRings(polys);
    }

    private static double SignedArea(List<(double, double)> poly)
    {
        double a = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var (ax, ay) = poly[i];
            var (bx, by) = poly[(i + 1) % poly.Count];
            a += ax * by - bx * ay;
        }
        return a / 2;
    }

    // outline of the union of non-overlapping polygons that share edges: the
    // shared edges cancel and the rest is walked into closed rings. holes come
    // out with the opposite winding, so a hole ring passed back in still
    // subtracts.
    private static List<List<(double x, double y)>> UnionRings(List<List<(double, double)>> polys)
    {
        // union boundary: collect all vertices, split every edge at vertices
        // lying on it so shared edges become ATOMIC segments that cancel
        // exactly. Diagonal cuts share PARTIAL edges with region cells (a cut
        // cell's edge extends past the region cell's corner), so exact
        // opposite-direction matching alone leaves the shared sub-segments in
        // the boundary and fragments the ring walk - the cut triangles then
        // render as EvenOdd holes and the slivers never fill.
        polys = polys.Where(p => p.Count >= 3).ToList();
        var verts = new HashSet<(double, double)>();
        foreach (var poly in polys) foreach (var v in poly) verts.Add(v);

        // net count per directed edge: an edge added twice (strips or rings
        // that overlap along a line) has to cancel twice, a plain set would
        // drop the second copy and leave a vertex with no way out
        var edges = new Dictionary<(double, double, double, double), int>();
        void AddEdge(double ax, double ay, double bx, double by)
        {
            if (ax == bx && ay == by) return;
            if (edges.TryGetValue((bx, by, ax, ay), out int back))
            {
                if (back == 1) edges.Remove((bx, by, ax, ay));
                else edges[(bx, by, ax, ay)] = back - 1;
            }
            else edges[(ax, ay, bx, by)] = edges.GetValueOrDefault((ax, ay, bx, by)) + 1;
        }
        foreach (var poly in polys)
        {
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                var (ax, ay) = poly[i];
                var (bx, by) = poly[(i + 1) % n];
                if (ax == bx && ay == by) continue;
                double dx = bx - ax, dy = by - ay;
                double len2 = dx * dx + dy * dy;
                var pts = new List<(double, double)> { (ax, ay) };
                foreach (var v in verts)
                {
                    double vx = v.Item1, vy = v.Item2;
                    if ((vx == ax && vy == ay) || (vx == bx && vy == by)) continue;
                    if (Math.Abs(dx * (vy - ay) - dy * (vx - ax)) > 1e-9) continue;
                    double t = (vx - ax) * dx + (vy - ay) * dy;
                    if (t > 1e-9 && t < len2 - 1e-9) pts.Add(v);
                }
                pts.Add((bx, by));
                pts.Sort((p, q) => (((p.Item1 - ax) * dx + (p.Item2 - ay) * dy))
                    .CompareTo(((q.Item1 - ax) * dx + (q.Item2 - ay) * dy)));
                for (int j = 0; j + 1 < pts.Count; j++)
                    AddEdge(pts[j].Item1, pts[j].Item2, pts[j + 1].Item1, pts[j + 1].Item2);
            }
        }

        // walk rings: every boundary vertex has as many out-edges as in-edges.
        // each walk pops the out-edges it uses.
        var outMap = new Dictionary<(double, double), List<(double, double)>>();
        foreach (var ((ax, ay, bx, by), count) in edges)
        {
            if (!outMap.TryGetValue((ax, ay), out var l)) outMap[(ax, ay)] = l = new List<(double, double)>();
            for (int k = 0; k < count; k++) l.Add((bx, by));
        }
        var rings = new List<List<(double, double)>>();
        foreach (var startKey in outMap.Keys.ToList())
        {
            while (outMap[startKey].Count > 0)
            {
                var start = startKey;
                var ring = new List<(double, double)> { start };
                var cur = outMap[start][^1];
                outMap[start].RemoveAt(outMap[start].Count - 1);
                while (cur != start)
                {
                    ring.Add(cur);
                    // a vertex with no way out only happens on bad input, the
                    // open piece is dropped instead of throwing
                    if (!outMap.TryGetValue(cur, out var outs) || outs.Count == 0) { ring.Clear(); break; }
                    var nxt = outs[^1];
                    outs.RemoveAt(outs.Count - 1);
                    cur = nxt;
                }
                // a ring that folds back on itself merges down to nothing
                if (ring.Count >= 3) ring = MergeCollinear(ring);
                if (ring.Count >= 3) rings.Add(ring);
            }
        }
        return rings;
    }

    // drops vertices where the two adjacent edges are parallel: the ring walk
    // emits unit edges, so a straight boundary arrives as collinear points that
    // merge to one corner per turn
    private static List<(double, double)> MergeCollinear(List<(double, double)> ring)
    {
        if (ring.Count < 3) return ring;
        var res = new List<(double, double)>();
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[(i - 1 + ring.Count) % ring.Count];
            var b = ring[i];
            var c = ring[(i + 1) % ring.Count];
            double d1x = b.Item1 - a.Item1, d1y = b.Item2 - a.Item2;
            double d2x = c.Item1 - b.Item1, d2y = c.Item2 - b.Item2;
            if (Math.Abs(d1x * d2y - d1y * d2x) > 1e-9) res.Add(b);
        }
        return res;
    }

    // Sutherland-Hodgman clip of a polygon by the half-plane of line A-B
    // that contains the sample point S.
    private static List<(double, double)> ClipHalfPlane(List<(double, double)> poly, double ax, double ay, double bx, double by, double sx, double sy)
    {
        double Cross(double px, double py) => (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        double sSide = Cross(sx, sy);
        var result = new List<(double, double)>();
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var (cx, cy) = poly[i];
            var (dx, dy) = poly[(i + 1) % n];
            double c1 = Cross(cx, cy), c2 = Cross(dx, dy);
            bool in1 = c1 * sSide >= 0, in2 = c2 * sSide >= 0;
            if (in1) result.Add((cx, cy));
            if (in1 != in2)
            {
                double t = c1 / (c1 - c2);
                result.Add((Math.Round(cx + t * (dx - cx), 10), Math.Round(cy + t * (dy - cy), 10)));
            }
        }
        return result;
    }

    // cell -> the angled walls passing through its interior
    private static Dictionary<CellKey, List<AngledWall>> CutCellWalls(List<AngledWall> walls)
    {
        var map = new Dictionary<CellKey, List<AngledWall>>();
        foreach (var w in walls)
        {
            int dx = w.Bx - w.Ax, dy = w.By - w.Ay;
            if (dx == 0 || dy == 0) continue;
            int x0 = Math.Min(w.Ax, w.Bx), x1 = Math.Max(w.Ax, w.Bx);
            int y0 = Math.Min(w.Ay, w.By), y1 = Math.Max(w.Ay, w.By);
            for (int k = x0; k < x1; k++)
                for (int l = y0; l < y1; l++)
                {
                    double t0 = (k - w.Ax) / (double)dx, t1 = (k + 1 - w.Ax) / (double)dx;
                    double yA = w.Ay + dy * t0, yB = w.Ay + dy * t1;
                    double lo = Math.Min(yA, yB), hi = Math.Max(yA, yB);
                    if (hi > l && lo < l + 1)
                    {
                        var cell = new CellKey(k, l);
                        if (!map.TryGetValue(cell, out var list)) map[cell] = list = new List<AngledWall>();
                        list.Add(w);
                    }
                }
        }
        return map;
    }

    public static bool FloodRegionTouchesRect(FloodRegion fr, int x0, int y0, int x1, int y1)
    {
        var (minX, minY, maxX, maxY) = fr.Bounds();
        return maxX >= x0 && minX <= x1 + 1 && maxY >= y0 && minY <= y1 + 1;
    }

    // keeps the part of a ring on one side of an axis line. vertical keeps
    // x <= at (or x >= at when !below), horizontal does the same for y.
    private static List<(double, double)> ClipAxis(List<(double, double)> ring, bool vertical, double at, bool below)
    {
        double s = below ? at - 1 : at + 1;
        return vertical
            ? ClipHalfPlane(ring, at, 0, at, 1, s, 0)
            : ClipHalfPlane(ring, 0, at, 1, at, 0, s);
    }

    /// the part of a flood region inside the cell rect [x0,y0]-[x1,y1], world
    /// bounds x in [x0, x1+1], y in [y0, y1+1]. empty rings when nothing is left.
    public static FloodRegion ClipFloodRegionToRect(FloodRegion fr, int x0, int y0, int x1, int y1)
    {
        var polys = new List<List<(double, double)>>();
        foreach (var ring in fr.Rings)
        {
            var r = ClipAxis(ring, true, x0, false);
            r = ClipAxis(r, true, x1 + 1, true);
            r = ClipAxis(r, false, y0, false);
            r = ClipAxis(r, false, y1 + 1, true);
            polys.Add(r);
        }
        return new FloodRegion { Rings = UnionRings(polys) };
    }

    /// cuts the cell rect [x0,y0]-[x1,y1] out of every flood region floor.
    /// each ring is split into the strips around the rect (left, right, and
    /// above and below between them) and the strips are joined back up.
    /// regions with nothing left are removed.
    public void SubtractFloorRect(int x0, int y0, int x1, int y1)
    {
        for (int i = FloodRegions.Count - 1; i >= 0; i--)
        {
            var fr = FloodRegions[i];
            if (!FloodRegionTouchesRect(fr, x0, y0, x1, y1)) continue;
            var polys = new List<List<(double, double)>>();
            foreach (var ring in fr.Rings)
            {
                polys.Add(ClipAxis(ring, true, x0, true));
                polys.Add(ClipAxis(ring, true, x1 + 1, false));
                var mid = ClipAxis(ClipAxis(ring, true, x0, false), true, x1 + 1, true);
                polys.Add(ClipAxis(mid, false, y0, true));
                polys.Add(ClipAxis(mid, false, y1 + 1, false));
            }
            var rings = UnionRings(polys);
            if (rings.Count == 0) FloodRegions.RemoveAt(i);
            else FloodRegions[i] = new FloodRegion { Rings = rings };
        }
    }

    /// Cells whose INTERIOR an angled wall cuts through. A lattice-to-lattice
    /// diagonal passes through the cells it crosses (a 45 degree wall from
    /// (2,2) to (3,3) is the diagonal of cells (2,2) and (3,3)); those cells
    /// belong to the boundary, so the flood fill stops before them.
    private HashSet<CellKey> BuildCutCells()
    {
        var cut = new HashSet<CellKey>();
        foreach (var w in AngledWalls)
        {
            int x0 = Math.Min(w.Ax, w.Bx), x1 = Math.Max(w.Ax, w.Bx);
            int y0 = Math.Min(w.Ay, w.By), y1 = Math.Max(w.Ay, w.By);
            for (int cx = x0; cx < x1; cx++)
                for (int cy = y0; cy < y1; cy++)
                    if (SegmentCutsCell(w.Ax, w.Ay, w.Bx, w.By, cx, cy))
                        cut.Add(new CellKey(cx, cy));
        }
        return cut;
    }

    /// Unit edges an angled wall crosses through their interior. Touching a
    /// lattice corner is not a crossing: a diagonal that only touches the
    /// shared edge at its endpoint leaves the 4-adjacency move open.
    private HashSet<EdgeKey> BuildCrossedEdges()
    {
        var crossed = new HashSet<EdgeKey>();
        foreach (var w in AngledWalls)
        {
            int x0 = Math.Min(w.Ax, w.Bx), x1 = Math.Max(w.Ax, w.Bx);
            int y0 = Math.Min(w.Ay, w.By), y1 = Math.Max(w.Ay, w.By);
            for (int x = x0; x <= x1; x++)
                for (int cy = y0; cy < y1; cy++)
                    if (SegmentsCross(w.Ax, w.Ay, w.Bx, w.By, x, cy, x, cy + 1))
                        crossed.Add(new EdgeKey(new CellKey(x - 1, cy), Side.East));
            for (int y = y0; y <= y1; y++)
                for (int cx = x0; cx < x1; cx++)
                    if (SegmentsCross(w.Ax, w.Ay, w.Bx, w.By, cx, y, cx + 1, y))
                        crossed.Add(new EdgeKey(new CellKey(cx, y), Side.North));
        }
        return crossed;
    }

    static double Orient(double ax, double ay, double bx, double by, double px, double py)
        => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

    /// Proper crossing of two OPEN segments: strict straddle on both lines,
    /// so endpoint touches and collinear overlaps do not count.
    static bool SegmentsCross(double ax, double ay, double bx, double by, double ex, double ey, double fx, double fy)
    {
        double o1 = Orient(ex, ey, fx, fy, ax, ay);
        double o2 = Orient(ex, ey, fx, fy, bx, by);
        double o3 = Orient(ax, ay, bx, by, ex, ey);
        double o4 = Orient(ax, ay, bx, by, fx, fy);
        return ((o1 > 0 && o2 < 0) || (o1 < 0 && o2 > 0)) && ((o3 > 0 && o4 < 0) || (o3 < 0 && o4 > 0));
    }

    /// Slab clipping: the open segment passes through the OPEN square
    /// (cx,cy)-(cx+1,cy+1) when the clipped t-interval has positive length.
    static bool SegmentCutsCell(double ax, double ay, double bx, double by, int cx, int cy)
    {
        double dx = bx - ax, dy = by - ay;
        double t0 = 0, t1 = 1;
        if (Math.Abs(dx) < 1e-9) { if (ax <= cx || ax >= cx + 1) return false; }
        else { double ta = (cx - ax) / dx, tb = (cx + 1 - ax) / dx; t0 = Math.Max(t0, Math.Min(ta, tb)); t1 = Math.Min(t1, Math.Max(ta, tb)); }
        if (Math.Abs(dy) < 1e-9) { if (ay <= cy || ay >= cy + 1) return false; }
        else { double ta = (cy - ay) / dy, tb = (cy + 1 - ay) / dy; t0 = Math.Max(t0, Math.Min(ta, tb)); t1 = Math.Min(t1, Math.Max(ta, tb)); }
        return t0 < t1;
    }

    /// All stairs connected to the given cell by 4-directional adjacency
    /// (up/down/left/right), including the start cell. Used so configuring one
    /// stair updates every stair in the same contiguous group. A wall on the
    /// shared edge blocks propagation: a stair row split by a wall is two
    /// separate groups. Doors imply walls, so a door on the edge blocks too.
    /// Removes the stair at start and every stair connected to it (same rules
    /// as ConnectedStairs). Returns how many were removed.
    public int RemoveConnectedStairs(CellKey start)
    {
        var group = ConnectedStairs(start).ToHashSet(ReferenceEqualityComparer.Instance);
        var cells = Stairs.Where(kv => group.Contains(kv.Value)).Select(kv => kv.Key).ToList();
        foreach (var c in cells) Stairs.Remove(c);
        return cells.Count;
    }

    public List<Stair> ConnectedStairs(CellKey start)
    {
        var result = new List<Stair>();
        if (!Stairs.ContainsKey(start)) return result;
        var seen = new HashSet<CellKey> { start };
        var queue = new Queue<CellKey>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            result.Add(Stairs[c]);
            foreach (var (dx, dy, side) in new[] { (1, 0, Side.East), (-1, 0, Side.West), (0, 1, Side.South), (0, -1, Side.North) })
            {
                var n = new CellKey(c.X + dx, c.Y + dy);
                if (!seen.Contains(n) && Stairs.ContainsKey(n) && !HasWall(EdgeKey.Normalize(c, side)))
                {
                    seen.Add(n);
                    queue.Enqueue(n);
                }
            }
        }
        return result;
    }

    /// Snapshot of a rectangular region, stored relative to its top-left origin
    /// so it can be pasted at any target cell.
    public sealed class RegionSnapshot
    {
        public int OriginX, OriginY;
        public HashSet<CellKey> Cells = new();
        public Dictionary<EdgeKey, bool> Walls = new();
        public Dictionary<EdgeKey, Door> Doors = new();
        public Dictionary<CellKey, Stair> Stairs = new();
        public List<MapLabel> Labels = new();
        public List<Pawn> Pawns = new();
        public List<AngledWall> AngledWalls = new();
        public Dictionary<AngledWall, List<AngledDoor>> AngledDoors = new();
        public List<FloodRegion> FloodRegions = new();
        public Dictionary<VertexDoorKey, VertexDoor> VertexDoors = new();
        public List<MapObject> Objects = new();
        public List<MapImage> Images = new();
        public bool Empty => Cells.Count == 0 && Walls.Count == 0 && Doors.Count == 0
                              && Stairs.Count == 0 && Labels.Count == 0 && Pawns.Count == 0 && Objects.Count == 0 && Images.Count == 0
                              && AngledWalls.Count == 0 && AngledDoors.Count == 0
                              && FloodRegions.Count == 0 && VertexDoors.Count == 0;
    }

    /// Captures all content within the rectangle [x0,y0]-[x1,y1] into a snapshot.
    /// Walls/doors are included when their edge segment lies within the region's
    /// world bounds. Normalization folds South/West edges to neighboring cells, so
    /// testing the owning cell alone would miss the south and west boundary walls.
    public RegionSnapshot CaptureRegion(int x0, int y0, int x1, int y1)
    {
        var region = new HashSet<CellKey>();
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                region.Add(new CellKey(x, y));

        // region rectangle in world coords: x in [x0, x1+1], y in [y0, y1+1]
        bool SegmentIn(EdgeKey e)
        {
            var (cx, cy) = (e.Cell.X, e.Cell.Y);
            (int ax, int ay, int bx, int by) = e.Side switch
            {
                Side.North => (cx, cy, cx + 1, cy),
                Side.East => (cx + 1, cy, cx + 1, cy + 1),
                _ => (cx, cy, cx, cy),
            };
            return ax >= x0 && ax <= x1 + 1 && bx >= x0 && bx <= x1 + 1
                && ay >= y0 && ay <= y1 + 1 && by >= y0 && by <= y1 + 1;
        }

        var snap = new RegionSnapshot { OriginX = x0, OriginY = y0 };
        foreach (var c in Cells) if (region.Contains(c)) snap.Cells.Add(c);
        foreach (var (e, w) in Walls) if (SegmentIn(e)) snap.Walls[e] = w;
        foreach (var (e, d) in Doors) if (SegmentIn(e)) snap.Doors[e] = d;
        foreach (var (c, s) in Stairs) if (region.Contains(c)) snap.Stairs[c] = s;
        foreach (var l in Labels) if (region.Contains(l.Cell)) snap.Labels.Add(l);
        foreach (var (c, p) in Pawns) if (region.Contains(c)) snap.Pawns.Add(new Pawn { Cell = p.Cell, TemplateId = p.TemplateId, Statuses = p.CloneStatuses(), DmOnly = p.DmOnly });
        // an object travels when all of it is inside the region
        foreach (var o in Objects) if (o.Inside(x0, y0, x1, y1)) snap.Objects.Add(o.Copy());
        foreach (var im in Images) if (im.Inside(x0, y0, x1, y1)) snap.Images.Add(im.Copy());
        foreach (var w in AngledWalls)
        {
            if (!AngledWallIntersectsRect(w, x0, y0, x1, y1)) continue;
            snap.AngledWalls.Add(w);
            if (AngledDoors.TryGetValue(w, out var doors))
                snap.AngledDoors[w] = doors.Select(d => new AngledDoor { T = d.T, Type = d.Type, State = d.State, DmOnly = d.DmOnly }).ToList();
        }
        // vertex doors travel when their vertex sits inside the region's world
        // bounds (x in [x0, x1+1], y in [y0, y1+1]).
        foreach (var (k, d) in VertexDoors)
            if (k.X >= x0 && k.X <= x1 + 1 && k.Y >= y0 && k.Y <= y1 + 1)
                snap.VertexDoors[k] = new VertexDoor { Type = d.Type, State = d.State, DmOnly = d.DmOnly };
        // flood floors are cut to the rect, the same as cells
        foreach (var fr in FloodRegions)
        {
            if (!FloodRegionTouchesRect(fr, x0, y0, x1, y1)) continue;
            var part = ClipFloodRegionToRect(fr, x0, y0, x1, y1);
            if (part.Rings.Count > 0) snap.FloodRegions.Add(part);
        }
        return snap;
    }

    /// Pastes a snapshot with its origin mapped to (targetX, targetY).
    /// Doors imply walls, so pasting a door also ensures its wall exists.
    public void PasteRegion(RegionSnapshot snap, int targetX, int targetY)
    {
        int dx = targetX - snap.OriginX, dy = targetY - snap.OriginY;
        foreach (var c in snap.Cells)
            Cells.Add(new CellKey(c.X + dx, c.Y + dy));
        foreach (var (e, w) in snap.Walls)
            SetWall(new EdgeKey(new CellKey(e.Cell.X + dx, e.Cell.Y + dy), e.Side), w);
        foreach (var (c, s) in snap.Stairs)
            Stairs[new CellKey(c.X + dx, c.Y + dy)] = new Stair { Direction = s.Direction, TargetMap = s.TargetMap };
        foreach (var l in snap.Labels)
        {
            var nl = l.CopyAt(new CellKey(l.Cell.X + dx, l.Cell.Y + dy));
            // a copy next to its original gets its own id (a move keeps it)
            if (nl.Id.Length > 0 && Labels.Any(o => o.Id == nl.Id)) nl.Id = MapLabel.NewId();
            Labels.Add(nl);
        }
        foreach (var p in snap.Pawns)
            Pawns[new CellKey(p.Cell.X + dx, p.Cell.Y + dy)] = new Pawn
            {
                Cell = new CellKey(p.Cell.X + dx, p.Cell.Y + dy),
                TemplateId = p.TemplateId,
                Statuses = p.CloneStatuses(),
                DmOnly = p.DmOnly,
            };
        foreach (var o in snap.Objects)
        {
            var n = o.Copy(newId: true); // a paste can land many copies
            n.X += dx; n.Y += dy;
            Objects.Add(n);
        }
        foreach (var im in snap.Images)
        {
            // a copy next to its original gets its own id (a move keeps it)
            var n = im.Copy(newId: Images.Any(o => o.Id == im.Id));
            n.X += dx; n.Y += dy;
            Images.Add(n);
        }
        foreach (var (e, d) in snap.Doors)
        {
            var edge = new EdgeKey(new CellKey(e.Cell.X + dx, e.Cell.Y + dy), e.Side);
            SetWall(edge, true);
            Doors[EdgeKey.Normalize(edge.Cell, edge.Side)] = new Door { Type = d.Type, State = d.State, DmOnly = d.DmOnly };
        }
        foreach (var w in snap.AngledWalls)
        {
            var nw = new AngledWall(w.Ax + dx, w.Ay + dy, w.Bx + dx, w.By + dy);
            AngledWalls.Add(nw);
            if (snap.AngledDoors.TryGetValue(w, out var doors))
            {
                var list = AngledDoors.TryGetValue(nw, out var l) ? l : (AngledDoors[nw] = new List<AngledDoor>());
                foreach (var d in doors)
                    list.Add(new AngledDoor { T = d.T, Type = d.Type, State = d.State, DmOnly = d.DmOnly });
            }
        }
        // vertex doors: the door implies walls, so pasting ensures both straddled
        // walls exist at the translated vertex.
        foreach (var (k, d) in snap.VertexDoors)
        {
            var nk = new VertexDoorKey(k.X + dx, k.Y + dy, k.Horizontal);
            VertexDoors[nk] = new VertexDoor { Type = d.Type, State = d.State, DmOnly = d.DmOnly };
            foreach (var e in VertexStraddles(nk)) SetWall(e, true);
        }
        foreach (var fr in snap.FloodRegions)
        {
            var rings = new List<List<(double, double)>>();
            foreach (var ring in fr.Rings)
            {
                var r = new List<(double, double)>();
                foreach (var (vx, vy) in ring) r.Add((vx + dx, vy + dy));
                rings.Add(r);
            }
            FloodRegions.Add(new FloodRegion { Rings = rings });
        }
    }

    /// Replaces all content of this document with the content of another.
    /// Used to apply undo/redo snapshots to the live instance in place.
    public void ApplyFrom(MapDocument other)
    {
        Cells.Clear();
        foreach (var c in other.Cells) Cells.Add(c);
        Walls.Clear();
        foreach (var (e, w) in other.Walls) Walls[e] = w;
        Doors.Clear();
        foreach (var (e, d) in other.Doors) Doors[e] = d;
        Stairs.Clear();
        foreach (var (c, s) in other.Stairs) Stairs[c] = s;
        Labels.Clear();
        foreach (var l in other.Labels) Labels.Add(l);
        Pawns.Clear();
        foreach (var (c, p) in other.Pawns) Pawns[c] = p;
        AngledWalls.Clear();
        foreach (var w in other.AngledWalls) AngledWalls.Add(w);
        AngledDoors.Clear();
        foreach (var (w, doors) in other.AngledDoors)
            AngledDoors[w] = doors.Select(d => new AngledDoor { T = d.T, Type = d.Type, State = d.State, DmOnly = d.DmOnly }).ToList();
        FloodRegions.Clear();
        foreach (var fr in other.FloodRegions) FloodRegions.Add(fr.Clone());
        VertexDoors.Clear();
        foreach (var (k, d) in other.VertexDoors) VertexDoors[k] = new VertexDoor { Type = d.Type, State = d.State, DmOnly = d.DmOnly };
        Objects.Clear();
        foreach (var o in other.Objects) Objects.Add(o.Copy());
        Images.Clear();
        foreach (var im in other.Images) Images.Add(im.Copy());
        Revealed.Clear();
        foreach (var c in other.Revealed) Revealed.Add(c);
        CombatTurn = other.CombatTurn;
    }

    /// Removes all content in the rectangle [x0,y0]-[x1,y1]: cells, stairs,
    /// pawns, labels, and walls on every edge of each cell (doors auto-removed
    /// by SetWall). Used by erase, floor, move, and cut.
    public void ClearRegion(int x0, int y0, int x1, int y1)
    {
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                var c = new CellKey(x, y);
                Cells.Remove(c);
                Stairs.Remove(c);
                Pawns.Remove(c);
                Labels.RemoveAll(l => l.Cell == c);
                foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                    SetWall(new EdgeKey(c, side), false);
            }
        foreach (var w in AngledWalls)
            if (AngledWallIntersectsRect(w, x0, y0, x1, y1)) AngledDoors.Remove(w);
        AngledWalls.RemoveAll(w => AngledWallIntersectsRect(w, x0, y0, x1, y1));
        // objects go when all of them is inside, the same rule as capture, so a
        // move or cut never deletes an object it did not pick up
        Objects.RemoveAll(o => o.Inside(x0, y0, x1, y1));
        Images.RemoveAll(o => o.Inside(x0, y0, x1, y1));

        // flood floors lose the rect, the rest of each floor stays
        SubtractFloorRect(x0, y0, x1, y1);
    }

    /// Rotates a snapshot counterclockwise by turns*90 degrees around its origin
    /// and returns a NEW snapshot with the same origin. In y-down screen coords a
    /// lattice vertex rotates CCW as (dx,dy) -> (dy,-dx); cell keys are top-left
    /// corners, so rotating the cell REGION CCW shifts the key one extra step and
    /// cells use (dx,dy) -> (dy,-dx-1). Wall sides rotate CCW (North->West->South->
    /// East), stair directions rotate CCW (Up->Left->Down->Right), angled wall
    /// endpoints rotate around the origin, and angled door T (distance along the
    /// segment) is preserved. Used by the move/copy rotation (R / Shift+R /
    /// shift+scroll).
    public static RegionSnapshot Rotate(RegionSnapshot snap, int turns)
    {
        turns = ((turns % 4) + 4) % 4;
        if (turns == 0) return snap;
        int x0 = snap.OriginX, y0 = snap.OriginY;
        (int, int) RotPoint(int x, int y)
        {
            int dx = x - x0, dy = y - y0;
            for (int i = 0; i < turns; i++) (dx, dy) = (dy, -dx);
            return (x0 + dx, y0 + dy);
        }
        (int, int) RotCell(int x, int y)
        {
            int dx = x - x0, dy = y - y0;
            for (int i = 0; i < turns; i++) (dx, dy) = (dy, -dx - 1);
            return (x0 + dx, y0 + dy);
        }
        Side RotSide(Side s)
        {
            for (int i = 0; i < turns; i++)
                s = s switch { Side.North => Side.West, Side.West => Side.South,
                               Side.South => Side.East, _ => Side.North };
            return s;
        }
        StairDirection RotDir(StairDirection d)
        {
            for (int i = 0; i < turns; i++)
                d = d switch { StairDirection.Up => StairDirection.Left,
                               StairDirection.Left => StairDirection.Down,
                               StairDirection.Down => StairDirection.Right,
                               _ => StairDirection.Up };
            return d;
        }

        var s = new RegionSnapshot { OriginX = x0, OriginY = y0 };
        foreach (var c in snap.Cells)
        {
            var (rx, ry) = RotCell(c.X, c.Y);
            s.Cells.Add(new CellKey(rx, ry));
        }
        foreach (var (e, w) in snap.Walls)
        {
            var (rx, ry) = RotCell(e.Cell.X, e.Cell.Y);
            s.Walls[EdgeKey.Normalize(new CellKey(rx, ry), RotSide(e.Side))] = w;
        }
        foreach (var fr in snap.FloodRegions)
        {
            var rings = new List<List<(double x, double y)>>();
            foreach (var ring in fr.Rings)
            {
                var r = new List<(double, double)>();
                foreach (var (vx, vy) in ring)
                {
                    double ddx = vx - x0, ddy = vy - y0;
                    for (int i = 0; i < turns; i++) (ddx, ddy) = (ddy, -ddx);
                    r.Add((x0 + ddx, y0 + ddy));
                }
                rings.Add(r);
            }
            s.FloodRegions.Add(new FloodRegion { Rings = rings });
        }
        foreach (var (e, d) in snap.Doors)
        {
            var (rx, ry) = RotCell(e.Cell.X, e.Cell.Y);
            var edge = EdgeKey.Normalize(new CellKey(rx, ry), RotSide(e.Side));
            s.Doors[edge] = new Door { Type = d.Type, State = d.State, DmOnly = d.DmOnly };
        }
        foreach (var (c, st) in snap.Stairs)
        {
            var (rx, ry) = RotCell(c.X, c.Y);
            s.Stairs[new CellKey(rx, ry)] = new Stair { Direction = RotDir(st.Direction), TargetMap = st.TargetMap };
        }
        foreach (var l in snap.Labels)
        {
            // a range label rotates as a rect: both corners, then the new top-left
            var (ax, ay) = RotCell(l.Cell.X, l.Cell.Y);
            var (bx, by) = RotCell(l.Cell.X + l.W - 1, l.Cell.Y + l.H - 1);
            var nl = l.CopyAt(new CellKey(Math.Min(ax, bx), Math.Min(ay, by)));
            nl.W = Math.Abs(bx - ax) + 1;
            nl.H = Math.Abs(by - ay) + 1;
            s.Labels.Add(nl);
        }
        foreach (var p in snap.Pawns)
        {
            var (rx, ry) = RotCell(p.Cell.X, p.Cell.Y);
            s.Pawns.Add(new Pawn { Cell = new CellKey(rx, ry), TemplateId = p.TemplateId, Statuses = p.CloneStatuses(), DmOnly = p.DmOnly });
        }
        foreach (var o in snap.Objects)
        {
            // a rect like range labels: both corners, then the new top-left. a
            // quarter turn swaps width and height
            var (ax, ay) = RotCell(o.X, o.Y);
            var (bx, by) = RotCell(o.X + o.W - 1, o.Y + o.H - 1);
            var n = o.Copy();
            n.X = Math.Min(ax, bx); n.Y = Math.Min(ay, by);
            n.W = Math.Abs(bx - ax) + 1; n.H = Math.Abs(by - ay) + 1;
            s.Objects.Add(n);
        }
        foreach (var im in snap.Images)
        {
            // like objects, and the picture turns with the region (the region
            // turns counterclockwise, image turns count clockwise)
            var (ax, ay) = RotCell(im.X, im.Y);
            var (bx, by) = RotCell(im.X + im.W - 1, im.Y + im.H - 1);
            var n = im.Copy();
            n.X = Math.Min(ax, bx); n.Y = Math.Min(ay, by);
            n.W = Math.Abs(bx - ax) + 1; n.H = Math.Abs(by - ay) + 1;
            n.Turns = im.Turns - turns;
            s.Images.Add(n);
        }
        foreach (var w in snap.AngledWalls)
        {
            var (ax, ay) = RotPoint(w.Ax, w.Ay);
            var (bx, by) = RotPoint(w.Bx, w.By);
            var nw = new AngledWall(ax, ay, bx, by);
            s.AngledWalls.Add(nw);
            if (snap.AngledDoors.TryGetValue(w, out var doors))
                s.AngledDoors[nw] = doors.Select(d => new AngledDoor { T = d.T, Type = d.Type, State = d.State, DmOnly = d.DmOnly }).ToList();
        }
        foreach (var (k, d) in snap.VertexDoors)
        {
            var (rx, ry) = RotPoint(k.X, k.Y);
            bool h = k.Horizontal;
            for (int i = 0; i < turns; i++) h = !h; // quarter-turn flips horizontal<->vertical
            s.VertexDoors[new VertexDoorKey(rx, ry, h)] = new VertexDoor { Type = d.Type, State = d.State, DmOnly = d.DmOnly };
        }
        return s;
    }

    /// Moves a rectangular region: captures it, clears the source, then pastes
    /// at the target origin. Clearing BEFORE pasting means a move that overlaps
    /// its own source survives (Excel-style shift) - the snapshot is a deep clone.
    public void MoveRegion(int x0, int y0, int x1, int y1, int targetX, int targetY)
    {
        var snap = CaptureRegion(x0, y0, x1, y1);
        ClearRegion(x0, y0, x1, y1);
        PasteRegion(snap, targetX, targetY);
    }

    /// Bounding box of all drawn content, in cell units. Null when empty.
    public (int minX, int minY, int maxX, int maxY)? ContentBounds()
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        bool any = false;
        foreach (var c in Cells)
        {
            if (c.X < minX) minX = c.X;
            if (c.Y < minY) minY = c.Y;
            if (c.X > maxX) maxX = c.X;
            if (c.Y > maxY) maxY = c.Y;
            any = true;
        }
        foreach (var w in AngledWalls)
        {
            if (w.Ax < minX) minX = w.Ax; if (w.Ax > maxX) maxX = w.Ax;
            if (w.Ay < minY) minY = w.Ay; if (w.Ay > maxY) maxY = w.Ay;
            if (w.Bx < minX) minX = w.Bx; if (w.Bx > maxX) maxX = w.Bx;
            if (w.By < minY) minY = w.By; if (w.By > maxY) maxY = w.By;
            any = true;
        }
        foreach (var o in Objects.Select(o => (o.X, o.Y, o.W, o.H)).Concat(Images.Select(i => (i.X, i.Y, i.W, i.H))))
        {
            if (o.X < minX) minX = o.X;
            if (o.Y < minY) minY = o.Y;
            if (o.X + o.W - 1 > maxX) maxX = o.X + o.W - 1;
            if (o.Y + o.H - 1 > maxY) maxY = o.Y + o.H - 1;
            any = true;
        }
        foreach (var fr in FloodRegions)
        {
            var (bminX, bminY, bmaxX, bmaxY) = fr.Bounds();
            int bxi = (int)Math.Floor(bminX), bxa = (int)Math.Ceiling(bmaxX);
            int byi = (int)Math.Floor(bminY), bya = (int)Math.Ceiling(bmaxY);
            if (bxi < minX) minX = bxi; if (bxa > maxX) maxX = bxa;
            if (byi < minY) minY = byi; if (bya > maxY) maxY = bya;
            any = true;
        }
        if (!any) return null;
        return (minX, minY, maxX, maxY);
    }
}
