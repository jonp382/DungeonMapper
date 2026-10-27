using DungeonMapper.Models;
using DungeonMapper.Controls;

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
    u.AddPolygon(new List<(int, int)> { (0, 0), (3, 0), (3, 2), (0, 2) });
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

    // room grown over a polygon removes the polygon's interior diagonal (union
    // handles angled walls, not just EdgeKey walls)
    var rp = new MapDocument();
    rp.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (0, 2) }); // triangle, diagonal (2,0)->(0,2)
    Check(rp.AngledWalls.Count == 1, "room over polygon: diagonal present before union");
    rp.AddRoom(0, 0, 2, 2); // 3x3 room covering the triangle fully
    Check(rp.AngledWalls.Count == 0, "room over polygon: interior diagonal removed by union");

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

    // room union over a polygon removes interior diagonal AND its door
    var ad8 = new MapDocument();
    ad8.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (0, 2) }); // diagonal (2,0)->(0,2)
    ad8.TryPlaceAngledDoor(new AngledWall(2, 0, 0, 2), 1.4, "standard", "closed", out _);
    Check(ad8.GetAngledDoors(new AngledWall(2, 0, 0, 2)).Count == 1,
          "angled door: placed on polygon diagonal");
    ad8.AddRoom(0, 0, 2, 2);
    Check(ad8.AngledWalls.Count == 0 && ad8.AngledDoors.Count == 0,
          "angled door: door removed when union removes interior diagonal");

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
    aw1.RebuildRoomBoundaryWalls();
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
    aw2.RebuildRoomBoundaryWalls();
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
    aw3.RebuildRoomBoundaryWalls();
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

    // a second placement pastes again without re-clearing the already-cleared source
    h.Click(9, 9);
    Check(h.Doc.Cells.Count == 8 && h.Doc.Cells.Contains(new CellKey(9, 9))
          && !h.Doc.Cells.Contains(new CellKey(0, 0)),
        "canvas move: re-place pastes again, source stays cleared");

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

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
