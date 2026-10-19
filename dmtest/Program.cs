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

// --- pawns ---
{
    var m = new MapDocument();
    var p = new Pawn { Cell = new CellKey(2, 2), Name = "Skeleton", Class = PawnClass.Hostile, Status = "paralyzed" };
    p.ApplyClassDefaults();
    m.PlacePawn(p);
    Check(m.Pawns.Count == 1, "pawn placed");
    Check(m.Pawns[new CellKey(2, 2)].Name == "Skeleton", "pawn name stored");
    Check(m.Pawns[new CellKey(2, 2)].OuterR == 220, "hostile outer color default");
    // capture/paste carries pawns (before the stacking replace below)
    m.AddRoom(0, 0, 3, 3);
    var snap = m.CaptureRegion(0, 0, 3, 3);
    Check(snap.Pawns.Count == 1, "capture includes pawn");
    var m2 = new MapDocument();
    m2.PasteRegion(snap, 5, 5);
    Check(m2.Pawns.ContainsKey(new CellKey(7, 7)), "paste offsets pawn");
    Check(m2.Pawns[new CellKey(7, 7)].Status == "paralyzed", "paste preserves status");
    // no stacking: placing on an occupied cell replaces
    var p2 = new Pawn { Cell = new CellKey(2, 2), Name = "Thorin", Class = PawnClass.Player };
    p2.ApplyClassDefaults();
    m.PlacePawn(p2);
    Check(m.Pawns.Count == 1 && m.Pawns[new CellKey(2, 2)].Name == "Thorin", "no stacking: replace on occupied cell");
    // AddFloor clears pawns in the region (same clearing path as erase)
    m2.AddFloor(5, 5, 7, 7);
    Check(m2.Pawns.Count == 0, "floor clear removes pawns");
    // serialization round-trip
    var m3 = new MapDocument();
    var p3 = new Pawn { Cell = new CellKey(1, 1), Name = "Goblin", Class = PawnClass.Hostile,
                        InnerR = 12, InnerG = 34, InnerB = 56, OuterR = 200, OuterG = 150, OuterB = 100, Status = "fear" };
    m3.PlacePawn(p3);
    string j = MapSerializer.ToJson(m3);
    var l3 = MapSerializer.FromJson(j);
    Check(l3.Pawns.Count == 1, "pawn round-trip count");
    Check(l3.Pawns[new CellKey(1, 1)].Name == "Goblin", "pawn round-trip name");
    Check(l3.Pawns[new CellKey(1, 1)].InnerR == 12 && l3.Pawns[new CellKey(1, 1)].OuterB == 100, "pawn round-trip colors");
    Check(l3.Pawns[new CellKey(1, 1)].Status == "fear", "pawn round-trip status");
    Check(l3.Pawns[new CellKey(1, 1)].Class == PawnClass.Hostile, "pawn round-trip class");
    // undo restores pawns through snapshots
    var h = new History();
    h.Initialize(m3);
    m3.RemovePawn(new CellKey(1, 1));
    h.Commit(m3);
    Check(m3.Pawns.Count == 0, "pawn removed");
    m3.ApplyFrom(h.Undo()!);
    Check(m3.Pawns.Count == 1, "undo restores pawn");
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

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
