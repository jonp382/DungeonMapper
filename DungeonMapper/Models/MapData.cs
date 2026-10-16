using System;
using System.Collections.Generic;

namespace DungeonMapper.Models;

/// A cell coordinate. 1 unit = 5ft. Y increases downward (screen space).
public readonly record struct CellKey(int X, int Y);

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

/// A door on an edge. A door implies a wall with a gap: the edge must have a wall.
/// Type is a free-form string (e.g. "standard", "portcullis", "bricked", "magical")
/// so custom doors stay modular; the renderer picks color/symbol per type.
public sealed class Door
{
    public string Type { get; set; } = "standard";
    public DoorState State { get; set; } = DoorState.Closed;
}

public enum StairDirection { Up, Down }

/// A stair is a cell feature, not an edge. TargetMap is reserved for future
/// multi-map tracking (stairs connecting maps); null = same map.
public sealed class Stair
{
    public StairDirection Direction { get; set; } = StairDirection.Down;
    public string? TargetMap { get; set; }
}

/// Text anchored to a cell, rendered centered on that cell.
public sealed class MapLabel
{
    public CellKey Cell { get; set; }
    public string Text { get; set; } = "";
}

/// The whole map. Rooms are not a data structure - a room is just a set of cells.
public sealed class MapDocument
{
    public HashSet<CellKey> Cells { get; } = new();
    public Dictionary<EdgeKey, bool> Walls { get; } = new();
    public Dictionary<EdgeKey, Door> Doors { get; } = new();
    public Dictionary<CellKey, Stair> Stairs { get; } = new();
    public List<MapLabel> Labels { get; } = new();

    public bool HasWall(EdgeKey edge) => Walls.TryGetValue(EdgeKey.Normalize(edge.Cell, edge.Side), out var w) && w;

    public void SetWall(EdgeKey edge, bool exists)
    {
        var key = EdgeKey.Normalize(edge.Cell, edge.Side);
        if (exists) Walls[key] = true;
        else Walls.Remove(key);
        // Removing a wall removes any door on it (door implies wall).
        if (!exists && Doors.ContainsKey(key)) Doors.Remove(key);
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

    public void RemoveDoor(EdgeKey edge)
    {
        Doors.Remove(EdgeKey.Normalize(edge.Cell, edge.Side));
    }

    /// Adds a rectangular room: fills cells, sets walls on the outer boundary,
    /// and removes walls strictly inside the region. Drawing a room over adjacent
    /// rooms merges them (boolean union) - the shared interior walls disappear.
    public void AddRoom(int x0, int y0, int x1, int y1)
    {
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                Cells.Add(new CellKey(x, y));
        // outer boundary walls
        for (int x = x0; x <= x1; x++)
        {
            SetWall(new EdgeKey(new CellKey(x, y0), Side.North), true);
            SetWall(new EdgeKey(new CellKey(x, y1), Side.South), true);
        }
        for (int y = y0; y <= y1; y++)
        {
            SetWall(new EdgeKey(new CellKey(x0, y), Side.West), true);
            SetWall(new EdgeKey(new CellKey(x1, y), Side.East), true);
        }
        // interior walls removed so adjacent rooms merge
        for (int x = x0 + 1; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                SetWall(new EdgeKey(new CellKey(x - 1, y), Side.East), false);
        for (int y = y0 + 1; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                SetWall(new EdgeKey(new CellKey(x, y), Side.North), false);
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
        public bool Empty => Cells.Count == 0 && Walls.Count == 0 && Doors.Count == 0
                              && Stairs.Count == 0 && Labels.Count == 0;
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
            Labels.Add(new MapLabel { Cell = new CellKey(l.Cell.X + dx, l.Cell.Y + dy), Text = l.Text });
        foreach (var (e, d) in snap.Doors)
        {
            var edge = new EdgeKey(new CellKey(e.Cell.X + dx, e.Cell.Y + dy), e.Side);
            SetWall(edge, true);
            Doors[EdgeKey.Normalize(edge.Cell, edge.Side)] = new Door { Type = d.Type, State = d.State };
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
        if (!any) return null;
        return (minX, minY, maxX, maxY);
    }
}
