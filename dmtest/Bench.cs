using System.Diagnostics;
using Avalonia;
using DungeonMapper.Controls;
using DungeonMapper.Models;

/// Performance probe for the "massive room slows the app even off-screen" report.
/// Measures per-frame Render cost and per-Commit cost as the document grows,
/// with the big room visible vs. panned fully off-screen. Run with:
///   dotnet run --project dmtest -- bench
public static class Bench
{
    static void ClearDoc(MapDocument d)
    {
        d.Cells.Clear(); d.Walls.Clear(); d.Doors.Clear(); d.Stairs.Clear();
        d.Labels.Clear(); d.Pawns.Clear(); d.AngledWalls.Clear();
        d.AngledDoors.Clear(); d.VertexDoors.Clear();
    }

    static double TimeRender(CanvasHarness h, int frames)
    {
        h.Render(); // warmup: JIT + first-render costs
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++) h.Render();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / frames;
    }

    // line of sight on a busy map: a 60x60 grid of 4x4 rooms with doorways
    // and a pawn in every room, 240 ft range
    static void SightBench()
    {
        var d = new MapDocument();
        for (int ry = 0; ry < 15; ry++)
            for (int rx = 0; rx < 15; rx++)
            {
                d.AddRoom(rx * 4, ry * 4, rx * 4 + 3, ry * 4 + 3);
                d.Pawns[new CellKey(rx * 4 + 1, ry * 4 + 1)] = new Pawn { Cell = new CellKey(rx * 4 + 1, ry * 4 + 1), TemplateId = "p" };
            }
        for (int ry = 0; ry < 15; ry++)
            for (int rx = 0; rx < 14; rx++)
                d.TryPlaceDoor(new EdgeKey(new CellKey(rx * 4 + 3, ry * 4 + 2), Side.East), "standard", "open", out _);
        var sw = Stopwatch.StartNew();
        var segs = Sight.Blockers(d);
        double tb = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var v = Sight.Visible(30.5, 30.5, 48, segs);
        double tv = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        int n = 0;
        foreach (var c in d.Pawns.Keys) if (Sight.Shadow(30.5, 30.5, c.X + 0.5, c.Y + 0.5, 48) is not null) n++;
        double ts = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"sight: blockers {segs.Count} segs {tb:F1} ms, region {v.Count} pts {tv:F1} ms, {n} shadows {ts:F1} ms");
        sw.Restart();
        var fp = Sight.Fingerprint(d);
        Console.WriteLine($"sight: fingerprint {sw.Elapsed.TotalMilliseconds:F2} ms");
        using var h = new CanvasHarness();
        h.Canvas.Library.Pawns.Add(new PawnTemplate { Id = "p", Name = "P" });
        h.Canvas.Document = d;
        h.Canvas.ViewScale = 10;
        h.Canvas.Mode = ToolMode.Sight;
        h.Canvas.SightFeet = 240;
        Console.WriteLine($"sight: render without {TimeRender(h, 10):F1} ms/frame");
        h.Canvas.ShowSight(new CellKey(30, 30));
        Console.WriteLine($"sight: render with sight {TimeRender(h, 10):F1} ms/frame");
    }

    public static void Run()
    {
        SightBench(); SightBench();
        using var h = new CanvasHarness();

        Console.WriteLine($"empty doc (0 cells)              {TimeRender(h, 10):F1} ms/frame");

        h.Doc.AddRoom(0, 0, 1, 1);
        Console.WriteLine($"2x2 room (4 cells, 8 walls)      {TimeRender(h, 10):F1} ms/frame");

        h.Doc.AddRoom(0, 0, 99, 99);
        Console.WriteLine($"100x100 room (10k cells, 400 walls), partially visible {TimeRender(h, 10):F1} ms/frame");

        // pan the big room fully off-screen: 6 right-drags of 998px at scale 40
        // move offset.X from -1 to ~148.7; the room (x 0..100) sits entirely left
        // of the visible window (x 148.7..173.7).
        for (int i = 0; i < 6; i++) h.PanBy(998, 0);
        Console.WriteLine($"same doc, room fully OFF-screen  {TimeRender(h, 10):F1} ms/frame");

        // second 100x100 room inside the NEW viewport (partially visible), so the
        // doc has 20k cells with 10k visible and 10k off-screen
        h.Doc.AddRoom(149, -100, 248, 0);
        Console.WriteLine($"two 100x100 rooms (20k cells), one visible one off-screen {TimeRender(h, 10):F1} ms/frame");

        // commit cost: full-document JSON snapshot per action
        var hist = new History();
        hist.Initialize(h.Doc);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) hist.Commit(h.Doc);
        sw.Stop();
        Console.WriteLine($"History.Commit on 20k-cell doc   {sw.Elapsed.TotalMilliseconds / 10:F1} ms/commit");

        var small = new MapDocument();
        small.AddRoom(0, 0, 1, 1);
        var h2 = new History(); h2.Initialize(small);
        sw.Restart();
        for (int i = 0; i < 10; i++) h2.Commit(small);
        sw.Stop();
        Console.WriteLine($"History.Commit on 4-cell doc     {sw.Elapsed.TotalMilliseconds / 10:F2} ms/commit");

        // drag interaction on the big doc: press, 20 moves, release, rendering per
        // move exactly like the live app (every pointer move InvalidateVisuals).
        // The 10k-cell room is fully off-screen; the drag happens on empty space.
        ClearDoc(h.Doc);
        h.Doc.AddRoom(0, 0, 99, 99);
        for (int i = 0; i < 6; i++) h.PanBy(998, 0);
        // current transform: offset (148.7, -1), scale 40. cell (cx,cy) -> screen
        // ((cx-148.7)*40, (cy+1)*40). cell (150,5) -> (52,240), horizontal drag.
        h.Canvas.Mode = ToolMode.Room;
        h.Render(); // warmup: JIT of the drag/preview path
        sw.Restart();
        h.PressAt(new Point(52, 240)); h.Render();
        double tPress = sw.Elapsed.TotalMilliseconds;
        for (int i = 1; i <= 20; i++) { h.MoveToAt(new Point(52 + 40 * i, 240)); h.Render(); }
        double tMoves = sw.Elapsed.TotalMilliseconds - tPress;
        h.ReleaseAt(new Point(852, 240)); h.Render();
        double tRelease = sw.Elapsed.TotalMilliseconds - tMoves - tPress;
        Console.WriteLine($"22-event room drag on 10k-cell doc (room off-screen): press+render {tPress:F1} ms, 20 moves+renders {tMoves:F1} ms ({tMoves / 20:F1} ms per move), release+render {tRelease:F1} ms");

        // same drag on an empty doc: if per-move cost matches, the drag cost is
        // NOT document-size dependent (the 10k off-screen room costs nothing).
        ClearDoc(h.Doc);
        h.Render();
        sw.Restart();
        h.PressAt(new Point(52, 240)); h.Render();
        double tPress2 = sw.Elapsed.TotalMilliseconds;
        for (int i = 1; i <= 20; i++) { h.MoveToAt(new Point(52 + 40 * i, 240)); h.Render(); }
        double tMoves2 = sw.Elapsed.TotalMilliseconds - tPress2;
        h.ReleaseAt(new Point(852, 240)); h.Render();
        Console.WriteLine($"same drag on EMPTY doc: 20 moves+renders {tMoves2:F1} ms ({tMoves2 / 20:F1} ms per move)");
    }
}
