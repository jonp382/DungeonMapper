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
            case Side.South: return new EdgeKey(new CellKey(cell.X, cell.Y - 1), Side.North);
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
