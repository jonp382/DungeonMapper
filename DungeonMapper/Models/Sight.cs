using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonMapper.Models;

/// how a straight line from the viewer to a point fares (the sight tool's
/// hover line)
public enum SightLine { Clear, OnePawn, TwoPawns, Blocked, OutOfRange }

/// a blocking segment in world units (cells)
public readonly record struct SightSegment(double Ax, double Ay, double Bx, double By);

/// line of sight geometry for the sight tool. pure maths over the map, no
/// drawing. see Agent.md "Line of Sight Design".
///  - walls, closed and locked doors (dm only ones too) and the floor's edge
///    block. open doors (one cell wide) and stairs do not. objects are ignored
///  - the visible region is an exact polygon from one point, cut off at the
///    range circle
///  - each other pawn is a circle casting a shadow away from the viewer
public static class Sight
{
    /// the pawn glyph's outer radius in cells (MapCanvas draws 0.42 * scale)
    public const double PawnRadius = 0.42;
    public const int MinFeet = 1, MaxFeet = 240, StepFeet = 5, DefaultFeet = 120;
    /// rays spread evenly around the circle, so the range edge is smooth
    public const int RoundRays = 720;
    private const double Eps = 1e-9;
    private const double Nudge = 1e-5; // radians either side of a corner

    // ---- blockers ----

    /// every blocking segment on the map. axis aligned pieces on one line are
    /// merged so long walls are one segment (fewer rays, and no seams).
    public static List<SightSegment> Blockers(MapDocument d)
    {
        // axis aligned pieces in half cell steps: horizontal on line y from x
        // to x+1 (half units), vertical the same
        var hor = new HashSet<(int line, int at)>();
        var ver = new HashSet<(int line, int at)>();
        void Edge(EdgeKey key, bool lowHalf, bool highHalf)
        {
            int x = key.Cell.X, y = key.Cell.Y;
            if (key.Side == Side.North)
            {
                if (lowHalf) hor.Add((y * 2, x * 2));
                if (highHalf) hor.Add((y * 2, x * 2 + 1));
            }
            else
            {
                if (lowHalf) ver.Add(((x + 1) * 2, y * 2));
                if (highHalf) ver.Add(((x + 1) * 2, y * 2 + 1));
            }
        }

        // open vertex doors leave the half edges next to their vertex open
        var openHalves = new HashSet<(bool horizontal, int line, int at)>();
        foreach (var (k, door) in d.VertexDoors)
        {
            if (door.State != DoorState.Open) continue;
            if (k.Horizontal) { openHalves.Add((true, k.Y * 2, k.X * 2 - 1)); openHalves.Add((true, k.Y * 2, k.X * 2)); }
            else { openHalves.Add((false, k.X * 2, k.Y * 2 - 1)); openHalves.Add((false, k.X * 2, k.Y * 2)); }
        }

        foreach (var (key, on) in d.Walls)
        {
            if (!on) continue;
            if (d.Doors.TryGetValue(key, out var door) && door.State == DoorState.Open) continue;
            Edge(key, true, true);
        }

        // the floor's edge: a cell side with no floor across it
        const double e = 1e-3;
        foreach (var c in d.Cells)
        {
            int x = c.X, y = c.Y;
            if (!d.FloorAt(x + 0.5, y - e)) Edge(new EdgeKey(c, Side.North), true, true);
            if (!d.FloorAt(x + 0.5, y + 1 + e)) Edge(new EdgeKey(new CellKey(x, y + 1), Side.North), true, true);
            if (!d.FloorAt(x - e, y + 0.5)) Edge(new EdgeKey(new CellKey(x - 1, y), Side.East), true, true);
            if (!d.FloorAt(x + 1 + e, y + 0.5)) Edge(new EdgeKey(c, Side.East), true, true);
        }

        hor.RemoveWhere(h => openHalves.Contains((true, h.line, h.at)));
        ver.RemoveWhere(v => openHalves.Contains((false, v.line, v.at)));

        var segs = new List<SightSegment>();
        foreach (var (line, runs) in Runs(hor))
            foreach (var (a, b) in runs) segs.Add(new SightSegment(a / 2.0, line / 2.0, b / 2.0, line / 2.0));
        foreach (var (line, runs) in Runs(ver))
            foreach (var (a, b) in runs) segs.Add(new SightSegment(line / 2.0, a / 2.0, line / 2.0, b / 2.0));

        // angled walls, with gaps for open doors (one cell wide at T)
        foreach (var w in d.AngledWalls)
        {
            double dx = w.Bx - w.Ax, dy = w.By - w.Ay, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < Eps) continue;
            var gaps = d.GetAngledDoors(w).Where(o => o.State == DoorState.Open)
                .Select(o => (lo: Math.Max(0, o.T - 0.5), hi: Math.Min(len, o.T + 0.5)))
                .OrderBy(g => g.lo).ToList();
            double from = 0;
            foreach (var (lo, hi) in gaps)
            {
                if (lo > from + Eps) segs.Add(Piece(w, dx, dy, len, from, lo));
                from = Math.Max(from, hi);
            }
            if (len > from + Eps) segs.Add(Piece(w, dx, dy, len, from, len));
        }

        // polygon floor rings: a ring side with floor on one side only
        foreach (var fr in d.FloodRegions)
            foreach (var ring in fr.Rings)
                for (int i = 0; i < ring.Count; i++)
                {
                    var (ax, ay) = ring[i];
                    var (bx, by) = ring[(i + 1) % ring.Count];
                    double sx = bx - ax, sy = by - ay, sl = Math.Sqrt(sx * sx + sy * sy);
                    if (sl < Eps) continue;
                    double mx = (ax + bx) / 2, my = (ay + by) / 2, nx = -sy / sl * e, ny = sx / sl * e;
                    if (d.FloorAt(mx + nx, my + ny) != d.FloorAt(mx - nx, my - ny))
                        segs.Add(new SightSegment(ax, ay, bx, by));
                }
        return segs;
    }

    private static SightSegment Piece(AngledWall w, double dx, double dy, double len, double from, double to)
        => new(w.Ax + dx * from / len, w.Ay + dy * from / len, w.Ax + dx * to / len, w.Ay + dy * to / len);

    // half unit pieces grouped by line and joined into runs (start, end)
    private static IEnumerable<(int line, List<(int a, int b)> runs)> Runs(HashSet<(int line, int at)> pieces)
    {
        foreach (var g in pieces.GroupBy(p => p.line))
        {
            var runs = new List<(int, int)>();
            int start = int.MinValue, end = int.MinValue;
            foreach (int at in g.Select(p => p.at).OrderBy(v => v))
            {
                if (at == end) { end = at + 1; continue; }
                if (start != int.MinValue) runs.Add((start, end));
                start = at; end = at + 1;
            }
            if (start != int.MinValue) runs.Add((start, end));
            yield return (g.Key, runs);
        }
    }

    /// changes whenever anything sight depends on changes (walls, doors,
    /// floor, pawns), so the drawn region can be cached between frames
    public static long Fingerprint(MapDocument d)
    {
        long h = 17;
        void Mix(long v) => h = unchecked(h * 31 + v);
        Mix(d.Walls.Count); Mix(d.Cells.Count); Mix(d.FloodRegions.Count); Mix(d.AngledWalls.Count); Mix(d.Pawns.Count);
        long sum = 0;
        foreach (var (k, on) in d.Walls) sum = unchecked(sum + (on ? k.GetHashCode() : 7));
        Mix(sum); sum = 0;
        foreach (var c in d.Cells) sum = unchecked(sum + c.GetHashCode());
        Mix(sum); sum = 0;
        foreach (var (k, door) in d.Doors) sum = unchecked(sum + k.GetHashCode() * 3 + (int)door.State);
        foreach (var (k, door) in d.VertexDoors) sum = unchecked(sum + k.GetHashCode() * 3 + (int)door.State);
        foreach (var (w, list) in d.AngledDoors)
            foreach (var door in list) sum = unchecked(sum + w.GetHashCode() * 3 + door.T.GetHashCode() + (int)door.State);
        Mix(sum); sum = 0;
        foreach (var w in d.AngledWalls) sum = unchecked(sum + w.GetHashCode());
        foreach (var fr in d.FloodRegions)
            foreach (var ring in fr.Rings)
                foreach (var p in ring) sum = unchecked(sum + p.GetHashCode());
        Mix(sum); sum = 0;
        foreach (var c in d.Pawns.Keys) sum = unchecked(sum + c.GetHashCode());
        Mix(sum);
        return h;
    }

    // ---- the visible region ----

    /// the segments that can matter within range r of (ox, oy)
    public static List<SightSegment> Near(List<SightSegment> all, double ox, double oy, double r)
        => all.Where(s => SegmentDistance(ox, oy, s.Ax, s.Ay, s.Bx, s.By) <= r + Eps).ToList();

    /// the region visible from (ox, oy) out to range r, as a polygon in angle
    /// order. rays go to every corner (and just either side of it), to where
    /// segments cross the range circle, and evenly around for a round edge.
    public static List<(double x, double y)> Visible(double ox, double oy, double r, List<SightSegment> segs)
    {
        var near = Near(segs, ox, oy, r);
        var angles = new List<double>(RoundRays + near.Count * 6);
        for (int i = 0; i < RoundRays; i++) angles.Add(-Math.PI + 2 * Math.PI * i / RoundRays);
        void Corner(double x, double y)
        {
            double a = Math.Atan2(y - oy, x - ox);
            angles.Add(a); angles.Add(a - Nudge); angles.Add(a + Nudge);
        }
        foreach (var s in near)
        {
            if (Dist(ox, oy, s.Ax, s.Ay) <= r) Corner(s.Ax, s.Ay);
            if (Dist(ox, oy, s.Bx, s.By) <= r) Corner(s.Bx, s.By);
            foreach (var (x, y) in CircleCrossings(ox, oy, r, s)) Corner(x, y);
        }
        for (int i = 0; i < angles.Count; i++)
        {
            // keep every angle in -pi..pi so the sort is one sweep
            double a = angles[i];
            if (a < -Math.PI) angles[i] = a + 2 * Math.PI;
            else if (a > Math.PI) angles[i] = a - 2 * Math.PI;
        }
        angles.Sort();

        var pts = new List<(double, double)>(angles.Count);
        double last = double.NaN;
        foreach (double a in angles)
        {
            if (a - last < 1e-12) continue;
            last = a;
            double dx = Math.Cos(a), dy = Math.Sin(a);
            double t = r;
            foreach (var s in near)
            {
                double hit = RayHit(ox, oy, dx, dy, s);
                if (hit < t) t = hit;
            }
            pts.Add((ox + dx * t, oy + dy * t));
        }
        return pts;
    }

    // ---- pawn shadows ----

    /// the area behind a pawn circle (center cx, cy) as seen from (ox, oy),
    /// out past range r. the near side follows the circle's far edge, so the
    /// pawn's own circle is not in its shadow. null when the viewer is on the
    /// circle.
    public static List<(double x, double y)>? Shadow(double ox, double oy, double cx, double cy, double r, double radius = PawnRadius)
    {
        double d = Dist(ox, oy, cx, cy);
        if (d <= radius + 1e-6) return null;
        double th = Math.Atan2(cy - oy, cx - ox), a = Math.Asin(radius / d);
        var pts = new List<(double, double)>();
        // the circle's far edge between the two tangent points
        double f1 = th - Math.PI / 2 - a, f2 = th + Math.PI / 2 + a;
        const int steps = 24;
        for (int i = 0; i <= steps; i++)
        {
            double f = f1 + (f2 - f1) * i / steps;
            pts.Add((cx + radius * Math.Cos(f), cy + radius * Math.Sin(f)));
        }
        // then out along the tangents, well past the range circle
        double far = (r + d + 2) / Math.Cos(a);
        pts.Add((ox + far * Math.Cos(th + a), oy + far * Math.Sin(th + a)));
        pts.Add((ox + far * Math.Cos(th - a), oy + far * Math.Sin(th - a)));
        return pts;
    }

    /// the angular span a pawn's shadow covers seen from the viewer, to skip
    /// pairs of shadows that can not overlap
    public static (double mid, double half) ShadowSpan(double ox, double oy, double cx, double cy, double radius = PawnRadius)
    {
        double d = Dist(ox, oy, cx, cy);
        return (Math.Atan2(cy - oy, cx - ox), d <= radius ? Math.PI : Math.Asin(radius / d));
    }

    public static bool SpansOverlap((double mid, double half) a, (double mid, double half) b)
    {
        double diff = Math.Abs(Math.IEEERemainder(a.mid - b.mid, 2 * Math.PI));
        return diff < a.half + b.half;
    }

    // ---- one line ----

    /// a straight line from the viewer to (tx, ty). pawns are the circle
    /// centers in the way (not the viewer's or the target's own).
    public static (SightLine result, int pawns) Line(double ox, double oy, double tx, double ty, double r,
        List<SightSegment> segs, IEnumerable<(double x, double y)> pawns)
    {
        double len = Dist(ox, oy, tx, ty);
        if (len > r + Eps) return (SightLine.OutOfRange, 0);
        if (len > Eps)
        {
            double dx = (tx - ox) / len, dy = (ty - oy) / len;
            foreach (var s in segs)
                if (RayHit(ox, oy, dx, dy, s) <= len + Eps) return (SightLine.Blocked, 0);
        }
        int n = 0;
        foreach (var (px, py) in pawns)
            if (Dist(px, py, tx, ty) > PawnRadius && SegmentDistance(px, py, ox, oy, tx, ty) < PawnRadius) n++;
        return (n == 0 ? SightLine.Clear : n == 1 ? SightLine.OnePawn : SightLine.TwoPawns, n);
    }

    // ---- maths ----

    private static double Dist(double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// distance from a ray start along unit (dx, dy) to the segment, or
    /// infinity when it misses. the ends count as hits so corners never leak.
    public static double RayHit(double ox, double oy, double dx, double dy, SightSegment s)
    {
        double ex = s.Bx - s.Ax, ey = s.By - s.Ay;
        double den = dx * ey - dy * ex;
        if (Math.Abs(den) < 1e-12) return double.PositiveInfinity; // parallel
        double qx = s.Ax - ox, qy = s.Ay - oy;
        double t = (qx * ey - qy * ex) / den;
        double u = (qx * dy - qy * dx) / den;
        if (t <= Eps || u < -1e-9 || u > 1 + 1e-9) return double.PositiveInfinity;
        return t;
    }

    /// shortest distance from point p to segment a-b
    public static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double ex = bx - ax, ey = by - ay, l2 = ex * ex + ey * ey;
        double k = l2 < Eps ? 0 : Math.Clamp(((px - ax) * ex + (py - ay) * ey) / l2, 0, 1);
        return Dist(px, py, ax + ex * k, ay + ey * k);
    }

    // where a segment crosses the range circle
    private static IEnumerable<(double x, double y)> CircleCrossings(double ox, double oy, double r, SightSegment s)
    {
        double ex = s.Bx - s.Ax, ey = s.By - s.Ay;
        double fx = s.Ax - ox, fy = s.Ay - oy;
        double a = ex * ex + ey * ey, b = 2 * (fx * ex + fy * ey), c = fx * fx + fy * fy - r * r;
        double disc = b * b - 4 * a * c;
        if (a < Eps || disc < 0) yield break;
        double sq = Math.Sqrt(disc);
        foreach (double k in new[] { (-b - sq) / (2 * a), (-b + sq) / (2 * a) })
            if (k >= 0 && k <= 1) yield return (s.Ax + ex * k, s.Ay + ey * k);
    }
}
