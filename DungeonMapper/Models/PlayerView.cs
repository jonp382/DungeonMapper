using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonMapper.Models;

/// what a player is sent: the cut down map plus only the library entries,
/// pictures and presets it uses
public sealed class PlayerPackage
{
    public MapDocument Map { get; init; } = new();
    public PawnLibrary Library { get; init; } = new();
    public ObjectLibrary Objects { get; init; } = new();
    public CampaignPresets Presets { get; init; } = new();
}

/// Builds the players' copy of a map (multiplayer, see Agent.md "Multiplayer
/// Design"). This is the anti-cheat: anything not in the result never leaves
/// the DM's machine. Every item is a fresh copy, nothing is shared with the
/// DM's map.
///  - floor: revealed room cells; flood floors clipped to revealed cells
///  - walls: edges with a revealed cell on at least one side, so a revealed
///    room shows its outline. nothing past them
///  - angled walls: the pieces (between lattice points on the wall) that pass
///    through a revealed cell
///  - doors: on a sent wall and not DM only. vertex doors bring their two
///    straddled walls (a door implies its walls)
///  - stairs and pawns: on revealed cells, pawns not DM only
///  - labels and objects: not DM only and covering at least one revealed cell.
///    labels players made go wherever they are (the players placed them)
///  - the players' light (the DM's anchored fog circle), when set
public static class PlayerView
{
    public static MapDocument Build(MapDocument doc)
    {
        var shown = new HashSet<CellKey>(doc.Revealed.Where(doc.IsFloorCell));
        var v = new MapDocument();
        foreach (var c in shown) v.Revealed.Add(c);
        v.PlayerLight = doc.PlayerLight;

        foreach (var c in doc.Cells) if (shown.Contains(c)) v.Cells.Add(c);
        foreach (var fr in doc.FloodRegions) ClipFlood(fr, shown, v);

        bool EdgeShown(EdgeKey e) => shown.Contains(e.Cell) || shown.Contains(e.NeighborCell());
        foreach (var (e, w) in doc.Walls)
            if (w && EdgeShown(e)) v.Walls[e] = true;
        foreach (var (e, d) in doc.Doors)
            if (!d.DmOnly && v.Walls.ContainsKey(e))
                v.Doors[e] = new Door { Type = d.Type, State = d.State, DmOnly = false };
        foreach (var (k, d) in doc.VertexDoors)
        {
            if (d.DmOnly) continue;
            var straddles = MapDocument.VertexStraddles(k);
            if (!straddles.Any(EdgeShown)) continue;
            foreach (var e in straddles) v.SetWall(e, true);
            v.VertexDoors[k] = new VertexDoor { Type = d.Type, State = d.State, DmOnly = false };
        }
        foreach (var w in doc.AngledWalls) ClipAngled(w, doc.GetAngledDoors(w), shown, v);

        foreach (var (c, s) in doc.Stairs)
            if (shown.Contains(c)) v.Stairs[c] = new Stair { Direction = s.Direction, TargetMap = s.TargetMap };
        foreach (var (c, p) in doc.Pawns)
            if (!p.DmOnly && shown.Contains(c))
                v.Pawns[c] = new Pawn { Cell = c, TemplateId = p.TemplateId, Status = p.Status, DmOnly = false };
        foreach (var l in doc.Labels)
            if (!l.DmOnly && (l.CreatedBy is not null || AnyShown(shown, l.Cell.X, l.Cell.Y, l.W, l.H)))
            {
                var n = l.CopyAt(l.Cell);
                n.DmOnly = false;
                v.Labels.Add(n);
            }
        foreach (var o in doc.Objects)
            if (!o.DmOnly && AnyShown(shown, o.X, o.Y, o.W, o.H))
            {
                var n = o.Copy();
                n.DmOnly = false;
                v.Objects.Add(n);
            }
        return v;
    }

    /// the map plus the pawn templates (those it uses and every player hero),
    /// object types, pictures, door types and statuses it uses. formations and
    /// favorites stay behind
    public static PlayerPackage Package(MapDocument doc, PawnLibrary library, ObjectLibrary objects, CampaignPresets presets)
    {
        var map = Build(doc);

        var lib = new PawnLibrary();
        // the players' own heroes always go, so they can place them
        var templateIds = new HashSet<string>(map.Pawns.Values.Select(p => p.TemplateId)
            .Concat(library.Pawns.Where(PlayerPawns.Controlled).Select(t => t.Id)));
        foreach (var t in library.Pawns.Where(t => templateIds.Contains(t.Id)))
            lib.Pawns.AddRange(PawnLibraryClone(t));

        var types = objects.Types.Where(t => map.Objects.Any(o => o.TypeId == t.Id))
            .Select(t => new ObjectType { Id = t.Id, Name = t.Name, Look = t.Look.Clone() }).ToList();
        var objs = new ObjectLibrary { Types = types };
        objs.AddPictures(objects.UsedPictures(new[] { map }, types));

        var doorTypes = new HashSet<string>(map.AllDoors().Select(d => d.Type));
        var statuses = new HashSet<string>(map.Pawns.Values.Select(p => p.Status ?? "")
            .Concat(lib.Pawns.Select(t => t.Status)));
        var pre = new CampaignPresets
        {
            DoorTypes = presets.DoorTypes.Where(t => doorTypes.Contains(t.Name)).ToList(),
            PawnStatuses = presets.PawnStatuses.Where(s => statuses.Contains(s.Name)).ToList(),
            AffiliationColors = presets.AffiliationColors.ToList(),
        };
        return new PlayerPackage { Map = map, Library = lib, Objects = objs, Presets = pre };
    }

    // a template copy through the wire format, without the DM's favorite mark
    private static List<PawnTemplate> PawnLibraryClone(PawnTemplate t)
    {
        var tmp = new PawnLibrary();
        tmp.Pawns.Add(t);
        var list = MapSerializer.LibraryFromDtos(MapSerializer.LibraryToDtos(tmp));
        foreach (var c in list) { c.Id = t.Id; c.Favorite = false; }
        return list;
    }

    private static bool AnyShown(HashSet<CellKey> shown, int x, int y, int w, int h)
    {
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
                if (shown.Contains(new CellKey(xx, yy))) return true;
        return false;
    }

    // the revealed cells in a flood floor's bounds as rectangles (row runs
    // stacked when rows repeat), each clipped from the floor as its own piece
    private static void ClipFlood(FloodRegion fr, HashSet<CellKey> shown, MapDocument v)
    {
        var (minX, minY, maxX, maxY) = fr.Bounds();
        int x0 = (int)Math.Floor(minX), x1 = (int)Math.Ceiling(maxX) - 1;
        int y0 = (int)Math.Floor(minY), y1 = (int)Math.Ceiling(maxY) - 1;
        var rects = new List<(int x0, int y0, int x1, int y1)>();
        var open = new Dictionary<(int, int), int>(); // run -> index in rects, for the row above
        for (int y = y0; y <= y1; y++)
        {
            var next = new Dictionary<(int, int), int>();
            int x = x0;
            while (x <= x1)
            {
                if (!shown.Contains(new CellKey(x, y))) { x++; continue; }
                int s = x;
                while (x + 1 <= x1 && shown.Contains(new CellKey(x + 1, y))) x++;
                if (open.TryGetValue((s, x), out int i)) { rects[i] = (s, rects[i].y0, x, y); next[(s, x)] = i; }
                else { rects.Add((s, y, x, y)); next[(s, x)] = rects.Count - 1; }
                x++;
            }
            open = next;
        }
        foreach (var (rx0, ry0, rx1, ry1) in rects)
        {
            var part = MapDocument.ClipFloodRegionToRect(fr, rx0, ry0, rx1, ry1);
            if (part.Rings.Count > 0) v.FloodRegions.Add(part);
        }
    }

    // an angled wall is split at the lattice points it passes through. pieces
    // that cross a revealed cell are kept, runs of kept pieces join back into
    // one wall, and doors whose center lands on a kept run go with it
    private static void ClipAngled(AngledWall w, List<AngledDoor> doors, HashSet<CellKey> shown, MapDocument v)
    {
        int dx = w.Bx - w.Ax, dy = w.By - w.Ay;
        int g = Gcd(Math.Abs(dx), Math.Abs(dy));
        if (g == 0) return;
        int sx = dx / g, sy = dy / g;
        double len = Math.Sqrt((double)dx * dx + (double)dy * dy), step = len / g;
        int i = 0;
        while (i < g)
        {
            bool Kept(int k) => MapDocument.CellsCrossed(w.Ax + sx * k, w.Ay + sy * k, w.Ax + sx * (k + 1), w.Ay + sy * (k + 1))
                .Any(shown.Contains);
            if (!Kept(i)) { i++; continue; }
            int s = i;
            while (i + 1 < g && Kept(i + 1)) i++;
            var piece = new AngledWall(w.Ax + sx * s, w.Ay + sy * s, w.Ax + sx * (i + 1), w.Ay + sy * (i + 1));
            v.AngledWalls.Add(piece);
            double t0 = s * step, t1 = (i + 1) * step;
            var kept = doors.Where(d => !d.DmOnly && d.T >= t0 && d.T <= t1)
                .Select(d => new AngledDoor { T = d.T - t0, Type = d.Type, State = d.State, DmOnly = false }).ToList();
            if (kept.Count > 0) v.AngledDoors[piece] = kept;
            i++;
        }
    }

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }
}
