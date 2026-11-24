using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonMapper.Models;

/// which kinds of items the reveal tool's item mode touches
[Flags]
public enum RevealItems { None = 0, Labels = 1, Doors = 2, Objects = 4, Pawns = 8, Images = 16, All = Labels | Doors | Objects | Pawns | Images }

// multiplayer reveal state (see Agent.md "Multiplayer Design"). Revealed is a
// mask of cells the DM has shown to players; DmOnly on labels, doors, objects
// and pawns hides an item even on revealed cells. both are DM edits: saved
// with the map and undoable.
public sealed partial class MapDocument
{
    /// a cell with any floor: a room cell, or a cell a flood region floor
    /// covers at its center or near a corner (diagonal cut cells)
    public bool IsFloorCell(CellKey c)
    {
        if (Cells.Contains(c)) return true;
        if (FloodRegions.Count == 0) return false;
        foreach (var (fx, fy) in new[] { (0.5, 0.5), (0.1, 0.1), (0.9, 0.1), (0.1, 0.9), (0.9, 0.9) })
            if (FloorAt(c.X + fx, c.Y + fy)) return true;
        return false;
    }

    /// every cell with floor: room cells plus the cells flood floors cover
    public HashSet<CellKey> FloorCells()
    {
        var all = new HashSet<CellKey>(Cells);
        foreach (var fr in FloodRegions)
        {
            var (minX, minY, maxX, maxY) = fr.Bounds();
            for (int y = (int)Math.Floor(minY); y < (int)Math.Ceiling(maxY); y++)
                for (int x = (int)Math.Floor(minX); x < (int)Math.Ceiling(maxX); x++)
                {
                    var c = new CellKey(x, y);
                    if (!all.Contains(c) && IsFloorCell(c)) all.Add(c);
                }
        }
        return all;
    }

    /// shows or hides cells. only floor cells are revealed, any cell can be
    /// hidden. returns how many changed
    public int SetRevealed(IEnumerable<CellKey> cells, bool show)
    {
        int n = 0;
        foreach (var c in cells)
            if (show ? IsFloorCell(c) && Revealed.Add(c) : Revealed.Remove(c)) n++;
        return n;
    }

    /// the room around a cell for "reveal room": room cells up to the walls
    /// (doors count as walls), or the floor cells of the flood floor under it
    public HashSet<CellKey> RevealRoomCells(CellKey start)
    {
        if (Cells.Contains(start)) return RoomAround(start);
        var room = new HashSet<CellKey>();
        foreach (var fr in FloodRegions)
        {
            if (!PointInRings(start.X + 0.5, start.Y + 0.5, fr.Rings)) continue;
            var (minX, minY, maxX, maxY) = fr.Bounds();
            for (int y = (int)Math.Floor(minY); y < (int)Math.Ceiling(maxY); y++)
                for (int x = (int)Math.Floor(minX); x < (int)Math.Ceiling(maxX); x++)
                {
                    var c = new CellKey(x, y);
                    foreach (var (fx, fy) in new[] { (0.5, 0.5), (0.1, 0.1), (0.9, 0.1), (0.1, 0.9), (0.9, 0.9) })
                        if (PointInRings(c.X + fx, c.Y + fy, fr.Rings)) { room.Add(c); break; }
                }
        }
        return room;
    }

    /// the world point a door on an angled wall sits at
    public static (double x, double y) AngledDoorPoint(AngledWall w, AngledDoor d)
    {
        double dx = w.Bx - w.Ax, dy = w.By - w.Ay;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 0) return (w.Ax, w.Ay);
        return (w.Ax + dx * d.T / len, w.Ay + dy * d.T / len);
    }

    /// every item of the chosen kinds touching the cell rect [x0,y0]-[x1,y1]
    /// (inclusive). doors touch when a cell on either side of their edge is in
    /// the rect, vertex and angled doors when their point is on or in it
    public List<object> ItemsIn(int x0, int y0, int x1, int y1, RevealItems kinds)
    {
        bool In(CellKey c) => c.X >= x0 && c.X <= x1 && c.Y >= y0 && c.Y <= y1;
        bool PtIn(double x, double y) => x >= x0 && x <= x1 + 1 && y >= y0 && y <= y1 + 1;
        var list = new List<object>();
        if (kinds.HasFlag(RevealItems.Labels))
            foreach (var l in Labels)
                if (l.Cell.X <= x1 && l.Cell.X + l.W - 1 >= x0 && l.Cell.Y <= y1 && l.Cell.Y + l.H - 1 >= y0) list.Add(l);
        if (kinds.HasFlag(RevealItems.Doors))
        {
            foreach (var (e, d) in Doors) if (In(e.Cell) || In(e.NeighborCell())) list.Add(d);
            foreach (var (k, d) in VertexDoors) if (PtIn(k.X, k.Y)) list.Add(d);
            foreach (var (w, doors) in AngledDoors)
                foreach (var d in doors)
                {
                    var (px, py) = AngledDoorPoint(w, d);
                    if (PtIn(px, py)) list.Add(d);
                }
        }
        if (kinds.HasFlag(RevealItems.Objects))
            foreach (var o in Objects) if (o.Touches(x0, y0, x1, y1)) list.Add(o);
        if (kinds.HasFlag(RevealItems.Images))
            foreach (var im in Images) if (im.Touches(x0, y0, x1, y1)) list.Add(im);
        if (kinds.HasFlag(RevealItems.Pawns))
            foreach (var (c, p) in Pawns) if (In(c)) list.Add(p);
        return list;
    }

    public static bool IsDmOnly(object item) => item switch
    {
        MapLabel l => l.DmOnly, IDoorConfig d => d.DmOnly, MapObject o => o.DmOnly, Pawn p => p.DmOnly, MapImage im => im.DmOnly, _ => false,
    };

    public static void SetDmOnly(object item, bool dmOnly)
    {
        switch (item)
        {
            case MapLabel l: l.DmOnly = dmOnly; break;
            case IDoorConfig d: d.DmOnly = dmOnly; break;
            case MapObject o: o.DmOnly = dmOnly; break;
            case Pawn p: p.DmOnly = dmOnly; break;
            case MapImage im: im.DmOnly = dmOnly; break;
        }
    }

    /// marks the items of the chosen kinds in the rect as DM-only or visible.
    /// returns how many changed
    public int SetDmOnly(int x0, int y0, int x1, int y1, RevealItems kinds, bool dmOnly)
    {
        int n = 0;
        foreach (var item in ItemsIn(x0, y0, x1, y1, kinds))
            if (IsDmOnly(item) != dmOnly) { SetDmOnly(item, dmOnly); n++; }
        return n;
    }

    /// every item of the chosen kinds on the map
    public List<object> AllItems(RevealItems kinds)
    {
        var list = new List<object>();
        if (kinds.HasFlag(RevealItems.Labels)) list.AddRange(Labels);
        if (kinds.HasFlag(RevealItems.Doors))
        {
            list.AddRange(Doors.Values);
            list.AddRange(VertexDoors.Values);
            foreach (var doors in AngledDoors.Values) list.AddRange(doors);
        }
        if (kinds.HasFlag(RevealItems.Objects)) list.AddRange(Objects);
        if (kinds.HasFlag(RevealItems.Images)) list.AddRange(Images);
        if (kinds.HasFlag(RevealItems.Pawns)) list.AddRange(Pawns.Values);
        return list;
    }

    /// DM-only count per kind, for the reveal panel
    public (int labels, int doors, int objects, int pawns, int images) DmOnlyCounts() => (
        AllItems(RevealItems.Labels).Count(IsDmOnly),
        AllItems(RevealItems.Doors).Count(IsDmOnly),
        AllItems(RevealItems.Objects).Count(IsDmOnly),
        AllItems(RevealItems.Pawns).Count(IsDmOnly),
        AllItems(RevealItems.Images).Count(IsDmOnly));
}
