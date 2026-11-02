using DungeonMapper.Models;
using DungeonMapper.Controls;
using Avalonia;

if (args.Length > 0 && args[0] == "bench") { Bench.Run(); return 0; }

int pass = 0, fail = 0;
void Check(bool cond, string name)
{
    if (cond) { pass++; Console.WriteLine($"PASS: {name}"); }
    else { fail++; Console.WriteLine($"FAIL: {name}"); }
}

// delegates to the real MapDocument.AddRoom
void AddRoom(MapDocument doc, int x0, int y0, int x1, int y1)
{
    doc.AddRoom(x0, y0, x1, y1);
}

// --- edge normalization ---
var south = EdgeKey.Normalize(new CellKey(3, 5), Side.South);
Check(south.Cell == new CellKey(3, 6) && south.Side == Side.North, "south edge folds to north of cell BELOW (true bottom)");
var west = EdgeKey.Normalize(new CellKey(3, 5), Side.West);
Check(west.Cell == new CellKey(2, 5) && west.Side == Side.East, "west edge folds to east of cell left");
var north = EdgeKey.Normalize(new CellKey(3, 5), Side.North);
Check(north.Cell == new CellKey(3, 5) && north.Side == Side.North, "north edge stays put");

// --- room auto-walls: 4x8 room at (0,0) ---
var doc = new MapDocument();
AddRoom(doc, 0, 0, 3, 7);
Check(doc.Cells.Count == 32, "room creates 32 cells");
Check(doc.Walls.Count == 24, "room perimeter walls (4*2 + 8*2 = 24)");

// THE BUG: bottom wall of last row (y=7) must be at the TRUE bottom edge (row 8),
// not bisecting at row 6.
Check(doc.HasWall(new EdgeKey(new CellKey(0, 7), Side.South)), "bottom wall of last row exists");
Check(doc.HasWall(new EdgeKey(new CellKey(3, 7), Side.South)), "bottom wall of last row, rightmost cell");
// the normalized key must be North of row 8, not row 6
var bottomKey = EdgeKey.Normalize(new CellKey(0, 7), Side.South);
Check(bottomKey.Cell == new CellKey(0, 8) && bottomKey.Side == Side.North, "bottom wall normalized to row 8 (true bottom, not bisecting row 6)");
Check(doc.HasWall(new EdgeKey(new CellKey(0, 0), Side.North)), "top wall exists");
Check(doc.HasWall(new EdgeKey(new CellKey(0, 0), Side.West)), "left wall exists");
Check(doc.HasWall(new EdgeKey(new CellKey(3, 0), Side.East)), "right wall exists");
// interior edges must have no walls
Check(!doc.HasWall(new EdgeKey(new CellKey(1, 1), Side.North)), "interior north edge has no wall");
Check(!doc.HasWall(new EdgeKey(new CellKey(1, 1), Side.South)), "interior south edge has no wall");

// --- boolean union: drawing a room over adjacent rooms removes interior walls ---
var doc2 = new MapDocument();
AddRoom(doc2, 0, 0, 1, 1);
AddRoom(doc2, 1, 0, 2, 1);
// shared wall between the two rooms exists at x=1 (East of cell 0)
Check(doc2.HasWall(new EdgeKey(new CellKey(0, 0), Side.East)), "shared wall exists before union");
Check(doc2.HasWall(new EdgeKey(new CellKey(0, 1), Side.East)), "shared wall exists before union (row 1)");
// drag a room spanning [0,0]-[2,1]: union
AddRoom(doc2, 0, 0, 2, 1);
Check(doc2.Cells.Count == 6, "union fills 6 cells");
Check(!doc2.HasWall(new EdgeKey(new CellKey(0, 0), Side.East)), "interior wall x=1 removed (row 0)");
Check(!doc2.HasWall(new EdgeKey(new CellKey(0, 1), Side.East)), "interior wall x=1 removed (row 1)");
Check(!doc2.HasWall(new EdgeKey(new CellKey(1, 0), Side.East)), "interior wall x=2 removed (row 0)");
Check(!doc2.HasWall(new EdgeKey(new CellKey(1, 1), Side.East)), "interior wall x=2 removed (row 1)");
// outer boundary walls remain
Check(doc2.HasWall(new EdgeKey(new CellKey(0, 0), Side.North)), "union top wall exists");
Check(doc2.HasWall(new EdgeKey(new CellKey(0, 1), Side.South)), "union bottom wall exists");
Check(doc2.HasWall(new EdgeKey(new CellKey(0, 0), Side.West)), "union left wall exists");
Check(doc2.HasWall(new EdgeKey(new CellKey(2, 0), Side.East)), "union right wall exists");

// --- door requires wall ---
Check(doc.TryPlaceDoor(new EdgeKey(new CellKey(1, 0), Side.North), out _), "door on wall edge succeeds");
Check(!doc.TryPlaceDoor(new EdgeKey(new CellKey(1, 1), Side.North), out _), "door on interior edge fails");

// --- copy/paste region ---
{
    var cp = new MapDocument();
    cp.AddRoom(0, 0, 1, 1);
    cp.TryPlaceDoor(EdgeKey.Normalize(new CellKey(0, 0), Side.North), out _); // outer wall edge
    cp.Stairs[new CellKey(0, 0)] = new Stair { Direction = StairDirection.Up };
    cp.Labels.Add(new MapLabel { Cell = new CellKey(1, 1), Text = "X" });

    var snap = cp.CaptureRegion(0, 0, 1, 1);
    Check(snap.Cells.Count == 4, "capture: cells");
    Check(snap.Walls.Count == 8, "capture: walls (2x2 room = 8 outer walls)");
    Check(snap.Doors.Count == 1, "capture: door");
    Check(snap.Stairs.Count == 1, "capture: stair");
    Check(snap.Labels.Count == 1, "capture: label");

    // paste at a new origin, far away -> full copy
    cp.PasteRegion(snap, 5, 5);
    Check(cp.Cells.Contains(new CellKey(5, 5)) && cp.Cells.Contains(new CellKey(6, 6)), "paste: cells offset");
    Check(cp.Doors.ContainsKey(EdgeKey.Normalize(new CellKey(5, 5), Side.North)), "paste: door offset");
    Check(cp.Stairs.TryGetValue(new CellKey(5, 5), out var ps) && ps.Direction == StairDirection.Up, "paste: stair offset");
    Check(cp.Labels.Any(l => l.Cell == new CellKey(6, 6) && l.Text == "X"), "paste: label offset");
    Check(cp.HasWall(EdgeKey.Normalize(new CellKey(5, 5), Side.North)), "paste: door implies wall");

    // paste overlapping the original at same origin -> idempotent (no growth)
    int cellsBefore = cp.Cells.Count;
    cp.PasteRegion(snap, 0, 0);
    Check(cp.Cells.Count == cellsBefore, "paste: same origin idempotent");
}

// --- floor tool ---
{
    var f = new MapDocument();
    f.AddRoom(0, 0, 2, 2); // room with walls all around
    f.Stairs[new CellKey(1, 1)] = new Stair { Direction = StairDirection.Up };
    f.Labels.Add(new MapLabel { Cell = new CellKey(1, 1), Text = "x" });
    f.TryPlaceDoor(EdgeKey.Normalize(new CellKey(0, 0), Side.North), out _); // door on boundary wall

    f.AddFloor(0, 0, 2, 2);
    // cells filled
    Check(f.Cells.Contains(new CellKey(0, 0)) && f.Cells.Contains(new CellKey(2, 2)), "floor: cells filled");
    // boundary walls preserved
    Check(f.HasWall(EdgeKey.Normalize(new CellKey(0, 0), Side.North)), "floor: boundary wall kept");
    Check(f.HasWall(EdgeKey.Normalize(new CellKey(2, 2), Side.South)), "floor: boundary wall kept (south)");
    Check(f.HasWall(EdgeKey.Normalize(new CellKey(0, 0), Side.West)), "floor: boundary wall kept (west)");
    // interior walls removed
    Check(!f.HasWall(EdgeKey.Normalize(new CellKey(1, 0), Side.East)), "floor: interior wall removed");
    Check(!f.HasWall(EdgeKey.Normalize(new CellKey(1, 1), Side.North)), "floor: interior wall removed (north)");
    // interior stair + label cleared
    Check(!f.Stairs.ContainsKey(new CellKey(1, 1)), "floor: interior stair removed");
    Check(!f.Labels.Any(l => l.Cell == new CellKey(1, 1)), "floor: interior label removed");
    // door on boundary wall survives (boundary wall kept)
    Check(f.Doors.ContainsKey(EdgeKey.Normalize(new CellKey(0, 0), Side.North)), "floor: boundary door kept");

    // floor on empty region draws NO walls
    var g = new MapDocument();
    g.AddFloor(3, 3, 4, 4);
    Check(g.Cells.Count == 4, "floor: empty region fills cells");
    Check(g.Walls.Count == 0, "floor: empty region draws no walls");
}

// --- connected stairs ---
{
    var s = new MapDocument();
    s.Stairs[new CellKey(0, 0)] = new Stair { Direction = StairDirection.Down };
    s.Stairs[new CellKey(1, 0)] = new Stair { Direction = StairDirection.Down };
    s.Stairs[new CellKey(2, 0)] = new Stair { Direction = StairDirection.Down };
    s.Stairs[new CellKey(5, 5)] = new Stair { Direction = StairDirection.Up }; // isolated
    var group = s.ConnectedStairs(new CellKey(0, 0));
    Check(group.Count == 3, "connected stairs: 3-cell run found");
    Check(!group.Contains(s.Stairs[new CellKey(5, 5)]), "connected stairs: isolated stair excluded");
    foreach (var st in group) st.Direction = StairDirection.Up;
    Check(s.Stairs[new CellKey(2, 0)].Direction == StairDirection.Up, "connected stairs: far end updated");
    Check(s.Stairs[new CellKey(5, 5)].Direction == StairDirection.Up, "connected stairs: isolated untouched by group apply");
    Check(s.ConnectedStairs(new CellKey(9, 9)).Count == 0, "connected stairs: empty for non-stair cell");
}

// --- connected stairs: walls block the flood fill ---
{
    var sw = new MapDocument();
    for (int x = 0; x < 12; x++) sw.Stairs[new CellKey(x, 0)] = new Stair { Direction = StairDirection.Down };
    sw.SetWall(EdgeKey.Normalize(new CellKey(5, 0), Side.East), true); // wall between cells 5 and 6
    var left = sw.ConnectedStairs(new CellKey(0, 0));
    var right = sw.ConnectedStairs(new CellKey(11, 0));
    Check(left.Count == 6, "connected stairs: wall splits row, left segment is 6");
    Check(right.Count == 6, "connected stairs: wall splits row, right segment is 6");
    Check(!left.Contains(sw.Stairs[new CellKey(6, 0)]), "connected stairs: flood does not cross wall");
    sw.SetWall(EdgeKey.Normalize(new CellKey(5, 0), Side.East), false); // wall removed
    Check(sw.ConnectedStairs(new CellKey(0, 0)).Count == 12, "connected stairs: whole row after wall removed");
    // wall + door on the shared edge also blocks (door implies wall; the wall
    // is the blocker)
    var sd = new MapDocument();
    sd.Stairs[new CellKey(0, 0)] = new Stair { Direction = StairDirection.Down };
    sd.Stairs[new CellKey(1, 0)] = new Stair { Direction = StairDirection.Down };
    sd.SetWall(EdgeKey.Normalize(new CellKey(0, 0), Side.East), true);
    sd.Doors[EdgeKey.Normalize(new CellKey(0, 0), Side.East)] = new Door { Type = "standard", State = DoorState.Open };
    Check(sd.ConnectedStairs(new CellKey(0, 0)).Count == 1, "connected stairs: wall with open door still blocks");
}

// --- erase filter ---
{
    var e = new MapDocument();
    e.AddRoom(0, 0, 1, 1); // cells + walls
    e.Stairs[new CellKey(0, 0)] = new Stair { Direction = StairDirection.Up };
    e.Labels.Add(new MapLabel { Cell = new CellKey(0, 0), Text = "x" });

    // walls-only filter: erase removes walls, keeps cells/stairs/labels
    var f = new EraseFilter { Cells = false, Walls = true, Stairs = false, Labels = false };
    // simulate the erase handler over region [0,0]-[1,1]
    for (int x = 0; x <= 1; x++)
        for (int y = 0; y <= 1; y++)
        {
            var c = new CellKey(x, y);
            if (f.Cells) e.Cells.Remove(c);
            if (f.Stairs) e.Stairs.Remove(c);
            if (f.Labels) e.Labels.RemoveAll(l => l.Cell == c);
            if (f.Walls)
                foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                    e.SetWall(new EdgeKey(c, side), false);
        }
    Check(e.Cells.Count > 0, "erase filter: cells kept when Cells off");
    Check(e.Stairs.ContainsKey(new CellKey(0, 0)), "erase filter: stair kept when Stairs off");
    Check(e.Labels.Any(l => l.Cell == new CellKey(0, 0)), "erase filter: label kept when Labels off");
    Check(e.Walls.Count == 0, "erase filter: walls removed when Walls on");

    // stairs-only filter: erase removes stairs, keeps walls
    var g = new MapDocument();
    g.AddRoom(0, 0, 1, 1);
    g.Stairs[new CellKey(0, 0)] = new Stair { Direction = StairDirection.Up };
    var f2 = new EraseFilter { Cells = false, Walls = false, Stairs = true, Labels = false };
    for (int x = 0; x <= 1; x++)
        for (int y = 0; y <= 1; y++)
        {
            var c = new CellKey(x, y);
            if (f2.Cells) g.Cells.Remove(c);
            if (f2.Stairs) g.Stairs.Remove(c);
            if (f2.Labels) g.Labels.RemoveAll(l => l.Cell == c);
            if (f2.Walls)
                foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                    g.SetWall(new EdgeKey(c, side), false);
        }
    Check(!g.Stairs.ContainsKey(new CellKey(0, 0)), "erase filter: stair removed when Stairs on");
    Check(g.Walls.Count > 0, "erase filter: walls kept when Walls off");
}

// --- door defaults + cut source clear ---
{
    var d = new MapDocument();
    d.AddRoom(0, 0, 1, 1);
    var edge = new EdgeKey(new CellKey(0, 0), Side.North);
    // place door with defaults "open"
    bool ok = d.TryPlaceDoor(edge, "magical", "open", out var door);
    Check(ok, "door defaults: placed on wall");
    Check(door.Type == "magical", "door defaults: type applied");
    Check(door.State == DoorState.Open, "door defaults: state applied");

    // existing door keeps its own type/state, not the defaults
    d.TryPlaceDoor(edge, "standard", "closed", out var door2);
    Check(ReferenceEquals(door, door2), "door defaults: existing door reused");
    Check(door2.Type == "magical", "door defaults: existing type not overwritten");

    // cut source clear: simulate paste-then-delete-source
    var src = new MapDocument();
    src.AddRoom(0, 0, 1, 1);
    var snap = src.CaptureRegion(0, 0, 1, 1);
    var dst = new MapDocument();
    dst.PasteRegion(snap, 5, 5);
    Check(dst.Cells.Contains(new CellKey(5, 5)), "cut: paste lands at target");
    // delete source region from src
    for (int x = 0; x <= 1; x++)
        for (int y = 0; y <= 1; y++)
        {
            var c = new CellKey(x, y);
            src.Cells.Remove(c);
            src.Stairs.Remove(c);
            src.Labels.RemoveAll(l => l.Cell == c);
            foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                src.SetWall(new EdgeKey(c, side), false);
        }
    Check(src.Cells.Count == 0, "cut: source region cleared");
    Check(dst.Cells.Contains(new CellKey(5, 5)), "cut: pasted copy survives source clear");
}

// --- settings model ---
{
    var s = AppSettings.Default();
    Check(s.GridDrawSpacing == 1, "settings: default grid spacing 1");
    Check(s.ToolHotkeys["Room"] == "R", "settings: room hotkey R");
    Check(s.DoorTypes.Count == 4, "settings: 4 default door types (locked type removed)");
    Check(!s.DoorTypes.Any(t => t.Name == "locked"), "settings: no locked door type (state covers it)");
    Check(s.FindDoorType("portcullis").R == 180, "settings: door color lookup");
    Check(s.FindDoorType("nonexistent").Name == "standard", "settings: unknown type falls back");
    Check(s.DefaultLabelText == "label", "settings: default label text non-blank");
}

// --- floor mod (grid spacing boundary for negative coords) ---
{
    int fm(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }
    Check(fm(0,2)==0 && fm(2,2)==0 && fm(4,2)==0, "floor mod: positive boundaries at spacing 2");
    Check(fm(1,2)==1 && fm(3,2)==1, "floor mod: non-boundaries");
    Check(fm(-2,2)==0 && fm(-4,2)==0, "floor mod: negative boundaries at spacing 2");
    Check(fm(-1,2)==1, "floor mod: negative non-boundary");
}

// --- move (capture + paste at target + clear source) ---
{
    var m = new MapDocument();
    m.AddRoom(0, 0, 1, 1);
    m.Stairs[new CellKey(0, 0)] = new Stair { Direction = StairDirection.Up };
    var snap = m.CaptureRegion(0, 0, 1, 1);
    // move to (5,5): paste then clear source
    m.PasteRegion(snap, 5, 5);
    for (int x = 0; x <= 1; x++)
        for (int y = 0; y <= 1; y++)
        {
            var c = new CellKey(x, y);
            m.Cells.Remove(c);
            m.Stairs.Remove(c);
            m.Labels.RemoveAll(l => l.Cell == c);
            foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                m.SetWall(new EdgeKey(c, side), false);
        }
    Check(m.Cells.Contains(new CellKey(5, 5)), "move: region lands at target");
    Check(m.Stairs.ContainsKey(new CellKey(5, 5)), "move: stair moved to target");
    Check(!m.Cells.Contains(new CellKey(0, 0)), "move: source cleared");
    Check(!m.Stairs.ContainsKey(new CellKey(0, 0)), "move: source stair cleared");
}

// --- room passageway (grow out of existing room keeps shared edge open) ---
{
    bool W(MapDocument m, int x, int y, Side side)
    {
        var k = EdgeKey.Normalize(new CellKey(x, y), side);
        return m.Walls.ContainsKey(k) && m.Walls[k];
    }
    var m = new MapDocument();
    m.AddRoom(0, 0, 2, 2);
    // grow a hallway out of the room: start inside (2,1), drag east to (4,1)
    m.AddRoom(2, 1, 4, 1, passageway: true);
    Check(m.Cells.Contains(new CellKey(3, 1)) && m.Cells.Contains(new CellKey(4, 1)), "passageway: new cells added");
    // the new region's WEST boundary at (2,1) touches existing room cell (1,1):
    // passageway leaves it open so the hallway connects to the room
    Check(!W(m, 2, 1, Side.West), "passageway: shared boundary open");
    // far end of hallway is walled
    Check(W(m, 4, 1, Side.East), "passageway: far end walled");
    // top/bottom of hallway walled
    Check(W(m, 3, 1, Side.North), "passageway: hallway top walled");
    Check(W(m, 3, 1, Side.South), "passageway: hallway bottom walled");
    // non-passageway room over same spot seals the shared boundary
    var m2 = new MapDocument();
    m2.AddRoom(0, 0, 2, 2);
    m2.AddRoom(2, 1, 4, 1, passageway: false);
    Check(W(m2, 2, 1, Side.West), "non-passageway: shared boundary walled");
}

// --- multi-map project round-trip ---
{
    var proj = new MapProject();
    proj.Maps[0].Name = "Cavern";
    proj.Maps[0].Doc.AddRoom(0, 0, 1, 1);
    proj.AddMap("Upper");
    proj.Maps[1].Doc.AddRoom(3, 3, 4, 4);
    proj.Maps[1].Doc.Stairs[new CellKey(3, 3)] = new Stair { Direction = StairDirection.Up, TargetMap = "Cavern" };
    proj.SetActive(0);
    string pjson = MapSerializer.ProjectToJson(proj);
    var back = MapSerializer.ProjectFromJson(pjson);
    Check(back.Maps.Count == 2, "project: two maps round-trip");
    Check(back.Maps[0].Name == "Cavern" && back.Maps[1].Name == "Upper", "project: names round-trip");
    Check(back.ActiveIndex == 0, "project: active index round-trip");
    Check(back.Maps[0].Doc.Cells.Contains(new CellKey(0, 0)), "project: map0 cells round-trip");
    Check(back.Maps[1].Doc.Cells.Contains(new CellKey(4, 4)), "project: map1 cells round-trip");
    Check(back.Maps[1].Doc.Stairs[new CellKey(3, 3)].TargetMap == "Cavern", "project: stair target map round-trip");
}

// --- label per-instance size + color round-trip ---
{
    var lm = new MapDocument();
    lm.Labels.Add(new MapLabel { Cell = new CellKey(2, 3), Text = "L", FontSize = 24, R = 200, G = 10, B = 50 });
    lm.Labels.Add(new MapLabel { Cell = new CellKey(4, 4), Text = "inherit" });
    string ljson = MapSerializer.ToJson(lm);
    var lback = MapSerializer.FromJson(ljson);
    var l1 = lback.Labels.First(l => l.Cell == new CellKey(2, 3));
    var l2 = lback.Labels.First(l => l.Cell == new CellKey(4, 4));
    Check(l1.FontSize == 24 && l1.R == 200 && l1.G == 10 && l1.B == 50, "label: size + color round-trip");
    Check(l2.FontSize == 0 && l2.R == -1 && l2.G == -1 && l2.B == -1, "label: inherit defaults round-trip");
}

// --- delete hotkey (erase selection with all categories on) ---
{
    var d = new MapDocument();
    d.AddRoom(0, 0, 1, 1);
    d.Stairs[new CellKey(0, 0)] = new Stair { Direction = StairDirection.Up };
    d.Labels.Add(new MapLabel { Cell = new CellKey(1, 1), Text = "x" });
    // simulate DeleteSelection over [0,0]-[1,1]: ClearRegion removes everything
    for (int x = 0; x <= 1; x++)
        for (int y = 0; y <= 1; y++)
        {
            var c = new CellKey(x, y);
            d.Cells.Remove(c);
            d.Stairs.Remove(c);
            d.Labels.RemoveAll(l => l.Cell == c);
            foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                d.SetWall(new EdgeKey(c, side), false);
        }
    Check(d.Cells.Count == 0, "delete: cells removed");
    Check(d.Walls.Count == 0, "delete: walls removed (all categories on)");
    Check(d.Stairs.Count == 0, "delete: stairs removed");
    Check(d.Labels.Count == 0, "delete: labels removed");
}

// --- cross-map cut (cut source deletes from ITS map, not the target map) ---
{
    var map1 = new MapDocument();
    var map2 = new MapDocument();
    map1.AddRoom(0, 0, 1, 1);
    var snap = map1.CaptureRegion(0, 0, 1, 1);
    var cutSource = (0, 0, 1, 1);
    MapDocument? cutSourceDoc = map1;

    // paste into map2: source deletes from map1 (its own map), map2 keeps paste
    map2.PasteRegion(snap, 5, 5);
    if (cutSourceDoc == map1)
        for (int x = 0; x <= 1; x++)
            for (int y = 0; y <= 1; y++)
            {
                var c = new CellKey(x, y);
                map1.Cells.Remove(c);
                map1.Stairs.Remove(c);
                map1.Labels.RemoveAll(l => l.Cell == c);
                foreach (var side in new[] { Side.North, Side.East, Side.South, Side.West })
                    map1.SetWall(new EdgeKey(c, side), false);
            }
    Check(map2.Cells.Count == 4, "cross-map cut: paste lands on map2, map2 untouched elsewhere");
    Check(!map1.Cells.Contains(new CellKey(0, 0)), "cross-map cut: source deleted from its own map");
    Check(map2.Cells.Contains(new CellKey(5, 5)), "cross-map cut: pasted cells on map2");

    // cross-map move: capture from map1, paste on map2, clear map1 source
    var map1b = new MapDocument();
    var map2b = new MapDocument();
    map1b.AddRoom(2, 2, 3, 3);
    var snap2 = map1b.CaptureRegion(2, 2, 3, 3);
    map2b.PasteRegion(snap2, 8, 8);
    for (int x = 2; x <= 3; x++)
        for (int y = 2; y <= 3; y++)
            map1b.Cells.Remove(new CellKey(x, y));
    Check(map1b.Cells.Count == 0, "cross-map move: source cleared on map1");
    Check(map2b.Cells.Count == 4, "cross-map move: region lands on map2");
    Check(map2b.Cells.Contains(new CellKey(8, 8)), "cross-map move: origin maps to target cell");
}

// --- room strict toggle (sub-room inside a room gets walls when strict) ---
{
    bool W(MapDocument m, int x, int y, Side side)
    {
        var k = EdgeKey.Normalize(new CellKey(x, y), side);
        return m.Walls.ContainsKey(k) && m.Walls[k];
    }
    // passageway (auto when drag starts inside a room): sub-room fully inside a
    // room has every boundary edge open -> no walls, sub-room invisible.
    var m = new MapDocument();
    m.AddRoom(0, 0, 3, 3);
    m.AddRoom(1, 1, 2, 2, passageway: true, strict: false);
    Check(!W(m, 1, 1, Side.North), "passageway: sub-room top wall open (invisible sub-room)");
    Check(!W(m, 1, 1, Side.West), "passageway: sub-room left wall open");

    // strict overrides passageway: outer walls drawn regardless
    var m2 = new MapDocument();
    m2.AddRoom(0, 0, 3, 3);
    m2.AddRoom(1, 1, 2, 2, passageway: true, strict: true);
    Check(W(m2, 1, 1, Side.North), "strict on: sub-room top wall drawn inside room");
    Check(W(m2, 1, 1, Side.West), "strict on: sub-room left wall drawn inside room");
    Check(W(m2, 2, 2, Side.South), "strict on: sub-room bottom wall drawn inside room");
    Check(W(m2, 2, 2, Side.East), "strict on: sub-room right wall drawn inside room");
    // strict over an existing room: outer border walled regardless
    var m3 = new MapDocument();
    m3.AddRoom(0, 0, 1, 1);
    m3.AddRoom(0, 0, 1, 1, passageway: false, strict: true);
    Check(W(m3, 0, 0, Side.North), "strict on: outer border walled over existing room");
}

// --- serialization round-trip ---
doc.Stairs[new CellKey(2, 3)] = new Stair { Direction = StairDirection.Down };
doc.Labels.Add(new MapLabel { Cell = new CellKey(1, 1), Text = "throne room" });
string json = MapSerializer.ToJson(doc);
var loaded = MapSerializer.FromJson(json);
Check(loaded.Cells.SetEquals(doc.Cells), "round-trip cells");
Check(loaded.Walls.Keys.ToHashSet().SetEquals(doc.Walls.Keys), "round-trip walls");
Check(loaded.Doors.Count == doc.Doors.Count, "round-trip door count");
Check(loaded.Stairs.Count == doc.Stairs.Count, "round-trip stair count");
Check(loaded.Labels.Count == doc.Labels.Count, "round-trip label count");
Check(loaded.Stairs[new CellKey(2, 3)].Direction == StairDirection.Down, "stair direction preserved");
Check(loaded.Labels[0].Text == "throne room", "label text preserved");

// --- undo/redo ---
var history = new History();
history.Initialize(doc);
doc.SetWall(new EdgeKey(new CellKey(0, 4), Side.South), true);
history.Commit(doc);
Check(doc.HasWall(new EdgeKey(new CellKey(0, 4), Side.South)), "wall added");
var afterUndo = history.Undo();
doc.ApplyFrom(afterUndo!);
Check(!doc.HasWall(new EdgeKey(new CellKey(0, 4), Side.South)), "undo removes wall");
var afterRedo = history.Redo();
doc.ApplyFrom(afterRedo!);
Check(doc.HasWall(new EdgeKey(new CellKey(0, 4), Side.South)), "redo restores wall");

// --- pawns (hard-reference model: pawn stores a TemplateId, visuals resolve from library) ---
{
    var lib = new PawnLibrary();
    var t = new PawnTemplate { Name = "Skeleton", Affiliation = "Enemy", Status = "paralyzed" };
    t.ApplyAffiliationDefaults();
    lib.Add(t);
    Check(t.Id == "p1", "library assigns stable Id");

    var m = new MapDocument();
    var p = new Pawn { Cell = new CellKey(2, 2), TemplateId = t.Id };
    m.PlacePawn(p);
    Check(m.Pawns.Count == 1, "pawn placed");
    Check(m.Pawns[new CellKey(2, 2)].TemplateId == t.Id, "pawn stores template reference");
    Check(lib.Find(t.Id)!.OuterR == 220, "enemy outer color default");

    // capture/paste carries the template reference (before the stacking replace below)
    m.AddRoom(0, 0, 3, 3);
    var snap = m.CaptureRegion(0, 0, 3, 3);
    Check(snap.Pawns.Count == 1, "capture includes pawn");
    var m2 = new MapDocument();
    m2.PasteRegion(snap, 5, 5);
    Check(m2.Pawns.ContainsKey(new CellKey(7, 7)), "paste offsets pawn");
    Check(m2.Pawns[new CellKey(7, 7)].TemplateId == t.Id, "paste preserves template reference");

    // per-instance status override: null inherits, set overrides, paste + round-trip carry it
    var ps = new Pawn { Cell = new CellKey(2, 2), TemplateId = t.Id, Status = "alert" };
    m.PlacePawn(ps);
    Check(m.Pawns[new CellKey(2, 2)].Status == "alert", "pawn stores per-instance status");
    var snapS = m.CaptureRegion(0, 0, 3, 3);
    var m3St = new MapDocument();
    m3St.PasteRegion(snapS, 5, 5);
    Check(m3St.Pawns[new CellKey(7, 7)].Status == "alert", "paste carries per-instance status");
    var jsonSt = MapSerializer.ToJson(m);
    var m4St = MapSerializer.FromJson(jsonSt);
    Check(m4St.Pawns[new CellKey(2, 2)].Status == "alert", "status round-trips through json");
    // null status round-trips as inherit (fresh doc so the shared map stays clean)
    var m6St = new MapDocument();
    m6St.PlacePawn(new Pawn { Cell = new CellKey(1, 1), TemplateId = t.Id });
    var m7St = MapSerializer.FromJson(MapSerializer.ToJson(m6St));
    Check(m7St.Pawns[new CellKey(1, 1)].Status is null, "null status round-trips as inherit");

    // no stacking: placing on an occupied cell replaces
    var p2 = new Pawn { Cell = new CellKey(2, 2), TemplateId = "p9" };
    m.PlacePawn(p2);
    Check(m.Pawns.Count == 1 && m.Pawns[new CellKey(2, 2)].TemplateId == "p9", "no stacking: replace on occupied cell");

    // change pawn: re-point a placed pawn to a different template, cell unchanged
    var p3 = new Pawn { Cell = new CellKey(2, 2), TemplateId = "p1" };
    m.PlacePawn(p3);
    p3.TemplateId = "p2";
    Check(m.Pawns[new CellKey(2, 2)].TemplateId == "p2" && m.Pawns.Count == 1, "change pawn: reference swaps, cell stays");

    // broken reference: a TemplateId missing from the library resolves to null (X render)
    Check(lib.Find("p9") is null, "broken reference resolves to null");

    // AddFloor clears pawns in the region (same clearing path as erase)
    m2.AddFloor(5, 5, 7, 7);
    Check(m2.Pawns.Count == 0, "floor clear removes pawns");

    // serialization round-trip preserves the template reference
    var m3 = new MapDocument();
    m3.PlacePawn(new Pawn { Cell = new CellKey(1, 1), TemplateId = "p42" });
    string j = MapSerializer.ToJson(m3);
    var l3 = MapSerializer.FromJson(j);
    Check(l3.Pawns.Count == 1, "pawn round-trip count");
    Check(l3.Pawns[new CellKey(1, 1)].TemplateId == "p42", "pawn round-trip template reference");

    // undo restores pawns through snapshots
    var h = new History();
    h.Initialize(m3);
    m3.RemovePawn(new CellKey(1, 1));
    h.Commit(m3);
    Check(m3.Pawns.Count == 0, "pawn removed");
    m3.ApplyFrom(h.Undo()!);
    Check(m3.Pawns.Count == 1, "undo restores pawn");
}

// --- pawn library: Ids, affiliation rename, inheritance ---
{
    var lib = new PawnLibrary();
    var a = new PawnTemplate { Name = "Ally", Affiliation = "Friendly" };
    a.ApplyAffiliationDefaults();
    lib.Add(a);
    Check(a.Id == "p1", "first template gets p1");
    Check(PawnTemplate.NormalizeAffiliation("Friendly") == "Ally", "legacy Friendly normalizes to Ally");
    Check(PawnTemplate.NormalizeAffiliation("Hostile") == "Enemy", "legacy Hostile normalizes to Enemy");
    Check(a.OuterR == 120, "Ally outer color default (green)");

    var b = new PawnTemplate { Name = "Boss", Affiliation = "Enemy" };
    b.ApplyAffiliationDefaults();
    lib.Add(b);
    Check(b.Id == "p2", "second template gets p2");
    Check(lib.Find("p2")!.Name == "Boss", "Find by Id");

    lib.Remove("p1");
    Check(lib.Pawns.Count == 1 && lib.Find("p1") is null, "Remove by Id drops template");

    // adding a template with an existing Id replaces it (no duplicate)
    var c = new PawnTemplate { Id = "p2", Name = "Boss2", Affiliation = "Neutral" };
    lib.Add(c);
    Check(lib.Pawns.Count == 1 && lib.Find("p2")!.Name == "Boss2", "Add replaces same-Id template");
}

// --- global affiliation color propagates to inheriting templates at render time ---
{
    var s = AppSettings.Default();
    var t = new PawnTemplate { Name = "grunt", Affiliation = "Enemy", ColorOverride = false };
    t.ApplyAffiliationDefaults();
    Check(t.Effective(s).outer.r == 220, "inheriting template uses global Enemy outer (220)");

    var enemy = s.AffiliationColors.Find(c => c.Name == "Enemy")!;
    enemy.OuterR = 255; enemy.OuterG = 255; enemy.OuterB = 255;
    Check(t.Effective(s).outer.r == 255, "global Enemy change propagates to inheriting template");

    var o = new PawnTemplate { Name = "boss", Affiliation = "Enemy", ColorOverride = true };
    o.OuterR = 10; o.OuterG = 20; o.OuterB = 30;
    Check(o.Effective(s).outer.r == 10, "override template keeps own color despite global change");
}

// --- overlapping move (excel-style shift): clear source BEFORE paste ---
{
    var m = new MapDocument();
    m.AddRoom(0, 0, 3, 3); // 4x4 room
    // move 1 cell left: source [0,0]-[3,3], target origin (-1,0), overlap 3 columns
    m.MoveRegion(0, 0, 3, 3, -1, 0);
    bool all = true;
    for (int x = -1; x <= 2; x++)
        for (int y = 0; y <= 3; y++)
            if (!m.Cells.Contains(new CellKey(x, y))) all = false;
    Check(all, "overlap move: full shifted region present (no voided overlap)");
    Check(m.Cells.Count == 16, "overlap move: exactly 16 cells");
    // walls follow the shifted room: left edge at x=-1, right edge at x=2
    bool W(int x, int y, Side s)
    {
        var k = EdgeKey.Normalize(new CellKey(x, y), s);
        return m.Walls.ContainsKey(k) && m.Walls[k];
    }
    Check(W(-1, 0, Side.West), "overlap move: new left wall");
    Check(W(2, 0, Side.East), "overlap move: new right wall");
    Check(!W(3, 0, Side.East), "overlap move: old right wall gone");
}

// --- polygon tool: cell-center fill, edge decomposition, capture/paste, erase, round-trip ---
{
    var p = new MapDocument();
    // 2x1 triangle: (0,0),(2,0),(0,1). cell (0,0) center (0.5,0.5) inside,
    // cell (1,0) center (1.5,0.5) outside -> room = cell (0,0).
    p.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (0, 1) });
    Check(p.Cells.Contains(new CellKey(0, 0)), "polygon fill: cell (0,0) center inside triangle");
    Check(!p.Cells.Contains(new CellKey(1, 0)), "polygon fill: cell (1,0) center outside triangle");
    Check(p.Cells.Count == 1, "polygon fill: exactly 1 cell");
    // edges: (0,0)->(2,0) horizontal at y=0 -> North walls of cells (0,0),(1,0)
    Check(p.HasWall(new EdgeKey(new CellKey(0, 0), Side.North)), "polygon: horizontal edge -> North wall cell (0,0)");
    Check(p.HasWall(new EdgeKey(new CellKey(1, 0), Side.North)), "polygon: horizontal edge -> North wall cell (1,0)");
    // (2,0)->(0,1) diagonal -> AngledWall
    Check(p.AngledWalls.Count == 1, "polygon: one diagonal edge -> one angled wall");
    Check(p.AngledWalls[0] == new AngledWall(2, 0, 0, 1), "polygon: diagonal stored as AngledWall(2,0,0,1)");
    // (0,1)->(0,0) vertical at x=0 -> East wall of cell (-1,0)
    Check(p.HasWall(new EdgeKey(new CellKey(-1, 0), Side.East)), "polygon: vertical edge -> East wall of cell left");

    // axis-aligned rectangle polygon = 4x3 room, no angled walls
    var r = new MapDocument();
    r.AddPolygon(new List<(int, int)> { (0, 0), (4, 0), (4, 3), (0, 3) });
    Check(r.Cells.Count == 12, "polygon rectangle: 4x3 = 12 cells");
    Check(r.AngledWalls.Count == 0, "polygon rectangle: no angled walls");

    // capture/paste translates angled wall endpoints
    var src = new MapDocument();
    src.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (0, 1) });
    var snap = src.CaptureRegion(0, 0, 1, 1);
    Check(snap.AngledWalls.Count == 1, "polygon capture: angled wall fully inside region captured");
    src.PasteRegion(snap, 5, 5);
    Check(src.AngledWalls.Any(w => w == new AngledWall(7, 5, 5, 6)), "polygon paste: angled wall translated by offset");

    // capture grabs angled walls that overlap ANY part of the region
    var p2 = new MapDocument();
    p2.AddPolygon(new List<(int, int)> { (0, 0), (4, 0), (4, 3), (0, 3) });
    // long diagonal passes through the corner of the small region -> overlaps
    p2.AngledWalls.Add(new AngledWall(0, 0, 10, 10));
    var snap2 = p2.CaptureRegion(0, 0, 3, 2);
    Check(snap2.AngledWalls.Any(w => w == new AngledWall(0, 0, 10, 10)), "polygon capture: angled wall overlapping region captured (any-part rule)");

    // capture excludes angled walls that do NOT touch the region
    var p2b = new MapDocument();
    p2b.AngledWalls.Add(new AngledWall(10, 10, 12, 12));
    var snap2b = p2b.CaptureRegion(0, 0, 3, 2);
    Check(!snap2b.AngledWalls.Any(w => w == new AngledWall(10, 10, 12, 12)), "polygon capture: angled wall fully outside region excluded");

    // clear removes angled walls that overlap ANY part of the rect
    var p3 = new MapDocument();
    p3.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (0, 1) });
    p3.AngledWalls.Add(new AngledWall(5, 5, 7, 7)); // fully inside [0,0]-[8,8]
    p3.ClearRegion(0, 0, 8, 8);
    Check(p3.AngledWalls.Count == 0, "polygon clear: angled walls fully inside rect removed");

    // clear removes angled walls that straddle the rect edge
    var p4 = new MapDocument();
    p4.AngledWalls.Add(new AngledWall(0, 0, 10, 10));
    p4.ClearRegion(0, 0, 3, 3);
    Check(p4.AngledWalls.Count == 0, "polygon clear: angled wall straddling rect edge removed (any-part rule)");

    // clear keeps angled walls fully outside the rect
    var p4b = new MapDocument();
    p4b.AngledWalls.Add(new AngledWall(10, 10, 12, 12));
    p4b.ClearRegion(0, 0, 3, 3);
    Check(p4b.AngledWalls.Count == 1, "polygon clear: angled wall fully outside rect kept");

    // polygon union: drawing a polygon spanning two rooms removes the shared
    // interior walls (the polygon acts as an angled room tool, same as AddRoom)
    var u = new MapDocument();
    u.AddRoom(0, 0, 1, 1);
    u.AddRoom(1, 0, 2, 1);
    Check(u.HasWall(new EdgeKey(new CellKey(0, 0), Side.East)), "union: shared wall exists before polygon union");
    Check(u.HasWall(new EdgeKey(new CellKey(0, 1), Side.East)), "union: shared wall exists before union (row 1)");
    // polygon spanning [0,0]-[3,2] = 3x2 cells, union over both rooms
    u.AddPolygon(new List<(int, int)> { (0, 0), (3, 0), (3, 2), (0, 2) }, false, false, true);
    Check(u.Cells.Count == 6, "union: polygon spanning fills 6 cells");
    Check(!u.HasWall(new EdgeKey(new CellKey(0, 0), Side.East)), "union: interior wall x=1 removed (row 0)");
    Check(!u.HasWall(new EdgeKey(new CellKey(0, 1), Side.East)), "union: interior wall x=1 removed (row 1)");
    Check(!u.HasWall(new EdgeKey(new CellKey(1, 0), Side.East)), "union: interior wall x=2 removed (row 0)");
    Check(!u.HasWall(new EdgeKey(new CellKey(1, 1), Side.East)), "union: interior wall x=2 removed (row 1)");
    // outer boundary walls remain
    Check(u.HasWall(new EdgeKey(new CellKey(0, 0), Side.North)), "union: top wall exists");
    Check(u.HasWall(new EdgeKey(new CellKey(0, 1), Side.South)), "union: bottom wall exists");
    Check(u.HasWall(new EdgeKey(new CellKey(0, 0), Side.West)), "union: left wall exists");
    Check(u.HasWall(new EdgeKey(new CellKey(2, 0), Side.East)), "union: right wall exists");

    // polygon strict: interior walls present before the polygon are kept when strict.
    // The shared wall at x=1 (East of cell (0,0)) exists (set by the second room's
    // west boundary); strict keeps it, non-strict union would remove it.
    var s = new MapDocument();
    s.AddRoom(0, 0, 1, 1);
    s.AddRoom(1, 0, 2, 1);
    Check(s.HasWall(new EdgeKey(new CellKey(0, 0), Side.East)), "strict: shared wall x=1 exists before polygon");
    s.AddPolygon(new List<(int, int)> { (0, 0), (3, 0), (3, 2), (0, 2) }, false, true); // strict
    Check(s.HasWall(new EdgeKey(new CellKey(0, 0), Side.East)), "strict: interior wall x=1 kept in strict mode");

    // polygon edges are owned by the polygon object: a room grown over a
    // polygon does NOT remove the polygon's diagonal, so the trapezoid walls
    // survive every room, far away or connected.
    var rp = new MapDocument();
    rp.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (0, 2) }); // triangle, diagonal (2,0)->(0,2)
    Check(rp.AngledWalls.Count == 1, "room over polygon: diagonal present before union");
    rp.AddRoom(0, 0, 2, 2); // 3x3 room covering the triangle fully
    Check(rp.AngledWalls.Count == 1, "room over polygon: polygon diagonal survives the room union");

    // room grown over a polygon keeps a diagonal that is NOT fully interior
    var rp2 = new MapDocument();
    rp2.AddPolygon(new List<(int, int)> { (0, 0), (4, 0), (4, 4), (0, 4) });
    rp2.AngledWalls.Add(new AngledWall(4, 4, 6, 6)); // diagonal extends outside the room
    rp2.AddRoom(0, 0, 3, 3); // room covers part of the polygon, not the far diagonal
    Check(rp2.AngledWalls.Count == 1, "room over polygon: non-interior diagonal kept");

    // round-trip preserves angled walls
    var p5 = new MapDocument();
    p5.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (0, 1) });
    string pj = MapSerializer.ToJson(p5);
    var pl = MapSerializer.FromJson(pj);
    Check(pl.AngledWalls.Count == 1 && pl.AngledWalls[0] == new AngledWall(2, 0, 0, 1), "polygon round-trip: angled wall preserved");
    Check(pl.Cells.Contains(new CellKey(0, 0)), "polygon round-trip: filled cell preserved");

    // ContentBounds includes angled wall endpoints
    var p6 = new MapDocument();
    p6.AngledWalls.Add(new AngledWall(0, 0, 10, 10));
    var b = p6.ContentBounds();
    Check(b != null && b.Value.maxX >= 10 && b.Value.maxY >= 10, "polygon bounds: angled wall endpoints extend bounds");

    // ===== ANGLED DOORS (v2) =====
    // door placement on an existing angled wall
    var ad1 = new MapDocument();
    ad1.AngledWalls.Add(new AngledWall(0, 0, 2, 2)); // len 2.83, t must be in [0.5, 2.33]
    Check(ad1.TryPlaceAngledDoor(new AngledWall(0, 0, 2, 2), 1.4, "standard", "closed", out var d1),
          "angled door: placed on existing wall");
    Check(d1.T == 1.4 && d1.Type == "standard" && d1.State == DoorState.Closed,
          "angled door: fields set from defaults");
    Check(ad1.GetAngledDoors(new AngledWall(0, 0, 2, 2)).Count == 1,
          "angled door: door listed on its wall");

    // door on a wall stored in reversed endpoint order matches
    var ad1b = new MapDocument();
    ad1b.AngledWalls.Add(new AngledWall(2, 2, 0, 0));
    Check(ad1b.TryPlaceAngledDoor(new AngledWall(2, 2, 0, 0), 1.4, "standard", "closed", out _),
          "angled door: placement on reversed-order wall");

    // door on too-short wall fails; door near the ends fails
    var ad2 = new MapDocument();
    ad2.AngledWalls.Add(new AngledWall(0, 0, 1, 1)); // len 1.41
    Check(!ad2.TryPlaceAngledDoor(new AngledWall(0, 0, 1, 1), 0.3, "standard", "closed", out _),
          "angled door: t near segment start fails (gap must fit)");
    Check(!ad2.TryPlaceAngledDoor(new AngledWall(0, 0, 1, 1), 1.1, "standard", "closed", out _),
          "angled door: t near segment end fails (gap must fit)");
    Check(ad2.TryPlaceAngledDoor(new AngledWall(0, 0, 1, 1), 0.7, "standard", "closed", out _),
          "angled door: t mid-segment succeeds on short wall");

    // multiple doors on one segment
    var ad3 = new MapDocument();
    ad3.AngledWalls.Add(new AngledWall(0, 0, 6, 6)); // len 8.49
    ad3.TryPlaceAngledDoor(new AngledWall(0, 0, 6, 6), 1.0, "standard", "closed", out _);
    ad3.TryPlaceAngledDoor(new AngledWall(0, 0, 6, 6), 5.0, "portcullis", "locked", out _);
    Check(ad3.GetAngledDoors(new AngledWall(0, 0, 6, 6)).Count == 2,
          "angled door: two doors on one segment");

    // round-trip preserves angled doors
    var ad4 = new MapDocument();
    ad4.AngledWalls.Add(new AngledWall(2, 0, 0, 1));
    ad4.TryPlaceAngledDoor(new AngledWall(2, 0, 0, 1), 0.8, "magical", "locked", out _);
    string adj = MapSerializer.ToJson(ad4);
    var adl = MapSerializer.FromJson(adj);
    Check(adl.AngledWalls.Count == 1 && adl.GetAngledDoors(adl.AngledWalls[0]).Count == 1,
          "angled door: round-trip preserves door on wall");
    var adlDoor = adl.GetAngledDoors(adl.AngledWalls[0])[0];
    Check(adlDoor.T == 0.8 && adlDoor.Type == "magical" && adlDoor.State == DoorState.Locked,
          "angled door: round-trip preserves T/type/state");

    // door referencing a wall stored in reversed order loads onto that wall
    var adl2 = MapSerializer.FromJson("{\"angled_walls\":[\"2,0,0,1\"],\"angled_doors\":[{\"wall\":\"0,1,2,0\",\"t\":0.8,\"type\":\"standard\",\"state\":\"closed\"}]}");
    Check(adl2.AngledWalls.Count == 1 && adl2.GetAngledDoors(adl2.AngledWalls[0]).Count == 1,
          "angled door: reversed-order wall reference matches on load");

    // orphan door (no matching wall) is dropped on load
    var adl3 = MapSerializer.FromJson("{\"angled_walls\":[],\"angled_doors\":[{\"wall\":\"0,0,2,2\",\"t\":1.0,\"type\":\"standard\",\"state\":\"closed\"}]}");
    Check(adl3.AngledDoors.Count == 0, "angled door: orphan door dropped on load");

    // capture/paste: doors travel with their wall
    var ad5 = new MapDocument();
    ad5.AngledWalls.Add(new AngledWall(0, 0, 2, 2));
    ad5.TryPlaceAngledDoor(new AngledWall(0, 0, 2, 2), 1.4, "standard", "closed", out _);
    var snapAd = ad5.CaptureRegion(0, 0, 2, 2);
    Check(snapAd.AngledDoors.Count == 1, "angled door: captured with its wall");
    var ad6 = new MapDocument();
    ad6.PasteRegion(snapAd, 5, 5);
    Check(ad6.AngledWalls.Count == 1 && ad6.GetAngledDoors(new AngledWall(5, 5, 7, 7)).Count == 1,
          "angled door: pasted door sits on pasted wall");

    // clear region: doors on cleared walls go with them
    var ad7 = new MapDocument();
    ad7.AngledWalls.Add(new AngledWall(0, 0, 2, 2));
    ad7.TryPlaceAngledDoor(new AngledWall(0, 0, 2, 2), 1.4, "standard", "closed", out _);
    ad7.ClearRegion(0, 0, 2, 2);
    Check(ad7.AngledWalls.Count == 0 && ad7.AngledDoors.Count == 0,
          "angled door: door removed with wall by clear region");

    // polygon edges are owned by the polygon: a room union over a polygon
    // keeps the diagonal, and its door stays with the wall.
    var ad8 = new MapDocument();
    ad8.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (0, 2) }); // diagonal (2,0)->(0,2)
    ad8.TryPlaceAngledDoor(new AngledWall(2, 0, 0, 2), 1.4, "standard", "closed", out _);
    Check(ad8.GetAngledDoors(new AngledWall(2, 0, 0, 2)).Count == 1,
          "angled door: placed on polygon diagonal");
    ad8.AddRoom(0, 0, 2, 2);
    Check(ad8.AngledWalls.Count == 1 && ad8.AngledDoors.Count == 1,
          "angled door: polygon diagonal and its door survive the room union");

    // ApplyFrom copies doors
    var ad9 = new MapDocument();
    ad9.AngledWalls.Add(new AngledWall(0, 0, 2, 2));
    ad9.TryPlaceAngledDoor(new AngledWall(0, 0, 2, 2), 1.4, "portcullis", "open", out _);
    var ad10 = new MapDocument();
    ad10.ApplyFrom(ad9);
    Check(ad10.AngledDoors.Count == 1 && ad10.GetAngledDoors(new AngledWall(0, 0, 2, 2))[0].Type == "portcullis",
          "angled door: ApplyFrom copies doors");

    // ===== room merge-nearby =====
    // user example: room (0,0)-(2,2), draw adjacent room (3,0)-(5,2) with merge
    // mode: shared boundary (x=2 east edges) opens, rooms connect.
    var mn = new MapDocument();
    mn.AddRoom(0, 0, 2, 2);
    Check(mn.HasWall(new EdgeKey(new CellKey(2, 0), Side.East)), "merge-nearby: first room east wall exists before merge");
    mn.AddRoom(3, 0, 5, 2, false, false, true);
    Check(!mn.HasWall(new EdgeKey(new CellKey(2, 0), Side.East)), "merge-nearby: shared boundary wall removed (row 0)");
    Check(!mn.HasWall(new EdgeKey(new CellKey(2, 2), Side.East)), "merge-nearby: shared boundary wall removed (row 2)");
    Check(mn.HasWall(new EdgeKey(new CellKey(5, 0), Side.East)), "merge-nearby: outer east wall of new room exists");
    Check(mn.HasWall(new EdgeKey(new CellKey(3, 0), Side.North)), "merge-nearby: outer north wall of new room exists");
    Check(mn.Cells.Contains(new CellKey(4, 1)), "merge-nearby: new room cells added");
    // gap of 2 cells: not touching, stays sealed
    var mn2 = new MapDocument();
    mn2.AddRoom(0, 0, 2, 2);
    mn2.AddRoom(4, 0, 6, 2, false, false, true);
    Check(mn2.HasWall(new EdgeKey(new CellKey(2, 0), Side.East)), "merge-nearby: non-touching room keeps first room's wall");
    Check(mn2.HasWall(new EdgeKey(new CellKey(4, 0), Side.West)), "merge-nearby: non-touching room keeps its own west wall");
    // merge mode off: adjacent room stays sealed (default union behavior unchanged)
    var mn3 = new MapDocument();
    mn3.AddRoom(0, 0, 2, 2);
    mn3.AddRoom(3, 0, 5, 2);
    Check(mn3.HasWall(new EdgeKey(new CellKey(2, 0), Side.East)), "merge-nearby off: shared wall stays (default behavior)");

    // user scenario: 2x4 room (x6-7, y2-5) bisected by walls at x=7 for rows
    // 3-5, plus a protruding cell (8,2) at the top right. Drag from the
    // protrusion straight down to y=5 with merge mode: the FULL shared boundary
    // opens (restored c77ab57 behavior), so the bisection walls are removed and
    // the result is one clean 3x4 room with walls only on the outer edge.
    var rm = new MapDocument();
    for (int x = 6; x <= 7; x++)
        for (int y = 2; y <= 5; y++)
            rm.Cells.Add(new CellKey(x, y));
    rm.Cells.Add(new CellKey(8, 2));
    foreach (var w in new[] { (5,2,"east"),(5,3,"east"),(5,4,"east"),(5,5,"east"),
                              (6,2,"north"),(6,6,"north"),(7,2,"north"),(7,6,"north"),
                              (7,3,"east"),(7,4,"east"),(7,5,"east"),
                              (8,2,"north"),(8,2,"east"),(8,3,"north") })
        rm.SetWall(new EdgeKey(new CellKey(w.Item1, w.Item2),
            w.Item3 == "north" ? Side.North : Side.East), true);
    rm.AddRoom(8, 2, 8, 5, false, false, true);
    Check(rm.Cells.Count == 12, "merge hallway: 12 cells after merge");
    Check(!rm.HasWall(new EdgeKey(new CellKey(7, 2), Side.East)), "merge hallway: shared wall at row 2 open");
    Check(!rm.HasWall(new EdgeKey(new CellKey(7, 3), Side.East)), "merge hallway: shared wall at row 3 opens");
    Check(!rm.HasWall(new EdgeKey(new CellKey(7, 4), Side.East)), "merge hallway: shared wall at row 4 opens");
    Check(!rm.HasWall(new EdgeKey(new CellKey(7, 5), Side.East)), "merge hallway: shared wall at row 5 opens");
    Check(rm.HasWall(new EdgeKey(new CellKey(8, 3), Side.East)), "merge hallway: new right wall row 3");
    Check(rm.HasWall(new EdgeKey(new CellKey(8, 4), Side.East)), "merge hallway: new right wall row 4");
    Check(rm.HasWall(new EdgeKey(new CellKey(8, 5), Side.East)), "merge hallway: new right wall row 5");
    Check(rm.HasWall(new EdgeKey(new CellKey(8, 6), Side.North)), "merge hallway: new bottom wall");
    Check(!rm.HasWall(new EdgeKey(new CellKey(8, 3), Side.North)), "merge hallway: old protrusion bottom wall removed");
    Check(rm.HasWall(new EdgeKey(new CellKey(5, 3), Side.East)), "merge hallway: untouched outer wall at x=5 survives");

    // sub-room inside an existing room with merge OFF (plain passageway): every
    // shared edge opens, including the drag-start cell - the sub-room stays
    // invisible, no strict-wall behavior on the clicked cell.
    var sr = new MapDocument();
    sr.AddRoom(0, 0, 3, 3);
    sr.AddRoom(1, 1, 2, 2, true, false, false);
    Check(!sr.HasWall(new EdgeKey(new CellKey(1, 1), Side.North)), "sub-room merge off: drag-start cell keeps its shared edges open");
    Check(!sr.HasWall(new EdgeKey(new CellKey(1, 1), Side.West)), "sub-room merge off: west edge open");
    Check(!sr.HasWall(new EdgeKey(new CellKey(2, 2), Side.South)), "sub-room merge off: far edges open too");
    Check(!sr.HasWall(new EdgeKey(new CellKey(2, 1), Side.East)), "sub-room merge off: east edge open");

    // user-reported bug: sub-room inside a room with merge ON must NOT create
    // walls. Every edge of the sub-room faces an existing room cell, so every
    // edge opens - the sub-room is invisible, no new wall set appears.
    var sm = new MapDocument();
    sm.AddRoom(0, 0, 3, 3);
    sm.AddRoom(1, 1, 2, 2, true, false, true);
    Check(!sm.HasWall(new EdgeKey(new CellKey(1, 1), Side.North)), "sub-room merge on: no wall on the clicked cell");
    Check(!sm.HasWall(new EdgeKey(new CellKey(2, 1), Side.North)), "sub-room merge on: no wall row 1 east");
    Check(!sm.HasWall(new EdgeKey(new CellKey(1, 2), Side.North)), "sub-room merge on: no wall row 2 west");
    Check(!sm.HasWall(new EdgeKey(new CellKey(2, 2), Side.North)), "sub-room merge on: no wall row 2 east");
    Check(!sm.HasWall(new EdgeKey(new CellKey(1, 1), Side.East)), "sub-room merge on: no wall inside the room");
    Check(!sm.HasWall(new EdgeKey(new CellKey(1, 1), Side.West)), "sub-room merge on: west edge open");
    Check(!sm.HasWall(new EdgeKey(new CellKey(2, 2), Side.East)), "sub-room merge on: east edge open");
    Check(!sm.HasWall(new EdgeKey(new CellKey(2, 2), Side.South)), "sub-room merge on: south edge open");

    // ===== stair direction: drag rule + round-trip =====
    Check(StairDrag.FromDrag(2, 0) == StairDirection.Right, "stair drag: L-to-R = Right");
    Check(StairDrag.FromDrag(-2, 0) == StairDirection.Left, "stair drag: R-to-L = Left");
    Check(StairDrag.FromDrag(0, -2) == StairDirection.Up, "stair drag: bottom-to-top = Up");
    Check(StairDrag.FromDrag(0, 2) == StairDirection.Down, "stair drag: top-to-bottom = Down");
    Check(StairDrag.FromDrag(0, 0) == StairDirection.Down, "stair drag: single click defaults to Down");
    Check(StairDrag.FromDrag(3, 1) == StairDirection.Right, "stair drag: dominant axis wins (mostly right)");
    Check(StairDrag.FromDrag(-1, -3) == StairDirection.Up, "stair drag: dominant axis wins (mostly up)");

    // all four directions round-trip through json
    var sa = new MapDocument();
    sa.Stairs[new CellKey(0, 0)] = new Stair { Direction = StairDirection.Up, TargetMap = "b.json" };
    sa.Stairs[new CellKey(1, 0)] = new Stair { Direction = StairDirection.Left };
    sa.Stairs[new CellKey(2, 0)] = new Stair { Direction = StairDirection.Right };
    sa.Stairs[new CellKey(3, 0)] = new Stair { Direction = StairDirection.Down };
    var saJson = MapSerializer.ToJson(sa);
    var saBack = MapSerializer.FromJson(saJson);
    Check(saBack.Stairs[new CellKey(0, 0)].Direction == StairDirection.Up, "stair direction: Up round-trips");
    Check(saBack.Stairs[new CellKey(0, 0)].TargetMap == "b.json", "stair direction: target map round-trips");
    Check(saBack.Stairs[new CellKey(1, 0)].Direction == StairDirection.Left, "stair direction: Left round-trips");
    Check(saBack.Stairs[new CellKey(2, 0)].Direction == StairDirection.Right, "stair direction: Right round-trips");
    Check(saBack.Stairs[new CellKey(3, 0)].Direction == StairDirection.Down, "stair direction: Down round-trips");
    // legacy json that still carries a dragAxis field loads fine (field ignored)
    var saLegacy = MapSerializer.FromJson(saJson.Replace("\"targetMap\":\"b.json\"", "\"targetMap\":\"b.json\",\"dragAxis\":\"W\""));
    Check(saLegacy.Stairs[new CellKey(0, 0)].Direction == StairDirection.Up, "stair direction: legacy dragAxis field ignored");

    // dropdown sets the whole connected group to one direction (group re-syncs
    // on one choice, not toggled individually)
    var sy = new MapDocument();
    for (int x = 0; x < 4; x++)
        sy.Stairs[new CellKey(x, 0)] = new Stair { Direction = x % 2 == 0 ? StairDirection.Up : StairDirection.Down };
    var target = StairDirection.Left;
    foreach (var s2 in sy.ConnectedStairs(new CellKey(0, 0)))
        s2.Direction = target;
    Check(sy.Stairs[new CellKey(0, 0)].Direction == StairDirection.Left, "direction dropdown: clicked stair set to target");
    Check(sy.Stairs[new CellKey(1, 0)].Direction == StairDirection.Left, "direction dropdown: down stair set to Left");
    Check(sy.Stairs[new CellKey(2, 0)].Direction == StairDirection.Left, "direction dropdown: up stair re-synced to Left");
    Check(sy.Stairs[new CellKey(3, 0)].Direction == StairDirection.Left, "direction dropdown: whole group synced to target");

    // ===== eraser adjust walls: RebuildRoomBoundaryWalls =====
    // strip every wall from a room, rebuild re-seals the outer boundary and
    // leaves interior edges open.
    var aw1 = new MapDocument();
    aw1.AddRoom(0, 0, 2, 2);
    foreach (var e in aw1.Walls.Keys.ToList()) aw1.SetWall(e, false);
    Check(!aw1.HasWall(new EdgeKey(new CellKey(0, 0), Side.North)), "adjust walls: wall stripped before rebuild");
    aw1.RebuildRoomBoundaryWalls(0, 0, 2, 2);
    Check(aw1.HasWall(new EdgeKey(new CellKey(0, 0), Side.North)), "adjust walls: top edge rebuilt");
    Check(aw1.HasWall(new EdgeKey(new CellKey(2, 2), Side.South)), "adjust walls: bottom edge rebuilt");
    Check(aw1.HasWall(new EdgeKey(new CellKey(0, 0), Side.West)), "adjust walls: west edge rebuilt");
    Check(aw1.HasWall(new EdgeKey(new CellKey(2, 0), Side.East)), "adjust walls: east edge rebuilt");
    Check(!aw1.HasWall(new EdgeKey(new CellKey(1, 1), Side.North)), "adjust walls: interior edge stays open");
    // shrink a room by erasing its right column: the NEW outer edge (x=1 east)
    // gets walls, the room stays sealed.
    var aw2 = new MapDocument();
    aw2.AddRoom(0, 0, 2, 2);
    for (int y = 0; y <= 2; y++) aw2.Cells.Remove(new CellKey(2, y));
    aw2.RebuildRoomBoundaryWalls(2, 0, 2, 2); // the erased column: new outer edge at x=1
    Check(aw2.HasWall(new EdgeKey(new CellKey(1, 0), Side.East)), "adjust walls: new outer edge after shrinking gets walls (row 0)");
    Check(aw2.HasWall(new EdgeKey(new CellKey(1, 2), Side.East)), "adjust walls: new outer edge after shrinking gets walls (row 2)");
    Check(aw2.HasWall(new EdgeKey(new CellKey(1, 0), Side.North)), "adjust walls: cell (1,0) north faces empty space, outer edge gets wall");
    Check(!aw2.HasWall(new EdgeKey(new CellKey(1, 1), Side.North)), "adjust walls: interior edge in shrunk room stays open");

    // user scenario: 3 rows x 2 columns bisected by a vertical wall, erase the
    // top row -> 2x2 room bisected down the middle. The bisection wall must
    // survive; only the walls on the erased row's level are removed.
    var aw3 = new MapDocument();
    aw3.AddRoom(0, 0, 1, 2); // x 0..1, y 0..2 = 2 columns x 3 rows
    for (int y = 0; y <= 2; y++) aw3.SetWall(new EdgeKey(new CellKey(0, y), Side.East), true); // bisection
    Check(aw3.HasWall(new EdgeKey(new CellKey(0, 0), Side.East)), "adjust walls: bisection wall present before erase");
    // erase the top row (y=0): cells (0,0),(1,0) and walls on that level.
    for (int x = 0; x <= 1; x++) aw3.Cells.Remove(new CellKey(x, 0));
    foreach (var e in aw3.Walls.Keys.ToList())
        if (e.Cell.Y == 0) aw3.SetWall(e, false);
    aw3.RebuildRoomBoundaryWalls(0, 0, 1, 0); // the erased top row
    Check(aw3.Cells.Count == 4, "adjust walls: top row cells deleted, 2x2 remains");
    Check(aw3.HasWall(new EdgeKey(new CellKey(0, 1), Side.North)), "adjust walls: new wall on the new top edge (left cell)");
    Check(aw3.HasWall(new EdgeKey(new CellKey(1, 1), Side.North)), "adjust walls: new wall on the new top edge (right cell)");
    Check(aw3.HasWall(new EdgeKey(new CellKey(0, 1), Side.East)), "adjust walls: bisection wall survives at row 1");
    Check(aw3.HasWall(new EdgeKey(new CellKey(0, 2), Side.East)), "adjust walls: bisection wall survives at row 2");
    Check(!aw3.HasWall(new EdgeKey(new CellKey(0, 0), Side.East)), "adjust walls: bisection wall on the erased row is gone");
}

// --- rotation (move/copy R / Shift+R / shift+scroll): CCW quarter-turns around origin ---
{
    var d = new MapDocument();
    d.AddRoom(0, 0, 1, 1); // cells (0,0),(1,0),(0,1),(1,1) + perimeter walls
    d.SetWall(new EdgeKey(new CellKey(0, 0), Side.East), true); // interior wall
    d.Stairs[new CellKey(1, 1)] = new Stair { Direction = StairDirection.Up };
    d.Labels.Add(new MapLabel { Cell = new CellKey(0, 0), Text = "x" });
    d.Pawns[new CellKey(1, 0)] = new Pawn { Cell = new CellKey(1, 0), TemplateId = "t", Status = "none" };
    d.Doors[EdgeKey.Normalize(new CellKey(1, 0), Side.North)] = new Door { Type = "standard", State = DoorState.Locked };
    var snap = d.CaptureRegion(0, 0, 1, 1);

    var r1 = MapDocument.Rotate(snap, 1); // one CCW quarter-turn
    Check(r1.OriginX == 0 && r1.OriginY == 0, "rotate: origin anchor preserved");
    Check(r1.Cells.Count == 4 && r1.Cells.Contains(new CellKey(0, -1)) && r1.Cells.Contains(new CellKey(0, -2))
        && r1.Cells.Contains(new CellKey(1, -1)) && r1.Cells.Contains(new CellKey(1, -2)),
        "rotate ccw: 2x2 block maps to x[0,2] y[-2,0] (cell (1,0) -> (0,-2))");
    Check(r1.Stairs[new CellKey(1, -2)].Direction == StairDirection.Left, "rotate ccw: stair Up -> Left at rotated cell");
    Check(r1.Labels.Any(l => l.Cell == new CellKey(0, -1) && l.Text == "x"), "rotate ccw: label cell rotates");
    Check(r1.Pawns.Any(p => p.Cell == new CellKey(0, -2) && p.TemplateId == "t"), "rotate ccw: pawn cell rotates");
    Check(r1.Walls[EdgeKey.Normalize(new CellKey(0, -1), Side.North)], "rotate ccw: interior wall East -> North at rotated cell");
    Check(r1.Walls[EdgeKey.Normalize(new CellKey(0, -2), Side.West)], "rotate ccw: outer wall North -> West at rotated cell");
    var door = r1.Doors[EdgeKey.Normalize(new CellKey(0, -2), Side.West)];
    Check(door is not null && door.Type == "standard" && door.State == DoorState.Locked, "rotate ccw: door travels with its wall, type/state kept");

    var r4 = MapDocument.Rotate(MapDocument.Rotate(MapDocument.Rotate(MapDocument.Rotate(snap, 1), 1), 1), 1);
    Check(r4.Cells.SetEquals(snap.Cells) && r4.Walls.Count == snap.Walls.Count, "rotate: 4 ccw quarter-turns = identity");

    var cw = MapDocument.Rotate(snap, -1); // clockwise = 3 ccw
    Check(cw.Cells.Contains(new CellKey(-2, 0)) && !cw.Cells.Contains(new CellKey(0, -1)),
        "rotate cw: cell (0,1) maps to (-2,0), ccw layout not reused");
    Check(cw.Stairs[new CellKey(-2, 1)].Direction == StairDirection.Right, "rotate cw: stair Up -> Right");

    // stair direction chain over 4 ccw turns: Up -> Left -> Down -> Right -> Up.
    // the snapshot origin is the top-left corner (0,0); the stair cell (1,0)
    // orbits that corner: (1,0) -> (0,-2) -> (-2,-1) -> (-1,1) -> (1,0).
    var st = new MapDocument();
    st.Stairs[new CellKey(1, 0)] = new Stair { Direction = StairDirection.Up };
    var ss = st.CaptureRegion(0, 0, 1, 0);
    var a = MapDocument.Rotate(ss, 1);
    var b = MapDocument.Rotate(a, 1);
    var c = MapDocument.Rotate(b, 1);
    var e = MapDocument.Rotate(c, 1);
    Check(a.Stairs[new CellKey(0, -2)].Direction == StairDirection.Left
        && b.Stairs[new CellKey(-2, -1)].Direction == StairDirection.Down
        && c.Stairs[new CellKey(-1, 1)].Direction == StairDirection.Right
        && e.Stairs[new CellKey(1, 0)].Direction == StairDirection.Up,
        "rotate: stair chain Up->Left->Down->Right->Up, cell orbits the origin corner");

    // angled wall + door T preserved
    var aw = new MapDocument();
    var wall = new AngledWall(0, 0, 1, 1);
    aw.AngledWalls.Add(wall);
    aw.AngledDoors[wall] = new List<AngledDoor> { new AngledDoor { T = 0.5, Type = "standard", State = DoorState.Closed } };
    var asnap = aw.CaptureRegion(0, 0, 1, 1);
    var ar = MapDocument.Rotate(asnap, 1);
    Check(ar.AngledWalls.Count == 1 && ar.AngledWalls[0].Ax == 0 && ar.AngledWalls[0].Ay == 0
        && ar.AngledWalls[0].Bx == 1 && ar.AngledWalls[0].By == -1, "rotate ccw: angled wall endpoints (1,1) -> (1,-1)");
    Check(ar.AngledDoors[ar.AngledWalls[0]][0].T == 0.5, "rotate ccw: angled door T preserved");

    // paste the rotated snapshot: content lands offset from the target origin
    var d2 = new MapDocument();
    d2.PasteRegion(r1, 10, 10);
    Check(d2.Cells.Contains(new CellKey(10, 8)) && d2.Cells.Contains(new CellKey(11, 9)),
        "rotate paste: rotated cells land at target (10,10)");
    Check(d2.HasWall(EdgeKey.Normalize(new CellKey(10, 8), Side.West)), "rotate paste: rotated wall lands at target");
}

{
// appended to Program.cs content via scratch: simulate MoveAt's model sequence
var d = new MapDocument();
d.AddRoom(0, 0, 1, 1);
var snap = d.CaptureRegion(0, 0, 1, 1);
d.ClearRegion(0, 0, 1, 1);
d.PasteRegion(snap, 5, 5);
Check(!d.Cells.Contains(new CellKey(0, 0)) && d.Cells.Contains(new CellKey(5, 5)),
    "move sim: source cleared, paste lands at target");

}
{
    var d = new MapDocument();
    d.AddRoom(0, 0, 1, 1);
    var snap = d.CaptureRegion(0, 0, 1, 1);
    d.ClearRegion(0, 0, 1, 1);
    d.PasteRegion(snap, 0, 0); // destination == source origin (rotate-in-place)
    Check(d.Cells.Contains(new CellKey(0, 0)) && d.Cells.Contains(new CellKey(1, 1)),
        "overlap sim: paste at source origin re-creates source content (looks like 'not cleared')");
}

// --- move tool driven through the REAL canvas pointer handlers (headless) ---
{
    var h = new CanvasHarness();
    h.Doc.AddRoom(0, 0, 1, 1);
    h.Canvas.Mode = ToolMode.Move;
    h.Drag(0, 0, 1, 1);
    Check(h.Canvas.MoveArmed, "canvas move: drag-select arms the move");

    h.Click(5, 5);
    Check(!h.Doc.Cells.Contains(new CellKey(0, 0)) && !h.Doc.Cells.Contains(new CellKey(1, 1)),
        "canvas move: source region cleared after destination click");
    Check(h.Doc.Cells.Contains(new CellKey(5, 5)) && h.Doc.Cells.Contains(new CellKey(6, 6)),
        "canvas move: pasted content lands at destination");
    Check(h.Doc.Cells.Count == 4, "canvas move: exactly the moved cells remain, no source left behind");

    // one placement per arm: the placement disarms, so a second click places nothing
    Check(!h.Canvas.MoveArmed, "canvas move: placement disarms the move");
    h.Click(9, 9);
    Check(h.Doc.Cells.Count == 4 && !h.Doc.Cells.Contains(new CellKey(9, 9))
          && !h.Doc.Cells.Contains(new CellKey(0, 0)),
        "canvas move: second click after placement places nothing");

    // rotation: the armed snapshot rotates (R / shift+R / shift+scroll) before placement
    var h2 = new CanvasHarness();
    h2.Doc.AddRoom(0, 0, 2, 0); // horizontal 3-cell bar
    h2.Canvas.Mode = ToolMode.Move;
    h2.Drag(0, 0, 2, 0);
    Check(h2.Canvas.MoveArmed, "canvas move: rotation run arms the move");
    h2.Canvas.RotateRegion(1); // 90 deg ccw: bar turns vertical
    h2.Click(5, 5);
    Check(!h2.Doc.Cells.Contains(new CellKey(0, 0)) && !h2.Doc.Cells.Contains(new CellKey(2, 0)),
        "canvas move: rotated placement clears the source");
    Check(h2.Doc.Cells.Count == 3 && h2.Doc.Cells.Contains(new CellKey(5, 2))
          && h2.Doc.Cells.Contains(new CellKey(5, 3)) && h2.Doc.Cells.Contains(new CellKey(5, 4)),
        "canvas move: rotated content lands vertical at destination");
    h.Dispose();
    h2.Dispose();
}

// --- polygon rooms: first-class object, exact floor geometry, erase, move, rotate, json ---
{
    var d = new MapDocument();
    d.AddPolygon(new List<(int, int)> { (0, 0), (3, 0), (3, 3), (0, 3) });
    Check(d.Polygons.Count == 1, "polygon: vertex loop stored as first-class object");
    Check(d.Polygons[0].Verts.Count == 4 && d.Polygons[0].Verts[0] == (0, 0),
        "polygon: verts stored verbatim");
    Check(d.Cells.Count == 9, "polygon: fill cells derived from the loop");
}
{
    // the sliver test: triangle (0,0)-(4,0)-(0,4). Cell (3,0) has its center
    // (3.5,0.5) OUTSIDE the hypotenuse x+y=4, so the old center test drew no
    // floor there, yet the triangle clips a sliver of the cell. Sample real
    // pixels off a headless software render: the sliver pixel must be floored
    // (exact geometry), the outside pixel must not. Screen = (world + 1) * 40.
    var h = new CanvasHarness();
    h.Doc.AddPolygon(new List<(int, int)> { (0, 0), (4, 0), (0, 4) });
    var bmp = h.Render();
    var floor = h.PixelAt(bmp, 60, 60);    // cell (0,0) center, fully inside
    var sliver = h.PixelAt(bmp, 184, 52);  // inside the clipped sliver of cell (3,0)
    var outside = h.PixelAt(bmp, 196, 76); // past the hypotenuse
    Check(floor.a > 0 && floor.r + floor.g + floor.b > 0,
        "polygon render: fully-inside cell is floored");
    Check(sliver.r == floor.r && sliver.g == floor.g && sliver.b == floor.b && sliver.a > 0,
        "polygon render: clipped sliver gets floor (exact geometry, not center test)");
    Check(!(outside.r == floor.r && outside.g == floor.g && outside.b == floor.b),
        "polygon render: area outside the polygon stays unfloored");
    h.Dispose();
}
{
    // the base-corner test: isosceles triangle (0,0)-(4,0)-(2,3). Cell (0,0)
    // has its center inside the triangle, so the fill-cell rect used to paint
    // the WHOLE square; the left edge y=1.5x cuts the cell, so the upper-left
    // of the cell is outside the polygon. Sample pixels: inside the edge gets
    // floor, outside the edge must not. Screen = (world + 1) * 40.
    var h = new CanvasHarness();
    h.Doc.AddPolygon(new List<(int, int)> { (0, 0), (4, 0), (2, 3) });
    var bmp = h.Render();
    var inside = h.PixelAt(bmp, 60, 48);   // world (0.5,0.2), below the left edge
    var outside = h.PixelAt(bmp, 48, 56);  // world (0.2,0.4), above the left edge
    Check(inside.a > 0 && inside.r + inside.g + inside.b > 0,
        "triangle base corner: inside the edge gets floor");
    Check(!(outside.r == inside.r && outside.g == inside.g && outside.b == inside.b),
        "triangle base corner: center-inside cell is NOT fully painted, only the polygon part");
    h.Dispose();
}
{
    // three triangles forming a trapezoid: the diagonals must survive drawing a
    // room far away. The old AddRoom removed every angled wall whose crossed
    // cells were all room cells, regardless of where the new room was drawn.
    var d = new MapDocument();
    d.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (1, 2) }, strict: true);
    d.AddPolygon(new List<(int, int)> { (2, 0), (4, 0), (3, 2) }, strict: true);
    d.AddPolygon(new List<(int, int)> { (1, 2), (3, 2), (2, 0) }, strict: true);
    int before = d.AngledWalls.Count;
    Check(before >= 3, "trapezoid: angled walls created");
    d.AddRoom(10, 10, 12, 12);
    Check(d.AngledWalls.Count == before,
        "trapezoid: angled walls survive a room drawn far away");
    d.AddRoom(0, 2, 4, 4);
    Check(d.AngledWalls.Count == before,
        "trapezoid: angled walls survive a room drawn adjacent");
}
{
    var d = new MapDocument();
    d.AddRoom(0, 0, 2, 2);
    d.AddPolygon(new List<(int, int)> { (3, 0), (6, 0), (3, 3) }, strict: true);
    Check(MapDocument.PolygonTouchesRect(d.Polygons[0], 3, 0, 5, 2),
        "polygon: touches the erase rect (hypotenuse crosses it)");
    d.ClearRegion(3, 0, 5, 2);
    Check(d.Polygons.Count == 0, "erase: polygon object removed whole");
    Check(!d.Cells.Contains(new CellKey(4, 0)) && !d.Cells.Contains(new CellKey(3, 2)),
        "erase: polygon fill cells removed");
    Check(d.AngledWalls.Count == 0, "erase: polygon angled walls removed");
    Check(d.Cells.Contains(new CellKey(2, 0)) && d.HasWall(new EdgeKey(new CellKey(2, 0), Side.East)),
        "erase: room boundary re-derived, shared wall restored");
}
{
    var h = new CanvasHarness();
    h.Doc.AddRoom(0, 0, 2, 2);
    h.Doc.AddPolygon(new List<(int, int)> { (3, 0), (6, 0), (3, 3) }, strict: true);
    h.Canvas.Mode = ToolMode.Erase;
    h.Canvas.EraseAdjustWalls = true; // the live app turns this on via the tool panel
    h.Drag(3, 0, 5, 2);
    Check(h.Doc.Polygons.Count == 0, "canvas erase tool: polygon removed whole");
    Check(h.Doc.AngledWalls.Count == 0, "canvas erase tool: angled walls removed with the polygon");
    Check(h.Doc.Cells.Contains(new CellKey(2, 0)) && h.Doc.HasWall(new EdgeKey(new CellKey(2, 0), Side.East)),
        "canvas erase tool: room east wall re-derived");
    h.Dispose();
}
{
    // reported bug: erase far away with Adjust Walls turns a polygon's diagonal
    // boundary into a staircase of horizontal + vertical walls. The rebuild was
    // global (scanned every cell in the document) and added axis-aligned walls
    // on every polygon fill cell edge facing empty space, approximating the
    // diagonal. Erasing at (20,20)-(21,21) must not touch the polygon at (0,0).
    var h = new CanvasHarness();
    h.Doc.AddPolygon(new List<(int, int)> { (0, 0), (4, 0), (0, 3) }, strict: true);
    h.Doc.AddRoom(10, 10, 13, 13);
    int angled = h.Doc.AngledWalls.Count;
    Check(angled == 1, "setup: polygon diagonal edge is one angled wall");
    Check(!h.Doc.HasWall(new EdgeKey(new CellKey(2, 0), Side.East)),
        "setup: no axis-aligned wall on the diagonal side before erase");
    h.Canvas.Mode = ToolMode.Erase;
    h.Canvas.EraseAdjustWalls = true;
    h.Drag(11, 11, 12, 12); // hole punched far away inside the room (window-safe coords)
    Check(h.Doc.AngledWalls.Count == angled, "far erase: polygon diagonal wall intact");
    // the six polygon-cell edges facing empty space across the diagonal side:
    // none of them is on the polygon's axis-aligned boundary, so none gets a wall
    Check(!h.Doc.HasWall(new EdgeKey(new CellKey(2, 0), Side.East)),
        "far erase: no staircase wall east of polygon cell (2,0)");
    Check(!h.Doc.HasWall(new EdgeKey(new CellKey(2, 0), Side.South)),
        "far erase: no staircase wall south of polygon cell (2,0)");
    Check(!h.Doc.HasWall(new EdgeKey(new CellKey(1, 1), Side.East)),
        "far erase: no staircase wall east of polygon cell (1,1)");
    Check(!h.Doc.HasWall(new EdgeKey(new CellKey(1, 1), Side.South)),
        "far erase: no staircase wall south of polygon cell (1,1)");
    Check(!h.Doc.HasWall(new EdgeKey(new CellKey(0, 2), Side.East)),
        "far erase: no staircase wall east of polygon cell (0,2)");
    Check(!h.Doc.HasWall(new EdgeKey(new CellKey(0, 2), Side.South)),
        "far erase: no staircase wall south of polygon cell (0,2)");
    Check(h.Doc.HasWall(new EdgeKey(new CellKey(2, 0), Side.North)),
        "far erase: polygon's own top boundary wall survives");
    Check(h.Doc.HasWall(new EdgeKey(new CellKey(11, 10), Side.South)),
        "far erase: hole edge in the far room gets its wall (adjust walls still works)");
    h.Dispose();
}
{
    var d = new MapDocument();
    d.AddPolygon(new List<(int, int)> { (0, 0), (4, 0), (0, 4) });
    var snap = d.CaptureRegion(0, 0, 1, 1); // selection clips the polygon corner
    Check(snap.Polygons.Count == 1, "move: whole polygon captured when the selection touches it");
    d.ClearRegion(0, 0, 1, 1);
    d.PasteRegion(snap, 10, 10);
    Check(d.Polygons.Count == 1 && d.Polygons[0].Verts[0] == (10, 10) && d.Polygons[0].Verts[1] == (14, 10),
        "move: whole polygon translated to the destination");
    Check(d.Cells.Contains(new CellKey(12, 10)) && d.Cells.Contains(new CellKey(10, 12)),
        "move: fill cells outside the selection re-derived at the destination");
    Check(!d.Cells.Contains(new CellKey(0, 0)), "move: source cleared");
}
{
    var d = new MapDocument();
    d.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (2, 1) });
    var snap = d.CaptureRegion(0, 0, 2, 1);
    var r = MapDocument.Rotate(snap, 1);
    Check(r.Polygons.Count == 1 && r.Polygons[0].Verts[1] == (0, -2) && r.Polygons[0].Verts[2] == (1, -2),
        "rotate: polygon verts rotate with the snapshot");
}
{
    var d = new MapDocument();
    d.AddRoom(0, 0, 1, 1);
    d.AddPolygon(new List<(int, int)> { (2, 0), (5, 0), (2, 3) });
    string mapJson = MapSerializer.ToJson(d);
    Check(mapJson.Contains("polygons") && mapJson.Contains("\"version\": 3"),
        "json: polygons saved with version 3");
    var back = MapSerializer.FromJson(mapJson);
    Check(back.Polygons.Count == 1 && back.Polygons[0].Verts.Count == 3 && back.Polygons[0].Verts[1] == (5, 0),
        "json: polygons round-trip");
    string v1 = mapJson.Replace("\"polygons\"", "\"polygons_x\"");
    var old = MapSerializer.FromJson(v1);
    Check(old.Polygons.Count == 0, "json: v1 file (no polygons field) loads with no polygons");

    // angled-wall merge: a polygon whose diagonal edge is shared with a room
    // opens that boundary when merge is on, keeps it when merge is off.
    var am = new MapDocument();
    am.AddRoom(0, 0, 2, 2);
    am.AddPolygon(new List<(int, int)> { (0, 0), (2, 2), (0, 2) }, false, false, true);
    Check(am.AngledWalls.Count == 0, "angled merge: shared diagonal wall removed");
    Check(am.Cells.Count >= 4, "angled merge: polygon fill cells present");

    var am2 = new MapDocument();
    am2.AddRoom(0, 0, 2, 2);
    am2.AddPolygon(new List<(int, int)> { (0, 0), (2, 2), (0, 2) }, false, false, false);
    Check(am2.AngledWalls.Count == 1, "angled default: shared diagonal wall kept");

    // base-edge merge: a triangle whose base is mated to a room face opens the
    // WHOLE shared face, not just the rows that pass the center-point fill test.
    // 5x4 room, triangle base 4 at x=5 (rows 0-3), apex at (6,2): the top and
    // bottom base rows are sliver cells (center outside), so the merged-cell pass
    // alone leaves their walls. The edge walk must open all four.
    var t = new MapDocument();
    t.AddRoom(0, 0, 5, 4);
    t.AddPolygon(new List<(int, int)> { (5, 0), (5, 4), (6, 2) }, false, false, true);
    Check(!t.HasWall(new EdgeKey(new CellKey(4, 0), Side.East)), "base merge: top base wall removed");
    Check(!t.HasWall(new EdgeKey(new CellKey(4, 1), Side.East)), "base merge: inner base wall removed (row 1)");
    Check(!t.HasWall(new EdgeKey(new CellKey(4, 2), Side.East)), "base merge: inner base wall removed (row 2)");
    Check(!t.HasWall(new EdgeKey(new CellKey(4, 3), Side.East)), "base merge: bottom base wall removed");
    Check(t.HasWall(new EdgeKey(new CellKey(4, 0), Side.North)), "base merge: room outer walls kept");
}


    // ---- vertex doors ----
    {
        // placement on empty doc: horizontal auto-creates both straddled walls
        var vd = new MapDocument();
        Check(vd.TryPlaceVertexDoor(2, 3, true, "standard", "closed", out var door1),
              "vertex door: placement on empty doc succeeds");
        Check(vd.HasWall(new EdgeKey(new CellKey(1, 3), Side.North)) &&
              vd.HasWall(new EdgeKey(new CellKey(2, 3), Side.North)),
              "vertex door: horizontal auto-creates both straddled walls");
        Check(vd.GetVertexDoor(new VertexDoorKey(2, 3, true)) is not null,
              "vertex door: door stored at vertex key");

        var vd2 = new MapDocument();
        vd2.TryPlaceVertexDoor(2, 3, false, "arch", "open", out var door2);
        Check(vd2.HasWall(new EdgeKey(new CellKey(1, 2), Side.East)) &&
              vd2.HasWall(new EdgeKey(new CellKey(1, 3), Side.East)),
              "vertex door: vertical auto-creates both straddled walls");
        Check(door2.Type == "arch" && door2.State == DoorState.Open,
              "vertex door: default type/state applied");

        // crossroads: all four edges walled -> both orientations rejected
        var cr = new MapDocument();
        cr.SetWall(new EdgeKey(new CellKey(1, 3), Side.North), true);
        cr.SetWall(new EdgeKey(new CellKey(2, 3), Side.North), true);
        cr.SetWall(new EdgeKey(new CellKey(1, 2), Side.East), true);
        cr.SetWall(new EdgeKey(new CellKey(1, 3), Side.East), true);
        Check(!cr.TryPlaceVertexDoor(2, 3, true, "standard", "closed", out _),
              "vertex door: crossroads rejects horizontal");
        Check(!cr.TryPlaceVertexDoor(2, 3, false, "standard", "closed", out _),
              "vertex door: crossroads rejects vertical");

        // L-corner: horizontal pair walled + top wall -> horizontal bisected by
        // the top wall, vertical bisected by the horizontal pair -> both rejected
        var lc = new MapDocument();
        lc.SetWall(new EdgeKey(new CellKey(1, 3), Side.North), true);
        lc.SetWall(new EdgeKey(new CellKey(2, 3), Side.North), true);
        lc.SetWall(new EdgeKey(new CellKey(1, 2), Side.East), true);
        Check(!lc.TryPlaceVertexDoor(2, 3, true, "standard", "closed", out _),
              "vertex door: corner rejects horizontal (top wall bisects)");
        Check(!lc.TryPlaceVertexDoor(2, 3, false, "standard", "closed", out _),
              "vertex door: corner rejects vertical (left+right walls bisect)");

        // partial straight wall: only left wall -> horizontal valid, auto-creates right
        var pw = new MapDocument();
        pw.SetWall(new EdgeKey(new CellKey(1, 3), Side.North), true);
        Check(pw.TryPlaceVertexDoor(2, 3, true, "standard", "closed", out _),
              "vertex door: partial straight wall places, auto-creates missing wall");
        Check(pw.HasWall(new EdgeKey(new CellKey(2, 3), Side.North)),
              "vertex door: missing straddled wall auto-created");

        // erase hook: removing either straddled wall removes the door
        var er = new MapDocument();
        er.TryPlaceVertexDoor(2, 3, true, "standard", "closed", out _);
        er.SetWall(new EdgeKey(new CellKey(1, 3), Side.North), false);
        Check(er.GetVertexDoor(new VertexDoorKey(2, 3, true)) is null,
              "vertex door: erasing first straddled wall removes door");
        var er2 = new MapDocument();
        er2.TryPlaceVertexDoor(2, 3, true, "standard", "closed", out _);
        er2.SetWall(new EdgeKey(new CellKey(2, 3), Side.North), false);
        Check(er2.GetVertexDoor(new VertexDoorKey(2, 3, true)) is null,
              "vertex door: erasing second straddled wall removes door");

        // ClearRegion covering a straddled wall removes the door
        var cl = new MapDocument();
        cl.TryPlaceVertexDoor(2, 3, true, "standard", "closed", out _);
        cl.ClearRegion(1, 3, 1, 3);
        Check(cl.GetVertexDoor(new VertexDoorKey(2, 3, true)) is null,
              "vertex door: ClearRegion over a straddled wall removes door");

        // serializer round-trip
        var sr = new MapDocument();
        sr.TryPlaceVertexDoor(4, 5, false, "arch", "locked", out _);
        var vjson = MapSerializer.ToJson(sr);
        var back = MapSerializer.FromJson(vjson);
        Check(back.GetVertexDoor(new VertexDoorKey(4, 5, false)) is { } vdb &&
              vdb.Type == "arch" && vdb.State == DoorState.Locked,
              "vertex door: serializer round-trip preserves type/state");
        Check(back.HasWall(new EdgeKey(new CellKey(3, 4), Side.East)) &&
              back.HasWall(new EdgeKey(new CellKey(3, 5), Side.East)),
              "vertex door: straddled walls round-trip");

        // old file without vertex_doors loads fine
        var oldJson = "{\"version\":1,\"cells\":[\"0,0\"],\"walls\":[\"0,0,north\"],\"doors\":[],\"stairs\":[],\"labels\":[],\"pawns\":[]}";
        var oldDoc = MapSerializer.FromJson(oldJson);
        Check(oldDoc.VertexDoors.Count == 0, "vertex door: old file loads with none");

        // vertex door dropped on load when a straddled wall is missing
        var badDoc = MapSerializer.FromJson(vjson);
        badDoc.SetWall(new EdgeKey(new CellKey(3, 4), Side.East), false);
        var re = MapSerializer.FromJson(MapSerializer.ToJson(badDoc));
        Check(re.GetVertexDoor(new VertexDoorKey(4, 5, false)) is null,
              "vertex door: dropped on load when a straddled wall is missing");

        // capture/paste: vertex door travels; PasteRegion maps the snapshot origin
        // to the target, so capture origin (1,3) pasted to (6,3) shifts +5 in x
        var cap = new MapDocument();
        cap.TryPlaceVertexDoor(2, 3, true, "standard", "closed", out _);
        var snap = cap.CaptureRegion(1, 3, 2, 4);
        Check(snap.VertexDoors.Count == 1, "vertex door: capture includes vertex door");
        var cp = new MapDocument();
        cp.PasteRegion(snap, 6, 3);
        Check(cp.GetVertexDoor(new VertexDoorKey(7, 3, true)) is not null,
              "vertex door: paste offsets vertex");
        Check(cp.HasWall(new EdgeKey(new CellKey(6, 3), Side.North)) &&
              cp.HasWall(new EdgeKey(new CellKey(7, 3), Side.North)),
              "vertex door: paste ensures straddled walls at target");

        // rotation: Rotate turns points around the snapshot origin, so a vertex
        // at the origin stays put; orientation flips per quarter-turn
        var rot = new MapDocument();
        rot.TryPlaceVertexDoor(2, 3, true, "standard", "closed", out _);
        var rsnap = rot.CaptureRegion(2, 3, 2, 3);
        var r1 = MapDocument.Rotate(rsnap, 1);
        Check(r1.VertexDoors.Keys.Any(k => k.X == 2 && k.Y == 3 && !k.Horizontal),
              "vertex door: quarter-turn at origin flips orientation to vertical");
        var r2 = MapDocument.Rotate(rsnap, 2);
        Check(r2.VertexDoors.Keys.Any(k => k.X == 2 && k.Y == 3 && k.Horizontal),
              "vertex door: half-turn at origin restores horizontal");
        var rsnap2 = rot.CaptureRegion(1, 3, 3, 4);
        var r3 = MapDocument.Rotate(rsnap2, 1);
        Check(r3.VertexDoors.Keys.Any(k => k.X == 1 && k.Y == 2 && !k.Horizontal),
              "vertex door: quarter-turn off origin moves vertex (2,3)->(1,2), flips");

        // canvas: door tool click at a vertex places a vertex door
        var h = new CanvasHarness();
        h.Canvas.Mode = ToolMode.Door;
        h.ClickW(2, 3);
        Check(h.Doc.GetVertexDoor(new VertexDoorKey(2, 3, true)) is not null,
              "canvas: door tool click at vertex places vertex door");
        Check(h.Doc.HasWall(new EdgeKey(new CellKey(1, 3), Side.North)) &&
              h.Doc.HasWall(new EdgeKey(new CellKey(2, 3), Side.North)),
              "canvas: vertex click auto-creates straddled walls");

        // click at an edge midpoint (vertex > 0.3 away) places an edge door
        var h2 = new CanvasHarness();
        h2.Canvas.Mode = ToolMode.Door;
        h2.ClickW(1.5, 2);
        Check(h2.Doc.Doors.Count == 1 && h2.Doc.VertexDoors.Count == 0,
              "canvas: edge-midpoint click places edge door, vertex loses");

        // vertex wins equidistant tie: world (1.75,3) is 0.25 from vertex (2,3)
        // and 0.25 from midpoint of North(1,3)
        var h3 = new CanvasHarness();
        h3.Canvas.Mode = ToolMode.Door;
        h3.ClickW(1.75, 3);
        Check(h3.Doc.VertexDoors.Count == 1, "canvas: vertex wins equidistant tie");

        // crossroads click: rejected with status, nothing placed
        var h4 = new CanvasHarness();
        h4.Canvas.Mode = ToolMode.Door;
        h4.Doc.SetWall(new EdgeKey(new CellKey(1, 3), Side.North), true);
        h4.Doc.SetWall(new EdgeKey(new CellKey(2, 3), Side.North), true);
        h4.Doc.SetWall(new EdgeKey(new CellKey(1, 2), Side.East), true);
        h4.Doc.SetWall(new EdgeKey(new CellKey(1, 3), Side.East), true);
        h4.ClickW(2, 3);
        Check(h4.Doc.VertexDoors.Count == 0, "canvas: crossroads vertex click places nothing");
        Check(h4.LastStatus.Contains("bisect"), "canvas: crossroads click shows status message");

        // second click on an existing vertex door opens its config
        var h5 = new CanvasHarness();
        h5.Canvas.Mode = ToolMode.Door;
        var cfgs = new List<IDoorConfig>();
        h5.Canvas.DoorConfigRequested += d => cfgs.Add(d);
        h5.ClickW(2, 3);
        h5.ClickW(2, 3);
        Check(cfgs.Count == 2 && cfgs[1] is VertexDoor,
              "canvas: second vertex click opens config");

        // select mode: single click on a vertex door opens its config
        var h6 = new CanvasHarness();
        h6.Canvas.Mode = ToolMode.Door;
        h6.ClickW(2, 3);
        h6.Canvas.Mode = ToolMode.Select;
        var cfgs6 = new List<IDoorConfig>();
        h6.Canvas.DoorConfigRequested += d => cfgs6.Add(d);
        h6.ClickW(2, 3);
        Check(cfgs6.Count == 1 && cfgs6[0] is VertexDoor,
              "canvas: select-mode vertex click opens config");

        // erase tool drag over a straddled wall removes the vertex door
        var h7 = new CanvasHarness();
        h7.Canvas.Mode = ToolMode.Door;
        h7.ClickW(2, 3);
        h7.Canvas.Mode = ToolMode.Erase;
        h7.Drag(1, 3, 1, 3);
        Check(h7.Doc.GetVertexDoor(new VertexDoorKey(2, 3, true)) is null,
              "canvas: erase drag over straddled wall removes vertex door");

        // render: glyph sits centered on the lattice vertex, full-cell gap
        // punches the straddled walls along the door orientation
        var h8 = new CanvasHarness();
        h8.Canvas.Mode = ToolMode.Door;
        h8.ClickW(2, 3);
        var bmp8 = h8.Render();
        var center = h8.PixelAt(bmp8, 120, 160); // vertex (2,3) -> screen (120,160)
        Check(center.r == 245 && center.g == 220 && center.b == 60,
              "render: vertex door glyph centered on lattice vertex");
        var wallSide = h8.PixelAt(bmp8, 90, 160); // wall left of the gap
        Check(wallSide.r == 235 && wallSide.g == 235 && wallSide.b == 240,
              "render: straddled wall drawn up to the door gap");
        var gap = h8.PixelAt(bmp8, 105, 160); // inside the gap, outside the glyph
        Check(gap.r == 20 && gap.g == 20 && gap.b == 25,
              "render: full-cell gap punched along the door orientation");

        // vertical orientation: walls on the vertical pair place a vertical door
        var h9 = new CanvasHarness();
        h9.Canvas.Mode = ToolMode.Door;
        h9.Doc.SetWall(new EdgeKey(new CellKey(1, 2), Side.East), true);
        h9.Doc.SetWall(new EdgeKey(new CellKey(1, 3), Side.East), true);
        h9.ClickW(2, 3);
        Check(h9.Doc.GetVertexDoor(new VertexDoorKey(2, 3, false)) is not null,
              "canvas: vertical-pair walls place vertical vertex door");
        var bmp9 = h9.Render();
        var vcenter = h9.PixelAt(bmp9, 120, 160);
        Check(vcenter.r == 245 && vcenter.g == 220 && vcenter.b == 60,
              "render: vertical vertex door glyph on the vertex");
        var vwall = h9.PixelAt(bmp9, 120, 130); // wall above the gap
        Check(vwall.r == 235 && vwall.g == 235 && vwall.b == 240,
              "render: vertical straddled wall drawn up to the gap");
        var vgap = h9.PixelAt(bmp9, 120, 145); // inside the vertical gap
        Check(vgap.r == 20 && vgap.g == 20 && vgap.b == 25,
              "render: vertical gap punched along the door");
    }

// --- per-map undo/redo: switching maps must not wipe the other map's history ---
// simulates MainWindow's per-entry history dictionary (HistoryFor + ActiveHistory)
{
    var project = new MapProject();
    var e1 = project.Maps[0];
    var e2 = project.AddMap("Map 2");
    project.SetActive(0);
    var histories = new Dictionary<MapEntry, History>();
    History For(MapEntry e)
    {
        if (!histories.TryGetValue(e, out var h)) { h = new History(); h.Initialize(e.Doc); histories[e] = h; }
        return h;
    }
    var k1 = new EdgeKey(new CellKey(0, 0), Side.North);
    var k2 = new EdgeKey(new CellKey(1, 0), Side.North);
    var k3 = new EdgeKey(new CellKey(0, 0), Side.West);
    e1.Doc.SetWall(k1, true); For(e1).Commit(e1.Doc);
    e1.Doc.SetWall(k2, true); For(e1).Commit(e1.Doc);
    project.SetActive(1); // switch to map 2: map 1's history must survive
    For(e2); // MainWindow initializes the target map's history at switch time
    e2.Doc.SetWall(k3, true); For(e2).Commit(e2.Doc);
    e2.Doc.ApplyFrom(For(e2).Undo()!);
    Check(!e2.Doc.HasWall(k3), "per-map: undo on active map removes its wall");
    Check(For(e1).CanUndo, "per-map: map1 history survives switch to map2");
    project.SetActive(0);
    e1.Doc.ApplyFrom(For(e1).Undo()!);
    Check(!e1.Doc.HasWall(k2) && e1.Doc.HasWall(k1), "per-map: map1 undo restores pre-switch state");
    e1.Doc.ApplyFrom(For(e1).Redo()!);
    Check(e1.Doc.HasWall(k2), "per-map: map1 redo works after round-trip switch");
    project.SetActive(1);
    e2.Doc.ApplyFrom(For(e2).Redo()!);
    Check(e2.Doc.HasWall(k3), "per-map: map2 redo restores its own wall");
    project.RemoveMap(0);
    Check(For(project.Maps[0]) == histories[e2], "per-map: history follows entry through index shift");
    Check(project.Maps[0].Doc.HasWall(k3), "per-map: surviving map keeps its state");
}

// --- wall drag commit batching: the old path committed (full-document JSON
// snapshot) per edge per pointer move; now press commits once, moves mutate +
// repaint only, release commits once. One drag = one undo step.
using (var h = new CanvasHarness())
{
    h.Canvas.Mode = ToolMode.Wall;
    // press on the edge midpoint world (2.5,2.0) = north edge of cell (2,2):
    // screen ((2.5+1)*40, (2.0+1)*40) = (140,120). drag east along the y=2.0
    // edge line; horizontal orientation is locked from the first edge.
    h.PressAt(new Point(140, 120));
    int afterPress = h.CommitCount;
    int wallsAfterPress = h.Doc.Walls.Count;
    for (int i = 1; i <= 4; i++) h.MoveToAt(new Point(140 + 40 * i, 120));
    int duringDrag = h.CommitCount;
    int wallsAfterDrag = h.Doc.Walls.Count;
    h.ReleaseAt(new Point(300, 120));
    Check(afterPress == 1, "wall add drag: press commits the first wall");
    Check(duringDrag == 1, "wall add drag: drag moves commit zero times (batched)");
    Check(h.CommitCount == 2, "wall add drag: release commits once");
    Check(wallsAfterDrag > wallsAfterPress, "wall add drag: walls added along the drag line");
    Check(h.Doc.HasWall(new EdgeKey(new CellKey(2, 2), Side.North)), "wall add drag: pressed wall exists");
    Check(h.Doc.HasWall(new EdgeKey(new CellKey(6, 2), Side.North)), "wall add drag: last dragged wall exists");
}
using (var h = new CanvasHarness())
{
    h.Doc.AddRoom(0, 0, 5, 0); // row of cells, north walls on cells 0..5
    h.Canvas.Mode = ToolMode.Wall;
    // press on the north wall of cell (0,0): world (0.5,0) -> screen (60,40)
    h.PressAt(new Point(60, 40));
    int afterPress = h.CommitCount;
    for (int i = 1; i <= 4; i++) h.MoveToAt(new Point(60 + 40 * i, 40));
    int duringDrag = h.CommitCount;
    h.ReleaseAt(new Point(220, 40));
    Check(afterPress == 1, "wall delete drag: press commits the first removal");
    Check(duringDrag == 1, "wall delete drag: moves commit zero times (batched)");
    Check(h.CommitCount == 2, "wall delete drag: release commits once");
    Check(!h.Doc.HasWall(new EdgeKey(new CellKey(0, 0), Side.North)), "wall delete drag: pressed wall removed");
    Check(!h.Doc.HasWall(new EdgeKey(new CellKey(1, 0), Side.North)), "wall delete drag: dragged wall removed (cell 1)");
    Check(!h.Doc.HasWall(new EdgeKey(new CellKey(4, 0), Side.North)), "wall delete drag: dragged wall removed (cell 4)");
    Check(h.Doc.HasWall(new EdgeKey(new CellKey(5, 0), Side.North)), "wall delete drag: wall past the drag end survives");
}

// ---------- line tool ----------
// model: horizontal line (2,3)->(6,3) = North edges of cells (2,3)..(5,3)
var ldoc = new MapDocument();
ldoc.AddLine(2, 3, 6, 3);
Check(ldoc.Walls.Count == 4, "line horizontal creates 4 walls");
Check(ldoc.HasWall(new EdgeKey(new CellKey(2, 3), Side.North))
    && ldoc.HasWall(new EdgeKey(new CellKey(3, 3), Side.North))
    && ldoc.HasWall(new EdgeKey(new CellKey(4, 3), Side.North))
    && ldoc.HasWall(new EdgeKey(new CellKey(5, 3), Side.North))
    && !ldoc.HasWall(new EdgeKey(new CellKey(6, 3), Side.North)),
    "line horizontal walls are the unit North edges along the span, end vertex exclusive");

// vertical line (3,0)->(3,4) = East edges of cells (2,0)..(2,3)
ldoc.AddLine(3, 0, 3, 4);
Check(ldoc.Walls.Count == 8
    && ldoc.HasWall(new EdgeKey(new CellKey(2, 0), Side.East))
    && ldoc.HasWall(new EdgeKey(new CellKey(2, 3), Side.East))
    && !ldoc.HasWall(new EdgeKey(new CellKey(2, 4), Side.East)),
    "line vertical creates the unit East edges along the span");

// diagonal (0,0)->(4,3) = one AngledWall, any lattice angle
ldoc.AddLine(0, 0, 4, 3);
Check(ldoc.AngledWalls.Count == 1 && ldoc.AngledWalls[0] == new AngledWall(0, 0, 4, 3),
    "line diagonal creates one angled wall from start to end vertex");

// reversed diagonal is the same wall: no duplicate entry
ldoc.AddLine(4, 3, 0, 0);
Check(ldoc.AngledWalls.Count == 1, "line diagonal reversed does not duplicate the angled wall");

// zero-length line does nothing
ldoc.AddLine(2, 2, 2, 2);
Check(ldoc.Walls.Count == 8 && ldoc.AngledWalls.Count == 1,
    "line zero-length does nothing");

// additive: line over an existing room wall keeps the door on it.
// AddRoom(2,2,4,4) is a 3x3 room: 12 perimeter walls. The line (2,2)->(4,2)
// runs along the room's top edge, so its 2 walls are already present: the
// count must stay 12 and the door on the top edge must survive.
var ldoc2 = new MapDocument();
ldoc2.AddRoom(2, 2, 4, 4);
ldoc2.TryPlaceDoor(new EdgeKey(new CellKey(3, 2), Side.North), out _);
ldoc2.AddLine(2, 2, 4, 2);
Check(ldoc2.GetDoor(new EdgeKey(new CellKey(3, 2), Side.North)) != null && ldoc2.Walls.Count == 12,
    "line over existing walls merges additively and preserves doors");

// serialization round-trip: line walls and angled wall survive
var ldoc3 = new MapDocument();
ldoc3.AddLine(1, 1, 5, 1);
ldoc3.AddLine(1, 1, 1, 4);
ldoc3.AddLine(1, 1, 4, 3);
var lrt = MapSerializer.FromJson(MapSerializer.ToJson(ldoc3));
Check(lrt.Walls.Count == 7 && lrt.AngledWalls.Count == 1
    && lrt.AngledWalls[0] == new AngledWall(1, 1, 4, 3),
    "line walls and angled wall round-trip through the serializer");

// canvas: press-drag-release commits ONE line of walls
{
    var h = new CanvasHarness();
    h.Canvas.Mode = ToolMode.Line;
    h.PressW(2, 2); h.MoveToAt(h.ScreenWorld(6, 2)); h.ReleaseW(6, 2);
    Check(h.CommitCount == 1, "line drag commits exactly once");
    Check(h.Doc.Walls.Count == 4
        && h.Doc.HasWall(new EdgeKey(new CellKey(2, 2), Side.North))
        && h.Doc.HasWall(new EdgeKey(new CellKey(5, 2), Side.North)),
        "line drag creates the wall series from start to end vertex");
    h.Dispose();
}

// snap: press at (2.4,2.4) snaps to (2,2), release at (5.6,2.4) snaps to (6,2)
{
    var h = new CanvasHarness();
    h.Canvas.Mode = ToolMode.Line;
    h.PressW(2.4, 2.4); h.MoveToAt(h.ScreenWorld(5.6, 2.4)); h.ReleaseW(5.6, 2.4);
    Check(h.Doc.Walls.Count == 4
        && h.Doc.HasWall(new EdgeKey(new CellKey(2, 2), Side.North))
        && h.Doc.HasWall(new EdgeKey(new CellKey(5, 2), Side.North)),
        "line snaps press and release to the nearest lattice vertices");
    h.Dispose();
}

// zero-length drag: press and release on the same vertex commits nothing
{
    var h = new CanvasHarness();
    h.Canvas.Mode = ToolMode.Line;
    h.PressW(3, 3); h.ReleaseW(3, 3);
    Check(h.CommitCount == 0 && h.Doc.Walls.Count == 0,
        "line zero-length drag commits nothing");
    h.Dispose();
}

// diagonal drag: one angled wall, one commit
{
    var h = new CanvasHarness();
    h.Canvas.Mode = ToolMode.Line;
    h.PressW(2, 2); h.MoveToAt(h.ScreenWorld(5, 4)); h.ReleaseW(5, 4);
    Check(h.CommitCount == 1 && h.Doc.AngledWalls.Count == 1
        && h.Doc.AngledWalls[0] == new AngledWall(2, 2, 5, 4),
        "line diagonal drag creates one angled wall with one commit");
    h.Dispose();
}

// undo: one line drag is one history entry (history wired like MainWindow)
{
    var h = new CanvasHarness();
    var hist = new History();
    hist.Initialize(h.Doc);
    h.Canvas.ActionCompleted += () => hist.Commit(h.Doc);
    h.Canvas.Mode = ToolMode.Line;
    h.PressW(2, 2); h.MoveToAt(h.ScreenWorld(6, 2)); h.ReleaseW(6, 2);
    var restored = hist.Undo();
    Check(restored != null && restored.Walls.Count == 0,
        "line drag undoes as a single history entry");
    h.Dispose();
}

// erase removes line walls
{
    var h = new CanvasHarness();
    h.Canvas.Mode = ToolMode.Line;
    h.PressW(2, 2); h.MoveToAt(h.ScreenWorld(6, 2)); h.ReleaseW(6, 2);
    h.Canvas.Mode = ToolMode.Erase;
    h.PressW(2, 2); h.MoveToAt(h.ScreenWorld(6, 2)); h.ReleaseW(6, 2);
    Check(h.Doc.Walls.Count == 0, "erase drag removes line walls");
    h.Dispose();
}


// --- floor flood fill (model) ---
{
    var fdoc = new MapDocument();
    fdoc.AddLine(0, 0, 4, 0); fdoc.AddLine(4, 0, 4, 4); fdoc.AddLine(4, 4, 0, 4); fdoc.AddLine(0, 4, 0, 0);
    var reg = fdoc.FloodFillRegion(new CellKey(1, 1));
    Check(reg is not null && reg.Count == 16, "line room floods 16 cells");
    var frings = fdoc.BuildFloodRings(reg!);
    Check(frings.Count == 1 && frings[0].Count == 4, "rect room: one ring, 4 corners");
    var ddoc = new MapDocument();
    ddoc.AddRoom(0, 0, 5, 5);
    for (int y = 0; y <= 5; y++) ddoc.SetWall(new EdgeKey(new CellKey(2, y), Side.East), true);
    ddoc.TryPlaceDoor(new EdgeKey(new CellKey(2, 2), Side.East), out _);
    var dreg = ddoc.FloodFillRegion(new CellKey(1, 1));
    Check(dreg is not null && dreg.Count == 18, "door blocks fill: left half 18 cells");
    var sdoc = new MapDocument();
    sdoc.AddRoom(0, 0, 3, 3);
    sdoc.Stairs[new CellKey(2, 2)] = new Stair { Direction = StairDirection.Up };
    var sreg = sdoc.FloodFillRegion(new CellKey(1, 1));
    Check(sreg is not null && sreg.Count == 15, "stair blocks fill: 15 cells");
    var xdoc = new MapDocument();
    xdoc.AddLine(0, 0, 4, 0); xdoc.AddLine(4, 0, 4, 4); xdoc.AddLine(4, 4, 0, 4); xdoc.AddLine(0, 4, 0, 0);
    xdoc.AddLine(0, 0, 4, 4);
    var xreg = xdoc.FloodFillRegion(new CellKey(1, 0));
    Check(xreg is not null && xreg.Count == 6, "diagonal splits room: 6 cells below");
    var xreg2 = xdoc.FloodFillRegion(new CellKey(0, 1));
    Check(xreg2 is not null && xreg2.Count == 6, "diagonal splits room: 6 cells above");
    // the exact floor geometry: the below region is the triangle (0,0),(4,0),(4,4)
    var xring = xdoc.BuildFloodRings(xreg!);
    Check(xring.Count == 1 && xring[0].Count == 3, "45 degree room: one triangle ring");
    var pts = new HashSet<(double, double)>(xring[0]);
    Check(pts.Contains((0, 0)) && pts.Contains((4, 0)) && pts.Contains((4, 4)), "triangle ring has the room corners");
    var xring2 = xdoc.BuildFloodRings(xreg2!);
    Check(xring2.Count == 1 && xring2[0].Count == 3, "above region: one triangle ring");
    // steep diagonal (0,0)-(3,2): cut points are rational, ring has 7 vertices
    var zdoc = new MapDocument();
    zdoc.AddLine(0, 0, 3, 0); zdoc.AddLine(3, 0, 3, 2); zdoc.AddLine(3, 2, 0, 2); zdoc.AddLine(0, 2, 0, 0);
    zdoc.AddLine(0, 0, 3, 2);
    var zreg = zdoc.FloodFillRegion(new CellKey(2, 0));
    Check(zreg is not null && zreg.Count == 1, "steep diagonal: below region is cell (2,0)");
    var zring = zdoc.BuildFloodRings(zreg!);
    Check(zring.Count == 1 && zring[0].Count == 3, "steep diagonal: ring is the triangle (3 corners)");
    var zpts = new HashSet<(double, double)>(zring[0]);
    Check(zpts.Contains((0, 0)) && zpts.Contains((3, 0)) && zpts.Contains((3, 2)), "steep diagonal ring has the room corners");
    // 3x4 right triangle: hypotenuse (0,3)-(4,0). Sliver cells (1,2),(2,1),(3,0)
    // are cut by the diagonal; their region-side triangles must be inside the ring.
    var tdoc = new MapDocument();
    tdoc.AddLine(0, 0, 4, 0); tdoc.AddLine(0, 0, 0, 3); tdoc.AddLine(0, 3, 4, 0);
    var treg = tdoc.FloodFillRegion(new CellKey(0, 0));
    Check(treg is not null && treg.Count == 3, "3x4 triangle: 3 full cells below");
    var tring = tdoc.BuildFloodRings(treg!);
    Check(tring.Count == 1 && tring[0].Count == 3, "3x4 triangle: one ring, 3 corners");
    var tpts = new HashSet<(double, double)>(tring[0]);
    Check(tpts.Contains((0, 0)) && tpts.Contains((4, 0)) && tpts.Contains((0, 3)), "3x4 triangle ring has the room corners");
    bool InRing(List<(double, double)> ring, double px, double py)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var (xi, yi) = ring[i]; var (xj, yj) = ring[j];
            if (((yi > py) != (yj > py)) && (px < (xj - xi) * (py - yi) / (yj - yi) + xi)) inside = !inside;
        }
        return inside;
    }
    Check(InRing(tring[0], 1.1, 2.1) && InRing(tring[0], 2.5, 1.1) && InRing(tring[0], 3.5, 0.3),
        "3x4 triangle: sliver cell interiors inside the ring");
    var cdoc = new MapDocument();
    Check(cdoc.FloodFillRegion(new CellKey(0, 0)) is null, "open field exceeds cap, returns null");
    // flood regions are whole objects: erase removes them, walls survive
    var edoc = new MapDocument();
    edoc.AddLine(0, 0, 4, 0); edoc.AddLine(4, 0, 4, 4); edoc.AddLine(4, 4, 0, 4); edoc.AddLine(0, 4, 0, 0);
    edoc.AddFloodRegion(new FloodRegion { Rings = edoc.BuildFloodRings(edoc.FloodFillRegion(new CellKey(1, 1))!) });
    Check(edoc.FloodRegions.Count == 1, "AddFloodRegion adds the region");
    edoc.ClearRegion(1, 1, 2, 2);
    Check(edoc.FloodRegions.Count == 0 && edoc.Walls.Count == 16, "erase removes flood region, walls survive");
    // move carries the flood region as a whole object
    var mdoc = new MapDocument();
    mdoc.AddLine(0, 0, 4, 0); mdoc.AddLine(4, 0, 4, 4); mdoc.AddLine(4, 4, 0, 4); mdoc.AddLine(0, 4, 0, 0);
    mdoc.AddFloodRegion(new FloodRegion { Rings = mdoc.BuildFloodRings(mdoc.FloodFillRegion(new CellKey(1, 1))!) });
    var msnap = mdoc.CaptureRegion(0, 0, 4, 4);
    Check(msnap.FloodRegions.Count == 1, "capture carries flood region");
    var pd = new MapDocument();
    pd.PasteRegion(msnap, 10, 0);
    Check(pd.FloodRegions.Count == 1 && pd.FloodRegions[0].Bounds().minX == 10, "paste translates flood region");
    // rotate turns the rings
    var rsnap = MapDocument.Rotate(msnap, 1);
    Check(rsnap.FloodRegions.Count == 1 && rsnap.FloodRegions[0].Rings[0].Count == 4, "rotate keeps the ring closed");
    // serializer round-trip
    var fjson = MapSerializer.ToJson(mdoc);
    var rdoc = MapSerializer.FromJson(fjson);
    Check(rdoc.FloodRegions.Count == 1 && rdoc.FloodRegions[0].Rings.Count == 1 && rdoc.FloodRegions[0].Rings[0].Count == 4, "flood region survives save/load");
}

// --- floor flood fill (canvas) ---
{
    var fc = new CanvasHarness();
    fc.Canvas.Mode = ToolMode.Line;
    fc.PressW(0, 0); fc.MoveToAt(fc.ScreenWorld(4, 0)); fc.ReleaseW(4, 0);
    fc.PressW(4, 0); fc.MoveToAt(fc.ScreenWorld(4, 4)); fc.ReleaseW(4, 4);
    fc.PressW(4, 4); fc.MoveToAt(fc.ScreenWorld(0, 4)); fc.ReleaseW(0, 4);
    fc.PressW(0, 4); fc.MoveToAt(fc.ScreenWorld(0, 0)); fc.ReleaseW(0, 0);
    fc.Canvas.Mode = ToolMode.Floor;
    fc.Canvas.FloorFloodFill = true;
    int baseCommits = fc.CommitCount;
    fc.ClickW(1.5, 1.5);
    Check(fc.Doc.FloodRegions.Count == 1 && fc.Doc.FloodRegions[0].Rings[0].Count == 4, "canvas flood adds one geometry region");
    Check(fc.Doc.Cells.Count == 0, "flood floor is geometry, not cells");
    Check(fc.CommitCount == baseCommits + 1, "canvas flood commits once");
    Check(fc.Doc.Walls.Count == 16, "canvas flood leaves walls untouched");
    fc.Canvas.FloorFloodFill = false;
    fc.PressW(1.5, 1.5); fc.MoveToAt(fc.ScreenWorld(2.5, 2.5)); fc.ReleaseW(2.5, 2.5);
    Check(fc.CommitCount == baseCommits + 2, "manual drag still commits after flood mode");
    fc.Dispose();
}

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
