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

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
