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
    var cp = CampaignPresets.Default();
    Check(cp.DoorTypes.Count == 4, "presets: 4 default door types (locked type removed)");
    Check(!cp.DoorTypes.Any(t => t.Name == "locked"), "presets: no locked door type (state covers it)");
    Check(cp.FindDoorType("portcullis").R == 180, "presets: door color lookup");
    Check(cp.FindDoorType("nonexistent").Name == "standard", "presets: unknown type falls back");
    Check(s.DefaultLabelText == "label", "settings: default label text non-blank");
}

// --- campaign presets (door types, pawn statuses, affiliation colors per campaign) ---
{
    var d = CampaignPresets.Default();
    Check(d.PawnStatuses[0].Name == "none" && d.AffiliationColors.Select(a => a.Name).SequenceEqual(CampaignPresets.FixedAffiliations),
        "presets: starters hold none and the four fixed affiliations");

    var n = new CampaignPresets
    {
        DoorTypes = { new DoorType("gate", 300, -5, 20), new DoorType("gate", 1, 1, 1), new DoorType("  ", 1, 1, 1) },
        PawnStatuses = { new PawnStatus("stunned", 1, 2, 3, 99) },
        AffiliationColors = { new AffiliationColor("Cult", (1, 1, 1), (2, 2, 2)) },
    }.Normalize();
    Check(n.DoorTypes.Count == 2 && n.DoorTypes[0].Name == "standard" && n.DoorTypes[1].R == 255 && n.DoorTypes[1].G == 0,
        "presets: normalize drops blank and repeated names, clamps colors, adds standard first");
    Check(n.PawnStatuses[0].Name == "none" && n.PawnStatuses[1].Thickness == CampaignPresets.MaxThickness, "presets: normalize adds none and clamps thickness");
    Check(CampaignPresets.FixedAffiliations.All(f => n.AffiliationColors.Any(a => a.Name == f)) && n.AffiliationColors.Any(a => a.Name == "Cult"),
        "presets: normalize adds the fixed affiliations and keeps custom ones");

    var t = new CampaignPresets { DoorTypes = { new DoorType("gate", 1, 2, 3) } }.Tidy();
    Check(t.DoorTypes.Count == 1 && t.PawnStatuses.Count == 0 && t.AffiliationColors.Count == 0, "presets: tidy adds nothing");

    Check(d.Classify(new DoorType("portcullis", 180, 180, 190)) == PawnLibrary.ImportKind.Have, "presets: same name same look is have");
    Check(d.Classify(new DoorType("portcullis", 1, 2, 3)) == PawnLibrary.ImportKind.NewLook, "presets: same name new look");
    Check(d.Classify(new DoorType("gate", 1, 2, 3)) == PawnLibrary.ImportKind.New, "presets: unknown name is new");
    Check(d.Classify(new PawnStatus("fear", 245, 245, 245, 1)) == PawnLibrary.ImportKind.NewLook, "presets: status thickness counts as look");
    Check(d.Classify(new AffiliationColor("Enemy", (220, 120, 120), (120, 30, 31))) == PawnLibrary.ImportKind.NewLook, "presets: affiliation inner color counts as look");

    var m = CampaignPresets.Default();
    int added = m.AddMissing(new[] { new DoorType("portcullis", 1, 1, 1), new DoorType("gate", 4, 5, 6), new DoorType("gate", 7, 7, 7) });
    Check(added == 1 && m.FindDoorType("portcullis").R == 180 && m.FindDoorType("gate").R == 4, "presets: add missing never overwrites and counts only new names");
    var other = new CampaignPresets { PawnStatuses = { new PawnStatus("fear", 0, 0, 0, 9), new PawnStatus("asleep", 1, 1, 1, 2) } };
    Check(m.MergeMissing(other) == 1 && m.PawnStatuses.Find(x => x.Name == "fear")!.Thickness == 3, "presets: merge missing keeps the existing look");
    var src = new DoorType("arch", 1, 1, 1);
    m.AddMissing(new[] { src });
    src.R = 99;
    Check(m.FindDoorType("arch").R == 1, "presets: added entries are copies");

    var cl = m.Clone();
    cl.DoorTypes[0].R = 0;
    Check(m.DoorTypes[0].R == 245, "presets: clone is deep");

    // used by: only what a map's doors and pawns point at
    var lib = new PawnLibrary();
    var tp = new PawnTemplate { Name = "Cultist", Affiliation = "Cult", Status = "asleep" };
    lib.Add(tp);
    var pdoc = new MapDocument();
    pdoc.Doors[EdgeKey.Normalize(new CellKey(0, 0), Side.East)] = new Door { Type = "gate", State = DoorState.Closed };
    pdoc.PlacePawn(new Pawn { Cell = new CellKey(1, 1), TemplateId = tp.Id, Status = "paralyzed" });
    m.AddMissing(new[] { new AffiliationColor("Cult", (5, 5, 5), (6, 6, 6)), new AffiliationColor("Spare", (1, 1, 1), (1, 1, 1)) });
    var used = m.UsedBy(new[] { pdoc }, lib);
    Check(used.DoorTypes.Select(x => x.Name).SequenceEqual(new[] { "gate" }), "presets: used by holds only the door types on the map");
    Check(used.PawnStatuses.Select(x => x.Name).OrderBy(x => x).SequenceEqual(new[] { "asleep", "paralyzed" }), "presets: used by holds pawn and template statuses");
    Check(used.AffiliationColors.Select(x => x.Name).SequenceEqual(new[] { "Cult" }), "presets: used by holds the template affiliations");

    // opening a campaign
    var fc = CampaignPresets.ForCampaign(null);
    Check(fc.DoorTypes.Count == 4 && fc.PawnStatuses.Count == 3, "presets: a campaign without lists gets the starters");
    var fc2 = CampaignPresets.ForCampaign(new PresetsWire { DoorTypes = new() { new DoorType("gate", 1, 1, 1) }, PawnStatuses = new() });
    Check(fc2.DoorTypes.Select(x => x.Name).SequenceEqual(new[] { "standard", "gate" }) && fc2.PawnStatuses.Count == 3,
        "presets: a campaign list is used as it is (standard added), an empty list counts as missing");
    var fm = CampaignPresets.ForMapFile(new PresetsWire { DoorTypes = new() { new DoorType("portcullis", 1, 1, 1) } });
    Check(fm.FindDoorType("portcullis").R == 1 && fm.DoorTypes.Any(x => x.Name == "magical") && fm.PawnStatuses.Count == 3,
        "presets: a map file's own entries win, starters fill in the rest");

    // export one list per file, import reads any file holding it
    foreach (var (part, kind, key) in new[] {
        (CampaignPresets.Part.Doors, CampaignPresets.KindDoors, "door_types"),
        (CampaignPresets.Part.Statuses, CampaignPresets.KindStatuses, "pawn_statuses"),
        (CampaignPresets.Part.Affiliations, CampaignPresets.KindAffiliations, "affiliation_colors") })
    {
        var js = m.ExportJson(part);
        using var jd = System.Text.Json.JsonDocument.Parse(js);
        var keys = jd.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray();
        Check(jd.RootElement.GetProperty("kind").GetString() == kind && keys.SequenceEqual(new[] { key, "kind" }.OrderBy(x => x)),
            $"presets export: {kind} holds only its own list");
    }
    var ri = CampaignPresets.ReadImport(m.ExportJson(CampaignPresets.Part.Doors));
    Check(ri.DoorTypes.Count == m.DoorTypes.Count && ri.PawnStatuses.Count == 0 && ri.AffiliationColors.Count == 0, "presets import: a door export gives doors only");
    var ri2 = CampaignPresets.ReadImport("{\"door_types\":[{\"name\":\"x\",\"r\":1,\"g\":2,\"b\":3},{\"name\":\"\"}]}");
    Check(ri2.DoorTypes.Count == 1 && !ri2.DoorTypes.Any(x => x.Name == "standard"), "presets import: tidied, fixed entries not added");
    Check(CampaignPresets.ReadImport("[1]").DoorTypes.Count == 0, "presets import: a json array has nothing");
    bool threw = false;
    try { CampaignPresets.ReadImport("nope"); } catch { threw = true; }
    Check(threw, "presets import: broken json throws for the caller to report");

    // campaign file round trip and map file carrying
    var proj = new MapProject();
    proj.Maps[0].Doc = pdoc;
    var pj = MapSerializer.ProjectToJson(proj, lib, m);
    var back = CampaignPresets.ForCampaign(CampaignPresets.ReadWire(pj));
    Check(back.FindDoorType("gate").R == 4 && back.PawnStatuses.Any(x => x.Name == "asleep") && back.AffiliationColors.Any(a => a.Name == "Spare"),
        "presets: whole lists survive a campaign round trip");
    var ri3 = CampaignPresets.ReadImport(pj);
    Check(ri3.DoorTypes.Count == m.DoorTypes.Count && ri3.AffiliationColors.Count == m.AffiliationColors.Count, "presets import: a campaign file can be picked as the source");
    var imC = MapSerializer.ImportFromJson(pj, "x");
    Check(imC.Presets.DoorTypes!.Count == m.DoorTypes.Count && imC.Presets.Kind is null, "presets: campaign import carries all its presets");
    var mj = MapSerializer.MapToJson(proj.Maps[0], lib, m);
    var imM = MapSerializer.ImportFromJson(mj, "x");
    Check(imM.Presets.DoorTypes!.Select(x => x.Name).SequenceEqual(new[] { "gate" }) && imM.Presets.AffiliationColors!.Single().Name == "Cult",
        "presets: map file carries only the presets its map uses");
    var plain = MapSerializer.MapToJson(proj.Maps[0], lib);
    var imP = MapSerializer.ImportFromJson(plain, "x");
    Check(imP.Presets.DoorTypes is null && imP.Presets.PawnStatuses is null, "presets: a map file without presets carries none");
    var target = CampaignPresets.Default();
    target.AddMissing(new[] { new DoorType("gate", 50, 50, 50) });
    target.MergeMissing(CampaignPresets.FromWire(imM.Presets));
    Check(target.FindDoorType("gate").R == 50 && target.AffiliationColors.Any(a => a.Name == "Cult"), "presets: map import adds missing names and keeps the campaign's own look");
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

// --- label own color flag: -1 on any channel follows the settings color ---
{
    Check(!new MapLabel().HasOwnColor, "label color: new label follows settings");
    Check(new MapLabel { R = 0, G = 0, B = 0 }.HasOwnColor, "label color: black is an own color");
    Check(!new MapLabel { R = 0, G = -1, B = 0 }.HasOwnColor, "label color: partial -1 follows settings");
}

// --- label font size clamp: negative or huge values cannot reach rendering ---
{
    var cl = new MapLabel { FontSize = -40 };
    Check(cl.FontSize == 0, "label size: negative clamps to 0 (auto)");
    cl.FontSize = int.MaxValue;
    Check(cl.FontSize == MapLabel.MaxFontSize, "label size: huge clamps to the max");
    string cjson = MapSerializer.ToJson(new MapDocument { Labels = { new MapLabel { Cell = new CellKey(0, 0), Text = "x", FontSize = 30 } } })
        .Replace("\"font_size\": 30", "\"font_size\": 999999");
    var cback = MapSerializer.FromJson(cjson);
    Check(cjson.Contains("999999") && cback.Labels[0].FontSize == MapLabel.MaxFontSize, "label size: huge size in a file clamps on load");
    Check(MapCanvas.LabelSize(cback.Labels[0]) == MapLabel.MaxFontSize, "label size: clamped size is what draws");
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

// --- campaign affiliation color propagates to inheriting templates at render time ---
{
    var s = CampaignPresets.Default();
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
    // 2x1 triangle: (0,0),(2,0),(0,1). the diagonal cuts both cells, so
    // neither is a plain cell and the flood floor covers the triangle.
    p.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (0, 1) });
    Check(p.Cells.Count == 0, "polygon fill: cut cells are not plain cells");
    Check(p.FloorAt(0.5, 0.5) && p.FloorAt(1.2, 0.2), "polygon fill: floor covers the triangle");
    Check(!p.FloorAt(1.5, 0.8), "polygon fill: no floor past the diagonal");
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

    // polygon edges are plain walls: a room grown over a polygon merges
    // with it like any other diagonal, a strict room keeps it
    var rp = new MapDocument();
    rp.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (0, 2) }); // triangle, diagonal (2,0)->(0,2)
    Check(rp.AngledWalls.Count == 1, "room over polygon: diagonal present before union");
    rp.AddRoom(0, 0, 2, 2); // 3x3 room covering the triangle fully
    Check(rp.AngledWalls.Count == 0, "room over polygon: diagonal merged into the room");
    var rps = new MapDocument();
    rps.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (0, 2) });
    rps.AddRoom(0, 0, 2, 2, strict: true);
    Check(rps.AngledWalls.Count == 1, "room over polygon: strict room keeps the diagonal");

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
    Check(pl.FloorAt(0.5, 0.5) && !pl.FloorAt(1.5, 0.8), "polygon round-trip: floor preserved");

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

    // a strict room drawn over a polygon keeps the diagonal and its door
    var ad8 = new MapDocument();
    ad8.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (0, 2) }); // diagonal (2,0)->(0,2)
    ad8.TryPlaceAngledDoor(new AngledWall(2, 0, 0, 2), 1.4, "standard", "closed", out _);
    Check(ad8.GetAngledDoors(new AngledWall(2, 0, 0, 2)).Count == 1,
          "angled door: placed on polygon diagonal");
    ad8.AddRoom(0, 0, 2, 2, strict: true);
    Check(ad8.AngledWalls.Count == 1 && ad8.AngledDoors.Count == 1,
          "angled door: polygon diagonal and its door survive a strict room");
    ad8.AddRoom(0, 0, 2, 2);
    Check(ad8.AngledWalls.Count == 0 && ad8.AngledDoors.Count == 0,
          "angled door: door goes with the diagonal when a room merges over it");

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

    // user-reported bug: a hallway grown out of a small room with merge off
    // also merged into a big room it ran alongside, and opened the wall
    // between the two rooms. passageway only opens to the drag-start room.
    var hw = new MapDocument();
    hw.AddRoom(0, 0, 2, 5);                       // big room
    hw.AddRoom(3, 0, 4, 1, false, true, false);   // small room, walled off
    hw.AddRoom(3, 0, 4, 5, true, false, false, new CellKey(3, 0));
    for (int y = 0; y <= 5; y++)
        Check(hw.HasWall(new EdgeKey(new CellKey(3, y), Side.West)), $"hallway merge off: wall to big room kept at y={y}");
    Check(!hw.HasWall(new EdgeKey(new CellKey(3, 2), Side.North)), "hallway merge off: opens into the room it grew from");
    Check(hw.HasWall(new EdgeKey(new CellKey(4, 3), Side.East)), "hallway merge off: outer wall drawn");
    // same drag with merge on joins the big room too
    var hm = new MapDocument();
    hm.AddRoom(0, 0, 2, 5);
    hm.AddRoom(3, 0, 4, 1, false, true, false);
    hm.AddRoom(3, 0, 4, 5, true, false, true, new CellKey(3, 0));
    Check(!hm.HasWall(new EdgeKey(new CellKey(3, 4), Side.West)), "hallway merge on: joins the big room");

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

// --- polygon rooms: plain walls, cells and a flood floor. exact floor geometry, erase, move, rotate, json ---
{
    var d = new MapDocument();
    d.AddPolygon(new List<(int, int)> { (0, 0), (3, 0), (3, 3), (0, 3) });
    Check(d.Cells.Count == 9, "polygon: fill cells derived from the loop");
    Check(d.FloodRegions.Count == 1 && d.FloodRegions[0].Rings[0].Contains((3, 3)),
        "polygon: floor stored as a flood region");
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
    d.ClearRegion(3, 0, 5, 2);
    d.RebuildRoomBoundaryWalls(3, 0, 5, 2);
    Check(d.FloodRegions.Count == 0, "erase: polygon floor clipped away");
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
    var bmpBefore = h.Render();
    var polyFloor = h.PixelAt(bmpBefore, 208, 88); // world (4.2,1.2): inside the clipped fill cell (4,1)
    Check(polyFloor.a > 0 && polyFloor.r + polyFloor.g + polyFloor.b > 0,
        "canvas erase: polygon floor renders as floor pixels");
    h.Canvas.Mode = ToolMode.Erase;
    h.Canvas.EraseAdjustWalls = true; // the live app turns this on via the tool panel
    h.Drag(3, 0, 5, 2);
    Check(h.Doc.FloodRegions.Count == 0, "canvas erase tool: polygon floor clipped away");
    Check(h.Doc.AngledWalls.Count == 0, "canvas erase tool: angled walls removed with the polygon");
    var bmpAfter = h.Render();
    var gone = h.PixelAt(bmpAfter, 208, 88);
    Check(!(gone.r == polyFloor.r && gone.g == polyFloor.g && gone.b == polyFloor.b && gone.a > 0),
        "canvas erase: polygon floor pixels vanish with the polygon");
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
    var snap = d.CaptureRegion(0, 0, 3, 3);
    Check(snap.FloodRegions.Count == 1, "move: polygon floor captured");
    d.ClearRegion(0, 0, 3, 3);
    d.PasteRegion(snap, 10, 10);
    Check(d.FloodRegions.Count == 1 && d.FloodRegions[0].Rings[0].Contains((14, 10))
        && d.FloodRegions[0].Rings[0].Contains((10, 14)),
        "move: polygon floor translated to the destination");
    Check(d.Cells.Contains(new CellKey(12, 10)) && d.Cells.Contains(new CellKey(10, 12)),
        "move: fill cells land at the destination");
    Check(d.AngledWalls.Count == 1 && d.AngledWalls[0].Ax == 14 && d.AngledWalls[0].By == 14,
        "move: diagonal wall translated");
    Check(!d.Cells.Contains(new CellKey(0, 0)), "move: source cleared");

    // a selection over part of the polygon takes part of the floor
    var pd = new MapDocument();
    pd.AddPolygon(new List<(int, int)> { (0, 0), (4, 0), (0, 4) });
    var part = pd.CaptureRegion(0, 0, 1, 1);
    pd.ClearRegion(0, 0, 1, 1);
    Check(pd.FloodRegions.Count == 1 && !pd.FloorAt(0.5, 0.5) && pd.FloorAt(2.5, 0.5),
        "erase: partial selection clips the polygon floor, the rest stays");
    pd.PasteRegion(part, 10, 10);
    Check(pd.FloorAt(10.5, 10.5) && pd.FloorAt(11.5, 11.5) && !pd.FloorAt(12.5, 10.5),
        "move: clipped piece of the polygon floor lands at the destination");
}
{
    var d = new MapDocument();
    d.AddPolygon(new List<(int, int)> { (0, 0), (2, 0), (2, 1) });
    var snap = d.CaptureRegion(0, 0, 2, 1);
    var r = MapDocument.Rotate(snap, 1);
    Check(r.FloodRegions.Count == 1 && r.FloodRegions[0].Rings[0].Count == 3
        && r.FloodRegions[0].Rings[0].Contains((0, 0)) && r.FloodRegions[0].Rings[0].Contains((0, -2))
        && r.FloodRegions[0].Rings[0].Contains((1, -2)),
        "floor rings: rotate turns the exact triangle boundary");
}
{
    var d = new MapDocument();
    d.AddRoom(0, 0, 1, 1);
    d.AddPolygon(new List<(int, int)> { (2, 0), (5, 0), (2, 3) });
    string mapJson = MapSerializer.ToJson(d);
    Check(!mapJson.Contains("polygons") && mapJson.Contains("\"version\": 4"),
        "json: saved as version 4 with no polygons field");
    var back = MapSerializer.FromJson(mapJson);
    Check(back.FloodRegions.Count == 1 && back.FloodRegions[0].Rings[0].Count == 3
        && back.FloodRegions[0].Rings[0].Contains((2, 0)) && back.FloodRegions[0].Rings[0].Contains((5, 0))
        && back.FloodRegions[0].Rings[0].Contains((2, 3)),
        "json: polygon floor round-trips as a flood region");
    Check(back.AngledWalls.Count == 1 && back.Cells.SetEquals(d.Cells), "json: walls and cells round-trip");
    // an unrelated diagonal adjacent to the fill must NOT pull its triangle
    // into the polygon floor: rectangle (0,0)-(4,3) + wall (4,2)-(5,3) cuts
    // cell (4,2), adjacent to fill cell (3,2). Ring 0 stays the plain rectangle.
    var ux = new MapDocument();
    ux.AddPolygon(new List<(int, int)> { (0, 0), (4, 0), (4, 3), (0, 3) }, false, true);
    ux.AddLine(4, 2, 5, 3);
    var uRing = ux.FloodRegions[0].Rings[0];
    Check(uRing.Count == 4 && uRing.Contains((0, 0)) && uRing.Contains((4, 0))
        && uRing.Contains((4, 3)) && uRing.Contains((0, 3)),
        "floor rings: unrelated diagonal excluded from polygon floor");

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

    // polygon to polygon merge: the shared diagonal goes whichever way each
    // loop was wound, at any slope, and only along the part actually shared
    bool WallCovers(MapDocument d, double px, double py) => d.AngledWalls.Any(w =>
        Math.Abs((w.Bx - w.Ax) * (py - w.Ay) - (w.By - w.Ay) * (px - w.Ax)) < 1e-9
        && px >= Math.Min(w.Ax, w.Bx) && px <= Math.Max(w.Ax, w.Bx)
        && py >= Math.Min(w.Ay, w.By) && py <= Math.Max(w.Ay, w.By));
    foreach (var (bverts, label) in new[]
    {
        (new List<(int, int)> { (4, 0), (4, 4), (0, 4) }, "cw"),
        (new List<(int, int)> { (4, 0), (0, 4), (4, 4) }, "ccw"),
    })
    {
        var pw = new MapDocument();
        pw.AddPolygon(new List<(int, int)> { (0, 0), (4, 0), (0, 4) }, false, false, true);
        pw.AddPolygon(bverts, false, false, true);
        Check(pw.AngledWalls.Count == 0, $"polygon merge: shared diagonal removed ({label} winding)");
    }
    var pg = new MapDocument();
    pg.AddPolygon(new List<(int, int)> { (0, 0), (3, 0), (5, 2), (2, 2) }, false, false, true);
    pg.AddPolygon(new List<(int, int)> { (3, 0), (6, 0), (8, 2), (5, 2) }, false, false, true);
    Check(pg.AngledWalls.Count == 2 && !WallCovers(pg, 4, 1), "polygon merge: parallelograms open the shared slope");
    var sh = new MapDocument();
    sh.AddPolygon(new List<(int, int)> { (0, 0), (6, 1), (6, 4), (0, 4) }, false, false, true);
    sh.AddPolygon(new List<(int, int)> { (0, -3), (6, -3), (6, 1), (0, 0) }, false, false, true);
    Check(sh.AngledWalls.Count == 0, "polygon merge: shallow 1 in 6 slope opens");
    var po = new MapDocument();
    po.AddPolygon(new List<(int, int)> { (0, 0), (6, 0), (0, 6) }, false, false, true);
    po.TryPlaceAngledDoor(po.AngledWalls[0], 1.0, "standard", "closed", out _);
    po.AddPolygon(new List<(int, int)> { (2, 4), (4, 2), (4, 4) }, false, false, true);
    Check(!WallCovers(po, 3, 3), "polygon merge: partial overlap opens the shared part");
    Check(WallCovers(po, 5, 1) && WallCovers(po, 1, 5), "polygon merge: partial overlap keeps the unshared ends");
    Check(po.AngledWalls.Count == 2, "polygon merge: partial overlap leaves two diagonal pieces");
    Check(po.AngledDoors.Values.Sum(l => l.Count) == 1, "polygon merge: door on a kept piece survives the split");
    var po2 = new MapDocument();
    po2.AddPolygon(new List<(int, int)> { (2, 4), (4, 2), (4, 4) }, false, false, true);
    po2.AddPolygon(new List<(int, int)> { (0, 0), (6, 0), (0, 6) }, false, false, true);
    Check(!WallCovers(po2, 3, 3) && WallCovers(po2, 5, 1) && WallCovers(po2, 1, 5),
        "polygon merge: partial overlap opens when the big polygon comes second");
    var nm = new MapDocument();
    nm.AddPolygon(new List<(int, int)> { (0, 0), (4, 0), (0, 4) }, false, false, true);
    nm.AddPolygon(new List<(int, int)> { (4, 0), (4, 4), (0, 4) }, false, false, false);
    Check(nm.AngledWalls.Count == 1, "polygon merge off: shared diagonal kept");

    // the opened diagonal stays open through paste, rotate, undo and save/load.
    // two merged triangles make one open 4x4 block at (bx,by).
    MapDocument MergedPair()
    {
        var m = new MapDocument();
        m.AddPolygon(new List<(int, int)> { (0, 0), (4, 0), (0, 4) }, false, false, true);
        m.AddPolygon(new List<(int, int)> { (4, 0), (4, 4), (0, 4) }, false, false, true);
        return m;
    }
    bool OpenBlock(MapDocument m, int bx, int by)
    {
        if (m.AngledWalls.Count != 0) return false;
        for (int x = bx; x < bx + 4; x++)
            for (int y = by; y < by + 4; y++)
            {
                if (!m.Cells.Contains(new CellKey(x, y)) || !m.FloorAt(x + 0.5, y + 0.5)) return false;
                if (x < bx + 3 && m.HasWall(new EdgeKey(new CellKey(x, y), Side.East))) return false;
                if (y < by + 3 && m.HasWall(new EdgeKey(new CellKey(x, y), Side.South))) return false;
            }
        return true;
    }
    var mp = MergedPair();
    Check(OpenBlock(mp, 0, 0), "merged pair: diagonal cells are floor with no wall");
    var mpSnap = mp.CaptureRegion(0, 0, 3, 3);
    mp.ClearRegion(0, 0, 3, 3);
    mp.PasteRegion(mpSnap, 10, 10);
    Check(OpenBlock(mp, 10, 10) && mp.Cells.Count == 16, "merged pair: shared wall stays gone after move");
    var mpCopy = MergedPair();
    mpCopy.PasteRegion(mpCopy.CaptureRegion(0, 0, 3, 3), 10, 0);
    Check(OpenBlock(mpCopy, 0, 0) && OpenBlock(mpCopy, 10, 0), "merged pair: shared wall stays gone after paste");
    var mr = MergedPair();
    var mrSnap = MapDocument.Rotate(mr.CaptureRegion(0, 0, 3, 3), 1);
    mr.ClearRegion(0, 0, 3, 3);
    mr.PasteRegion(mrSnap, 20, 20);
    Check(OpenBlock(mr, 20, 16) && mr.Cells.Count == 16, "merged pair: shared wall stays gone after rotate");
    var mu = new MapDocument();
    var muHist = new History();
    muHist.Initialize(mu);
    mu.AddPolygon(new List<(int, int)> { (0, 0), (4, 0), (0, 4) }, false, false, true);
    muHist.Commit(mu);
    mu.AddPolygon(new List<(int, int)> { (4, 0), (4, 4), (0, 4) }, false, false, true);
    muHist.Commit(mu);
    mu.AddRoom(10, 10, 11, 11);
    muHist.Commit(mu);
    mu.ApplyFrom(muHist.Undo()!);
    Check(OpenBlock(mu, 0, 0) && !mu.Cells.Contains(new CellKey(10, 10)),
        "merged pair: shared wall stays gone after undo");
    mu.ApplyFrom(muHist.Undo()!);
    Check(mu.AngledWalls.Count == 1, "merged pair: undoing the second polygon brings the diagonal back");
    mu.ApplyFrom(muHist.Redo()!);
    Check(OpenBlock(mu, 0, 0), "merged pair: redo opens it again");
    var ml = MapSerializer.FromJson(MapSerializer.ToJson(MergedPair()));
    Check(OpenBlock(ml, 0, 0), "merged pair: shared wall stays gone after save and load");

    // erasing one of two merged polygons gives the other its wall back
    var e2 = new MapDocument();
    e2.AddPolygon(new List<(int, int)> { (0, 0), (4, 0), (0, 4) }, false, false, true);
    e2.AddPolygon(new List<(int, int)> { (4, 0), (8, 0), (8, 4), (0, 4) }, false, false, true);
    Check(e2.AngledWalls.Count == 0, "erase merged: setup diagonal opened");
    var eh = new CanvasHarness();
    eh.Doc.ApplyFrom(e2);
    eh.Canvas.Mode = ToolMode.Erase;
    eh.Canvas.EraseAdjustWalls = true;
    eh.Drag(0, 0, 3, 3);
    var ed = eh.Doc;
    Check(!ed.FloorAt(0.5, 0.5) && !ed.FloorAt(3.5, 0.5) && !ed.FloorAt(2.5, 3.5),
        "erase merged: erased polygon's floor gone");
    Check(ed.FloorAt(4.5, 0.5) && ed.FloorAt(7.5, 3.5) && ed.FloodRegions.Count == 1,
        "erase merged: other polygon keeps the floor outside the erase");
    bool westWalls = true;
    for (int y = 0; y < 4; y++) westWalls &= ed.HasWall(new EdgeKey(new CellKey(4, y), Side.West));
    Check(westWalls, "erase merged: other polygon gets a wall along the erase edge");
    Check(ed.AngledWalls.Count == 0, "erase merged: no angled walls left behind");
    eh.Dispose();

    // room with merged polygons attached, erased in pieces. the strips around
    // an erase can overlap along a line, which used to leave the ring walk
    // with a dead end and throw.
    var rm = new MapDocument();
    rm.AddRoom(0, 0, 9, 5);
    rm.AddPolygon(new List<(int, int)> { (10, 0), (14, 0), (10, 4) }, false, false, true);
    rm.AddPolygon(new List<(int, int)> { (14, 0), (14, 4), (10, 4) }, false, false, true);
    bool threw = false;
    try
    {
        foreach (var (x0, y0, x1, y1) in new[] { (13, 1, 14, 3), (8, 1, 11, 2), (8, 3, 11, 4) })
        {
            rm.ClearRegion(x0, y0, x1, y1);
            rm.RebuildRoomBoundaryWalls(x0, y0, x1, y1);
        }
    }
    catch (Exception) { threw = true; }
    Check(!threw, "room plus merged polygons: erasing in pieces does not throw");
    Check(!rm.FloorAt(10.5, 1.5) && !rm.FloorAt(11.5, 4.5) && !rm.FloorAt(13.5, 2.5),
        "room plus merged polygons: erased floor gone");
    Check(rm.FloorAt(12.5, 0.5) && rm.FloorAt(10.5, 0.5) && rm.FloorAt(12.5, 3.5),
        "room plus merged polygons: floor outside the erases kept");
}
{
    // legacy v3 file: polygons stored as a vertex loop next to their walls
    // and fill cells, with cut cells in the cell list and no flood region
    var src = new MapDocument();
    src.AddRoom(0, 0, 1, 1);
    src.AddPolygon(new List<(int, int)> { (2, 0), (5, 0), (2, 3) });
    var node = System.Text.Json.Nodes.JsonNode.Parse(MapSerializer.ToJson(src))!.AsObject();
    node["version"] = 3;
    node["flood_regions"] = new System.Text.Json.Nodes.JsonArray();
    var cellArr = node["cells"]!.AsArray();
    foreach (var cut in new[] { "4,0", "3,1", "2,2" }) cellArr.Add(cut);
    node["polygons"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"verts\":[\"2,0\",\"5,0\",\"2,3\"]}]");
    string v3 = node.ToJsonString();
    var lg = MapSerializer.FromJson(v3);
    Check(lg.FloodRegions.Count == 1 && lg.FloodRegions[0].Rings[0].Count == 3
        && lg.FloodRegions[0].Rings[0].Contains((2, 0)) && lg.FloodRegions[0].Rings[0].Contains((5, 0))
        && lg.FloodRegions[0].Rings[0].Contains((2, 3)),
        "legacy v3: polygon loads as a flood region on the exact boundary");
    Check(!lg.Cells.Contains(new CellKey(4, 0)) && !lg.Cells.Contains(new CellKey(3, 1))
        && !lg.Cells.Contains(new CellKey(2, 2)) && lg.Cells.Contains(new CellKey(2, 0)),
        "legacy v3: cut cells dropped, fill cells kept");
    Check(lg.AngledWalls.Count == 1 && lg.Cells.Contains(new CellKey(0, 0)), "legacy v3: walls and room intact");
    Check(!MapSerializer.ToJson(lg).Contains("polygons"), "legacy v3: saving writes no polygons field");

    // mapporter helper
    var dto = System.Text.Json.JsonSerializer.Deserialize<MapFileDto>(v3)!;
    Check(dto.Polygons is { Count: 1 }, "port polygons: v3 dto reads the polygons field");
    var ported = MapSerializer.PortPolygons(dto);
    Check(ported.Polygons is null && ported.FloodRegions.Count == 1 && ported.AngledWalls.Count == 1
        && !ported.Cells.Contains("4,0"),
        "port polygons: polygons become walls, cells and a flood region");
    var plain = System.Text.Json.JsonSerializer.Deserialize<MapFileDto>(MapSerializer.ToJson(src))!;
    Check(ReferenceEquals(MapSerializer.PortPolygons(plain), plain), "port polygons: map without polygons unchanged");

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
        // a door is 75% of a cell long (30px at scale 40): glyph to 15px out, wall past it
        var tip = h8.PixelAt(bmp8, 107, 160);
        Check(tip.r == 245 && tip.g == 220 && tip.b == 60,
              "render: vertex door is 75% of one wall long");
        var past = h8.PixelAt(bmp8, 101, 160);
        Check(past.r == 235 && past.g == 235 && past.b == 240,
              "render: vertex door is the same length as a single wall door");

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
        var vtip = h9.PixelAt(bmp9, 120, 147);
        Check(vtip.r == 245 && vtip.g == 220 && vtip.b == 60,
              "render: vertical vertex door runs along the wall");

        // a single wall door is a rectangle with its long side along the wall
        var h10 = new CanvasHarness();
        var ve = EdgeKey.Normalize(new CellKey(1, 1), Side.East); // x=2, y 1..2 -> screen (120, 80..120)
        h10.Doc.SetWall(ve, true);
        h10.Doc.TryPlaceDoor(ve, out _);
        var bmp10 = h10.Render();
        var along = h10.PixelAt(bmp10, 120, 113);
        Check(along.r == 245 && along.g == 220 && along.b == 60,
              "render: door is long along its wall");
        var across = h10.PixelAt(bmp10, 133, 100);
        Check(!(across.r == 245 && across.g == 220 && across.b == 60),
              "render: door is thin across its wall");
        var wallPast = h10.PixelAt(bmp10, 120, 83);
        Check(wallPast.r == 235 && wallPast.g == 235 && wallPast.b == 240,
              "render: wall resumes past the 75% door");
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

// ---------- wall tool ----------
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
    h.Canvas.Mode = ToolMode.Wall;
    h.PressW(2, 2); h.MoveToAt(h.ScreenWorld(6, 2)); h.ReleaseW(6, 2);
    Check(h.CommitCount == 1, "wall drag commits exactly once");
    Check(h.Doc.Walls.Count == 4
        && h.Doc.HasWall(new EdgeKey(new CellKey(2, 2), Side.North))
        && h.Doc.HasWall(new EdgeKey(new CellKey(5, 2), Side.North)),
        "wall drag creates the wall series from start to end vertex");
    h.Dispose();
}

// snap: press at (2.4,2.4) snaps to (2,2), release at (5.6,2.4) snaps to (6,2)
{
    var h = new CanvasHarness();
    h.Canvas.Mode = ToolMode.Wall;
    h.PressW(2.4, 2.4); h.MoveToAt(h.ScreenWorld(5.6, 2.4)); h.ReleaseW(5.6, 2.4);
    Check(h.Doc.Walls.Count == 4
        && h.Doc.HasWall(new EdgeKey(new CellKey(2, 2), Side.North))
        && h.Doc.HasWall(new EdgeKey(new CellKey(5, 2), Side.North)),
        "wall snaps press and release to the nearest lattice vertices");
    h.Dispose();
}

// zero-length drag: press and release on the same vertex commits nothing
{
    var h = new CanvasHarness();
    h.Canvas.Mode = ToolMode.Wall;
    h.PressW(3, 3); h.ReleaseW(3, 3);
    Check(h.CommitCount == 0 && h.Doc.Walls.Count == 0,
        "wall zero-length drag commits nothing");
    h.Dispose();
}

// diagonal drag: one angled wall, one commit
{
    var h = new CanvasHarness();
    h.Canvas.Mode = ToolMode.Wall;
    h.PressW(2, 2); h.MoveToAt(h.ScreenWorld(5, 4)); h.ReleaseW(5, 4);
    Check(h.CommitCount == 1 && h.Doc.AngledWalls.Count == 1
        && h.Doc.AngledWalls[0] == new AngledWall(2, 2, 5, 4),
        "wall diagonal drag creates one angled wall with one commit");
    h.Dispose();
}

// undo: one line drag is one history entry (history wired like MainWindow)
{
    var h = new CanvasHarness();
    var hist = new History();
    hist.Initialize(h.Doc);
    h.Canvas.ActionCompleted += () => hist.Commit(h.Doc);
    h.Canvas.Mode = ToolMode.Wall;
    h.PressW(2, 2); h.MoveToAt(h.ScreenWorld(6, 2)); h.ReleaseW(6, 2);
    var restored = hist.Undo();
    Check(restored != null && restored.Walls.Count == 0,
        "wall drag undoes as a single history entry");
    h.Dispose();
}

// erase removes line walls
{
    var h = new CanvasHarness();
    h.Canvas.Mode = ToolMode.Wall;
    h.PressW(2, 2); h.MoveToAt(h.ScreenWorld(6, 2)); h.ReleaseW(6, 2);
    h.Canvas.Mode = ToolMode.Erase;
    h.PressW(2, 2); h.MoveToAt(h.ScreenWorld(6, 2)); h.ReleaseW(6, 2);
    Check(h.Doc.Walls.Count == 0, "erase drag removes wall-line walls");
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
    // two disconnected triangles 5 cells apart: filling A must not add B's cells
    var tdoc2 = new MapDocument();
    tdoc2.AddLine(0, 0, 4, 0); tdoc2.AddLine(0, 0, 0, 3); tdoc2.AddLine(0, 3, 4, 0);
    tdoc2.AddLine(6, 0, 10, 0); tdoc2.AddLine(6, 0, 6, 3); tdoc2.AddLine(6, 3, 10, 0);
    var treg2 = tdoc2.FloodFillRegion(new CellKey(0, 0));
    Check(treg2 is not null && treg2.Count == 3, "disconnected triangles: fill region is only triangle A cells");
    var tring2 = tdoc2.BuildFloodRings(treg2!);
    Check(tring2.Count == 1 && tring2[0].Count == 3, "disconnected triangles: one ring, 3 corners");
    var tpts2 = new HashSet<(double, double)>(tring2[0]);
    Check(tpts2.Contains((0, 0)) && tpts2.Contains((4, 0)) && tpts2.Contains((0, 3)), "disconnected triangles ring has triangle A corners");
    Check(!tpts2.Contains((6, 0)) && !tpts2.Contains((10, 0)) && !tpts2.Contains((6, 3)), "disconnected triangles ring excludes triangle B corners");
    var cdoc = new MapDocument();
    Check(cdoc.FloodFillRegion(new CellKey(0, 0)) is null, "open field exceeds cap, returns null");
    // erase cuts a hole in a flood region, walls survive
    var edoc = new MapDocument();
    edoc.AddLine(0, 0, 4, 0); edoc.AddLine(4, 0, 4, 4); edoc.AddLine(4, 4, 0, 4); edoc.AddLine(0, 4, 0, 0);
    edoc.AddFloodRegion(new FloodRegion { Rings = edoc.BuildFloodRings(edoc.FloodFillRegion(new CellKey(1, 1))!) });
    Check(edoc.FloodRegions.Count == 1, "AddFloodRegion adds the region");
    edoc.ClearRegion(1, 1, 2, 2);
    Check(edoc.FloodRegions.Count == 1 && !edoc.FloorAt(1.5, 1.5) && !edoc.FloorAt(2.5, 2.5)
        && edoc.FloorAt(0.5, 0.5) && edoc.FloorAt(3.5, 3.5) && edoc.Walls.Count == 16,
        "erase clips a hole in the flood region, walls survive");
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
    fc.Canvas.Mode = ToolMode.Wall;
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

// --- PROBE: pawn save round-trips (user bug report: pawns vanish on re-open) ---
{
    var proj = new MapProject();
    var lib = new PawnLibrary();
    var t = new PawnTemplate { Name = "Skeleton", Affiliation = "Enemy" };
    t.ApplyAffiliationDefaults();
    lib.Add(t);
    var t2 = new PawnTemplate { Name = "Boss", Affiliation = "Enemy", Status = "alert", ImageB64 = "AB", ImageExt = ".png" };
    t2.ApplyAffiliationDefaults();
    lib.Add(t2);
    proj.Maps[0].Doc.AddRoom(0, 0, 3, 3);
    proj.Maps[0].Doc.PlacePawn(new Pawn { Cell = new CellKey(1, 1), TemplateId = t.Id });
    proj.Maps[0].Doc.PlacePawn(new Pawn { Cell = new CellKey(2, 2), TemplateId = t2.Id, Status = "none" });
    string pj = MapSerializer.ProjectToJson(proj);
    var proj2 = MapSerializer.ProjectFromJson(pj);
    Check(proj2.Maps[0].Doc.Pawns.Count == 2, "probe: project save/load keeps pawn instances");
    Check(proj2.Maps[0].Doc.Pawns[new CellKey(1, 1)].TemplateId == "p1", "probe: template id survives project round-trip");
    Check(proj2.Maps[0].Doc.Pawns[new CellKey(2, 2)].Status == "none", "probe: per-instance status survives project round-trip");

    // per-project library: ids embedded in a project file keep their ids when
    // merged into a fresh project library (there is no global library file).
    var freshLib = new PawnLibrary();
    var embedded = MapSerializer.LibraryFromDtos(new List<PawnTemplateDto>
    {
        new PawnTemplateDto { Id = "p1", Name = "Skeleton" },
        new PawnTemplateDto { Id = "p5", Name = "Ghost" },
    });
    var idMap2 = freshLib.MergeAppend(embedded);
    Check(freshLib.Find("p1")!.Name == "Skeleton" && freshLib.Find("p5")!.Name == "Ghost", "probe: embedded ids keep their ids in a fresh project library");
    Check(idMap2["p1"] == "p1" && idMap2["p5"] == "p5", "probe: free ids map to themselves (identity remap)");
    Check(freshLib.NewId() == "p6", "probe: counter bumps past embedded ids");
    // an older settings.json still holding the lists: read once as legacy
    // presets, then the next settings write drops them
    var legacyPath = new AppSettings().SettingsPath();
    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(legacyPath)!);
    System.IO.File.WriteAllText(legacyPath, "{\"grid_draw_spacing\":1,\"default_door_type\":\"gate\",\"door_types\":[{\"name\":\"gate\",\"r\":10,\"g\":20,\"b\":30}],\"pawn_statuses\":[{\"name\":\"stunned\",\"r\":1,\"g\":2,\"b\":3,\"thickness\":4}]}");
    var lp = AppSettings.LegacyPresets();
    Check(lp is not null && lp.DoorTypes!.Any(d => d.Name == "gate" && d.R == 10) && lp.PawnStatuses!.Any(x => x.Name == "stunned") && lp.AffiliationColors is null,
        "legacy presets: lists read from an old settings.json, missing ones stay null");
    Check(AppSettings.Load().GridDrawSpacing == 1, "legacy presets: old settings.json still loads as settings");
    // AppSettings real Save/Load: app wide values persist, preset lists are not written
    var st = AppSettings.Default();
    st.GridDrawSpacing = 2;
    st.Save();
    var savedJson = System.IO.File.ReadAllText(st.SettingsPath());
    Check(!savedJson.Contains("door_types") && !savedJson.Contains("pawn_statuses") && !savedJson.Contains("affiliation_colors") && !savedJson.Contains("default_door"),
        "probe: settings.json has no preset lists and no default door");
    Check(AppSettings.LegacyPresets() is null, "probe: settings.json without lists gives no legacy presets");
    var st2 = AppSettings.Load();
    Check(st2.GridDrawSpacing == 2, "probe: scalar setting survives settings disk round-trip");
    Check(st2.ToolHotkeys["Wall"] == "W", "probe: hotkeys survive settings disk round-trip");
    // settings import/export: app wide only, campaign keys never travel
    var expJson = st2.ExportJson();
    using (var expDoc = System.Text.Json.JsonDocument.Parse(expJson))
    {
        var r = expDoc.RootElement;
        Check(r.GetProperty("kind").GetString() == AppSettings.KindSettings, "settings export: marked kind settings");
        Check(!r.TryGetProperty("door_types", out _) && !r.TryGetProperty("pawn_statuses", out _) && !r.TryGetProperty("affiliation_colors", out _),
            "settings export: door presets, pawn statuses and affiliation colors left out");
        Check(r.GetProperty("grid_draw_spacing").GetInt32() == 2 && r.GetProperty("tool_hotkeys").GetProperty("Wall").GetString() == "W",
            "settings export: values and hotkeys written");
    }
    Check(expJson.Contains('\n'), "settings export: file is indented");
    Check(AppSettings.ParseImport("{\"kind\":\"campaign\",\"maps\":[]}") is null, "settings import: a campaign file is refused");
    Check(AppSettings.ParseImport("{\"kind\":\"map\",\"grid_draw_spacing\":3}") is null, "settings import: any other kind is refused");
    Check(AppSettings.ParseImport("{\"maps\":[],\"name\":\"x\"}") is null, "settings import: an old campaign without kind is refused");
    Check(AppSettings.ParseImport("{\"door_types\":[{\"name\":\"x\"}]}") is null, "settings import: a file with only campaign keys is refused");
    Check(AppSettings.ParseImport("[1,2]") is null, "settings import: a json array is refused");
    bool threw = false;
    try { AppSettings.ParseImport("not json"); } catch { threw = true; }
    Check(threw, "settings import: broken json throws for the caller to report");
    Check(AppSettings.ParseImport(st2.ToJson()) is { GridDrawSpacing: 2 }, "settings import: a plain settings.json (the before import backup) is accepted");
    var imp = AppSettings.ParseImport("{\"grid_draw_spacing\":0,\"wall_thickness\":999,\"autosave_minutes\":-5,\"default_label_text\":\"  \",\"default_door_state\":\"ajar\",\"tool_hotkeys\":{\"Wall\":\"q\",\"Room\":\"F1\",\"Bogus\":\"Z\"}}")!;
    Check(imp is not null && imp.GridDrawSpacing == 1 && imp.WallThickness == AppSettings.MaxWallThickness && imp.AutosaveMinutes == 0,
        "settings import: out of range numbers are clamped");
    Check(imp!.DefaultLabelText == AppSettings.Default().DefaultLabelText,
        "settings import: blank label text falls back to default, an old default_door_state key is ignored");
    Check(imp.ToolHotkeys["Wall"] == "Q" && imp.ToolHotkeys["Room"] == "R" && imp.ToolHotkeys["Select"] == "S" && !imp.ToolHotkeys.ContainsKey("Bogus"),
        "settings import: letters kept upper case, bad or missing hotkeys back to default, unknown tools dropped");
    var liveSet = AppSettings.Default();
    var liveRef = liveSet;
    var src = AppSettings.ParseImport(st2.ExportJson())!;
    liveSet.CopyAppWideFrom(src);
    Check(ReferenceEquals(liveSet, liveRef) && liveSet.GridDrawSpacing == 2, "settings import: copy fills the existing object");
    src.ToolHotkeys["Wall"] = "K";
    Check(liveSet.ToolHotkeys["Wall"] == "W", "settings import: hotkeys copied, not shared with the imported object");
    // legacy library (ids p1..pN, no counter): BumpNextId must not re-issue a live id
    var legacy = new PawnLibrary();
    legacy.Pawns.Add(new PawnTemplate { Id = "p1", Name = "old1" });
    legacy.Pawns.Add(new PawnTemplate { Id = "p7", Name = "old7" });
    legacy.BumpNextId();
    Check(legacy.NewId() == "p8", "probe: BumpNextId skips legacy live ids");

    // --- library embedding probes (task 1: library travels inside the project file) ---
    {
        // any bytes will do for the round-trip checks below
        var probeB64 = System.Convert.ToBase64String(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());

        var probeLib = new PawnLibrary();
        probeLib.Pawns.Add(new PawnTemplate { Id = "p1", Name = "Skeleton", Affiliation = "Skeleton", ImageB64 = probeB64, ImageExt = ".png", Status = "stunned" });

        // DTO round-trip keeps fields
        var dtos = MapSerializer.LibraryToDtos(probeLib);
        var back = MapSerializer.LibraryFromDtos(dtos);
        Check(back.Count == 1 && back[0].Name == "Skeleton" && back[0].Status == "stunned", "probe: library DTO round-trip keeps fields");
        Check(dtos[0].ImageB64.Length > 0 && dtos[0].ImageExt == ".png", "probe: embedded base64 image present in DTO");

        // project file embeds the library; extraction returns it
        var probeProj = new MapProject();
        probeProj.Maps[0].Doc.Pawns[new CellKey(1, 1)] = new Pawn { TemplateId = "p1" };
        var probeJson = MapSerializer.ProjectToJson(probeProj, probeLib);
        var extracted = MapSerializer.ExtractLibrary(probeJson);
        Check(extracted.Count == 1 && extracted[0].Id == "p1" && extracted[0].ImageB64.Length > 0, "probe: project file embeds library + image");

        // v2 project file (no library field) loads with empty embedded library
        var v2 = "{\"version\":2,\"active\":0,\"maps\":[{\"name\":\"map\",\"map\":{\"width\":10,\"height\":10}}]}";
        Check(MapSerializer.ExtractLibrary(v2).Count == 0, "probe: v2 project file has empty embedded library");

        // library export file round-trip (import/export format)
        var libJson = MapSerializer.LibraryToJson(probeLib);
        var libBack = MapSerializer.LibraryFromDtos(MapSerializer.LibraryFromJson(libJson));
        Check(libBack.Count == 1 && libBack[0].Name == "Skeleton", "probe: library export file round-trip");
        // source generated json keeps the old file format: same key name, still indented
        Check(libJson.Contains("\"pawn_library\": ["), "probe: library export keeps pawn_library key and indentation");
        Check(probeJson.Contains("\n  "), "probe: project file is still indented");

        // append: free ids keep their id, colliding ids get fresh ids, remap returned
        var live = new PawnLibrary();
        live.Pawns.Add(new PawnTemplate { Id = "p1", Name = "Skeleton" });
        var incoming = MapSerializer.LibraryFromDtos(new List<PawnTemplateDto>
        {
            new PawnTemplateDto { Id = "p1", Name = "Skeleton" },   // collides -> fresh id
            new PawnTemplateDto { Id = "p9", Name = "Ghost" },      // free -> keeps p9
        });
        var map = live.MergeAppend(incoming);
        Check(live.Pawns.Count == 3, "probe: append adds all imported templates");
        Check(live.Find("p9") is not null && live.Find("p9")!.Name == "Ghost", "probe: free id keeps its id on append");
        Check(map["p1"] != "p1" && live.Find(map["p1"])!.Name == "Skeleton", "probe: colliding id gets fresh id + remap on append");

        // overwrite: deletes all, appends imported
        var live2 = new PawnLibrary();
        live2.Pawns.Add(new PawnTemplate { Id = "p1", Name = "Skeleton" });
        var map2 = live2.MergeOverwrite(new List<PawnTemplate> { new PawnTemplate { Id = "p1", Name = "Wraith" } });
        Check(live2.Pawns.Count == 1 && live2.Find("p1")!.Name == "Wraith", "probe: overwrite deletes all, free id keeps its id");

        // base64 travels with the template through a merge: a fresh-id import
        // keeps its image bytes; no files are written anywhere.
        var live3 = new PawnLibrary();
        live3.Pawns.Add(new PawnTemplate { Id = "p1", Name = "Skeleton", ImageB64 = probeB64, ImageExt = ".png" });
        var incoming2 = MapSerializer.LibraryFromDtos(new List<PawnTemplateDto>
        {
            new PawnTemplateDto { Id = "p1", Name = "Ghost", ImageB64 = probeB64, ImageExt = ".png" },
        });
        var map3 = live3.MergeAppend(incoming2);
        var ghost = live3.Find(map3["p1"])!;
        Check(ghost.Id != "p1" && ghost.ImageB64 == probeB64, "probe: base64 travels with a fresh-id import");
        Check(live3.Find("p1")!.Name == "Skeleton" && live3.Find("p1")!.ImageB64 == probeB64, "probe: existing template base64 untouched on append");

        // project save/load round-trip with embedded library
        var probeProj2 = MapSerializer.ProjectFromJson(probeJson);
        Check(probeProj2.Maps[0].Doc.Pawns[new CellKey(1, 1)].TemplateId == "p1", "probe: pawn instance survives project round-trip with library embedded");
    }
}

// --- pawn image upload: every upload compresses to a 50x50 png ---
// bitmap decode/encode needs the headless avalonia boot
using var pawnHarness = new CanvasHarness();
byte[] SourcePng(int w, int h)
{
    var wb = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
        Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
    var px = new byte[w * h * 4];
    for (int i = 0; i < px.Length; i += 4) { px[i] = 20; px[i + 1] = 20; px[i + 2] = 200; px[i + 3] = 255; }
    using (var lk = wb.Lock())
        System.Runtime.InteropServices.Marshal.Copy(px, 0, lk.Address, px.Length);
    using var ms = new System.IO.MemoryStream();
    wb.Save(ms);
    wb.Dispose();
    return ms.ToArray();
}
bool CompressIs50(byte[] png)
{
    using var ms = new System.IO.MemoryStream(png);
    using var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
    return bmp.PixelSize.Width == 50 && bmp.PixelSize.Height == 50;
}
var c1 = DungeonMapper.Models.PawnImage.Compress(SourcePng(100, 60));
Check(c1[0] == 0x89 && c1[1] == (byte)'P' && c1[2] == (byte)'N' && c1[3] == (byte)'G', "pawn compress: output is png bytes");
Check(CompressIs50(c1), "pawn compress: 100x60 becomes 50x50");
Check(CompressIs50(DungeonMapper.Models.PawnImage.Compress(SourcePng(20, 10))), "pawn compress: 20x10 upscales to 50x50");
Check(CompressIs50(DungeonMapper.Models.PawnImage.Compress(SourcePng(80, 80))), "pawn compress: square source stays 50x50");
// center crop: solid color survives the crop+scale pipeline
using var skb = SkiaSharp.SKBitmap.Decode(c1);
var sp = skb.GetPixel(25, 25);
Check(sp.Red == 200 && sp.Green == 20 && sp.Blue == 20, "pawn compress: solid color survives crop+scale");

// --- campaigns and map files: kind field, export/import, library dedupe ---
{
    var lib = new PawnLibrary();
    var used = new PawnTemplate { Name = "Ghoul", Affiliation = "Enemy" }; used.ApplyAffiliationDefaults(); lib.Add(used);
    var unused = new PawnTemplate { Name = "Elf", Affiliation = "Ally" }; unused.ApplyAffiliationDefaults(); lib.Add(unused);
    var proj = new MapProject();
    proj.Maps[0].Name = "Crypt";
    AddRoom(proj.Maps[0].Doc, 0, 0, 3, 3);
    proj.Maps[0].Doc.PlacePawn(new Pawn { Cell = new CellKey(1, 1), TemplateId = used.Id });
    proj.AddMap("Tower");

    var campaignJson = MapSerializer.ProjectToJson(proj, lib);
    Check(campaignJson.Contains("\"kind\": \"campaign\""), "campaign file carries kind campaign");
    Check(MapSerializer.DetectKind(campaignJson) == SaveFileKind.Campaign, "detect: campaign by kind");
    Check(MapSerializer.DetectKind("{\"version\":3,\"maps\":[]}") == SaveFileKind.Campaign, "detect: old campaign by maps list");
    Check(MapSerializer.DetectKind(MapSerializer.ToJson(new MapDocument())) == SaveFileKind.BareMap, "detect: bare map by cells");
    Check(MapSerializer.DetectKind("{\"hello\":1}") == SaveFileKind.Unknown, "detect: unrelated json is unknown");
    Check(MapSerializer.DetectKind("not json") == SaveFileKind.Unknown, "detect: garbage is unknown");

    var mapJson = MapSerializer.MapToJson(proj.Maps[0], lib);
    Check(MapSerializer.DetectKind(mapJson) == SaveFileKind.Map, "detect: exported map by kind");
    var imp = MapSerializer.ImportFromJson(mapJson, "fallback");
    Check(imp.Maps.Count == 1 && imp.Maps[0].Name == "Crypt", "map file import keeps its name");
    Check(imp.Maps[0].Doc.Cells.Count == proj.Maps[0].Doc.Cells.Count, "map file import keeps the cells");
    Check(imp.Library.Count == 1 && imp.Library[0].Name == "Ghoul", "map file embeds only the templates its pawns use");

    // re-importing into the same library reuses the identical template
    var idMap = lib.MergeImport(MapSerializer.LibraryFromDtos(imp.Library));
    Check(lib.Pawns.Count == 2, "merge import: identical template is not duplicated");
    Check(idMap.TryGetValue(used.Id, out var same) && same == used.Id, "merge import: id maps to the existing template");
    // a changed template is added as a new one
    var changed = MapSerializer.LibraryFromDtos(imp.Library);
    changed[0].Name = "Ghoul king";
    lib.MergeImport(changed);
    Check(lib.Pawns.Count == 3, "merge import: a different template is added");
    // importing into an empty library keeps the template
    var fresh = new PawnLibrary();
    fresh.MergeImport(MapSerializer.LibraryFromDtos(imp.Library));
    Check(fresh.Pawns.Count == 1, "merge import: empty library gains the template");

    var all = MapSerializer.ImportFromJson(campaignJson, "fallback");
    Check(all.Maps.Count == 2 && all.Maps[1].Name == "Tower", "campaign import brings every map");
    Check(all.Active == 1, "campaign import keeps the active index");
    var bare = MapSerializer.ImportFromJson(MapSerializer.ToJson(proj.Maps[0].Doc), "old cave");
    Check(bare.Maps.Count == 1 && bare.Maps[0].Name == "old cave", "bare map import is named after the file");
    bool threw = false;
    try { MapSerializer.ImportFromJson("{\"hello\":1}", "x"); } catch (System.IO.InvalidDataException) { threw = true; }
    Check(threw, "import of an unknown file throws");

    // names and order
    Check(proj.UniqueName("Lair") == "Lair", "unique name: free name is kept");
    Check(proj.UniqueName("crypt") == "crypt (2)", "unique name: case-insensitive clash gets a suffix");
    proj.AddMap("Crypt (2)");
    Check(proj.UniqueName("Crypt") == "Crypt (3)", "unique name: skips taken suffixes");
    Check(proj.UniqueName("Crypt", 0) == "Crypt", "unique name: the renamed map does not clash with itself");
    proj.SetActive(0);
    proj.MoveMap(0, 2);
    Check(proj.Maps[2].Name == "Crypt" && proj.ActiveIndex == 2, "move map: active map follows the move");
    proj.MoveMap(2, 5);
    Check(proj.Maps[2].Name == "Crypt", "move map: out of range is ignored");
}

// a polygon floor covers exactly the polygon: cells whose center sits on a
// diagonal, cells on the wrong side of a concave edge, and cells crossed by two
// edges are all cut down to the inside part. a hole ring winds the other way,
// so the areas are summed signed.
{
    double Area(List<(double x, double y)> r)
    {
        double a = 0;
        for (int i = 0; i < r.Count; i++) { var (ax, ay) = r[i]; var (bx, by) = r[(i + 1) % r.Count]; a += ax * by - bx * ay; }
        return a / 2;
    }
    void Same(List<(int, int)> v, string what)
    {
        var d = new MapDocument();
        d.AddPolygon(v);
        double want = Math.Abs(Area(v.Select(p => ((double)p.Item1, (double)p.Item2)).ToList()));
        double got = Math.Abs(d.FloodRegions.Sum(fr => fr.Rings.Sum(Area)));
        Check(d.FloodRegions.Count == 1 && d.FloodRegions[0].Rings.All(r => r.Count >= 3) && Math.Abs(got - want) < 1e-6, $"polygon floor matches the polygon: {what} ({got} vs {want})");
    }
    Same(new List<(int, int)> { (-1, 0), (4, 0), (8, 9), (2, 7) }, "centers on the diagonal");
    Same(new List<(int, int)> { (0, 0), (2, 0), (2, 2) }, "45 degree edge through centers");
    Same(new List<(int, int)> { (0, 0), (6, 0), (6, 6), (3, 2), (0, 6) }, "concave");
    Same(new List<(int, int)> { (16, 10), (11, 14), (11, 21), (7, 19) }, "cells crossed by two edges");
    Same(new List<(int, int)> { (8, 10), (6, 10), (4, 4), (9, 9), (8, 2), (18, 6) }, "reflex spike pinched at a corner");
}

// a merging polygon only opens walls that touch it. two rooms side by side
// keep the wall and door between them when a polygon is drawn below one.
{
    var d = new MapDocument();
    d.AddRoom(0, 0, 3, 3);
    d.AddRoom(4, 0, 7, 3);
    var shared = EdgeKey.Normalize(new CellKey(3, 1), Side.East);
    d.TryPlaceDoor(shared, out _);
    d.AddPolygon(new List<(int, int)> { (0, 4), (4, 4), (6, 6), (0, 6) }, mergeNearby: true);
    Check(d.Doors.ContainsKey(shared), "polygon merge: a door between two other rooms survives");
    Check(d.Walls.ContainsKey(EdgeKey.Normalize(new CellKey(3, 2), Side.East)), "polygon merge: a wall between two other rooms survives");
    Check(!d.Walls.ContainsKey(EdgeKey.Normalize(new CellKey(1, 3), Side.South)), "polygon merge: the wall shared with the room above opens");
}

// --- range labels ---
{
    var rl = new MapLabel { Cell = new CellKey(2, 3), W = 4, H = 2, Text = "Hall" };
    Check(rl.IsRange && rl.Covers(new CellKey(5, 4)) && rl.Covers(new CellKey(2, 3)), "range label: covers its corners");
    Check(!rl.Covers(new CellKey(6, 3)) && !rl.Covers(new CellKey(2, 5)), "range label: not past its edges");
    Check(!new MapLabel { Cell = new CellKey(0, 0) }.IsRange, "range label: 1x1 is not a range");

    var m = new MapDocument();
    m.Labels.Add(new MapLabel { Cell = new CellKey(1, 1), W = 3, H = 2, Text = "R", Align = LabelAlign.Right, FontSize = 30, R = 1, G = 2, B = 3 });
    m.Labels.Add(new MapLabel { Cell = new CellKey(9, 9), Text = "one" });
    var back = MapSerializer.FromJson(MapSerializer.ToJson(m));
    var r = back.Labels.First(l => l.Cell == new CellKey(1, 1));
    var o = back.Labels.First(l => l.Cell == new CellKey(9, 9));
    Check(r.W == 3 && r.H == 2 && r.Align == LabelAlign.Right, "range label: size and align round-trip");
    Check(o.W == 1 && o.H == 1 && o.Align == LabelAlign.Center, "range label: single label defaults round-trip");
    var old = MapSerializer.FromJson("{\"labels\":[{\"cell\":\"1,2\",\"text\":\"a\"}]}");
    Check(old.Labels.Count == 1 && old.Labels[0].W == 1 && old.Labels[0].Align == LabelAlign.Center, "range label: old files load as single centered labels");

    // paste keeps the range, align, size and color
    var snap = m.CaptureRegion(0, 0, 4, 4);
    var p2 = new MapDocument();
    p2.PasteRegion(snap, 10, 10);
    var pl = p2.Labels.Single();
    Check(pl.Cell == new CellKey(11, 11) && pl.W == 3 && pl.H == 2 && pl.Align == LabelAlign.Right
        && pl.FontSize == 30 && pl.R == 1, "range label: paste keeps every field");

    // a ccw quarter turn of a 4x2 label covering (0..3, 0..1) gives a 2x4 at (0,-4)
    var rd = new MapDocument();
    rd.Labels.Add(new MapLabel { Cell = new CellKey(0, 0), W = 4, H = 2, Text = "t" });
    var rs = MapDocument.Rotate(rd.CaptureRegion(0, 0, 3, 1), 1);
    var rot = rs.Labels.Single();
    Check(rot.Cell == new CellKey(0, -4) && rot.W == 2 && rot.H == 4, "range label: rotate swaps width and height");
    var full = MapDocument.Rotate(MapDocument.Rotate(rs, 1), 2);
    Check(full.Labels.Single().Cell == new CellKey(0, 0) && full.Labels.Single().W == 4, "range label: four turns come back");
}

// --- multi-line labels ---
{
    var ls = MapCanvas.LabelLines("a\r\nb\n\nc");
    Check(ls.Length == 4 && ls[0] == "a" && ls[1] == "b" && ls[2] == "" && ls[3] == "c", "multi-line label: splits on line breaks and keeps blank lines");
    Check(MapCanvas.LabelLines("one").Length == 1, "multi-line label: one line stays one line");

    var m = new MapDocument();
    m.Labels.Add(new MapLabel { Cell = new CellKey(0, 0), W = 3, H = 2, Text = "Great Hall\nnorth wing" });
    var back = MapSerializer.FromJson(MapSerializer.ToJson(m));
    Check(back.Labels.Single().Text == "Great Hall\nnorth wing", "multi-line label: line breaks round-trip");

    // an empty range label sits at 80% of half its height
    var empty = new MapLabel { Cell = new CellKey(0, 0), W = 4, H = 2 };
    Check(Math.Abs(MapCanvas.LabelDefaultSize(empty) - 2 * MapLabel.RefScale * 0.5 * 0.8) < 1e-9, "multi-line label: range default is 80% of the cap");
    Check(MapCanvas.LabelDefaultSize(new MapLabel { Cell = new CellKey(0, 0) }) == MapLabel.DefaultSize, "multi-line label: single cell keeps the global default");
    var two = new MapLabel { Cell = new CellKey(0, 0), W = 6, H = 1, Text = "hall" };
    var twoLines = new MapLabel { Cell = new CellKey(0, 0), W = 6, H = 1, Text = "hall\nhall" };
    Check(MapCanvas.LabelDefaultSize(twoLines) < MapCanvas.LabelDefaultSize(two), "multi-line label: more lines shrink a range label to fit");
}

// --- wall thickness setting ---
{
    Check(AppSettings.Default().WallThickness == 3, "wall thickness: defaults to 3 pixels");
    var oldSettings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"wall_r\":1}")!;
    Check(oldSettings.WallThickness == 3, "wall thickness: old settings files get the default");
    var ws = AppSettings.Default(); ws.WallThickness = 9;
    var wsBack = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(ws))!;
    Check(wsBack.WallThickness == 9, "wall thickness: round-trips through settings json");

    // a north wall on cell (1,1) runs along screen y=80 from x 80 to 120
    using var thin = new CanvasHarness();
    thin.Doc.SetWall(EdgeKey.Normalize(new CellKey(1, 1), Side.North), true);
    var tp = thin.PixelAt(thin.Render(), 100, 84);
    Check(!(tp.r == 235 && tp.g == 235 && tp.b == 240), "wall thickness: a 3 pixel wall stays thin");
    using var thick = new CanvasHarness();
    thick.Canvas.Settings.WallThickness = 10;
    thick.Doc.SetWall(EdgeKey.Normalize(new CellKey(1, 1), Side.North), true);
    var kp = thick.PixelAt(thick.Render(), 100, 84);
    Check(kp.r == 235 && kp.g == 235 && kp.b == 240, "wall thickness: a 10 pixel wall draws wider");
    thick.Canvas.Settings.WallThickness = 500;
    var cp = thick.PixelAt(thick.Render(), 100, 95);
    Check(!(cp.r == 235 && cp.g == 235 && cp.b == 240), "wall thickness: clamped to 20 pixels");
}

// grid: lines inside rooms, a dot on each vertex in empty space
{
    Check(AppSettings.Default().GridDotR == 105, "grid dots: default dot color");
    using var gh = new CanvasHarness();
    var gs = gh.Canvas.Settings;
    gs.MajorGridR = 255; gs.MajorGridG = 0; gs.MajorGridB = 0;
    gs.MinorGridR = 255; gs.MinorGridG = 0; gs.MinorGridB = 0;
    gs.GridDotR = 0; gs.GridDotG = 255; gs.GridDotB = 0;
    for (int x = 3; x <= 5; x++) for (int y = 3; y <= 5; y++) gh.Doc.Cells.Add(new CellKey(x, y));
    gh.Doc.AddFloodRegion(new FloodRegion { Rings = { new() { (10, 2), (13, 2), (13, 5), (10, 5) } } });
    // world (x,y) is screen ((x+1)*40, (y+1)*40). a 1px line on a whole pixel
    // splits over two pixels, so look at the 2x2 block around a point.
    bool Near(Avalonia.Media.Imaging.RenderTargetBitmap bmp, int sx, int sy, Func<(byte r, byte g, byte b, byte a), bool> f)
    {
        for (int dx = -1; dx <= 0; dx++) for (int dy = -1; dy <= 0; dy++)
            if (f(gh.PixelAt(bmp, sx + dx, sy + dy))) return true;
        return false;
    }
    bool Red((byte r, byte g, byte b, byte a) c) => c.r > c.g + 60;
    bool Green((byte r, byte g, byte b, byte a) c) => c.g > c.r + 60;
    var gb = gh.Render();
    Check(!Near(gb, 380, 360, Red), "grid: no line in empty space between vertices");
    Check(Near(gb, 360, 360, Green), "grid: a dot on a vertex in empty space");
    Check(!Near(gb, 380, 360, Green), "grid: no dot between vertices in empty space");
    Check(Near(gb, 200, 220, Red), "grid: lines still draw inside a cell room");
    Check(!Near(gb, 200, 200, Green), "grid: no dot on a vertex inside a cell room");
    Check(Near(gb, 480, 180, Red), "grid: lines still draw inside a flood floor");
    Check(!Near(gb, 480, 160, Green), "grid: no dot on a vertex inside a flood floor");
    Check(!Near(gb, 200, 300, Red), "grid: room lines stop at the room edge");
    // spacing 2 with minor off: only both-major vertices get a dot
    gs.GridDrawSpacing = 2; gs.ShowMinorGrid = false;
    var gb2 = gh.Render();
    Check(Near(gb2, 360, 360, Green), "grid: major dot at spacing 2");
    Check(!Near(gb2, 400, 360, Green), "grid: no minor dot when minor grid is off");
    gs.ShowMinorGrid = true;
    Check(Near(gh.Render(), 400, 360, Green), "grid: minor dot when minor grid is on");
    // dots stay on their vertices when the view is panned by odd amounts
    var offField = typeof(DungeonMapper.Controls.MapCanvas).GetField("_offset", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    gs.ShowMinorGrid = false;
    bool panOk = true;
    foreach (int psp in new[] { 2, 3 })
        foreach (var (ox, oy) in new[] { (-1.37, -0.81), (5.23, 2.6), (-11.9, 7.45) })
        {
            gs.GridDrawSpacing = psp;
            offField.SetValue(gh.Canvas, new Avalonia.Point(ox, oy));
            var pb = gh.Render();
            bool DotAt(double wx, double wy)
            {
                int sx = (int)Math.Round((wx - ox) * 40), sy = (int)Math.Round((wy - oy) * 40);
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
                    if (Green(gh.PixelAt(pb, sx + dx, sy + dy))) return true;
                return false;
            }
            // a major vertex in empty space well inside the view, and the
            // vertex one cell over which is not major
            double mx = Math.Ceiling((ox + 18) / psp) * psp, my = Math.Ceiling((oy + 12) / psp) * psp;
            if (!DotAt(mx, my) || DotAt(mx + 1, my) || DotAt(mx, my + 1)) panOk = false;
        }
    Check(panOk, "grid: dots stay on their vertices when panned");
}

// open doors are hollow: the floor shows through, not a background strip
{
    using var dh = new CanvasHarness();
    var ds = dh.Canvas.Settings;
    ds.CellFillR = 0; ds.CellFillG = 0; ds.CellFillB = 255;
    ds.BackgroundR = 255; ds.BackgroundG = 0; ds.BackgroundB = 0;
    for (int x = 1; x <= 6; x++) for (int y = 1; y <= 3; y++) dh.Doc.Cells.Add(new CellKey(x, y));
    for (int y = 1; y <= 3; y++) dh.Doc.SetWall(EdgeKey.Normalize(new CellKey(3, y), Side.East), true);
    dh.Doc.SetWall(EdgeKey.Normalize(new CellKey(5, 1), Side.East), true);
    dh.Doc.SetWall(EdgeKey.Normalize(new CellKey(5, 2), Side.East), true);
    dh.Doc.Doors[EdgeKey.Normalize(new CellKey(3, 2), Side.East)] = new Door { Type = "standard", State = DoorState.Open };
    dh.Doc.VertexDoors[new VertexDoorKey(6, 2, false)] = new VertexDoor { Type = "standard", State = DoorState.Open };
    var db = dh.Render(400, 260);
    bool Blue((byte r, byte g, byte b, byte a) p) => p.b > 200 && p.r < 40;
    // door centers: edge door at world (4,2.5), vertex door at world (6,2)
    Check(Blue(dh.PixelAt(db, 200, 140)) && Blue(dh.PixelAt(db, 200, 138)), "door: open wall door shows floor inside");
    Check(Blue(dh.PixelAt(db, 280, 120)) && Blue(dh.PixelAt(db, 280, 116)), "door: open vertex door shows floor inside");
    dh.Doc.Doors[EdgeKey.Normalize(new CellKey(3, 2), Side.East)].State = DoorState.Closed;
    Check(!Blue(dh.PixelAt(dh.Render(400, 260), 200, 140)), "door: closed door stays solid");
}

// open door outline is a fixed pixel width like walls, a bit thinner than them
{
    var widths = new List<(int door, int wall)>();
    foreach (double zs in new[] { 40.0, 80.0, 120.0 })
    {
        using var zh = new CanvasHarness();
        zh.Canvas.Settings.WallThickness = 8;
        typeof(DungeonMapper.Controls.MapCanvas).GetField("_scale", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(zh.Canvas, zs);
        for (int x = 1; x <= 6; x++) for (int y = 1; y <= 3; y++) zh.Doc.Cells.Add(new CellKey(x, y));
        for (int y = 1; y <= 3; y++) zh.Doc.SetWall(EdgeKey.Normalize(new CellKey(3, y), Side.East), true);
        zh.Doc.Doors[EdgeKey.Normalize(new CellKey(3, 2), Side.East)] = new Door { Type = "standard", State = DoorState.Open };
        var zb = zh.Render(800, 600);
        int cx = (int)(5 * zs);
        int Count(int sy, Func<(byte r, byte g, byte b, byte a), bool> f)
        {
            int n = 0;
            for (int x = cx - (int)Math.Max(20, zs * 0.3); x <= cx; x++) if (f(zh.PixelAt(zb, x, sy))) n++;
            return n;
        }
        int door = Count((int)(3.5 * zs), p => p.r > 200 && p.g > 180 && p.b < 120);
        int wall = Count((int)(2.2 * zs), p => p.r > 200 && p.g > 200 && p.b > 200) * 2;
        widths.Add((door, wall));
    }
    // the wall count covers its left half only, so it is doubled above
    Check(widths.Max(w => w.door) - widths.Min(w => w.door) <= 1, $"door: open outline width holds across zoom ({string.Join(",", widths.Select(w => w.door))})");
    Check(widths.All(w => w.door >= 5 && w.door < w.wall), $"door: open outline a bit thinner than the wall ({string.Join(",", widths)})");
    // zoomed far out with thick walls the outline is capped so the door stays hollow
    {
        using var fh = new CanvasHarness();
        fh.Canvas.Settings.WallThickness = 20;
        fh.Canvas.Settings.CellFillR = 0; fh.Canvas.Settings.CellFillG = 0; fh.Canvas.Settings.CellFillB = 255;
        typeof(DungeonMapper.Controls.MapCanvas).GetField("_scale", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(fh.Canvas, 12.0);
        for (int x = 1; x <= 6; x++) for (int y = 1; y <= 3; y++) fh.Doc.Cells.Add(new CellKey(x, y));
        fh.Doc.SetWall(EdgeKey.Normalize(new CellKey(3, 2), Side.East), true);
        fh.Doc.Doors[EdgeKey.Normalize(new CellKey(3, 2), Side.East)] = new Door { Type = "standard", State = DoorState.Open };
        var p = fh.PixelAt(fh.Render(200, 100), 60, 42);
        Check(p.b > 200 && p.r < 40, "door: open door stays hollow zoomed out with thick walls");
    }
}

// pawn library: import classify, export subset, recent list, favorites
{
    var lib = new PawnLibrary();
    var sk = new PawnTemplate { Name = "Skeleton", Affiliation = "Enemy" }; sk.ApplyAffiliationDefaults(); lib.Add(sk);
    var wo = new PawnTemplate { Name = "Wolf", Affiliation = "Enemy", Favorite = true, Hero = true }; wo.ApplyAffiliationDefaults(); lib.Add(wo);
    var ga = new PawnTemplate { Name = "Gareth", Affiliation = "Player" }; ga.ApplyAffiliationDefaults(); lib.Add(ga);

    var same = new PawnTemplate { Name = "Skeleton", Affiliation = "Enemy" }; same.ApplyAffiliationDefaults();
    var look = new PawnTemplate { Name = "skeleton", Affiliation = "Enemy", ColorOverride = true, InnerR = 9 }; look.ApplyAffiliationDefaults();
    var fresh = new PawnTemplate { Name = "Ghoul", Affiliation = "Enemy" }; fresh.ApplyAffiliationDefaults();
    Check(lib.Classify(same) == PawnLibrary.ImportKind.Have, "pawns: identical template classifies as already have");
    Check(lib.Classify(look) == PawnLibrary.ImportKind.NewLook, "pawns: same name with a new look classifies as new look");
    Check(lib.Classify(fresh) == PawnLibrary.ImportKind.New, "pawns: unknown template classifies as new");

    var sub = lib.Subset(new[] { ga.Id, sk.Id });
    Check(sub.Pawns.Count == 2 && sub.Pawns[0] == sk && sub.Pawns[1] == ga, "pawns: export subset keeps library order");
    var back = MapSerializer.LibraryFromDtos(MapSerializer.LibraryFromJson(MapSerializer.LibraryToJson(sub)));
    Check(back.Count == 2 && back[1].Name == "Gareth", "pawns: exported subset reads back");

    var full = MapSerializer.LibraryFromDtos(MapSerializer.LibraryFromJson(MapSerializer.LibraryToJson(lib)));
    Check(full.Single(x => x.Name == "Wolf").Favorite && !full.Single(x => x.Name == "Skeleton").Favorite, "pawns: favorite survives library json");
    Check(full.Single(x => x.Name == "Wolf").Hero && !full.Single(x => x.Name == "Skeleton").Hero, "pawns: hero survives library json");
    var proj = new MapProject();
    var pjson = MapSerializer.ProjectToJson(proj, lib);
    var fromProj = MapSerializer.LibraryFromDtos(MapSerializer.LibraryFromJson(pjson));
    Check(fromProj.Count == 3 && fromProj.Single(x => x.Name == "Wolf").Favorite, "pawns: import picker reads a campaign file's library");

    // importing into a library that already has them adds nothing
    int before = lib.Pawns.Count;
    lib.MergeImport(MapSerializer.LibraryFromDtos(MapSerializer.LibraryFromJson(MapSerializer.LibraryToJson(lib))));
    Check(lib.Pawns.Count == before, "pawns: re-importing the same pawns does not duplicate them");

    var rl = new PawnLibrary();
    foreach (var id in new[] { "a", "b", "c", "a", "d", "e", "f", "g" }) rl.NoteRecent(id);
    Check(string.Join(",", rl.Recent) == "g,f,e,d,a", $"pawns: recent is newest first, deduplicated, max 5 ({string.Join(",", rl.Recent)})");
}

// ---- door types: rename and retype across doors and undo snapshots ----
{
    var dd = new MapDocument();
    var ek = EdgeKey.Normalize(new CellKey(0, 0), Side.North);
    dd.Walls[ek] = true;
    dd.Doors[ek] = new Door { Type = "iron" };
    var dw = new AngledWall(0, 0, 2, 2);
    dd.AngledWalls.Add(dw);
    dd.AngledDoors[dw] = new List<AngledDoor> { new AngledDoor { Type = "iron" }, new AngledDoor { Type = "standard" } };
    dd.TryPlaceVertexDoor(5, 5, true, "iron", "closed", out _);
    Check(dd.AllDoors().Count() == 4, "door types: AllDoors covers edge, angled and vertex doors");
    Check(dd.AllDoors().Count(x => x.Type == "iron") == 3, "door types: counts by type include vertex doors");

    var hist = new History();
    hist.Initialize(dd);
    dd.Doors[ek].State = DoorState.Open;
    hist.Commit(dd);
    int changed = dd.RetypeDoors("iron", "steel");
    hist.Rewrite(x => x.RetypeDoors("iron", "steel"));
    Check(changed == 3 && dd.AllDoors().All(x => x.Type != "iron"), "door types: RetypeDoors renames every door of the type");
    var back = hist.Undo();
    Check(back is not null && back.AllDoors().Count(x => x.Type == "steel") == 3 && back.AllDoors().All(x => x.Type != "iron"),
        "door types: undo after a rename keeps the new name");
    Check(back is not null && back.Doors.Values.First().State == DoorState.Closed, "door types: rewrite keeps the rest of the snapshot");
}

// ---- tool side panel info box counts ----
{
    var td = new MapDocument();
    Check(DungeonMapper.Views.MainWindow.ToolInfoText(DungeonMapper.Controls.ToolMode.Stair, td, "standard") == "0 stairs on this map", "tool info: empty map says 0 stairs");
    td.Cells.Add(new CellKey(0, 0));
    Check(DungeonMapper.Views.MainWindow.ToolInfoText(DungeonMapper.Controls.ToolMode.Floor, td, "standard") == "1 floor cell on this map", "tool info: one floor cell is singular");
    var e1 = EdgeKey.Normalize(new CellKey(0, 0), Side.North);
    var e2 = EdgeKey.Normalize(new CellKey(0, 0), Side.South);
    td.Doors[e1] = new Door { Type = "iron" };
    td.Doors[e2] = new Door { Type = "standard" };
    Check(DungeonMapper.Views.MainWindow.ToolInfoText(DungeonMapper.Controls.ToolMode.Door, td, "iron") == "2 doors on this map\n1 of them iron", "tool info: door count with the default type");
    Check(DungeonMapper.Views.MainWindow.ToolInfoText(DungeonMapper.Controls.ToolMode.Erase, td, "iron") == "", "tool info: erase has no info");
}

// ---- config views: door spots survive undo, delete keeps the wall ----
{
    var cd = new MapDocument();
    var ce = EdgeKey.Normalize(new CellKey(0, 0), Side.North);
    cd.Walls[ce] = true;
    var edgeDoor = new Door { Type = "iron" };
    cd.Doors[ce] = edgeDoor;
    var vk = new VertexDoorKey(3, 3, true);
    var vDoor = new VertexDoor();
    cd.VertexDoors[vk] = vDoor;
    var aw = new AngledWall(0, 0, 4, 4);
    cd.AngledWalls.Add(aw);
    var a0 = new AngledDoor { T = 0.25 };
    var a1 = new AngledDoor { T = 0.75, Type = "iron" };
    cd.AngledDoors[aw] = new List<AngledDoor> { a0, a1 };

    Check(ReferenceEquals(cd.DoorAtSpot(cd.DoorSpotOf(edgeDoor)), edgeDoor), "config views: edge door spot round trips");
    Check(ReferenceEquals(cd.DoorAtSpot(cd.DoorSpotOf(vDoor)), vDoor), "config views: vertex door spot round trips");
    Check(ReferenceEquals(cd.DoorAtSpot(cd.DoorSpotOf(a1)), a1), "config views: angled door spot round trips");
    Check(cd.DoorSpotOf(new Door()) is null && cd.DoorAtSpot(null) is null, "config views: a door not on the map has no spot");

    // a lone vertex door would not survive the json undo snapshot (no walls
    // around it), so it is deleted before the round trip
    Check(cd.RemoveDoorConfig(cd.VertexDoors[vk]) && cd.VertexDoors.Count == 0, "config views: deleting a vertex door");

    // undo swaps in copies, the spot finds the copy of the same door
    var hist = new History();
    hist.Initialize(cd);
    var spot = cd.DoorSpotOf(a1);
    cd.AngledDoors[aw][1].State = DoorState.Open;
    hist.Commit(cd);
    cd.ApplyFrom(hist.Undo()!);
    Check(cd.DoorAtSpot(spot) is AngledDoor again && !ReferenceEquals(again, a1) && again.T == 0.75 && again.State == DoorState.Closed,
        "config views: after undo the spot finds the restored door");

    var e2 = cd.Doors[ce];
    Check(cd.RemoveDoorConfig(e2) && !cd.Doors.ContainsKey(ce) && cd.HasWall(ce), "config views: deleting an edge door keeps its wall");
    var first = cd.AngledDoors[aw][0];
    Check(cd.RemoveDoorConfig(first) && cd.AngledDoors[aw].Count == 1 && cd.AngledDoors[aw][0].T == 0.75, "config views: deleting one angled door keeps the other");
    Check(cd.RemoveDoorConfig(cd.AngledDoors[aw][0]) && !cd.AngledDoors.ContainsKey(aw) && cd.AngledWalls.Contains(aw),
        "config views: deleting the last angled door drops the entry, the wall stays");
    Check(!cd.RemoveDoorConfig(first), "config views: deleting a door twice does nothing");

    // info boxes
    var m1 = new MapDocument();
    m1.Doors[EdgeKey.Normalize(new CellKey(0, 0), Side.North)] = new Door { Type = "iron" };
    m1.Doors[EdgeKey.Normalize(new CellKey(1, 0), Side.North)] = new Door { Type = "iron" };
    var m2 = new MapDocument();
    m2.VertexDoors[new VertexDoorKey(0, 0, false)] = new VertexDoor { Type = "iron" };
    Check(DungeonMapper.Views.MainWindow.DoorConfigInfo("iron", m1, new[] { m1, m2 }) == "2 doors of this type on this map, 3 in the campaign",
        "config views: door info counts this map and the campaign");
    Check(DungeonMapper.Views.MainWindow.DoorConfigInfo("iron", m2, new[] { m1 }) == "1 door of this type on this map, 3 in the campaign",
        "config views: door info counts an unlisted open map once");
    Check(DungeonMapper.Views.MainWindow.StairConfigInfo(3) == "3 connected stairs, all change direction together", "config views: stair info for a group");
    Check(DungeonMapper.Views.MainWindow.StairConfigInfo(1) == "a single stair, no others are connected to it", "config views: stair info for a lone stair");
}

// --- move tool: a leftover selection must not block a new move ---
{
    // a region dragged with the select tool, then the move tool picked
    var h = new CanvasHarness();
    h.Doc.AddRoom(0, 0, 1, 1);
    h.Canvas.Mode = ToolMode.Select;
    h.Drag(0, 0, 1, 1);
    h.Canvas.Mode = ToolMode.Move; h.Canvas.CancelMove();
    h.Drag(0, 0, 1, 1);
    Check(h.Canvas.MoveArmed, "move stuck: a select tool selection does not block a new move drag");
    h.Click(5, 5);
    Check(h.Doc.Cells.Contains(new CellKey(5, 5)) && !h.Doc.Cells.Contains(new CellKey(0, 0)), "move stuck: the move after a select tool selection lands");

    // a move drag over empty space selects nothing, the next drag still works
    var h2 = new CanvasHarness();
    h2.Doc.AddRoom(0, 0, 1, 1);
    h2.Canvas.Mode = ToolMode.Move;
    h2.Drag(10, 10, 12, 12);
    Check(!h2.Canvas.MoveArmed && !h2.Canvas.HasSelection, "move stuck: an empty move drag leaves no selection");
    h2.Drag(0, 0, 1, 1);
    Check(h2.Canvas.MoveArmed, "move stuck: a drag after an empty one arms the move");
    h2.Click(5, 5);
    Check(h2.Doc.Cells.Contains(new CellKey(5, 5)) && !h2.Doc.Cells.Contains(new CellKey(0, 0)), "move stuck: the move after an empty drag lands");
}

// --- move tool: pawns only shrinks the selection to the pawns ---
{
    var h = new CanvasHarness();
    h.Doc.AddRoom(0, 0, 8, 8);
    h.Doc.PlacePawn(new Pawn { Cell = new CellKey(3, 3), TemplateId = "p1" });
    h.Doc.PlacePawn(new Pawn { Cell = new CellKey(4, 5), TemplateId = "p2" });
    h.Canvas.Mode = ToolMode.Move;
    h.Canvas.MovePawnsOnly = true;
    h.Drag(0, 0, 8, 8);
    Check(h.Canvas.MoveArmed, "pawns only: a wide drag around the pawns arms the move");
    // the anchor is the pawn box (3,3)-(4,5), so a click at (10,10) puts the
    // top left pawn there, not 3 cells in from it
    h.Click(10, 10);
    Check(h.Doc.Pawns.TryGetValue(new CellKey(10, 10), out var a) && a.TemplateId == "p1"
        && h.Doc.Pawns.TryGetValue(new CellKey(11, 12), out var b) && b.TemplateId == "p2",
        "pawns only: the move lands anchored on the pawns, not the dragged rectangle");
    Check(h.Doc.Pawns.Count == 2 && h.Doc.Cells.Contains(new CellKey(3, 3)) && h.Doc.Cells.Count == 81,
        "pawns only: the pawns leave the source and the room stays");

    // one pawn shrinks to a single cell and still moves, without opening its config
    var h2 = new CanvasHarness();
    h2.Doc.AddRoom(0, 0, 5, 5);
    h2.Doc.PlacePawn(new Pawn { Cell = new CellKey(2, 2), TemplateId = "p1" });
    int cfg = 0; h2.Canvas.PawnConfigRequested += _ => cfg++;
    h2.Canvas.Mode = ToolMode.Move;
    h2.Canvas.MovePawnsOnly = true;
    h2.Drag(0, 0, 4, 4);
    Check(h2.Canvas.MoveArmed && cfg == 0, "pawns only: one pawn in a wide drag arms without opening its config");
    h2.Click(8, 8);
    Check(h2.Doc.Pawns.ContainsKey(new CellKey(8, 8)) && h2.Doc.Pawns.Count == 1, "pawns only: one pawn lands on the click");

    // a drag with no pawns in it leaves nothing armed and nothing selected
    var h3 = new CanvasHarness();
    h3.Doc.AddRoom(0, 0, 5, 5);
    h3.Canvas.Mode = ToolMode.Move;
    h3.Canvas.MovePawnsOnly = true;
    h3.Drag(0, 0, 4, 4);
    Check(!h3.Canvas.MoveArmed && !h3.Canvas.HasSelection && h3.Statuses.Contains("no pawns in that region"),
        "pawns only: a drag over no pawns arms nothing");

    // flipping pawns only while armed drops the move
    var h4 = new CanvasHarness();
    h4.Doc.AddRoom(0, 0, 5, 5);
    h4.Doc.PlacePawn(new Pawn { Cell = new CellKey(2, 2), TemplateId = "p1" });
    h4.Canvas.Mode = ToolMode.Move;
    h4.Canvas.MovePawnsOnly = true;
    h4.Drag(0, 0, 4, 4);
    h4.Canvas.MovePawnsOnly = false;
    Check(!h4.Canvas.MoveArmed && !h4.Canvas.HasSelection, "pawns only: toggling it while armed cancels the move");

    // without pawns only the whole dragged rectangle still moves
    var h5 = new CanvasHarness();
    h5.Doc.AddRoom(0, 0, 3, 3);
    h5.Doc.PlacePawn(new Pawn { Cell = new CellKey(2, 2), TemplateId = "p1" });
    h5.Canvas.Mode = ToolMode.Move;
    h5.Drag(0, 0, 3, 3);
    h5.Click(10, 10);
    Check(h5.Doc.Pawns.ContainsKey(new CellKey(12, 12)) && h5.Doc.Cells.Contains(new CellKey(10, 10)),
        "move: the full move still anchors on the dragged rectangle");
}

// --- move tool: the pawns only ghost draws the pawns at the cursor ---
{
    var h = new CanvasHarness();
    h.Doc.AddRoom(0, 0, 5, 5);
    h.Doc.PlacePawn(new Pawn { Cell = new CellKey(2, 2), TemplateId = "p1" });
    h.Canvas.Mode = ToolMode.Move;
    h.Canvas.MovePawnsOnly = true;
    h.Drag(0, 0, 4, 4);
    h.MoveTo(12, 3);
    var bmp = h.Render();
    var at = h.PixelAt(bmp, (int)h.ScreenOf(12, 3).X, (int)h.ScreenOf(12, 3).Y);
    var off = h.PixelAt(bmp, (int)h.ScreenOf(18, 3).X, (int)h.ScreenOf(18, 3).Y);
    Check(at != off, "pawns only: the ghost shows the pawn under the cursor");
}

// --- stair delete takes the whole connected group ---
{
    var d = new MapDocument();
    for (int x = 0; x < 4; x++) d.Stairs[new CellKey(x, 0)] = new Stair();
    d.Stairs[new CellKey(8, 0)] = new Stair();
    Check(d.RemoveConnectedStairs(new CellKey(2, 0)) == 4 && d.Stairs.Count == 1 && d.Stairs.ContainsKey(new CellKey(8, 0)),
        "stair delete: removes the connected group and leaves a separate stair");
    var w = new MapDocument();
    for (int x = 0; x < 4; x++) w.Stairs[new CellKey(x, 0)] = new Stair();
    w.SetWall(EdgeKey.Normalize(new CellKey(1, 0), Side.East), true);
    Check(w.RemoveConnectedStairs(new CellKey(0, 0)) == 2 && w.Stairs.ContainsKey(new CellKey(2, 0)),
        "stair delete: a wall splits the group, the far side stays");
    Check(w.RemoveConnectedStairs(new CellKey(9, 9)) == 0, "stair delete: no stair, nothing removed");
}

// --- clear clipboard (select tool button) ---
{
    var h = new CanvasHarness();
    h.Doc.AddRoom(0, 0, 3, 3);
    h.Canvas.Mode = ToolMode.Select;
    h.Drag(0, 0, 3, 3);
    h.Canvas.CopySelection();
    Check(h.Canvas.HasClipboard && h.Canvas.HasSelection, "clear clipboard: copy arms the clipboard");
    h.MoveTo(10, 3);
    var ghost = h.Render();
    var withGhost = h.PixelAt(ghost, (int)h.ScreenOf(10, 3).X, (int)h.ScreenOf(10, 3).Y);
    h.Canvas.ClearClipboard();
    Check(!h.Canvas.HasClipboard && !h.Canvas.HasSelection, "clear clipboard: clipboard and selection gone");
    var bmp = h.Render();
    var after = h.PixelAt(bmp, (int)h.ScreenOf(10, 3).X, (int)h.ScreenOf(10, 3).Y);
    var empty = h.PixelAt(bmp, (int)h.ScreenOf(18, 3).X, (int)h.ScreenOf(18, 3).Y);
    Check(withGhost != empty && after == empty, "clear clipboard: the paste ghost is no longer drawn");
    Check(h.Statuses.Contains("clipboard cleared"), "clear clipboard: status message");
}

// --- zoom to fit hotkey ---
{
    var s = AppSettings.Default();
    Check(s.ToolHotkeys[AppSettings.FitHotkey] == "Z", "fit hotkey: Z by default");
    Check(s.ToolHotkeys.Values.Count(v => v == "Z") == 1, "fit hotkey: Z not used by any tool");
    var old = AppSettings.Parse("{\"tool_hotkeys\":{\"Room\":\"R\"}}");
    Check(old is not null && old.ToolHotkeys[AppSettings.FitHotkey] == "Z", "fit hotkey: old settings files get the default");
}

// --- floor grid opacity ---
{
    var h = new CanvasHarness();
    h.Doc.AddRoom(0, 0, 3, 3);
    int lx = (int)h.ScreenWorld(2, 0).X, ly = (int)h.ScreenOf(1, 1).Y; // the line at world x=2, mid cell
    int fx = (int)h.ScreenOf(1, 1).X; // floor, no line
    var full = h.Render();
    var line100 = h.PixelAt(full, lx, ly);
    var floor = h.PixelAt(full, fx, ly);
    h.Canvas.Settings.FloorGridOpacity = 0;
    var none = h.Render();
    Check(line100 != floor, "floor grid opacity: line visible at 100");
    Check(h.PixelAt(none, lx, ly) == h.PixelAt(none, fx, ly), "floor grid opacity: line hidden at 0");
    h.Canvas.Settings.FloorGridOpacity = 50;
    var half = h.PixelAt(h.Render(), lx, ly);
    Check(half != line100 && half != floor, "floor grid opacity: 50 blends the line into the floor");
    Check(AppSettings.Default().FloorGridOpacity == 100, "floor grid opacity: default 100");
    var bad = AppSettings.ParseImport("{\"floor_grid_opacity\":250}");
    Check(bad is not null && bad.FloorGridOpacity == 100, "floor grid opacity: import clamps out of range values");
}

// --- campaign content key (autosave skip, save prompt on exit) ---
{
    MapProject Two()
    {
        var pr = new MapProject();
        pr.Maps.Add(new MapEntry { Name = "B" });
        return pr;
    }
    var a = Two();
    a.Maps[0].Doc.AddRoom(0, 0, 3, 3);
    a.Maps[0].Doc.AddRoom(10, 0, 12, 2);
    var b = Two();
    b.Maps[0].Doc.AddRoom(10, 0, 12, 2);
    b.Maps[0].Doc.AddRoom(0, 0, 3, 3);
    string ka = MapSerializer.ProjectContentKey(a);
    Check(ka == MapSerializer.ProjectContentKey(b), "content key: same map drawn in a different order matches");
    a.ActiveIndex = 1;
    Check(ka == MapSerializer.ProjectContentKey(a), "content key: switching the active map is not a change");
    a.Maps[0].Doc.Stairs[new CellKey(1, 1)] = new Stair();
    Check(ka != MapSerializer.ProjectContentKey(a), "content key: an edit is a change");
    a.Maps[0].Doc.Stairs.Remove(new CellKey(1, 1));
    Check(ka == MapSerializer.ProjectContentKey(a), "content key: undoing the edit by hand matches again");
    a.Maps[1].Name = "C";
    Check(ka != MapSerializer.ProjectContentKey(a), "content key: renaming a map is a change");
    var lib = new PawnLibrary();
    string kl = MapSerializer.ProjectContentKey(b, lib);
    lib.Add(new PawnTemplate { Name = "orc" });
    Check(kl != MapSerializer.ProjectContentKey(b, lib), "content key: a pawn library edit is a change");
}

// --- close open doors (door tool) ---
{
    var d = new MapDocument();
    d.AddRoom(0, 0, 5, 0);
    Door Put(int x, string type, string state)
    {
        d.TryPlaceDoor(new EdgeKey(new CellKey(x, 0), Side.North), type, state, out var door);
        return door;
    }
    var a = Put(0, "standard", "open");
    var b = Put(1, "standard", "open");
    var c = Put(2, "standard", "locked");
    var e = Put(3, "standard", "closed");
    var f = Put(4, "portcullis", "open");
    var hist = new History();
    hist.Commit(d);
    Check(d.CloseOpenDoors("standard") == 2, "close doors: two open standard doors closed");
    Check(a.State == DoorState.Closed && b.State == DoorState.Closed, "close doors: open became closed");
    Check(c.State == DoorState.Locked && e.State == DoorState.Closed, "close doors: locked and closed untouched");
    Check(f.State == DoorState.Open, "close doors: other types untouched");
    hist.Commit(d);
    var back = hist.Undo();
    Check(back is not null && back.Doors.Values.Count(x => x.State == DoorState.Open) == 3, "close doors: one undo step reopens all of them");
    Check(d.CloseOpenDoors("portcullis") == 1 && d.CloseOpenDoors("portcullis") == 0, "close doors: nothing left to close");
}

// --- door wall gap ---
{
    // a long wall between two rooms with a door in the middle (edge x=4..5 at y=3)
    var h = new CanvasHarness();
    h.Doc.AddRoom(0, 0, 9, 2);
    h.Doc.AddRoom(0, 3, 9, 5);
    h.Doc.TryPlaceDoor(new EdgeKey(new CellKey(4, 3), Side.North), "standard", "open", out _);
    // a point on the wall 0.3 of a cell past the door edge's end, at the wall line
    var p = h.ScreenWorld(5.3, 3);
    var floorPt = h.ScreenOf(7, 4);
    var none = h.Render();
    var wallPx = h.PixelAt(none, (int)p.X, (int)p.Y);
    h.Canvas.Settings.DoorWallGap = 50;
    var gapBmp = h.Render();
    Check(wallPx != h.PixelAt(none, (int)floorPt.X, (int)floorPt.Y), "door gap: at 0 the wall is drawn next to the door");
    Check(h.PixelAt(gapBmp, (int)p.X, (int)p.Y) == h.PixelAt(gapBmp, (int)floorPt.X, (int)floorPt.Y), "door gap: at 50 the next wall is cleared to floor color");
    var far = h.ScreenWorld(6.6, 3);
    Check(h.PixelAt(gapBmp, (int)far.X, (int)far.Y) == wallPx, "door gap: the wall past the gap is still drawn");
    // a door next to a corner: the perpendicular wall stays closed
    var c = new CanvasHarness();
    c.Doc.AddRoom(0, 0, 3, 3);
    c.Doc.TryPlaceDoor(new EdgeKey(new CellKey(0, 0), Side.North), "standard", "open", out _);
    c.Canvas.Settings.DoorWallGap = 50;
    var cb = c.Render();
    var corner = c.ScreenWorld(0, 0.3);
    Check(c.PixelAt(cb, (int)corner.X, (int)corner.Y) == wallPx, "door gap: the corner wall is not cut");
    var outside = c.ScreenWorld(-0.3, 0);
    Check(c.PixelAt(cb, (int)outside.X, (int)outside.Y) != c.PixelAt(cb, (int)c.ScreenOf(2, 2).X, (int)c.ScreenOf(2, 2).Y),
        "door gap: no floor painted past the end of the wall");
    var bad = AppSettings.ParseImport("{\"door_wall_gap\":90}");
    Check(bad is not null && bad.DoorWallGap == 50, "door gap: import clamps to 50");
}

// --- map objects: model and save format ---
{
    var lib = new ObjectLibrary();
    var statue = lib.Add(new ObjectType { Name = "Statue", Look = new ObjectLook { Fill = ObjectFill.Radial, ColorA = "#aabbcc", Caption = ObjectCaption.Initials } });
    var d = new MapDocument();
    var a = new MapObject { TypeId = statue.Id, X = 2, Y = 3, W = 2, H = 3 };
    var b = new MapObject { TypeId = statue.Id, X = 2, Y = 3, W = 1, H = 1, Name = "Orcus", Look = new ObjectLook { Fill = ObjectFill.Stripes, PictureId = "abc" } };
    d.Objects.Add(a); d.Objects.Add(b);
    Check(d.ObjectAt(new CellKey(2, 3)) == b && d.ObjectAt(new CellKey(3, 5)) == a && d.ObjectAt(new CellKey(4, 3)) is null,
        "objects: ObjectAt finds the topmost object");
    var back = MapSerializer.FromJson(MapSerializer.ToJson(d));
    Check(back.Objects.Count == 2 && back.Objects[0].Id == a.Id && back.Objects[0].W == 2 && back.Objects[0].H == 3 && back.Objects[0].Look is null,
        "objects: map round trip keeps id, rect and type look");
    Check(back.Objects[1].Name == "Orcus" && back.Objects[1].Look?.Fill == ObjectFill.Stripes && back.Objects[1].Look?.PictureId == "abc",
        "objects: own name and own look round trip");
    Check(MapSerializer.FromJson("{\"cells\":[\"0,0\"]}").Objects.Count == 0, "objects: an older map loads with none");
    var bad = MapSerializer.FromJson("{\"objects\":[{\"type_id\":\"x\",\"rect\":\"1,1,0,2\"},{\"type_id\":\"x\",\"rect\":\"oops\"}]}");
    Check(bad.Objects.Count == 0, "objects: rects with no size or bad text are dropped");

    var pr = new MapProject();
    pr.Maps[0].Doc.Objects.Add(a.Copy());
    var ojson = MapSerializer.ProjectToJson(pr, null, null, lib);
    var imp = MapSerializer.ImportFromJson(ojson, "x");
    Check(imp.ObjectTypes.Count == 1 && imp.ObjectTypes[0].Name == "Statue" && imp.ObjectTypes[0].Look.Fill == ObjectFill.Radial,
        "objects: the campaign file carries the object library");
    Check(ojson.Contains("\"Radial\"") && ojson.Contains("\"Initials\""), "objects: look enums are saved as words");
    var lib2 = new ObjectLibrary();
    var map = lib2.MergeAppend(MapSerializer.TidyObjectTypes(imp.ObjectTypes));
    Check(lib2.Types.Count == 1 && map[statue.Id] == statue.Id, "objects: merge into an empty library keeps the id");
    var lib3 = new ObjectLibrary();
    var clash = lib3.Add(new ObjectType { Id = statue.Id, Name = "Painting" });
    var map3 = lib3.MergeAppend(MapSerializer.TidyObjectTypes(MapSerializer.ImportFromJson(ojson, "x").ObjectTypes));
    Check(lib3.Types.Count == 2 && map3[statue.Id] != statue.Id, "objects: an id clash gets a new id");
    var map4 = lib3.MergeAppend(MapSerializer.TidyObjectTypes(MapSerializer.ImportFromJson(ojson, "x").ObjectTypes));
    Check(lib3.Types.Count == 2 && map4[statue.Id] == map3[statue.Id], "objects: importing the same type again reuses it");

    var exp = new MapEntry { Name = "m" };
    exp.Doc.Objects.Add(a.Copy());
    var lib5 = new ObjectLibrary();
    lib5.Add(statue.Clone());
    lib5.Add(new ObjectType { Name = "Unused" });
    var mf = MapSerializer.ImportFromJson(MapSerializer.MapToJson(exp, null, null, lib5), "m");
    Check(mf.ObjectTypes.Count == 1 && mf.ObjectTypes[0].Id == statue.Id && mf.Maps[0].Doc.Objects.Count == 1,
        "objects: a map file carries only the types its objects use");

    string k0 = MapSerializer.ProjectContentKey(pr, null, null, lib);
    lib.Types[0].Look.ColorA = "#000000";
    Check(k0 != MapSerializer.ProjectContentKey(pr, null, null, lib), "objects: a type look edit is an unsaved change");
}

// --- map objects: region operations and undo ---
{
    var d = new MapDocument();
    d.Objects.Add(new MapObject { TypeId = "t", X = 1, Y = 1, W = 2, H = 3 });
    d.Objects.Add(new MapObject { TypeId = "t", X = 5, Y = 0, W = 3, H = 1 }); // sticks out of the region
    var snap = d.CaptureRegion(0, 0, 5, 5);
    Check(snap.Objects.Count == 1 && snap.Objects[0].X == 1, "objects: capture takes objects fully inside");
    d.PasteRegion(snap, 10, 10);
    var pasted = d.Objects[^1];
    Check(d.Objects.Count == 3 && pasted.X == 11 && pasted.Y == 11 && pasted.Id != d.Objects[0].Id, "objects: paste offsets and gives a new id");
    var rot = MapDocument.Rotate(snap, 1);
    Check(rot.Objects.Count == 1 && rot.Objects[0].W == 3 && rot.Objects[0].H == 2, "objects: a quarter turn swaps width and height");
    var rot4 = MapDocument.Rotate(MapDocument.Rotate(rot, 1), 2);
    Check(rot4.Objects[0].X == 1 && rot4.Objects[0].Y == 1 && rot4.Objects[0].W == 2, "objects: four quarter turns come back");
    d.ClearRegion(0, 0, 5, 5);
    Check(d.Objects.Count == 2 && !d.Objects.Any(o => o.X == 1 && o.Y == 1), "objects: clear removes objects fully inside, not ones sticking out");
    var h = new History();
    var u = new MapDocument();
    h.Commit(u);
    u.Objects.Add(new MapObject { TypeId = "t", X = 0, Y = 0, W = 2, H = 2 });
    string id = u.Objects[0].Id;
    h.Commit(u);
    u.ApplyFrom(h.Undo()!);
    Check(u.Objects.Count == 0, "objects: undo removes a placed object");
    u.ApplyFrom(h.Redo()!);
    Check(u.Objects.Count == 1 && u.Objects[0].Id == id, "objects: redo brings it back with the same id");
    var mv = new MapDocument();
    mv.Objects.Add(new MapObject { TypeId = "t", X = 0, Y = 0, W = 2, H = 2 });
    mv.MoveRegion(0, 0, 3, 3, 10, 0);
    Check(mv.Objects.Count == 1 && mv.Objects[0].X == 10, "objects: move region carries objects");
    Check(mv.ContentBounds() is { } cb && cb.maxX == 11, "objects: content bounds include objects (zoom to fit)");
}

// --- map objects: object tool on the canvas ---
{
    var h = new CanvasHarness();
    var lib = new ObjectLibrary();
    var red = lib.Add(new ObjectType { Name = "Altar", Look = new ObjectLook { Fill = ObjectFill.Solid, ColorA = "#ff0000", Border = false } });
    h.Canvas.ObjectTypes = lib;
    h.Canvas.Mode = ToolMode.Object;
    h.Click(1, 1);
    Check(h.Doc.Objects.Count == 0 && h.LastStatus.Contains("pick an object type"), "object tool: nothing armed, nothing placed");
    h.Canvas.ArmedObject = red;
    h.Drag(1, 1, 2, 3);
    Check(h.Doc.Objects.Count == 1 && h.Doc.Objects[0] is { X: 1, Y: 1, W: 2, H: 3 } && h.Doc.Objects[0].TypeId == red.Id,
        "object tool: drag places a 2x3 object");
    Check(h.CommitCount == 1, "object tool: placing is one undo step");
    var bmp = h.Render();
    var px = h.PixelAt(bmp, (int)h.ScreenOf(2, 2).X, (int)h.ScreenOf(2, 2).Y);
    Check(px.r > 200 && px.g < 60 && px.b < 60, "object tool: the object draws in its type color");
    MapObject? opened = null;
    h.Canvas.ObjectConfigRequested += o => opened = o;
    h.Click(2, 3);
    Check(opened == h.Doc.Objects[0] && h.Canvas.SelectedObjectId == opened!.Id && h.Doc.Objects.Count == 1,
        "object tool: clicking an object opens it instead of placing");
    // drag the bottom right handle from (3,4) to (5,4): two cells wider
    h.PressW(3, 4); h.MoveToAt(h.ScreenWorld(5, 4)); h.ReleaseW(5, 4);
    Check(h.Doc.Objects[0] is { X: 1, Y: 1, W: 4, H: 3 } && h.CommitCount == 2, "object tool: the corner handle resizes, one undo step");
    // the left handle can not cross the right side
    h.PressW(1, 2.5); h.MoveToAt(h.ScreenWorld(9, 2.5)); h.ReleaseW(9, 2.5);
    Check(h.Doc.Objects[0] is { X: 4, W: 1 }, "object tool: resize stops at one cell");
    // shift drag over the object places a second one
    Avalonia.Headless.HeadlessWindowExtensions.MouseDown(h.Window, h.ScreenOf(4, 2), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Shift);
    h.MoveTo(4, 2);
    Avalonia.Headless.HeadlessWindowExtensions.MouseUp(h.Window, h.ScreenOf(4, 2), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Shift);
    Check(h.Doc.Objects.Count == 2, "object tool: shift places over an existing object");

    // select tool click opens the object, erase takes any object touched
    var s = new CanvasHarness();
    s.Canvas.ObjectTypes = lib;
    s.Doc.Objects.Add(new MapObject { TypeId = red.Id, X = 2, Y = 2, W = 3, H = 2 });
    MapObject? sel = null;
    s.Canvas.ObjectConfigRequested += o => sel = o;
    s.Canvas.Mode = ToolMode.Select;
    s.Click(3, 3);
    Check(sel == s.Doc.Objects[0], "select tool: clicking an object opens it");
    s.Canvas.Mode = ToolMode.Erase;
    s.Canvas.EraseFilter.Objects = false;
    s.Drag(4, 3, 4, 3);
    Check(s.Doc.Objects.Count == 1, "erase: the objects filter off keeps objects");
    s.Canvas.EraseFilter.Objects = true;
    s.Drag(4, 3, 6, 6);
    Check(s.Doc.Objects.Count == 0, "erase: touching a corner removes the whole object");

    // move tool carries a whole object
    var m = new CanvasHarness();
    m.Doc.Objects.Add(new MapObject { TypeId = red.Id, X = 1, Y = 1, W = 2, H = 2 });
    m.Canvas.Mode = ToolMode.Move;
    m.Drag(0, 0, 3, 3);
    m.Click(8, 0);
    Check(m.Doc.Objects.Count == 1 && m.Doc.Objects[0].X == 9 && m.Doc.Objects[0].Y == 1, "move tool: an object moves with its region");

    // a placed object with a missing type still draws
    var g = new CanvasHarness();
    g.Doc.Objects.Add(new MapObject { TypeId = "gone", X = 1, Y = 1, W = 1, H = 1 });
    var gb = g.Render();
    Check(g.PixelAt(gb, (int)g.ScreenOf(1, 1).X, (int)g.ScreenOf(1, 1).Y) != g.PixelAt(gb, (int)g.ScreenOf(5, 5).X, (int)g.ScreenOf(5, 5).Y),
        "objects: a missing type draws in the plain look");

    // every fill renders without throwing, own look beats the type look
    var f = new CanvasHarness();
    f.Canvas.ObjectTypes = lib;
    int x = 0;
    foreach (var fill in Enum.GetValues<ObjectFill>())
    {
        foreach (var cap in Enum.GetValues<ObjectCaption>())
            f.Doc.Objects.Add(new MapObject { TypeId = red.Id, X = x, Y = (int)cap * 3, W = 2, H = 2, Look = new ObjectLook { Fill = fill, Caption = cap, ColorA = "#00ff00", PictureId = fill == ObjectFill.Picture ? "missing" : "" } });
        x += 3;
    }
    bool ok = true;
    try { f.Render(); } catch { ok = false; }
    Check(ok, "objects: every fill and caption renders");
    var fb = f.Render();
    var fp = f.PixelAt(fb, (int)f.ScreenOf(1, 1).X - 5, (int)f.ScreenOf(1, 1).Y - 5);
    Check(fp.g > 200 && fp.r < 60, "objects: an own look draws instead of the type look");
}

// --- map objects: window smoke test (legend, object view, change type, menu) ---
{
    _ = new CanvasHarness(); // boots the headless app
    string step = "construct";
    try
    {
        var w = new DungeonMapper.Views.MainWindow();
        w.Show();
        var t = typeof(DungeonMapper.Views.MainWindow);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? Call(string name, params object?[] args) =>
            t.GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(w, args);
        var canvas = (MapCanvas)t.GetField("Canvas", flags)!.GetValue(w)!;
        step = "object tool"; Call("SetTool", ToolMode.Object);
        step = "objects menu"; Call("ShowObjectManagement");
        var lib = canvas.ObjectTypes;
        var type = lib.Add(new ObjectType { Name = "Statue of Orcus" });
        t.GetField("_objEditId", flags)!.SetValue(w, type.Id);
        step = "editor"; Call("RefreshObjectManager");
        foreach (var fill in Enum.GetValues<ObjectFill>())
        {
            type.Look.Fill = fill; type.Look.Caption = ObjectCaption.Initials;
            step = $"editor {fill}"; Call("RefreshObjectManager");
        }
        step = "close menu"; Call("CloseManage");
        step = "arm"; Call("ArmObject", type);
        step = "legend"; Call("RefreshObjectPanel");
        var o = new MapObject { TypeId = type.Id, X = 0, Y = 0, W = 2, H = 3 };
        canvas.Document.Objects.Add(o);
        step = "object view"; Call("ShowObjectInfo", o);
        o.Look = type.Look.Clone();
        step = "object view own look"; Call("ShowObjectInfo", o);
        step = "change type"; Call("ShowChangeObjectType", o);
        o.TypeId = "gone";
        step = "broken object view"; Call("ShowObjectInfo", o);
        step = "undo"; Call("DoUndo");
        step = "erase panel"; Call("SetTool", ToolMode.Erase);
        Check(true, "objects: window views build without errors");
    }
    catch (Exception ex)
    {
        Check(false, $"objects: window views build without errors (failed at {step}: {(ex.InnerException ?? ex)})");
    }
}

// --- map objects: picture pool and border thickness ---
{
    // a real 50x50 png, as the look editor stores it
    var png = PawnImage.Compress(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
    string b64 = Convert.ToBase64String(png);
    // the picture as it appears inside the json text (+ and / get escaped)
    string esc = System.Text.Json.JsonSerializer.Serialize(b64).Trim('"');
    var lib = new ObjectLibrary();
    string key = lib.AddPicture(b64);
    Check(lib.AddPicture(b64) == key && lib.Pictures.Count == 1, "pictures: the same picture is stored once");
    var obelisk = lib.Add(new ObjectType { Name = "Obelisk", Look = new ObjectLook { Fill = ObjectFill.Picture, PictureId = key } });
    var pr = new MapProject();
    for (int i = 0; i < 100; i++)
        pr.Maps[0].Doc.Objects.Add(new MapObject { TypeId = obelisk.Id, X = i * 2, Y = 0, Look = i % 2 == 0 ? obelisk.Look.Clone() : null });
    var pjson = MapSerializer.ProjectToJson(pr, null, null, lib);
    int copies = (pjson.Length - pjson.Replace(esc, "").Length) / esc.Length;
    Check(copies == 1, $"pictures: 100 obelisks (50 with their own look) save the picture once (found {copies})");
    var unused = lib.AddPicture(Convert.ToBase64String(PawnImage.Compress(png.Concat(new byte[0]).ToArray())) + "x");
    Check(!MapSerializer.ProjectToJson(pr, null, null, lib).Contains("\"" + unused + "\""), "pictures: a picture nothing uses is not saved");

    // load into a fresh campaign: the pool comes back, keys still match
    var imp = MapSerializer.ImportFromJson(pjson, "x");
    var lib2 = new ObjectLibrary();
    lib2.AddPictures(imp.ObjectPictures);
    var map = lib2.MergeAppend(MapSerializer.TidyObjectTypes(imp.ObjectTypes));
    Check(lib2.Picture(key) == b64 && lib2.Find(map[obelisk.Id])!.Look.PictureId == key, "pictures: the pool round trips with the campaign");
    var exp = new MapEntry();
    exp.Doc.Objects.Add(new MapObject { TypeId = obelisk.Id });
    var mf = MapSerializer.ImportFromJson(MapSerializer.MapToJson(exp, null, null, lib), "m");
    Check(mf.ObjectPictures.Count == 1 && mf.ObjectPictures.ContainsKey(key), "pictures: a map file carries the pictures it uses");

    // a file from before the pool: the picture sits inline in each look
    string legacy = "{\"kind\":\"campaign\",\"maps\":[{\"name\":\"m\",\"map\":{\"objects\":[{\"id\":\"o1\",\"type_id\":\"t1\",\"rect\":\"0,0,1,1\",\"look\":{\"fill\":\"Picture\",\"image_b64\":\"" + b64 + "\"}}]}}],"
        + "\"object_library\":[{\"id\":\"t1\",\"name\":\"Obelisk\",\"look\":{\"fill\":\"Picture\",\"image_b64\":\"" + b64 + "\",\"image_ext\":\".png\"}}]}";
    var li = MapSerializer.ImportFromJson(legacy, "x");
    var lib3 = new ObjectLibrary();
    var types = MapSerializer.TidyObjectTypes(li.ObjectTypes);
    lib3.AdoptLegacy(types.Select(t => t.Look).Concat(li.Maps.SelectMany(m => m.Doc.Objects).Select(o => o.Look!)));
    lib3.MergeAppend(types);
    var pr3 = new MapProject();
    pr3.Maps.Clear(); pr3.Maps.AddRange(li.Maps);
    var again = MapSerializer.ProjectToJson(pr3, null, null, lib3);
    Check(lib3.Types[0].Look.PictureId == key && li.Maps[0].Doc.Objects[0].Look!.PictureId == key, "pictures: an older file's inline pictures move into the pool");
    Check((again.Length - again.Replace(esc, "").Length) / esc.Length == 1 && !again.Contains("image_b64"), "pictures: resaving an older file stores the picture once");

    // canvas draws the pooled picture
    var h = new CanvasHarness();
    h.Canvas.ObjectTypes = lib;
    h.Doc.Objects.Add(new MapObject { TypeId = obelisk.Id, X = 1, Y = 1, W = 2, H = 2 });
    bool drew = true;
    try { h.Render(); } catch { drew = false; }
    Check(drew && h.Canvas.LoadObjectImage(key) is not null, "pictures: the canvas loads a picture from the pool");

    // border thickness
    var look = new ObjectLook { BorderWidth = 99 }.Tidy();
    Check(look.BorderWidth == 10 && new ObjectLook { BorderWidth = 0 }.Tidy().BorderWidth == 1, "border: thickness clamps to 1..10");
    Check(new ObjectLook().BorderWidth == 2, "border: default thickness 2");
    var d = new MapDocument();
    d.Objects.Add(new MapObject { Look = new ObjectLook { BorderWidth = 7 } });
    Check(MapSerializer.FromJson(MapSerializer.ToJson(d)).Objects[0].Look!.BorderWidth == 7, "border: thickness round trips");
    Check(!new ObjectLook { BorderWidth = 3 }.Same(new ObjectLook { BorderWidth = 4 }), "border: thickness is part of the look");
    var bh = new CanvasHarness();
    var blib = new ObjectLibrary();
    var bt = blib.Add(new ObjectType { Name = "b", Look = new ObjectLook { ColorA = "#000000", BorderColor = "#ffffff", BorderWidth = 1 } });
    bh.Canvas.ObjectTypes = blib;
    bh.Doc.Objects.Add(new MapObject { TypeId = bt.Id, X = 1, Y = 1, W = 3, H = 3 });
    // a point a little inside the left edge of the object: dark at width 1, border color at width 10
    var probe = bh.ScreenWorld(1.25, 2.5);
    var thin = bh.PixelAt(bh.Render(), (int)probe.X, (int)probe.Y);
    bt.Look.BorderWidth = 10;
    var thick = bh.PixelAt(bh.Render(), (int)probe.X, (int)probe.Y);
    Check(thin.r < 60 && thick.r > 200, "border: a thicker border covers more of the object");
}

// --- map objects: transparent pictures ---
{
    // a 2x2 png: left column opaque red, right column fully transparent
    using var sk = new SkiaSharp.SKBitmap(2, 2);
    sk.SetPixel(0, 0, new SkiaSharp.SKColor(255, 0, 0, 255)); sk.SetPixel(0, 1, new SkiaSharp.SKColor(255, 0, 0, 255));
    sk.SetPixel(1, 0, SkiaSharp.SKColors.Transparent); sk.SetPixel(1, 1, SkiaSharp.SKColors.Transparent);
    using var data = sk.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
    var png = PawnImage.Compress(data.ToArray());
    using (var back = SkiaSharp.SKBitmap.Decode(png))
        Check(back.GetPixel(45, 25).Alpha == 0 && back.GetPixel(5, 25).Alpha == 255, "pictures: compression keeps transparency");
    var lib = new ObjectLibrary();
    var key = lib.AddPicture(Convert.ToBase64String(png));
    var t = lib.Add(new ObjectType { Name = "glass", Look = new ObjectLook { Fill = ObjectFill.Picture, PictureId = key, ColorA = "#00ff00", Border = false } });
    var h = new CanvasHarness();
    h.Canvas.ObjectTypes = lib;
    h.Doc.AddRoom(0, 0, 5, 5);
    h.Doc.Objects.Add(new MapObject { TypeId = t.Id, X = 1, Y = 1, W = 2, H = 2 });
    var probe = h.ScreenWorld(2.6, 2.5); // the transparent right half of the picture, off the grid lines
    var floor = h.ScreenWorld(4.5, 4.5);
    var bmp = h.Render();
    Check(h.PixelAt(bmp, (int)probe.X, (int)probe.Y) == h.PixelAt(bmp, (int)floor.X, (int)floor.Y), "pictures: see through shows the floor in transparent parts");
    Check(new ObjectLook().PictureBacking == false, "pictures: new looks are see through");
    t.Look.PictureBacking = true;
    var px = h.PixelAt(h.Render(), (int)probe.X, (int)probe.Y);
    Check(px.g > 200 && px.r < 60, "pictures: fill color shows behind transparent parts");
    var d = new MapDocument();
    d.Objects.Add(new MapObject { Look = new ObjectLook { PictureBacking = true } });
    Check(MapSerializer.FromJson(MapSerializer.ToJson(d)).Objects[0].Look!.PictureBacking, "pictures: backing choice round trips");
}

// --- pawn images stay inside the pawn circle ---
{
    using var sk = new SkiaSharp.SKBitmap(8, 8);
    sk.Erase(new SkiaSharp.SKColor(255, 0, 0, 255));
    using var data = sk.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
    var lib = new PawnLibrary();
    lib.Pawns.Add(new PawnTemplate { Id = "sq", Name = "square", ImageB64 = Convert.ToBase64String(PawnImage.Compress(data.ToArray())), ImageExt = ".png" });
    var h = new CanvasHarness();
    h.Canvas.Library = lib;
    h.Doc.PlacePawn(new Pawn { Cell = new CellKey(2, 2), TemplateId = "sq" });
    var bmp = h.Render();
    var mid = h.PixelAt(bmp, (int)h.ScreenOf(2, 2).X, (int)h.ScreenOf(2, 2).Y);
    // near the corner of the old square image: 0.12 of a cell in from the cell corner, outside the circle
    var corner = h.ScreenWorld(2.12, 2.12);
    var cpx = h.PixelAt(bmp, (int)corner.X, (int)corner.Y);
    var empty = h.ScreenWorld(6.12, 6.12);
    Check(mid.r > 200 && mid.g < 60, "pawn image: drawn in the pawn circle");
    Check(cpx == h.PixelAt(bmp, (int)empty.X, (int)empty.Y), "pawn image: the square's corners are cut off by the circle");
}

// --- formations: model and save ---
{
    var pawns = new[] { new Pawn { Cell = new CellKey(5, 3), TemplateId = "a" }, new Pawn { Cell = new CellKey(7, 4), TemplateId = "b" }, new Pawn { Cell = new CellKey(6, 3), TemplateId = "c" } };
    var mem = Formation.Capture(pawns);
    Check(mem.Count == 3 && mem[0] is { TemplateId: "a", Dx: 0, Dy: 0 } && mem[1] is { TemplateId: "c", Dx: 1, Dy: 0 } && mem[2] is { TemplateId: "b", Dx: 2, Dy: 1 },
        "formation: capture stores offsets from the box's top-left, in reading order");
    var f = new Formation { Id = "f1", Name = "march", Members = mem };
    Check(f.Width == 3 && f.Height == 2, "formation: width and height");
    var rot = Formation.Rotate(mem.Select(m => (m.TemplateId, m.Dx, m.Dy)), -1);
    Check(rot.Max(m => m.dx) == 1 && rot.Max(m => m.dy) == 2 && rot.Min(m => m.dx) == 0 && rot.Min(m => m.dy) == 0, "formation: a quarter turn swaps the box and stays at 0,0");
    var back4 = Formation.Rotate(Formation.Rotate(rot, -1), -2);
    Check(back4.SequenceEqual(mem.Select(m => (m.TemplateId, m.Dx, m.Dy))), "formation: four quarter turns come back");
    Check(Formation.UniqueName(new[] { f, new Formation { Name = "formation" } }) == "formation 2", "formation: new names do not repeat");
    var lib = new PawnLibrary();
    lib.Formations.Add(f);
    var pr = new MapProject();
    var fjson = MapSerializer.ProjectToJson(pr, lib);
    var imp = MapSerializer.ImportFromJson(fjson, "x");
    Check(imp.Formations.Count == 1 && imp.Formations[0].Name == "march" && imp.Formations[0].Members.Count == 3 && imp.Formations[0].Members[2].Dx == 2,
        "formation: saved in the campaign file");
    Check(!MapSerializer.LibraryToJson(lib).Contains("march"), "formation: not in pawn library export files");
    Check(MapSerializer.ImportFromJson(MapSerializer.ProjectToJson(new MapProject(), new PawnLibrary()), "x").Formations.Count == 0, "formation: none is fine");
    string k0 = MapSerializer.ProjectContentKey(pr, lib);
    f.Members[0].Dx = 9;
    Check(k0 != MapSerializer.ProjectContentKey(pr, lib), "formation: an edit is an unsaved change");
    var bad = MapSerializer.ImportFromJson("{\"kind\":\"campaign\",\"maps\":[],\"formations\":[{\"name\":\"x\",\"members\":[{\"template_id\":\"\",\"dx\":0,\"dy\":0},{\"template_id\":\"p\",\"dx\":1,\"dy\":0}]}]}", "x");
    Check(bad.Formations[0].Members.Count == 1 && bad.Formations[0].Id.Length > 0, "formation: members with no pawn are dropped, a missing id is filled");
}

// --- formations: deploy on the canvas ---
{
    var lib = new PawnLibrary();
    lib.Pawns.Add(new PawnTemplate { Id = "h1", Name = "Fighter", Hero = true });
    lib.Pawns.Add(new PawnTemplate { Id = "h2", Name = "Cleric", Hero = true });
    lib.Pawns.Add(new PawnTemplate { Id = "g", Name = "Goblin" });
    lib.Pawns.Add(new PawnTemplate { Id = "x", Name = "Orc" });
    var h = new CanvasHarness();
    h.Canvas.Library = lib;
    h.Doc.AddRoom(0, 0, 9, 9);
    h.Doc.PlacePawn(new Pawn { Cell = new CellKey(1, 1), TemplateId = "h1", Status = "fear" });
    h.Doc.PlacePawn(new Pawn { Cell = new CellKey(2, 1), TemplateId = "h2" });
    var asked = new List<string>();
    h.Canvas.TakeHeroFromOtherMaps = id => { asked.Add(id); return null; };
    int commits = h.CommitCount;
    // a row of three: fighter, cleric, goblin. cursor cell is the middle one
    h.Canvas.ArmFormation("line", new[] { ("h1", 0, 0), ("h2", 1, 0), ("g", 2, 0) });
    Check(h.Canvas.FormationArmed, "deploy: armed");
    h.MoveTo(5, 5);
    h.Click(5, 5);
    Check(!h.Canvas.FormationArmed && h.CommitCount == commits + 1, "deploy: one click places and disarms, one undo step");
    Check(h.Doc.Pawns.TryGetValue(new CellKey(4, 5), out var f1) && f1.TemplateId == "h1" && f1.Status == "fear", "deploy: a hero moves and keeps its status");
    Check(!h.Doc.Pawns.ContainsKey(new CellKey(1, 1)) && !h.Doc.Pawns.ContainsKey(new CellKey(2, 1)), "deploy: heroes leave their old cells");
    Check(h.Doc.Pawns.TryGetValue(new CellKey(6, 5), out var gob) && gob.TemplateId == "g", "deploy: a non-hero member is a new copy");
    Check(asked.Contains("h1") && asked.Contains("h2") && !asked.Contains("g"), "deploy: heroes are pulled from other maps");
    Check(h.LastStatus.StartsWith("deployed line: 3 pawns") && !h.LastStatus.Contains("outside"), "deploy: status names the formation");

    // deploying again over its own heroes is fine, the heroes move
    h.Canvas.ArmFormation("line", new[] { ("h1", 0, 0), ("h2", 1, 0) });
    h.Click(4, 5);
    // two wide: the cursor cell takes the left member. (4,5) holds h1 now, (5,5) is empty
    Check(h.Doc.Pawns[new CellKey(4, 5)].TemplateId == "h1" && h.Doc.Pawns[new CellKey(5, 5)].TemplateId == "h2" && h.Doc.Pawns.Values.Count(p => p.TemplateId == "h1") == 1,
        "deploy: landing on cells the heroes stand on now is not blocked");

    // another pawn in the way blocks the whole deploy
    h.Doc.PlacePawn(new Pawn { Cell = new CellKey(8, 8), TemplateId = "x" });
    int before = h.Doc.Pawns.Count, c2 = h.CommitCount;
    h.Canvas.ArmFormation("line", new[] { ("h1", 0, 0), ("h2", 1, 0), ("g", 2, 0) });
    h.MoveTo(7, 8);
    var bmp = h.Render();
    var px = h.PixelAt(bmp, (int)h.ScreenWorld(8.1, 8.1).X, (int)h.ScreenWorld(8.1, 8.1).Y);
    h.Click(7, 8);
    Check(h.Canvas.FormationArmed && h.Doc.Pawns.Count == before && h.CommitCount == c2 && h.Doc.Pawns[new CellKey(8, 8)].TemplateId == "x",
        "deploy: a pawn in the way blocks it, nothing moves, still armed");
    Check(h.LastStatus.Contains("can not deploy"), "deploy: blocked status");
    Check(px.r > px.g + 40, "deploy: the blocked cell shows red in the ghost");

    // outside a room: allowed with a warning
    h.Click(15, 2); // past the room's east edge, inside the test window
    Check(!h.Canvas.FormationArmed && h.Doc.Pawns.ContainsKey(new CellKey(15, 2)) && h.LastStatus.Contains("warning: 3 outside a room"),
        "deploy: outside a room places with a warning");

    // rotation and cancel
    h.Canvas.ArmFormation("line", new[] { ("h1", 0, 0), ("h2", 1, 0) });
    h.Canvas.RotateFormation(-1);
    h.Click(3, 3);
    var spots = h.Doc.Pawns.Where(kv => kv.Value.TemplateId is "h1" or "h2").Select(kv => kv.Key).ToList();
    Check(spots.All(c => c.X == 3) && spots.Select(c => c.Y).Distinct().Count() == 2, "deploy: a rotated row lands as a column");
    h.Canvas.ArmFormation("line", new[] { ("h1", 0, 0) });
    h.Canvas.CancelFormation();
    int n = h.Doc.Pawns.Count;
    h.Canvas.Mode = ToolMode.Select;
    h.Click(1, 8);
    Check(!h.Canvas.FormationArmed && h.Doc.Pawns.Count == n, "deploy: cancel disarms without placing");

    // area pick for capture
    (int, int, int, int)? got = null;
    h.Canvas.BeginAreaPick((a, b, c, d) => got = (a, b, c, d));
    Check(h.Canvas.PickingArea, "capture: picking");
    h.Drag(6, 6, 2, 3);
    Check(got == (2, 3, 6, 6) && !h.Canvas.PickingArea, "capture: the drag hands back the area and ends");
}

// --- formations: window smoke test ---
{
    _ = new CanvasHarness();
    string step = "construct";
    try
    {
        var w = new DungeonMapper.Views.MainWindow();
        w.Show();
        var t = typeof(DungeonMapper.Views.MainWindow);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? Call(string name, params object?[] args) =>
            t.GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(w, args);
        var canvas = (MapCanvas)t.GetField("Canvas", flags)!.GetValue(w)!;
        var lib = canvas.Library;
        for (int i = 0; i < 12; i++) lib.Pawns.Add(new PawnTemplate { Id = $"p{i}", Name = $"hero {i}", Hero = i < 8, Affiliation = i < 8 ? "Player" : "Enemy", Status = i == 3 ? "paralyzed" : "none" });
        var f = new Formation { Id = "f", Name = "party", Members = Enumerable.Range(0, 12).Select(i => new FormationMember { TemplateId = $"p{i}", Dx = i % 4, Dy = i / 4 }).ToList() };
        f.Members.Add(new FormationMember { TemplateId = "gone", Dx = 5, Dy = 5 });
        lib.Formations.Add(f);
        step = "pawn panel"; Call("SetTool", ToolMode.Pawn);
        t.GetField("_formationOpen", flags)!.SetValue(w, "f");
        step = "open formation"; Call("RefreshFormations");
        t.GetField("_formationRenaming", flags)!.SetValue(w, "f");
        step = "rename box"; Call("RefreshFormations");
        t.GetField("_formationRenaming", flags)!.SetValue(w, null);
        step = "deploy"; Call("DeployFormation", f);
        Check(canvas.FormationArmed, "formations: deploy from the panel arms the canvas");
        canvas.CancelFormation();
        step = "deploy last"; Call("DeployLastFormation");
        Check(canvas.FormationArmed, "formations: the hotkey deploys the last formation");
        step = "capture"; Call("CaptureFormation", new object?[] { null });
        Check(canvas.PickingArea && !canvas.FormationArmed, "formations: new formation starts an area pick");
        step = "tool switch"; Call("SetTool", ToolMode.Room);
        Check(!canvas.PickingArea, "formations: a tool switch cancels the pick");
        Check(AppSettings.Default().ToolHotkeys[AppSettings.FormationHotkey] == "I" && AppSettings.Default().ToolHotkeys.Values.Count(v => v == "I") == 1,
            "formations: hotkey I by default, used by nothing else");
        Check(true, "formations: window views build without errors");
    }
    catch (Exception ex)
    {
        Check(false, $"formations: window views build without errors (failed at {step}: {(ex.InnerException ?? ex)})");
    }
}

// --- measure tool ---
{
    // math: straight line is pythagoras, readout gives cells and feet
    Check(MapCanvas.FormatCells(10) == "10 cells (50 ft)", "measure: whole cells print without a decimal");
    Check(MapCanvas.FormatCells(Math.Sqrt(18)) == "4.2 cells (21.2 ft)", "measure: one decimal for cells and feet");
    Check(MapCanvas.FormatCells(1) == "1 cell (5 ft)", "measure: one cell is singular");
    var r1 = MapCanvas.CellsInRadius(new Point(5.5, 5.5), 1);
    Check(r1.Count == 5, "measure: radius 1 from a cell center is a plus (center inside or on the circle)");
    Check(MapCanvas.CellsInRadius(new Point(5.5, 5.5), 2).Count == 13, "measure: radius 2 from a center catches 13 cells");
    Check(MapCanvas.CellsInRadius(new Point(5, 5), 1).Count == 4, "measure: radius 1 from a corner catches the 4 touching cells");
    Check(MapCanvas.CellsInRadius(new Point(5.5, 5.5), 0).Count == 0, "measure: radius 0 catches nothing");

    using var h = new CanvasHarness();
    h.Canvas.Mode = ToolMode.Measure;
    int commits = h.CommitCount;

    // line, cell centers
    h.Drag(1, 1, 4, 5);
    var leg = h.Canvas.MeasureLeg();
    Check(h.Canvas.MeasureMode == MapCanvas.MeasureKind.Line && leg.h == 3 && leg.v == 4 && Math.Abs(leg.straight - 5) < 1e-9,
        "measure: a drag measures horizontal 3, vertical 4, straight 5");
    Check(h.Canvas.MeasureReadout()[0] == "5 cells (25 ft)", "measure: readout leads with the straight line");
    Check(h.Canvas.MeasurePoints[0] == new Point(1.5, 1.5), "measure: ends snap to cell centers by default");

    // waypoint
    h.DragMods(4, 5, 4, 8, Avalonia.Input.RawInputModifiers.Control);
    Check(h.Canvas.MeasureLegCount == 2 && Math.Abs(h.Canvas.MeasurePathTotal() - 8) < 1e-9 && h.Canvas.MeasureLeg().v == 3,
        "measure: ctrl+drag adds a leg, path total 8, current leg 3");
    Check(h.Canvas.MeasureReadout().Any(l => l.StartsWith("path total 8 cells (40 ft)")), "measure: readout shows the path total");
    h.DragMods(4, 8, 6, 8, Avalonia.Input.RawInputModifiers.Control);
    Check(h.Canvas.MeasureLegCount == 3, "measure: another ctrl+drag adds a third leg");
    h.Drag(2, 2, 3, 2);
    Check(h.Canvas.MeasureLegCount == 1, "measure: a plain drag starts over");

    // corners
    h.Canvas.MeasureCorners = true;
    h.Canvas.ClearMeasure();
    h.PressW(1.1, 1.2); h.MoveToAt(h.ScreenWorld(3.9, 1.1)); h.ReleaseW(3.9, 1.1);
    Check(h.Canvas.MeasurePoints[0] == new Point(1, 1) && h.Canvas.MeasurePoints[1] == new Point(4, 1), "measure: corners chip snaps ends to grid corners");
    h.Canvas.MeasureCorners = false;

    // radius from a pawn
    h.Doc.Pawns[new CellKey(5, 5)] = new Pawn { Cell = new CellKey(5, 5), TemplateId = "a" };
    h.Doc.Pawns[new CellKey(7, 5)] = new Pawn { Cell = new CellKey(7, 5), TemplateId = "b" };
    h.Doc.Pawns[new CellKey(8, 8)] = new Pawn { Cell = new CellKey(8, 8), TemplateId = "c" };
    h.Drag(5, 5, 5, 8);
    Check(h.Canvas.MeasureMode == MapCanvas.MeasureKind.Radius && h.Canvas.MeasureRadius == 3 && h.Canvas.MeasureCenter == new Point(5.5, 5.5),
        "measure: dragging out from a pawn gives a whole cell radius of 3");
    var caught = h.Canvas.MeasureCaughtPawns().Select(p => p.TemplateId).OrderBy(x => x).ToList();
    Check(caught.SequenceEqual(new[] { "a", "b" }), "measure: pawns inside the radius are caught, the one outside is not");
    Check(h.Canvas.MeasureReadout()[1] == "2 pawns inside", "measure: readout counts the pawns inside");

    // pawn to pawn
    h.Drag(5, 5, 8, 8);
    Check(h.Canvas.MeasureMode == MapCanvas.MeasureKind.Line && h.Canvas.MeasureLeg().h == 3 && h.Canvas.MeasureLeg().v == 3,
        "measure: dragging from a pawn onto another pawn measures the line between them");
    h.Press(5, 5); h.MoveTo(8, 8); h.MoveTo(5, 9); h.Release(5, 9);
    Check(h.Canvas.MeasureMode == MapCanvas.MeasureKind.Radius && h.Canvas.MeasureRadius == 4, "measure: moving off the other pawn goes back to a radius");

    // shift+drag radius from empty floor
    h.DragMods(2, 10, 2, 12, Avalonia.Input.RawInputModifiers.Shift);
    Check(h.Canvas.MeasureMode == MapCanvas.MeasureKind.Radius && h.Canvas.MeasureCenter == new Point(2.5, 10.5) && h.Canvas.MeasureRadius == 2,
        "measure: shift+drag from an empty cell gives a radius");
    h.DragMods(5, 5, 5, 7, Avalonia.Input.RawInputModifiers.Shift);
    Check(h.Canvas.MeasureMode == MapCanvas.MeasureKind.Radius && h.Canvas.MeasureRadius == 2, "measure: shift+drag on a pawn is a radius too");

    // pixels: shaded caught cell, ring
    h.Drag(5, 5, 5, 7);
    var bmp = h.Render();
    var msp = h.ScreenOf(5, 6);
    var px = h.PixelAt(bmp, (int)msp.X, (int)msp.Y + 8);
    Check(px.r > px.b + 40, "measure: a caught cell is shaded in the measure color");

    // reading only
    Check(h.CommitCount == commits && h.Doc.Pawns.Count == 3 && h.Doc.Cells.Count == 0, "measure: no commits (no undo steps) and the map is untouched");
    h.Canvas.ClearMeasure();
    Check(!h.Canvas.HasMeasure, "measure: clear removes the measurement");

    // settings
    var d = AppSettings.Default();
    Check(d.ToolHotkeys["Measure"] == "G" && d.ToolHotkeys.Values.Count(v => v == "G") == 1, "measure: hotkey G by default, used by nothing else");
    var imp = AppSettings.ParseImport("{\"kind\":\"settings\",\"measure_shade_opacity\":500,\"measure_r\":10}")!;
    Check(imp.MeasureShadeOpacity == 100 && imp.MeasureR == 10, "measure: settings import clamps the shading and keeps the color");
}

// --- measure tool: window smoke test ---
{
    _ = new CanvasHarness();
    string step = "construct";
    try
    {
        var w = new DungeonMapper.Views.MainWindow();
        w.Show();
        var t = typeof(DungeonMapper.Views.MainWindow);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? Call(string name, params object?[] args) =>
            t.GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(w, args);
        var canvas = (MapCanvas)t.GetField("Canvas", flags)!.GetValue(w)!;
        step = "measure panel"; Call("SetTool", ToolMode.Measure);
        var info = (Avalonia.Controls.TextBlock?)t.GetField("_measureInfo", flags)!.GetValue(w);
        Check(info?.Text == "nothing measured yet", "measure: panel builds with an empty readout");
        canvas.Library.Pawns.Add(new PawnTemplate { Id = "m1", Name = "orc", Affiliation = "Enemy" });
        canvas.Document.Pawns[new CellKey(3, 3)] = new Pawn { Cell = new CellKey(3, 3), TemplateId = "m1" };
        step = "settings"; Call("ShowSettings"); Call("CloseSettings");
        step = "tool switch"; Call("SetTool", ToolMode.Room);
        Check(!canvas.HasMeasure, "measure: a tool switch clears the measurement");
        Check(true, "measure: window views build without errors");
    }
    catch (Exception ex)
    {
        Check(false, $"measure: window views build without errors (failed at {step}: {(ex.InnerException ?? ex)})");
    }
}

// --- label resize handles ---
{
    using var h = new CanvasHarness();
    var lbl = new MapLabel { Cell = new CellKey(2, 2), W = 1, H = 1, Text = "hall" };
    h.Doc.Labels.Add(lbl);
    h.Canvas.Mode = ToolMode.Label;
    h.Canvas.SelectedLabel = lbl;
    int commits = h.CommitCount;
    MapLabel? resized = null;
    h.Canvas.LabelResized += l => resized = l;

    // bottom right handle sits on the label's bottom right corner
    h.PressAt(h.ScreenWorld(3, 3)); h.MoveToAt(h.ScreenWorld(5.2, 3.9)); h.ReleaseAt(h.ScreenWorld(5.2, 3.9));
    Check(lbl.Cell == new CellKey(2, 2) && lbl.W == 3 && lbl.H == 2, "label resize: bottom right handle grows to 3x2, snapped to grid lines");
    Check(h.CommitCount == commits + 1 && resized == lbl, "label resize: one undo step and the resized event");
    Check(h.Doc.Labels.Count == 1, "label resize: no new label placed");

    // top left handle moves the corner cell
    h.PressAt(h.ScreenWorld(2, 2)); h.MoveToAt(h.ScreenWorld(0.8, 1.1)); h.ReleaseAt(h.ScreenWorld(0.8, 1.1));
    Check(lbl.Cell == new CellKey(1, 1) && lbl.W == 4 && lbl.H == 3, "label resize: top left handle moves the corner");

    // never below one cell
    h.PressAt(h.ScreenWorld(5, 4)); h.MoveToAt(h.ScreenWorld(-3, -3)); h.ReleaseAt(h.ScreenWorld(-3, -3));
    Check(lbl.Cell == new CellKey(1, 1) && lbl.W == 1 && lbl.H == 1, "label resize: at least one cell");

    // edge handle changes one side only
    h.PressAt(h.ScreenWorld(2, 1.5)); h.MoveToAt(h.ScreenWorld(4, 1.5)); h.ReleaseAt(h.ScreenWorld(4, 1.5));
    Check(lbl.W == 3 && lbl.H == 1, "label resize: right edge handle changes the width only");

    // a press and release without moving takes no undo step
    int c2 = h.CommitCount;
    h.PressAt(h.ScreenWorld(4, 2)); h.ReleaseAt(h.ScreenWorld(4, 2));
    Check(h.CommitCount == c2 && lbl.W == 3, "label resize: a click on a handle changes nothing");

    // handles only in the label tool, only for the selected label
    h.Canvas.Mode = ToolMode.Select;
    h.PressAt(h.ScreenWorld(4, 2)); h.MoveToAt(h.ScreenWorld(6, 2)); h.ReleaseAt(h.ScreenWorld(6, 2));
    Check(lbl.W == 3, "label resize: no handles outside the label tool");
    h.Canvas.Mode = ToolMode.Label;
    h.Canvas.SelectedLabel = null;
    h.PressAt(h.ScreenWorld(4, 2)); h.MoveToAt(h.ScreenWorld(6, 2)); h.ReleaseAt(h.ScreenWorld(6, 2));
    Check(lbl.W == 3, "label resize: no handles without a selected label");

    // auto font refits to the new range
    var big = new MapLabel { Cell = new CellKey(10, 10), W = 1, H = 1, Text = "a long label" };
    double small = MapCanvas.LabelSize(big);
    big.W = 6; big.H = 2;
    Check(MapCanvas.LabelSize(big) >= small, "label resize: an auto size label is not smaller in a bigger range");
}

// --- label resize: window ---
{
    _ = new CanvasHarness();
    try
    {
        var w = new DungeonMapper.Views.MainWindow();
        w.Show();
        var t = typeof(DungeonMapper.Views.MainWindow);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? Call(string name, params object?[] args) =>
            t.GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(w, args);
        var canvas = (MapCanvas)t.GetField("Canvas", flags)!.GetValue(w)!;
        Call("SetTool", ToolMode.Label);
        var l = new MapLabel { Cell = new CellKey(1, 1), Text = "x" };
        canvas.Document.Labels.Add(l);
        Call("ShowLabelConfig", l);
        Check(canvas.SelectedLabel == l, "label resize: opening a label selects it for handles");
        Call("SetTool", ToolMode.Room);
        Check(canvas.SelectedLabel is null, "label resize: going back to a tool view drops the handles");
    }
    catch (Exception ex)
    {
        Check(false, $"label resize: window test ({(ex.InnerException ?? ex).Message})");
    }
}

// --- fog tool ---
{
    using var h = new CanvasHarness();
    h.Doc.Pawns[new CellKey(5, 5)] = new Pawn { Cell = new CellKey(5, 5), TemplateId = "p" };
    var c = h.Canvas;
    Check(c.FogSnap(new Point(2.1, 2.1)) == new Point(2, 2), "fog: near a corner snaps to the corner");
    Check(c.FogSnap(new Point(2.4, 2.6)) == new Point(2.5, 2.5), "fog: near a center snaps to the center");
    Check(c.FogSnap(new Point(5.05, 5.05)) == new Point(5.5, 5.5), "fog: anywhere on a pawn's cell snaps to the pawn");

    Check(c.FogFeet == 30 && c.FogRadiusCells == 6, "fog: radius starts at 30 ft (6 cells)");
    c.FogFeet = 135; Check(c.FogFeet == 120, "fog: radius stops at 120 ft");
    c.FogFeet = -5; Check(c.FogFeet == 0, "fog: radius stops at 0");
    c.FogFeet = 34; Check(c.FogFeet == 30, "fog: radius snaps to 10 ft steps");

    int commits = h.CommitCount;
    c.Mode = ToolMode.Fog;
    c.ResetFog();
    h.MoveToAt(h.ScreenWorld(3.5, 3.5));
    Check(c.FogCenter == new Point(3.5, 3.5) && !c.FogAnchored, "fog: the center follows the cursor");
    Check(c.FogRevealed().Count == MapCanvas.CellsInRadius(new Point(3.5, 3.5), 6).Count, "fog: reveals the same cells the measure radius catches");
    h.ClickW(7.05, 7.05);
    Check(c.FogAnchored && c.FogCenter == new Point(7, 7), "fog: a click anchors the center (snapped to a corner)");
    h.MoveToAt(h.ScreenWorld(2.5, 2.5));
    Check(c.FogCenter == new Point(7, 7), "fog: the anchor stays when the cursor moves");
    h.ClickW(9.5, 4.5);
    Check(c.FogCenter == new Point(9.5, 4.5), "fog: a click elsewhere moves the anchor");
    Check(c.ReleaseFogAnchor() && !c.FogAnchored, "fog: release drops the anchor");
    h.MoveToAt(h.ScreenWorld(2.5, 2.5));
    Check(c.FogCenter == new Point(2.5, 2.5), "fog: released, the center follows the cursor again");

    // shift+scroll steps the radius
    h.ShiftWheelAt(h.ScreenWorld(2.5, 2.5), 1);
    Check(c.FogFeet == 40, "fog: shift+scroll up grows the radius by 10 ft");
    h.ShiftWheelAt(h.ScreenWorld(2.5, 2.5), -1);
    h.ShiftWheelAt(h.ScreenWorld(2.5, 2.5), -1);
    Check(c.FogFeet == 20, "fog: shift+scroll down shrinks it");

    // pixels: anchored on the pawn, 30 ft. a revealed cell is brighter than a fogged one
    c.FogFeet = 30;
    h.Click(5, 5);
    int Lum((byte r, byte g, byte b, byte a) p) => p.r + p.g + p.b;
    var bmp = h.Render();
    var inside = h.PixelAt(bmp, (int)h.ScreenOf(8, 5).X + 8, (int)h.ScreenOf(8, 5).Y + 8);
    var outside = h.PixelAt(bmp, (int)h.ScreenOf(14, 5).X + 8, (int)h.ScreenOf(14, 5).Y + 8);
    Check(Lum(inside) > Lum(outside), "fog: cells outside the radius are dimmed, inside are not");
    c.FogOn = false;
    bmp = h.Render();
    var off = h.PixelAt(bmp, (int)h.ScreenOf(14, 5).X + 8, (int)h.ScreenOf(14, 5).Y + 8);
    Check(Lum(off) == Lum(inside), "fog: unticked, nothing is dimmed");
    c.FogOn = true;
    c.Mode = ToolMode.Select;
    bmp = h.Render();
    off = h.PixelAt(bmp, (int)h.ScreenOf(14, 5).X + 8, (int)h.ScreenOf(14, 5).Y + 8);
    Check(Lum(off) == Lum(inside), "fog: never shows outside the fog tool");

    Check(h.CommitCount == commits && h.Doc.Pawns.Count == 1 && h.Doc.Cells.Count == 0, "fog: no commits (no undo steps) and the map is untouched");

    var d = AppSettings.Default();
    Check(d.ToolHotkeys["Fog"] == "V" && d.ToolHotkeys.Values.Count(v => v == "V") == 1, "fog: hotkey V by default, used by nothing else");
    Check(d.FogOpacity == 30 && d.FogR == 0 && d.FogG == 0 && d.FogB == 0, "fog: default dim is black at 30 percent");
    var imp = AppSettings.ParseImport("{\"kind\":\"settings\",\"fog_opacity\":500,\"fog_r\":10}")!;
    Check(imp.FogOpacity == 100 && imp.FogR == 10, "fog: settings import clamps the opacity and keeps the color");
}

// --- fog tool: window smoke test ---
{
    _ = new CanvasHarness();
    string step = "construct";
    try
    {
        var w = new DungeonMapper.Views.MainWindow();
        w.Show();
        var t = typeof(DungeonMapper.Views.MainWindow);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? Call(string name, params object?[] args) =>
            t.GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(w, args);
        var canvas = (MapCanvas)t.GetField("Canvas", flags)!.GetValue(w)!;
        step = "fog panel"; Call("SetTool", ToolMode.Fog);
        var slider = (Avalonia.Controls.Slider?)t.GetField("_fogSlider", flags)!.GetValue(w);
        Check(slider?.Value == canvas.FogFeet && canvas.FogActive, "fog: panel builds, slider shows the radius, fog is on");
        canvas.FogFeet = 60;
        Check(slider!.Value == 60, "fog: a radius change (shift+scroll) moves the slider");
        slider.Value = 90;
        Check(canvas.FogFeet == 90, "fog: the slider sets the radius");
        canvas.FogOn = false;
        step = "settings"; Call("ShowSettings"); Call("CloseSettings");
        step = "tool switch"; Call("SetTool", ToolMode.Room);
        Check(!canvas.FogActive, "fog: a tool switch hides the fog");
        Call("SetTool", ToolMode.Fog);
        Check(canvas.FogActive && !canvas.FogAnchored && canvas.FogFeet == 90, "fog: picking the tool again turns it on, unanchored, radius kept");
    }
    catch (Exception ex)
    {
        Check(false, $"fog: window views build without errors (failed at {step}: {(ex.InnerException ?? ex)})");
    }
}

// --- multiplayer: dm only flag and revealed cells (model) ---
{
    Check(new Door().DmOnly && new VertexDoor().DmOnly && new AngledDoor().DmOnly && new MapLabel().DmOnly
        && new Pawn().DmOnly && new MapObject().DmOnly, "reveal: new items start DM only");

    // a file from before the flag: everything visible, nothing revealed
    var old = new MapDocument();
    AddRoom(old, 0, 0, 3, 3);
    old.TryPlaceDoor(new EdgeKey(new CellKey(3, 1), Side.East), out _);
    old.Labels.Add(new MapLabel { Cell = new CellKey(1, 1), Text = "x" });
    old.Pawns[new CellKey(2, 2)] = new Pawn { Cell = new CellKey(2, 2), TemplateId = "p" };
    old.Objects.Add(new MapObject { TypeId = "t", X = 0, Y = 0 });
    // clear the flags so nothing writes the key, like a file from before it
    foreach (var it in old.AllItems(RevealItems.All)) MapDocument.SetDmOnly(it, false);
    var oldJson = MapSerializer.ToJson(old);
    Check(!oldJson.Contains("dm_only") && !oldJson.Contains("\"revealed\":[\""), "reveal: test file has no dm_only or revealed cells");
    var rLoaded = MapSerializer.FromJson(oldJson.Replace(",\"revealed\":[]", ""));
    Check(rLoaded.Doors.Values.All(d => !d.DmOnly) && rLoaded.Labels.All(l => !l.DmOnly) && rLoaded.Pawns.Values.All(p => !p.DmOnly)
        && rLoaded.Objects.All(o => !o.DmOnly), "reveal: older files load every item visible");
    Check(rLoaded.Revealed.Count == 0, "reveal: older files load with nothing revealed");

    // round trip: flags and revealed cells survive, visible items write no key
    var rDoc = new MapDocument();
    AddRoom(rDoc, 0, 0, 3, 3);
    rDoc.TryPlaceDoor(new EdgeKey(new CellKey(3, 1), Side.East), out var door);
    rDoc.TryPlaceVertexDoor(2, 0, true, "standard", "closed", out var vdoor);
    vdoor.DmOnly = false;
    rDoc.Labels.Add(new MapLabel { Cell = new CellKey(1, 1), Text = "secret" });
    rDoc.Labels.Add(new MapLabel { Cell = new CellKey(2, 1), Text = "public", DmOnly = false });
    rDoc.Pawns[new CellKey(2, 2)] = new Pawn { Cell = new CellKey(2, 2), TemplateId = "p", DmOnly = false };
    rDoc.Pawns[new CellKey(1, 2)] = new Pawn { Cell = new CellKey(1, 2), TemplateId = "m" };
    rDoc.Objects.Add(new MapObject { TypeId = "t", X = 0, Y = 3 });
    rDoc.SetRevealed(new[] { new CellKey(0, 0), new CellKey(1, 0) }, true);
    var rJson = MapSerializer.ToJson(rDoc);
    var back = MapSerializer.FromJson(rJson);
    Check(back.Doors.Values.Single().DmOnly && !back.VertexDoors.Values.Single().DmOnly, "reveal: door flags round trip");
    Check(back.Labels.Single(l => l.Text == "secret").DmOnly && !back.Labels.Single(l => l.Text == "public").DmOnly, "reveal: label flags round trip");
    Check(!back.Pawns[new CellKey(2, 2)].DmOnly && back.Pawns[new CellKey(1, 2)].DmOnly && back.Objects[0].DmOnly, "reveal: pawn and object flags round trip");
    Check(back.Revealed.SetEquals(rDoc.Revealed) && back.Revealed.Count == 2, "reveal: revealed cells round trip");
    Check(rJson.Split("dm_only").Length - 1 == 4, "reveal: only DM only items write the key");

    // copy, paste, rotate and undo keep the flags
    var snap = rDoc.CaptureRegion(0, 0, 3, 3);
    var copy = new MapDocument();
    copy.PasteRegion(MapDocument.Rotate(snap, 1), 10, 10);
    Check(copy.Labels.Count(l => l.DmOnly) == 1 && copy.Labels.Count(l => !l.DmOnly) == 1, "reveal: a pasted, rotated copy keeps label flags");
    Check(copy.Pawns.Values.Count(p => p.DmOnly) == 1 && copy.Pawns.Values.Count(p => !p.DmOnly) == 1, "reveal: and pawn flags");
    Check(copy.Doors.Values.Single().DmOnly && !copy.VertexDoors.Values.Single().DmOnly, "reveal: and door flags");
    Check(copy.Revealed.Count == 0, "reveal: copy and paste do not carry revealed cells");
    var undo = new MapDocument();
    undo.ApplyFrom(MapSerializer.FromJson(rJson));
    Check(undo.Revealed.Count == 2 && !undo.VertexDoors.Values.Single().DmOnly, "reveal: an undo restore brings back revealed cells and flags");

    // only floor is revealed, anything can be hidden
    var r = new MapDocument();
    AddRoom(r, 0, 0, 3, 3);
    Check(r.SetRevealed(new[] { new CellKey(0, 0), new CellKey(9, 9) }, true) == 1 && !r.Revealed.Contains(new CellKey(9, 9)), "reveal: empty space is not revealed");
    Check(r.SetRevealed(new[] { new CellKey(0, 0) }, true) == 0, "reveal: revealing twice changes nothing");
    Check(r.SetRevealed(new[] { new CellKey(0, 0) }, false) == 1 && r.Revealed.Count == 0, "reveal: hiding removes it");

    // reveal room stops at walls, doors included
    var two = new MapDocument();
    AddRoom(two, 0, 0, 3, 3);
    AddRoom(two, 4, 0, 6, 3);
    two.TryPlaceDoor(new EdgeKey(new CellKey(3, 1), Side.East), out _);
    var room = two.RevealRoomCells(new CellKey(1, 1));
    Check(room.Count == 16 && room.All(c => c.X <= 3), "reveal: room fills to the walls and stops at a door");
    Check(two.RevealRoomCells(new CellKey(20, 20)).Count == 0, "reveal: room outside the floor is empty");

    // items in a box, filtered by kind
    var items = rDoc.ItemsIn(1, 1, 1, 1, RevealItems.All);
    Check(items.Count == 1 && items[0] is MapLabel, "reveal: a one cell box finds the label on it");
    Check(rDoc.ItemsIn(3, 1, 3, 1, RevealItems.Doors).Count == 1 && rDoc.ItemsIn(4, 1, 4, 1, RevealItems.Doors).Count == 1,
        "reveal: an edge door is found from the cell on either side");
    Check(rDoc.ItemsIn(0, 0, 3, 3, RevealItems.Pawns).Count == 2 && rDoc.ItemsIn(0, 0, 3, 3, RevealItems.Labels | RevealItems.Objects).Count == 3,
        "reveal: the kind filter picks what is found");
    Check(rDoc.SetDmOnly(0, 0, 3, 3, RevealItems.Pawns, false) == 1 && rDoc.Pawns.Values.All(p => !p.DmOnly), "reveal: a box marks pawns visible, counting only changes");
    var (lc, dc, oc, pc) = rDoc.DmOnlyCounts();
    Check(lc == 1 && dc == 1 && oc == 1 && pc == 0, "reveal: DM only counts per kind");

    // revealing is an unsaved change
    var proj = new MapProject();
    proj.Maps.Add(new MapEntry { Name = "a", Doc = two });
    var k1 = MapSerializer.ProjectContentKey(proj);
    two.SetRevealed(new[] { new CellKey(5, 1) }, true);
    Check(k1 != MapSerializer.ProjectContentKey(proj), "reveal: revealing counts as an unsaved change");

    var d = AppSettings.Default();
    Check(d.ToolHotkeys["Reveal"] == "H" && d.ToolHotkeys.Values.Count(v => v == "H") == 1, "reveal: hotkey H by default, used by nothing else");
}

// --- multiplayer: reveal tool (canvas) ---
{
    using var h = new CanvasHarness();
    var c = h.Canvas;
    AddRoom(h.Doc, 0, 0, 3, 3);
    AddRoom(h.Doc, 4, 0, 6, 3);
    h.Doc.TryPlaceDoor(new EdgeKey(new CellKey(3, 1), Side.East), out var door);
    c.Mode = ToolMode.Reveal;
    c.ResetReveal();

    int commits = h.CommitCount;
    h.Drag(0, 0, 1, 1);
    Check(h.Doc.Revealed.Count == 4 && h.CommitCount == commits + 1, "reveal tool: a box reveals its floor in one undo step");
    h.Drag(1, 1, 8, 1);
    Check(h.Doc.Revealed.Count == 4 + 5 && !h.Doc.Revealed.Contains(new CellKey(8, 1)), "reveal tool: the box skips empty space");
    c.RevealShow = false;
    h.Click(0, 0);
    Check(!h.Doc.Revealed.Contains(new CellKey(0, 0)), "reveal tool: hide with a click hides one cell");
    commits = h.CommitCount;
    h.Click(15, 15);
    Check(h.CommitCount == commits, "reveal tool: a change of nothing takes no undo step");

    c.RevealShow = true;
    c.RevealShape = RevealShape.Room;
    h.Click(5, 2);
    Check(h.Doc.Revealed.Contains(new CellKey(6, 3)) && h.Doc.Revealed.Contains(new CellKey(4, 0)), "reveal tool: room reveals the room clicked");
    Check(!h.Doc.Revealed.Contains(new CellKey(0, 3)), "reveal tool: and not the room behind the door");

    c.RevealShape = RevealShape.Circle;
    c.RevealAll(false);
    Check(h.Doc.Revealed.Count == 0, "reveal tool: hide whole map");
    h.PressW(1.5, 1.5); h.MoveToAt(h.ScreenWorld(2.5, 1.5)); h.ReleaseW(2.5, 1.5);
    Check(h.Doc.Revealed.SetEquals(MapCanvas.CellsInRadius(new Point(1.5, 1.5), 1)), "reveal tool: circle reveals the cells in the radius");
    c.RevealAll(true);
    Check(h.Doc.Revealed.Count == 28, "reveal tool: reveal whole map takes every floor cell");

    // pixels: revealed floor is brighter than hidden floor while the tool is selected
    c.RevealShape = RevealShape.Box;
    c.RevealShow = false;
    h.Drag(4, 0, 6, 3);
    int Lum((byte r, byte g, byte b, byte a) p) => p.r + p.g + p.b;
    var bmp = h.Render();
    var shown = h.PixelAt(bmp, (int)h.ScreenOf(1, 2).X + 8, (int)h.ScreenOf(1, 2).Y + 8);
    var hidden = h.PixelAt(bmp, (int)h.ScreenOf(5, 2).X + 8, (int)h.ScreenOf(5, 2).Y + 8);
    Check(Lum(shown) > Lum(hidden), "reveal tool: hidden floor is dimmed");
    c.Mode = ToolMode.Select;
    bmp = h.Render();
    hidden = h.PixelAt(bmp, (int)h.ScreenOf(5, 2).X + 8, (int)h.ScreenOf(5, 2).Y + 8);
    Check(Lum(shown) == Lum(hidden), "reveal tool: no dimming in other tools");

    // items: a box flips the ticked kinds
    h.Doc.Labels.Add(new MapLabel { Cell = new CellKey(1, 1), Text = "x" });
    h.Doc.Pawns[new CellKey(2, 2)] = new Pawn { Cell = new CellKey(2, 2), TemplateId = "p" };
    c.Mode = ToolMode.Reveal;
    c.RevealTarget = RevealTarget.Items;
    c.RevealShow = true;
    c.RevealKinds = RevealItems.Pawns | RevealItems.Doors;
    commits = h.CommitCount;
    h.Drag(0, 0, 3, 3);
    Check(!h.Doc.Pawns[new CellKey(2, 2)].DmOnly && !door.DmOnly && h.Doc.Labels[0].DmOnly, "reveal tool: items box marks ticked kinds visible, leaves others");
    Check(h.CommitCount == commits + 1, "reveal tool: in one undo step");
    c.RevealKinds = RevealItems.All;
    c.SetAllDmOnly(true);
    Check(h.Doc.AllItems(RevealItems.All).All(MapDocument.IsDmOnly), "reveal tool: make all DM only");

    // a pawn moved with the move tool keeps its flag
    h.Doc.Pawns[new CellKey(2, 2)].DmOnly = false;
    c.Mode = ToolMode.Move;
    h.Drag(2, 2, 1, 2);
    Check(h.Doc.Pawns.TryGetValue(new CellKey(1, 2), out var moved) && !moved.DmOnly, "reveal: a moved pawn keeps its flag");
}

// --- multiplayer: reveal tool window smoke test ---
{
    _ = new CanvasHarness();
    string step = "construct";
    try
    {
        var w = new DungeonMapper.Views.MainWindow();
        w.Show();
        var t = typeof(DungeonMapper.Views.MainWindow);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? Call(string name, params object?[] args) =>
            t.GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(w, args);
        var canvas = (MapCanvas)t.GetField("Canvas", flags)!.GetValue(w)!;
        AddRoom(canvas.Document, 0, 0, 3, 3);
        step = "cells panel"; Call("SetTool", ToolMode.Reveal);
        canvas.RevealTarget = RevealTarget.Items;
        step = "items panel"; Call("BuildRevealPanel");
        canvas.RevealTarget = RevealTarget.Cells;
        Call("BuildRevealPanel");
        var p = new Pawn { Cell = new CellKey(1, 1), TemplateId = "none" };
        canvas.Document.Pawns[p.Cell] = p;
        canvas.Document.TryPlaceDoor(new EdgeKey(new CellKey(3, 1), Side.East), out var dr);
        var l = new MapLabel { Cell = new CellKey(2, 2), Text = "x" };
        canvas.Document.Labels.Add(l);
        step = "pawn view"; Call("ShowPawnInfo", p);
        step = "door view"; Call("ShowDoorConfig", dr);
        step = "label view"; Call("ShowLabelConfig", l);
        string info = DungeonMapper.Views.MainWindow.RevealInfo(canvas.Document);
        Check(info.StartsWith("0 of 16 floor cells revealed") && info.Contains("1 label, 1 door") && info.Contains("1 pawn"),
            $"reveal: panel info counts ({info.Replace('\n', ' ')})");
        Check(true, "reveal: window views build without errors");
    }
    catch (Exception ex)
    {
        Check(false, $"reveal: window views build without errors (failed at {step}: {(ex.InnerException ?? ex)})");
    }
}

// --- multiplayer: player view (the anti-cheat filter) ---
{
    // room A (0..3) revealed, room B (4..6) behind a door, room C far away
    var m = new MapDocument();
    AddRoom(m, 0, 0, 3, 3);
    AddRoom(m, 4, 0, 6, 3);
    AddRoom(m, 10, 0, 12, 3);
    m.TryPlaceDoor(new EdgeKey(new CellKey(3, 1), Side.East), out var mid); mid.DmOnly = false;
    m.TryPlaceDoor(new EdgeKey(new CellKey(3, 2), Side.East), out var secret); // DM only
    m.TryPlaceDoor(new EdgeKey(new CellKey(6, 1), Side.East), out var far); far.DmOnly = false;
    m.TryPlaceVertexDoor(2, 0, true, "standard", "closed", out var vd); vd.DmOnly = false;
    m.Stairs[new CellKey(1, 1)] = new Stair();
    m.Stairs[new CellKey(5, 1)] = new Stair();
    m.Pawns[new CellKey(1, 2)] = new Pawn { Cell = new CellKey(1, 2), TemplateId = "hero", DmOnly = false };
    m.Pawns[new CellKey(2, 2)] = new Pawn { Cell = new CellKey(2, 2), TemplateId = "lurker" }; // DM only
    m.Pawns[new CellKey(5, 2)] = new Pawn { Cell = new CellKey(5, 2), TemplateId = "monster", DmOnly = false };
    m.Labels.Add(new MapLabel { Cell = new CellKey(0, 3), Text = "entry", DmOnly = false });
    m.Labels.Add(new MapLabel { Cell = new CellKey(1, 3), Text = "trap here" }); // DM only
    m.Labels.Add(new MapLabel { Cell = new CellKey(5, 3), Text = "treasure vault", DmOnly = false });
    m.Labels.Add(new MapLabel { Cell = new CellKey(3, 0), W = 3, Text = "spans", DmOnly = false });
    m.Objects.Add(new MapObject { TypeId = "statue", X = 0, Y = 0, DmOnly = false });
    m.Objects.Add(new MapObject { TypeId = "idol", X = 5, Y = 0, DmOnly = false });
    m.Objects.Add(new MapObject { TypeId = "altar", X = 2, Y = 1 }); // DM only

    var none = PlayerView.Build(m);
    Check(none.Cells.Count == 0 && none.Walls.Count == 0 && none.Doors.Count == 0 && none.VertexDoors.Count == 0 && none.Pawns.Count == 0
        && none.Labels.Count == 0 && none.Objects.Count == 0 && none.Stairs.Count == 0 && none.FloodRegions.Count == 0 && none.AngledWalls.Count == 0,
        "player view: nothing revealed sends nothing at all");

    m.SetRevealed(m.RevealRoomCells(new CellKey(1, 1)), true);
    var v = PlayerView.Build(m);
    Check(v.Cells.SetEquals(m.RevealRoomCells(new CellKey(1, 1))) && v.Cells.Count == 16, "player view: only revealed floor is sent");
    Check(v.Walls.Keys.All(e => v.Cells.Contains(e.Cell) || v.Cells.Contains(e.NeighborCell())), "player view: every wall sent touches revealed floor");
    Check(v.Walls.ContainsKey(EdgeKey.Normalize(new CellKey(0, 0), Side.West)) && v.Walls.ContainsKey(EdgeKey.Normalize(new CellKey(3, 1), Side.East)),
        "player view: the revealed room's outline is sent");
    Check(!v.Walls.ContainsKey(EdgeKey.Normalize(new CellKey(6, 1), Side.East)) && !v.Walls.Keys.Any(e => e.Cell.X >= 10),
        "player view: walls past the revealed room are not");
    Check(v.Doors.ContainsKey(EdgeKey.Normalize(new CellKey(3, 1), Side.East)), "player view: a visible door in the room's wall is sent");
    Check(!v.Doors.ContainsKey(EdgeKey.Normalize(new CellKey(3, 2), Side.East)), "player view: a DM only door is not (its wall is)");
    Check(!v.Doors.ContainsKey(EdgeKey.Normalize(new CellKey(6, 1), Side.East)), "player view: a door in a hidden room is not");
    Check(v.VertexDoors.Count == 1 && MapDocument.VertexStraddles(new VertexDoorKey(2, 0, true)).All(v.HasWall), "player view: a vertex door comes with its walls");
    Check(v.Stairs.Count == 1 && v.Stairs.ContainsKey(new CellKey(1, 1)), "player view: only stairs on revealed floor");
    Check(v.Pawns.Count == 1 && v.Pawns.ContainsKey(new CellKey(1, 2)), "player view: only visible pawns on revealed floor");
    Check(v.Labels.Select(l => l.Text).OrderBy(t => t).SequenceEqual(new[] { "entry", "spans" }), "player view: visible labels touching revealed floor, not DM only or hidden ones");
    Check(v.Objects.Count == 1 && v.Objects[0].TypeId == "statue", "player view: only visible objects on revealed floor");
    var vJson = MapSerializer.ToJson(v);
    Check(!vJson.Contains("trap here") && !vJson.Contains("treasure vault") && !vJson.Contains("lurker") && !vJson.Contains("monster")
        && !vJson.Contains("idol") && !vJson.Contains("altar"), "player view: no hidden text, pawn or object ids anywhere in the sent json");
    Check(!vJson.Contains("dm_only") && v.AllItems(RevealItems.All).All(i => !MapDocument.IsDmOnly(i)), "player view: sent items carry no DM only flag");
    v.Doors.Values.First().State = DoorState.Locked;
    v.Labels[0].Text = "changed";
    Check(mid.State != DoorState.Locked && m.Labels[0].Text == "entry", "player view: the copy shares nothing with the DM's map");

    // revealed cells with no floor are ignored
    m.Revealed.Add(new CellKey(30, 30));
    Check(!PlayerView.Build(m).Revealed.Contains(new CellKey(30, 30)), "player view: a revealed cell with no floor is dropped");

    // flood floors are cut to the revealed cells
    var f = new MapDocument();
    f.FloodRegions.Add(new FloodRegion { Rings = { new() { (20.0, 20.0), (25.0, 20.0), (25.0, 25.0), (20.0, 25.0) } } });
    f.SetRevealed(new[] { new CellKey(20, 20), new CellKey(21, 20), new CellKey(20, 21), new CellKey(21, 21) }, true);
    var fv = PlayerView.Build(f);
    var fb = fv.FloodRegions.Select(r => r.Bounds()).ToList();
    Check(fv.FloodRegions.Count == 1 && fb[0] == (20, 20, 22, 22), $"player view: a flood floor is clipped to the revealed cells ({string.Join(" ", fb)})");
    Check(fv.FloorAt(21.5, 21.5) && !fv.FloorAt(23.5, 23.5), "player view: hidden flood floor is gone");

    // angled walls are cut at lattice points, doors follow their piece
    var a = new MapDocument();
    var aw = new AngledWall(40, 0, 46, 6);
    a.AngledWalls.Add(aw);
    a.AngledDoors[aw] = new List<AngledDoor>
    {
        new() { T = 1.4, DmOnly = false },                 // on the revealed part
        new() { T = 7.0, DmOnly = false },                 // far end, hidden
        new() { T = 2.1 },                                  // revealed part but DM only
    };
    a.Cells.Add(new CellKey(40, 0)); a.Cells.Add(new CellKey(41, 1));
    a.SetRevealed(new[] { new CellKey(40, 0), new CellKey(41, 1) }, true);
    var av = PlayerView.Build(a);
    Check(av.AngledWalls.Count == 1 && av.AngledWalls[0] == new AngledWall(40, 0, 42, 2), $"player view: an angled wall keeps only the revealed piece ({string.Join(" ", av.AngledWalls)})");
    var ad = av.GetAngledDoors(av.AngledWalls[0]);
    Check(ad.Count == 1 && Math.Abs(ad[0].T - 1.4) < 1e-9, "player view: only the visible door on that piece goes with it");

    // the package: only what the sent map uses
    var lib = new PawnLibrary();
    lib.Pawns.Add(new PawnTemplate { Id = "hero", Name = "Hero", Status = "blessed", Favorite = true });
    lib.Pawns.Add(new PawnTemplate { Id = "lurker", Name = "Lurker" });
    lib.Pawns.Add(new PawnTemplate { Id = "monster", Name = "Monster" });
    lib.Formations.Add(new Formation { Name = "party" });
    var objs = new ObjectLibrary();
    string pic1 = objs.AddPicture("AAAA"), pic2 = objs.AddPicture("BBBB");
    objs.Types.Add(new ObjectType { Id = "statue", Name = "Statue", Look = new ObjectLook { PictureId = pic1 } });
    objs.Types.Add(new ObjectType { Id = "idol", Name = "Idol", Look = new ObjectLook { PictureId = pic2 } });
    var pre = new CampaignPresets
    {
        DoorTypes = { new DoorType { Name = "standard" }, new DoorType { Name = "secret door" } },
        PawnStatuses = { new PawnStatus { Name = "blessed" }, new PawnStatus { Name = "invisible" } },
        AffiliationColors = { new AffiliationColor { Name = "Enemy" } },
    };
    secret.Type = "secret door";
    var pk = PlayerView.Package(m, lib, objs, pre);
    Check(pk.Library.Pawns.Count == 1 && pk.Library.Pawns[0].Id == "hero" && !pk.Library.Pawns[0].Favorite && pk.Library.Formations.Count == 0,
        "player view package: only templates of sent pawns, no favorites or formations");
    Check(pk.Objects.Types.Count == 1 && pk.Objects.Types[0].Id == "statue" && pk.Objects.Pictures.Keys.SequenceEqual(new[] { pic1 }),
        "player view package: only object types and pictures in use");
    Check(pk.Presets.DoorTypes.Select(t => t.Name).SequenceEqual(new[] { "standard" }), "player view package: a hidden door's type name is not sent");
    Check(pk.Presets.PawnStatuses.Select(t => t.Name).SequenceEqual(new[] { "blessed", "invisible" }) && pk.Presets.AffiliationColors.Count == 1,
        "player view package: every status (players set their pawns' status), every affiliation color");
}

// --- multiplayer: player view preview (canvas) ---
{
    using var h = new CanvasHarness();
    var c = h.Canvas;
    AddRoom(h.Doc, 0, 0, 3, 3);
    AddRoom(h.Doc, 4, 0, 6, 3);
    h.Doc.SetRevealed(h.Doc.RevealRoomCells(new CellKey(1, 1)), true);
    int Lum((byte r, byte g, byte b, byte a) p) => p.r + p.g + p.b;
    c.Mode = ToolMode.Select;
    var bmp = h.Render();
    var a0 = h.PixelAt(bmp, (int)h.ScreenOf(1, 2).X + 8, (int)h.ScreenOf(1, 2).Y + 8);
    var b0 = h.PixelAt(bmp, (int)h.ScreenOf(5, 2).X + 8, (int)h.ScreenOf(5, 2).Y + 8);
    Check(Lum(a0) == Lum(b0), "preview: off, both rooms draw the same");

    c.PlayerPreview = true;
    Check(c.Document != h.Doc && c.Document.Cells.Count == 16, "preview: the canvas shows the players' copy");
    bmp = h.Render();
    var a1 = h.PixelAt(bmp, (int)h.ScreenOf(1, 2).X + 8, (int)h.ScreenOf(1, 2).Y + 8);
    var b1 = h.PixelAt(bmp, (int)h.ScreenOf(5, 2).X + 8, (int)h.ScreenOf(5, 2).Y + 8);
    Check(Lum(a1) == Lum(a0) && Lum(b1) != Lum(b0), "preview: the hidden room is not drawn");

    int commits = h.CommitCount;
    c.Mode = ToolMode.Room;
    h.Drag(10, 10, 12, 12);
    c.Mode = ToolMode.Erase;
    h.Drag(0, 0, 3, 3);
    Check(h.CommitCount == commits && h.Doc.Cells.Count == 28 && !h.Doc.Cells.Contains(new CellKey(10, 10)), "preview: clicks and drags edit nothing");

    h.Doc.SetRevealed(new[] { new CellKey(5, 1) }, true);
    c.Refresh();
    Check(c.Document.Cells.Contains(new CellKey(5, 1)), "preview: a refresh after a change shows it");
    c.PlayerPreview = false;
    Check(c.Document == h.Doc, "preview: off, the canvas is back on the real map");
}

// --- multiplayer: player view preview (window) ---
{
    _ = new CanvasHarness();
    string step = "construct";
    try
    {
        var w = new DungeonMapper.Views.MainWindow();
        w.Show();
        var t = typeof(DungeonMapper.Views.MainWindow);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? Call(string name, params object?[] args) =>
            t.GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(w, args);
        var canvas = (MapCanvas)t.GetField("Canvas", flags)!.GetValue(w)!;
        var panel = (DungeonMapper.Views.ToolPanel)t.GetField("ToolPanel", flags)!.GetValue(w)!;
        step = "reveal tool"; Call("SetTool", ToolMode.Reveal);
        step = "preview on"; Call("SetPlayerPreview", true);
        Check(canvas.PlayerPreview && !panel.IsEnabled, "preview: on from the window, the side panel is disabled");
        step = "tool switch"; Call("SetTool", ToolMode.Room);
        Check(!canvas.PlayerPreview && panel.IsEnabled, "preview: picking a tool returns to editing");
    }
    catch (Exception ex)
    {
        Check(false, $"preview: window test (failed at {step}: {(ex.InnerException ?? ex)})");
    }
}

// --- multiplayer: network ---
{
    bool WaitFor(Func<bool> cond, int ms = 4000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(20);
        }
        return cond();
    }
    T Sync<T>(Func<Task<T>> f) => Task.Run(f).GetAwaiter().GetResult();
    void SyncV(Func<Task> f) => Task.Run(f).GetAwaiter().GetResult();

    // framing
    var ms0 = new MemoryStream();
    SyncV(() => DungeonMapper.Net.NetProtocol.WriteAsync(ms0, new DungeonMapper.Net.NetMessage { Type = "hello", Name = "Ana" }));
    ms0.Position = 0;
    var rd = Sync(() => DungeonMapper.Net.NetProtocol.ReadAsync(ms0));
    Check(rd?.Type == "hello" && rd.Name == "Ana" && rd.Protocol == DungeonMapper.Net.NetProtocol.Version, "net: a message round trips through the framing");
    Check(Sync(() => DungeonMapper.Net.NetProtocol.ReadAsync(ms0)) is null, "net: end of stream reads as closed");
    var huge = new MemoryStream(new byte[] { 0x7f, 0xff, 0xff, 0xff });
    bool refusedHuge = false;
    try { Sync(() => DungeonMapper.Net.NetProtocol.ReadAsync(huge)); } catch (InvalidDataException) { refusedHuge = true; }
    Check(refusedHuge, "net: an oversized message is refused");

    string app = DungeonMapper.Net.NetProtocol.AppVersion;
    Check(DungeonMapper.Net.NetProtocol.CheckHello(new() { Type = "hello", App = app }, "") is null, "net: a matching hello is accepted");
    Check(DungeonMapper.Net.NetProtocol.CheckHello(new() { Type = "hello", App = "0.0.0.1" }, "")?.Contains("version") == true, "net: another app version is refused");
    Check(DungeonMapper.Net.NetProtocol.CheckHello(new() { Type = "hello", App = app, Protocol = 99 }, "")?.Contains("version") == true, "net: another protocol is refused");
    Check(DungeonMapper.Net.NetProtocol.CheckHello(new() { Type = "hello", App = app, Code = "x" }, "dragon") == "wrong session code", "net: a wrong code is refused");
    Check(DungeonMapper.Net.NetProtocol.CheckHello(new() { Type = "hello", App = app, Code = "dragon" }, "dragon") is null, "net: the right code is accepted");

    // a map to show
    var map = new MapDocument();
    AddRoom(map, 0, 0, 3, 3);
    AddRoom(map, 4, 0, 6, 3);
    map.SetRevealed(map.RevealRoomCells(new CellKey(1, 1)), true);
    map.Labels.Add(new MapLabel { Cell = new CellKey(5, 1), Text = "secret vault", DmOnly = false });
    var objs = new ObjectLibrary();
    string pic = objs.AddPicture("QUJDRA==");
    objs.Types.Add(new ObjectType { Id = "st", Name = "Statue", Look = new ObjectLook { PictureId = pic } });
    map.Objects.Add(new MapObject { TypeId = "st", X = 1, Y = 1, DmOnly = false });
    var lib = new PawnLibrary();
    var pre = CampaignPresets.Default();
    PlayerPackage Pack() => PlayerView.Package(map, lib, objs, pre);

    using var host = new DungeonMapper.Net.SessionHost { SessionName = "Barrow", Code = "" };
    host.Start(0, beacon: false);
    Check(host.Running && host.Port > 0, "net: the host listens");
    host.Publish("Level 1", Pack());

    var client = Sync(() => DungeonMapper.Net.SessionClient.ConnectAsync("127.0.0.1", host.Port, "", "Ana"));
    var got = new System.Collections.Concurrent.ConcurrentQueue<(string, PlayerPackage)>();
    string? gone = null;
    client.StateReceived += (n, pk) => got.Enqueue((n, pk));
    client.Disconnected += r => gone = r;
    client.Start();
    Check(client.SessionName == "Barrow", "net: the welcome carries the session name");
    Check(WaitFor(() => got.Count >= 1), "net: a joining player gets the current state");
    got.TryDequeue(out var first);
    Check(first.Item1 == "Level 1" && first.Item2.Map.Cells.Count == 16 && first.Item2.Map.Labels.Count == 0, "net: the state is the player copy (revealed room only, hidden label left out)");
    Check(first.Item2.Objects.Picture(pic) == "QUJDRA==" && first.Item2.Objects.Types.Count == 1, "net: object types and their pictures arrive");
    Check(WaitFor(() => host.Players.SequenceEqual(new[] { "Ana" })), "net: the host lists the player by name");

    Check(!host.Publish("Level 1", Pack()), "net: publishing the same thing again sends nothing");
    map.SetRevealed(map.RevealRoomCells(new CellKey(5, 1)), true);
    Check(host.Publish("Level 1", Pack()), "net: a reveal is new");
    Check(WaitFor(() => got.Count >= 1), "net: the player gets the update");
    got.TryDequeue(out var second);
    Check(second.Item2.Map.Cells.Count == 28 && second.Item2.Map.Labels.Count == 1, "net: the update shows the newly revealed room");
    Check(second.Item2.Objects.Picture(pic) == "QUJDRA==", "net: the player keeps pictures it already has");

    // raw: a picture is sent once per player
    SyncV(async () =>
    {
        using var raw = new System.Net.Sockets.TcpClient();
        await raw.ConnectAsync("127.0.0.1", host.Port);
        var st = raw.GetStream();
        await DungeonMapper.Net.NetProtocol.WriteAsync(st, new() { Type = "hello", App = app, Name = "raw" });
        var w = await DungeonMapper.Net.NetProtocol.ReadAsync(st);
        var s1 = await DungeonMapper.Net.NetProtocol.ReadAsync(st);
        map.Stairs[new CellKey(1, 1)] = new Stair();
        host.Publish("Level 1", Pack());
        var s2 = await DungeonMapper.Net.NetProtocol.ReadAsync(st);
        Check(w?.Type == "welcome" && s1?.Package?.ObjectPictures?.Count == 1 && s2?.Package?.ObjectPictures?.Count == 0,
            "net: each picture goes to a player once");
    });

    // refusals
    host.Code = "dragon";
    string? why = null;
    try { Sync(() => DungeonMapper.Net.SessionClient.ConnectAsync("127.0.0.1", host.Port, "nope", "Bo")); }
    catch (DungeonMapper.Net.SessionRefusedException ex) { why = ex.Message; }
    Check(why == "wrong session code", "net: a wrong code is turned away");
    SyncV(async () =>
    {
        using var raw = new System.Net.Sockets.TcpClient();
        await raw.ConnectAsync("127.0.0.1", host.Port);
        var st = raw.GetStream();
        await DungeonMapper.Net.NetProtocol.WriteAsync(st, new() { Type = "hello", App = "0.0.0.1", Code = "dragon" });
        var r = await DungeonMapper.Net.NetProtocol.ReadAsync(st);
        Check(r?.Type == "refuse" && r.Reason!.Contains("version"), "net: an older app is turned away with the reason");
    });

    // leaving and stopping
    client.Dispose();
    Check(WaitFor(() => host.Players.Count == 0), "net: a player leaving drops off the host's list");
    host.Code = "";
    var c2 = Sync(() => DungeonMapper.Net.SessionClient.ConnectAsync("127.0.0.1", host.Port, "", "Cy"));
    string? c2gone = null;
    c2.Disconnected += r => c2gone = r;
    c2.Start();
    Check(WaitFor(() => host.Players.Count == 1), "net: another player joins");
    host.Stop();
    Check(WaitFor(() => c2gone is not null), $"net: stopping the host tells the player ({c2gone})");
    bool noHost = false;
    try { Sync(() => DungeonMapper.Net.SessionClient.ConnectAsync("127.0.0.1", host.Port, "", "Cy", 2000)); }
    catch (Exception) { noHost = true; }
    Check(noHost, "net: a stopped host takes nobody");

    // discovery: beacons to loopback on a test port
    using var browser = new DungeonMapper.Net.SessionBrowser();
    Check(browser.Start(47391), "net: the browser listens");
    using var h2 = new DungeonMapper.Net.SessionHost { SessionName = "Crypt", Code = "x", BeaconMillis = 200,
        BeaconTargets = new() { new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 47391) } };
    h2.Start(0);
    Check(WaitFor(() => browser.Sessions.Count == 1), "net: the browser hears the host's beacon");
    var fs = browser.Sessions.FirstOrDefault();
    Check(fs is not null && fs.Session == "Crypt" && fs.Port == h2.Port && fs.NeedsCode && fs.SameVersion && fs.Address == "127.0.0.1",
        "net: the beacon carries name, port, code needed and version");
    h2.Stop();
}

// --- multiplayer: player mode (window) ---
{
    _ = new CanvasHarness();
    string step = "construct";
    try
    {
        bool WaitFor(Func<bool> cond, int ms = 4000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (cond()) return true;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                System.Threading.Thread.Sleep(20);
            }
            return cond();
        }
        var w = new DungeonMapper.Views.MainWindow();
        w.Show();
        var t = typeof(DungeonMapper.Views.MainWindow);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? Call(string name, params object?[] args) =>
            t.GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(w, args);
        object? Field(string name) => t.GetField(name, flags)!.GetValue(w);
        var canvas = (MapCanvas)Field("Canvas")!;
        AddRoom(canvas.Document, 0, 0, 3, 3);
        canvas.Document.SetRevealed(canvas.Document.RevealRoomCells(new CellKey(1, 1)), true);

        // hosting from the window, on a free port
        step = "host modal"; Call("ShowHostSession");
        t.GetField("_hostPort", flags)!.SetValue(w, 0);
        step = "start hosting"; Check(Call("StartHosting") is null, "session window: hosting starts");
        var host = (DungeonMapper.Net.SessionHost)Field("_host")!;
        step = "hosting modal"; Call("ShowHostSession");
        var client = Task.Run(() => DungeonMapper.Net.SessionClient.ConnectAsync("127.0.0.1", host.Port, "", "Dee")).GetAwaiter().GetResult();
        PlayerPackage? last = null;
        client.StateReceived += (_, pk) => last = pk;
        client.Start();
        Check(WaitFor(() => last?.Map.Cells.Count == 16), "session window: the host sends the shown map");
        canvas.Document.SetRevealed(new[] { new CellKey(9, 9) }, true); // no floor: no change
        canvas.Document.SetRevealed(new[] { new CellKey(0, 0) }, false);
        // the publish timer does not tick in the headless test, so publish the way it does
        Call("PublishNow");
        Check(WaitFor(() => last?.Map.Cells.Count == 15), "session window: a change on the DM's map reaches the player");
        client.Dispose();
        step = "stop hosting"; Call("StopHosting");
        Check(Field("_host") is null, "session window: hosting stops");

        // the join modal builds and closes
        step = "join modal"; Call("ShowJoinSession", (string?)null);
        Call("CloseModals");
        Check(Field("_browser") is null, "session window: closing the join window stops listening");

        // player mode against a test host
        step = "player mode";
        using var th = new DungeonMapper.Net.SessionHost { SessionName = "Tomb" };
        th.Start(0, beacon: false);
        var remote = new MapDocument();
        AddRoom(remote, 20, 20, 21, 21);
        remote.SetRevealed(remote.FloorCells(), true);
        th.Publish("Tomb 1", PlayerView.Package(remote, new PawnLibrary(), new ObjectLibrary(), CampaignPresets.Default()));
        var pc = Task.Run(() => DungeonMapper.Net.SessionClient.ConnectAsync("127.0.0.1", th.Port, "", "Eve")).GetAwaiter().GetResult();
        Call("EnterPlayerMode", pc);
        Check(WaitFor(() => canvas.RemoteDocument?.Cells.Count == 4), "player mode: the canvas shows the host's map");
        Check(canvas.ReadOnlyView && canvas.Mode == ToolMode.Measure, "player mode: read only, measure tool picked");
        var room = (Avalonia.Controls.Button)Field("ToolRoom")!;
        var measure = (Avalonia.Controls.Button)Field("ToolMeasure")!;
        var file = (Avalonia.Controls.MenuItem)Field("MenuFile")!;
        var move = (Avalonia.Controls.Button)Field("ToolMove")!;
        Check(!room.IsEnabled && measure.IsEnabled && move.IsEnabled && !file.IsEnabled, "player mode: editing tools and the file menu are off, measure and move (own heroes) on");
        step = "your pawns panel"; Call("SetTool", ToolMode.Move); Call("SetTool", ToolMode.Measure);
        Check(canvas.Document != remote && canvas.Document.Cells.Contains(new CellKey(20, 20)), "player mode: the received copy is drawn");
        th.Stop();
        Check(WaitFor(() => canvas.RemoteDocument is null), "player mode: the host stopping returns the player to their own maps");
        Check(room.IsEnabled && file.IsEnabled && canvas.Document.Cells.Count == 16, "player mode: own map and tools are back");
    }
    catch (Exception ex)
    {
        Check(false, $"session window: test failed at {step}: {(ex.InnerException ?? ex)}");
    }
}

// --- multiplayer: pings ---
{
    bool WaitFor(Func<bool> cond, int ms = 4000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(20);
        }
        return cond();
    }
    var d = AppSettings.Default();
    Check(d.ToolHotkeys[AppSettings.PingHotkey] == "OemPipe" && d.ToolHotkeys.Values.Count(v => v == "OemPipe") == 1, "ping: backslash by default, used by nothing else");
    Check(AppSettings.HotkeyNameOf(Avalonia.Input.Key.OemPipe) == "OemPipe" && AppSettings.HotkeyNameOf(Avalonia.Input.Key.Q) == "Q"
        && AppSettings.HotkeyNameOf(Avalonia.Input.Key.D7) == "D7" && AppSettings.HotkeyNameOf(Avalonia.Input.Key.F5) is null,
        "ping: hotkeys take letters, digits and punctuation keys, not function keys");
    Check(AppSettings.HotkeyLabel("OemPipe") == "\\" && AppSettings.HotkeyLabel("D7") == "7" && AppSettings.HotkeyLabel("Q") == "Q", "ping: keys show as their symbol");
    var imp = AppSettings.ParseImport("{\"kind\":\"settings\",\"tool_hotkeys\":{\"Ping\":\"OemOpenBrackets\",\"Fog\":\"F5\"}}")!;
    Check(imp.ToolHotkeys["Ping"] == "OemOpenBrackets" && imp.ToolHotkeys["Fog"] == "V", "ping: a punctuation hotkey survives loading, a bad one goes back to the default");

    using (var h = new CanvasHarness())
    {
        h.MoveTo(3, 4);
        Check(h.Canvas.CursorCell == new CellKey(3, 4), "ping: the canvas knows the cell under the cursor");
        h.Canvas.ShowPing(new CellKey(3, 4), "Ana");
        Check(h.Canvas.PingCount == 1, "ping: a ping shows on the map");
        var bmp = h.Render();
        var px = h.PixelAt(bmp, (int)h.ScreenOf(3, 4).X, (int)h.ScreenOf(3, 4).Y);
        Check(px.b > 150 && px.g > 150 && px.r < 200, $"ping: the ping dot is drawn ({px})");
    }

    // over the network: player to DM and the other players, DM to players
    using var host = new DungeonMapper.Net.SessionHost();
    host.Start(0, beacon: false);
    var hostPings = new System.Collections.Concurrent.ConcurrentQueue<(string, int, int)>();
    host.PingReceived += (n, x, y) => hostPings.Enqueue((n, x, y));
    var a = Task.Run(() => DungeonMapper.Net.SessionClient.ConnectAsync("127.0.0.1", host.Port, "", "Ana")).GetAwaiter().GetResult();
    var b = Task.Run(() => DungeonMapper.Net.SessionClient.ConnectAsync("127.0.0.1", host.Port, "", "Bo")).GetAwaiter().GetResult();
    var aPings = new System.Collections.Concurrent.ConcurrentQueue<(string, int, int)>();
    var bPings = new System.Collections.Concurrent.ConcurrentQueue<(string, int, int)>();
    a.PingReceived += (n, x, y) => aPings.Enqueue((n, x, y));
    b.PingReceived += (n, x, y) => bPings.Enqueue((n, x, y));
    a.Start(); b.Start();
    WaitFor(() => host.Players.Count == 2);
    Task.Run(() => a.SendPing(12, 7)).Wait();
    Check(WaitFor(() => hostPings.Count == 1) && hostPings.TryPeek(out var hp) && hp == ("Ana", 12, 7), "ping: the DM gets a player's ping with their name");
    Check(WaitFor(() => bPings.Count == 1) && bPings.TryPeek(out var bp) && bp == ("Ana", 12, 7), "ping: the other players get it too");
    System.Threading.Thread.Sleep(200);
    Check(aPings.IsEmpty, "ping: the pinging player does not get their own ping back");
    Task.Run(() => a.SendPing(1, 1)).Wait();
    Task.Run(() => a.SendPing(2, 2)).Wait();
    System.Threading.Thread.Sleep(300);
    Check(hostPings.Count == 1, "ping: pings closer together than the gap are dropped");
    host.Ping("DM", -3, 5);
    Check(WaitFor(() => aPings.Count == 1 && bPings.Count == 2) && aPings.TryPeek(out var ap) && ap == ("DM", -3, 5), "ping: every player gets the DM's ping");
    a.Dispose(); b.Dispose();
}

// --- multiplayer: ping toasts (window) ---
{
    _ = new CanvasHarness();
    try
    {
        var w = new DungeonMapper.Views.MainWindow();
        w.Show();
        var t = typeof(DungeonMapper.Views.MainWindow);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? Call(string name, params object?[] args) =>
            t.GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(w, args);
        var canvas = (MapCanvas)t.GetField("Canvas", flags)!.GetValue(w)!;
        var toasts = (Avalonia.Controls.StackPanel)t.GetField("PingToasts", flags)!.GetValue(w)!;
        Call("OnClientPing", "DM", 4, 2);
        Check(toasts.Children.Count == 1 && canvas.PingCount == 1, "ping toast: a ping adds a toast and rings on the map");
        var text = ((toasts.Children[0] as Avalonia.Controls.Border)!.Child as Avalonia.Controls.Grid)!.Children.OfType<Avalonia.Controls.TextBlock>().First().Text;
        Check(text == "DM pinged 4,2", $"ping toast: says who and where ({text})");
        for (int i = 0; i < 6; i++) Call("OnClientPing", "Bo", i, i);
        Check(toasts.Children.Count == DungeonMapper.Views.MainWindow.MaxToasts, "ping toast: at most four at once");
        var jump = ((toasts.Children[0] as Avalonia.Controls.Border)!.Child as Avalonia.Controls.Grid)!.Children.OfType<Avalonia.Controls.Button>().First();
        jump.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Check(toasts.Children.Count == DungeonMapper.Views.MainWindow.MaxToasts - 1, "ping toast: jump closes it");
    }
    catch (Exception ex)
    {
        Check(false, $"ping toast: window test ({(ex.InnerException ?? ex)})");
    }
}

// --- multiplayer: player pawns (rules) ---
{
    var lib = new PawnLibrary();
    PawnTemplate T(string id, string aff, bool hero) { var t = new PawnTemplate { Id = id, Name = id, Affiliation = aff, Hero = hero }; lib.Pawns.Add(t); return t; }
    var pc = T("pc", "Player", true);
    T("pc2", "Player", true);
    var grunt = T("pg", "Player", false);
    var allyHero = T("ah", "Ally", true);
    T("orc", "Enemy", false);
    T("cat", "Neutral", false);
    T("old", "Friendly", true);
    Check(PlayerPawns.Controlled(pc) && PlayerPawns.Controlled(grunt) && !PlayerPawns.Controlled(allyHero)
        && !PlayerPawns.Controlled(lib.Find("orc")) && !PlayerPawns.Controlled(lib.Find("cat")) && !PlayerPawns.Controlled(lib.Find("old")) && !PlayerPawns.Controlled(null),
        "player pawns: every Player affiliation pawn is the players', hero or not");

    var d = new MapDocument();
    AddRoom(d, 0, 0, 5, 5);
    AddRoom(d, 7, 0, 9, 2);
    d.SetRevealed(d.RevealRoomCells(new CellKey(1, 1)), true);
    void Put(int x, int y, string id, bool dm = false) => d.Pawns[new CellKey(x, y)] = new Pawn { Cell = new CellKey(x, y), TemplateId = id, DmOnly = dm };
    Put(1, 1, "pc"); Put(2, 2, "orc"); Put(3, 3, "pg"); Put(4, 4, "pc2", dm: true);
    d.Pawns[new CellKey(1, 1)].Status = "hasted";
    CellKey K(int x, int y) => new(x, y);
    Check(PlayerPawns.CheckMove(d, lib, K(1, 1), K(1, 4)) is null, "player pawns: a hero moves onto empty revealed floor");
    Check(PlayerPawns.CheckMove(d, lib, K(3, 3), K(0, 4)) is null, "player pawns: a Player pawn that is not a hero moves too");
    Check(PlayerPawns.CheckMove(d, lib, K(2, 2), K(1, 4)) == "only the DM moves that pawn", "player pawns: an enemy is refused");
    Check(PlayerPawns.CheckMove(d, lib, K(4, 4), K(1, 4)) == "there is no pawn there", "player pawns: a DM only pawn can not be moved by players");
    Check(PlayerPawns.CheckMove(d, lib, K(1, 1), K(2, 2)) == "that cell already has a pawn", "player pawns: an occupied cell is refused");
    Check(PlayerPawns.CheckMove(d, lib, K(1, 1), K(8, 1)) == "pawns can only go onto revealed floor", "player pawns: hidden floor is refused");
    Check(PlayerPawns.CheckMove(d, lib, K(1, 1), K(6, 1)) == "pawns can only go onto revealed floor", "player pawns: off the floor is refused");
    PlayerPawns.ApplyMove(d, K(1, 1), K(1, 4));
    Check(!d.Pawns.ContainsKey(K(1, 1)) && d.Pawns[K(1, 4)].TemplateId == "pc" && d.Pawns[K(1, 4)].Status == "hasted" && !d.Pawns[K(1, 4)].DmOnly, "player pawns: a move keeps the status and stays visible");

    Check(PlayerPawns.CheckPlace(d, lib, "orc", K(0, 0)) == "only the DM places that pawn", "player pawns: placing an enemy is refused");
    Check(PlayerPawns.CheckPlace(d, lib, "ah", K(0, 0)) == "only the DM places that pawn", "player pawns: placing an ally hero is refused");
    Check(PlayerPawns.CheckPlace(d, lib, "pc2", K(0, 0)) == PlayerPawns.HiddenHero, "player pawns: a hero the DM hid on this map is refused");
    Check(PlayerPawns.CheckPlace(d, lib, "pc", K(0, 0), onOtherMap: true) == PlayerPawns.HiddenHero, "player pawns: a hero on another map is refused");
    d.Pawns[K(4, 4)].DmOnly = false;
    d.Revealed.Remove(K(4, 4));
    Check(PlayerPawns.CheckPlace(d, lib, "pc2", K(0, 0)) == PlayerPawns.HiddenHero, "player pawns: a hero on unrevealed floor is refused");
    d.Revealed.Add(K(4, 4));
    Check(PlayerPawns.CheckPlace(d, lib, "pc", K(8, 1)) is not null && PlayerPawns.CheckPlace(d, lib, "pc", K(0, 0)) is null, "player pawns: a hero goes onto revealed floor only");
    d.Pawns[K(4, 4)].Status = "prone";
    bool moved = PlayerPawns.ApplyPlace(d, lib.Find("pc2")!, K(0, 0));
    Check(moved && !d.Pawns.ContainsKey(K(4, 4)) && d.Pawns[K(0, 0)].Status == "prone", "player pawns: placing a visible hero moves it, status kept");
    Check(!PlayerPawns.ApplyPlace(d, grunt, K(0, 1)) && !PlayerPawns.ApplyPlace(d, grunt, K(0, 2)) && d.Pawns.Values.Count(p => p.TemplateId == "pg") == 3,
        "player pawns: a non-hero Player pawn adds a copy each time");

    // status
    var pre = CampaignPresets.Default();
    string st = pre.PawnStatuses.First(x => x.Name != CampaignPresets.NoStatus).Name;
    Check(PlayerPawns.CheckStatus(d, lib, pre, K(0, 1), st) is null && PlayerPawns.CheckStatus(d, lib, pre, K(0, 1), null) is null, "player pawns: a status on a Player pawn is allowed");
    Check(PlayerPawns.CheckStatus(d, lib, pre, K(2, 2), st) == "only the DM changes that pawn", "player pawns: an enemy's status is refused");
    Check(PlayerPawns.CheckStatus(d, lib, pre, K(0, 1), "made up") == "that status does not exist", "player pawns: an unknown status is refused");

    // deploy
    var dep = new List<(string, CellKey)> { ("pc", K(2, 4)), ("pg", K(3, 4)) };
    Check(PlayerPawns.CheckDeploy(d, lib, dep) is null, "player pawns: a deploy of Player pawns is allowed");
    Check(PlayerPawns.CheckDeploy(d, lib, new List<(string, CellKey)> { ("pc", K(1, 4)), ("pg", K(3, 4)) }) is null, "player pawns: a deploy may land where its own hero stands");
    Check(PlayerPawns.CheckDeploy(d, lib, new List<(string, CellKey)> { ("pg", K(2, 2)) }) == "a cell already has a pawn", "player pawns: a deploy onto another pawn is refused");
    Check(PlayerPawns.CheckDeploy(d, lib, new List<(string, CellKey)> { ("orc", K(4, 1)) }) is not null, "player pawns: a deploy with an enemy is refused");
    Check(PlayerPawns.CheckDeploy(d, lib, new List<(string, CellKey)> { ("pg", K(8, 1)) }) is not null, "player pawns: a deploy onto hidden floor is refused");
    Check(PlayerPawns.CheckDeploy(d, lib, dep, id => id == "pc") == PlayerPawns.HiddenHero, "player pawns: a deploy with a hero on another map is refused");
    d.Pawns[K(1, 4)].Status = "hasted";
    PlayerPawns.ApplyDeploy(d, new[] { pc, grunt }, dep);
    Check(!d.Pawns.ContainsKey(K(1, 4)) && d.Pawns[K(2, 4)].Status == "hasted" && d.Pawns[K(3, 4)].TemplateId == "pg" && d.Pawns.Values.Count(p => p.TemplateId == "pc") == 1,
        "player pawns: a deploy moves the hero (status kept) and adds the rest");

    // formations
    var good = new Formation { Id = "f1", Name = "party", Members = { new FormationMember { TemplateId = "pc" }, new FormationMember { TemplateId = "pg", Dx = 1 } } };
    var mixed = new Formation { Id = "f2", Name = "ambush", Members = { new FormationMember { TemplateId = "pc" }, new FormationMember { TemplateId = "orc", Dx = 1 } } };
    Check(PlayerPawns.CheckFormation(lib, good) is null && PlayerPawns.CheckFormation(lib, mixed) is not null && PlayerPawns.CheckFormation(lib, new Formation { Id = "e" }) is not null,
        "player pawns: only formations of Player pawns are the players'");
    lib.Formations.Add(good);
    lib.Formations.Add(mixed);

    // the package carries every Player pawn, their formations and every status
    var pkg = PlayerView.Package(d, lib, new ObjectLibrary(), pre);
    var ids = pkg.Library.Pawns.Select(t => t.Id).ToHashSet();
    Check(ids.Contains("pc") && ids.Contains("pc2") && ids.Contains("pg") && !ids.Contains("ah") && ids.Contains("orc") && !ids.Contains("cat"),
        "player pawns: the package has every Player pawn plus the templates on the map");
    Check(pkg.Library.Formations.Select(f => f.Id).SequenceEqual(new[] { "f1" }), "player pawns: only Player formations are sent");
    Check(pkg.Presets.PawnStatuses.Count == pre.PawnStatuses.Count, "player pawns: every status is sent");
    var wire = MapSerializer.PackageFromWire(MapSerializer.PackageToWire(pkg));
    Check(wire.Library.Formations.Count == 1 && wire.Library.Formations[0].Members.Count == 2, "player pawns: formations cross the wire");
}

// --- multiplayer: player pawns (canvas) ---
{
    using var h = new CanvasHarness();
    var c = h.Canvas;
    var lib = new PawnLibrary();
    lib.Pawns.Add(new PawnTemplate { Id = "pc", Name = "Ana", Affiliation = "Player", Hero = true });
    lib.Pawns.Add(new PawnTemplate { Id = "pg", Name = "Hireling", Affiliation = "Player" });
    lib.Pawns.Add(new PawnTemplate { Id = "orc", Name = "Orc", Affiliation = "Enemy" });
    c.Library = lib;
    var r = new MapDocument();
    AddRoom(r, 0, 0, 5, 5);
    AddRoom(r, 7, 0, 9, 2);
    r.SetRevealed(r.RevealRoomCells(new CellKey(1, 1)), true);
    r.Pawns[new CellKey(1, 1)] = new Pawn { Cell = new CellKey(1, 1), TemplateId = "pc", DmOnly = false };
    r.Pawns[new CellKey(2, 2)] = new Pawn { Cell = new CellKey(2, 2), TemplateId = "orc", DmOnly = false };
    r.Pawns[new CellKey(3, 3)] = new Pawn { Cell = new CellKey(3, 3), TemplateId = "pg", DmOnly = false };
    c.RemoteDocument = r;
    c.Mode = ToolMode.Move;
    var moves = new List<(CellKey, CellKey)>();
    var places = new List<(string, CellKey)>();
    var deploys = new List<List<(string id, CellKey cell)>>();
    var opened = new List<Pawn>();
    c.PlayerMoveRequested += (f, t) => moves.Add((f, t));
    c.PlayerPlaceRequested += (id, at) => places.Add((id, at));
    c.PlayerDeployRequested += l => deploys.Add(l);
    c.PawnConfigRequested += p => opened.Add(p);
    int commits = h.CommitCount;

    h.Drag(1, 1, 4, 1);
    Check(moves.Count == 1 && moves[0] == (new CellKey(1, 1), new CellKey(4, 1)), "player pawns canvas: dragging a hero asks the DM");
    h.Drag(3, 3, 3, 4);
    Check(moves.Count == 2, "player pawns canvas: dragging a non-hero Player pawn asks the DM");
    Check(r.Pawns.ContainsKey(new CellKey(1, 1)) && h.CommitCount == commits, "player pawns canvas: nothing changes until the DM sends the map");
    h.Drag(2, 2, 4, 2);
    Check(moves.Count == 2 && h.LastStatus == "only the DM moves that pawn", "player pawns canvas: an enemy can not be dragged");
    h.Drag(1, 1, 8, 1);
    Check(moves.Count == 2 && h.LastStatus == "pawns can only go onto revealed floor", "player pawns canvas: hidden floor is refused before sending");

    // the pawn tool: place the armed pawn, open a placed Player pawn
    c.Mode = ToolMode.Pawn;
    c.ArmedPawn = lib.Find("orc");
    h.Click(0, 4);
    Check(places.Count == 0 && h.LastStatus == "only the DM places that pawn", "player pawns canvas: an enemy can not be placed");
    c.ArmedPawn = lib.Find("pg");
    h.Click(9, 9);
    Check(places.Count == 0, "player pawns canvas: placing off the revealed floor is refused");
    h.Click(0, 4);
    h.Click(1, 4);
    Check(places.Count == 2 && places[0] == ("pg", new CellKey(0, 4)) && c.ArmedPawn?.Id == "pg", "player pawns canvas: a click places the armed pawn and it stays armed");
    h.Click(3, 3);
    Check(opened.Count == 1 && opened[0].TemplateId == "pg" && places.Count == 2, "player pawns canvas: clicking a placed Player pawn opens it");
    h.Click(2, 2);
    Check(opened.Count == 1 && h.LastStatus == "only the DM changes that pawn", "player pawns canvas: an enemy does not open");

    // formations: a deploy goes to the DM, a blocked one is refused
    c.ArmFormation("party", new[] { ("pc", 0, 0), ("pg", 1, 0) });
    h.Click(2, 2);
    Check(deploys.Count == 0 && c.FormationArmed, "player pawns canvas: a deploy onto an enemy stays armed");
    h.Click(0, 0);
    Check(deploys.Count == 1 && deploys[0].Count == 2 && !c.FormationArmed && r.Pawns.Count == 3, "player pawns canvas: a deploy asks the DM and disarms");

    c.Mode = ToolMode.Measure;
    h.Drag(1, 1, 4, 1);
    Check(moves.Count == 2, "player pawns canvas: the measure tool does not move");
    c.ArmFormation("party", new[] { ("pc", 0, 0) });
    c.RemoteDocument = null;
    Check(c.ArmedPawn is null && !c.FormationArmed && c.Document == h.Doc, "player pawns canvas: leaving the session disarms");

    // outside a session the move tool moves for real
    c.Mode = ToolMode.Move;
    h.Doc.Pawns[new CellKey(1, 1)] = new Pawn { Cell = new CellKey(1, 1), TemplateId = "orc" };
    AddRoom(h.Doc, 0, 0, 5, 5);
    h.Drag(1, 1, 3, 3);
    Check(h.Doc.Pawns.ContainsKey(new CellKey(3, 3)) && moves.Count == 2, "player pawns canvas: outside a session the move tool moves for real");
}

// --- multiplayer: player pawns (two windows) ---
{
    _ = new CanvasHarness();
    string step = "construct";
    try
    {
        bool WaitFor(Func<bool> cond, int ms = 4000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (cond()) return true;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                System.Threading.Thread.Sleep(20);
            }
            return cond();
        }
        var t = typeof(DungeonMapper.Views.MainWindow);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? CallOn(object w, string name, params object?[] args) =>
            t.GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(w, args);
        object? FieldOf(object w, string name) => t.GetField(name, flags)!.GetValue(w);
        var dm = new DungeonMapper.Views.MainWindow(); dm.Show();
        var pl = new DungeonMapper.Views.MainWindow(); pl.Show();
        var dmCanvas = (MapCanvas)FieldOf(dm, "Canvas")!;
        var plCanvas = (MapCanvas)FieldOf(pl, "Canvas")!;
        var lib = (PawnLibrary)t.GetProperty("ActiveLibrary", flags)!.GetValue(dm)!;
        var pre = (CampaignPresets)t.GetProperty("ActivePresets", flags)!.GetValue(dm)!;
        lib.Pawns.Add(new PawnTemplate { Id = "pc", Name = "Ana", Affiliation = "Player", Hero = true });
        lib.Pawns.Add(new PawnTemplate { Id = "pc2", Name = "Bo", Affiliation = "Player", Hero = true });
        lib.Pawns.Add(new PawnTemplate { Id = "pg", Name = "Hireling", Affiliation = "Player" });
        lib.Pawns.Add(new PawnTemplate { Id = "orc", Name = "Orc", Affiliation = "Enemy" });
        lib.Formations.Add(new Formation { Id = "dmf", Name = "ambush", Members = { new FormationMember { TemplateId = "orc" } } });
        var dmDoc = dmCanvas.Document;
        AddRoom(dmDoc, 0, 0, 5, 5);
        dmDoc.SetRevealed(dmDoc.RevealRoomCells(new CellKey(1, 1)), true);
        dmDoc.Pawns[new CellKey(1, 1)] = new Pawn { Cell = new CellKey(1, 1), TemplateId = "pc", DmOnly = false };
        dmDoc.Pawns[new CellKey(2, 2)] = new Pawn { Cell = new CellKey(2, 2), TemplateId = "orc", DmOnly = false };
        dmDoc.Pawns[new CellKey(4, 4)] = new Pawn { Cell = new CellKey(4, 4), TemplateId = "pc2", DmOnly = true };
        ((History)t.GetProperty("ActiveHistory", flags)!.GetValue(dm)!).Commit(dmDoc);

        step = "host";
        t.GetField("_hostPort", flags)!.SetValue(dm, 0);
        Check(CallOn(dm, "StartHosting") is null, "pawns window: hosting starts");
        var host = (DungeonMapper.Net.SessionHost)FieldOf(dm, "_host")!;
        var client = Task.Run(() => DungeonMapper.Net.SessionClient.ConnectAsync("127.0.0.1", host.Port, "", "Dee")).GetAwaiter().GetResult();
        string? notice = null;
        client.NoticeReceived += why => notice = why;
        CallOn(pl, "EnterPlayerMode", client);
        Check(WaitFor(() => plCanvas.Library.Find("pg") is not null && plCanvas.RemoteDocument?.Pawns.Count == 2), "pawns window: the player gets every Player pawn");
        Check(plCanvas.Library.Formations.Count == 0, "pawns window: a DM formation with enemies is not sent");
        var pawnBtn = (Avalonia.Controls.Button)FieldOf(pl, "ToolPawn")!;
        Check(pawnBtn.IsEnabled, "pawns window: the pawn tool is on for players");

        step = "panel";
        CallOn(pl, "SetTool", ToolMode.Pawn);
        var listHost = (Avalonia.Controls.StackPanel)FieldOf(pl, "_pawnListHost")!;
        string Texts(Avalonia.Controls.Control root) => string.Join("|", Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<Avalonia.Controls.TextBlock>().Select(x => x.Text));
        var shownText = Texts(listHost);
        Check(shownText.Contains("Ana") && shownText.Contains("Hireling") && !shownText.Contains("Orc"), "pawns window: the player's pawn list shows only Player pawns");

        step = "move";
        Task.Run(() => client.SendPawnMove(new CellKey(1, 1), new CellKey(3, 1))).Wait();
        Check(WaitFor(() => dmDoc.Pawns.ContainsKey(new CellKey(3, 1))), "pawns window: a player's move lands on the DM's map");
        CallOn(dm, "DoUndo");
        Check(dmCanvas.Document.Pawns.ContainsKey(new CellKey(1, 1)), "pawns window: the DM can undo it");
        CallOn(dm, "PublishNow");

        step = "place";
        Task.Run(() => client.SendPawnPlace("pg", new CellKey(0, 3))).Wait();
        Task.Run(() => client.SendPawnPlace("pg", new CellKey(0, 4))).Wait();
        Check(WaitFor(() => dmDoc.Pawns.Values.Count(p => p.TemplateId == "pg") == 2), "pawns window: a player places two copies of a non-hero pawn");
        Task.Run(() => client.SendPawnPlace("pc2", new CellKey(1, 3))).Wait();
        Check(WaitFor(() => notice == PlayerPawns.HiddenHero) && dmDoc.Pawns.ContainsKey(new CellKey(4, 4)), "pawns window: placing a hidden hero is refused");
        notice = null;
        Task.Run(() => client.SendPawnMove(new CellKey(2, 2), new CellKey(3, 2))).Wait();
        Check(WaitFor(() => notice == "only the DM moves that pawn"), "pawns window: moving an enemy is refused with a notice");

        step = "status";
        string st = pre.PawnStatuses.First(x => x.Name != CampaignPresets.NoStatus).Name;
        Task.Run(() => client.SendPawnStatus(new CellKey(1, 1), st)).Wait();
        Check(WaitFor(() => dmDoc.Pawns[new CellKey(1, 1)].Status == st), "pawns window: a player sets their pawn's status");
        CallOn(dm, "PublishNow");
        Check(WaitFor(() => plCanvas.Document.Pawns.TryGetValue(new CellKey(1, 1), out var pp) && pp.Status == st), "pawns window: the status comes back");
        CallOn(pl, "ShowPawnInfo", plCanvas.Document.Pawns[new CellKey(1, 1)]);

        step = "formations";
        var f = new Formation { Id = Formation.NewId(), Name = "party", Members = { new FormationMember { TemplateId = "pc" }, new FormationMember { TemplateId = "pg", Dx = 1 } } };
        Task.Run(() => client.SendFormation(f)).Wait();
        Check(WaitFor(() => lib.Formations.Any(x => x.Id == f.Id && x.Name == "party")), "pawns window: a player's formation is kept in the DM's campaign");
        Check(WaitFor(() => plCanvas.Library.Formations.Any(x => x.Id == f.Id)), "pawns window: and comes back to the player");
        f.Name = "the party";
        Task.Run(() => client.SendFormation(f)).Wait();
        Check(WaitFor(() => lib.Formations.Any(x => x.Id == f.Id && x.Name == "the party")), "pawns window: a player renames it");
        Task.Run(() => client.SendFormation(new Formation { Id = "dmf", Name = "mine now", Members = { new FormationMember { TemplateId = "pc" } } })).Wait();
        Check(WaitFor(() => notice == "only the DM changes that formation") && lib.Formations.First(x => x.Id == "dmf").Name == "ambush", "pawns window: a DM formation can not be overwritten");
        Task.Run(() => client.SendFormation(new Formation { Id = "bad", Name = "x", Members = { new FormationMember { TemplateId = "orc" } } })).Wait();
        Check(WaitFor(() => notice == "formations can only hold player pawns") && !lib.Formations.Any(x => x.Id == "bad"), "pawns window: a formation with an enemy is refused");

        step = "deploy";
        var pgBefore = dmDoc.Pawns.Values.Count(p => p.TemplateId == "pg");
        Task.Run(() => client.SendDeploy(new[] { ("pc", new CellKey(2, 4)), ("pg", new CellKey(3, 4)) })).Wait();
        Check(WaitFor(() => dmDoc.Pawns.ContainsKey(new CellKey(2, 4))) && !dmDoc.Pawns.ContainsKey(new CellKey(1, 1))
            && dmDoc.Pawns[new CellKey(2, 4)].Status == st && dmDoc.Pawns.Values.Count(p => p.TemplateId == "pg") == pgBefore + 1,
            "pawns window: a deploy moves the hero with its status and adds the rest");
        CallOn(dm, "DoUndo");
        Check(dmCanvas.Document.Pawns.ContainsKey(new CellKey(1, 1)) && !dmCanvas.Document.Pawns.ContainsKey(new CellKey(2, 4)), "pawns window: a deploy is one undo step");

        step = "delete formation";
        Task.Run(() => client.SendFormationDelete(f.Id)).Wait();
        Check(WaitFor(() => !lib.Formations.Any(x => x.Id == f.Id)), "pawns window: a player deletes their formation");
        CallOn(pl, "SetTool", ToolMode.Move);
        CallOn(pl, "LeavePlayerMode", "test");
        CallOn(dm, "StopHosting");
        dm.Close(); pl.Close();
    }
    catch (Exception ex)
    {
        Check(false, $"pawns window: test failed at {step}: {(ex.InnerException ?? ex)}");
    }
}

// --- multiplayer: player labels (model) ---
{
    var d = new MapDocument();
    AddRoom(d, 0, 0, 5, 5);
    d.SetRevealed(d.RevealRoomCells(new CellKey(1, 1)), true);
    d.Labels.Add(new MapLabel { Cell = new CellKey(1, 1), Text = "dm", DmOnly = false });
    d.Labels.Add(new MapLabel { Cell = new CellKey(2, 2), Text = "open", DmOnly = false, ClientEditable = true, Id = "L1" });
    var back = MapSerializer.FromJson(MapSerializer.ToJson(d));
    var l1 = back.Labels.First(l => l.Text == "open");
    Check(l1.ClientEditable && l1.Id == "L1" && l1.CreatedBy is null, "player labels: id and client editable are saved");
    var dmL = back.Labels.First(l => l.Text == "dm");
    Check(!dmL.ClientEditable && dmL.Id == "" && !MapSerializer.ToJson(new MapDocument { }).Contains("client_editable"), "player labels: plain labels save nothing new");

    var sent = new MapLabel { Id = "P1", Cell = new CellKey(30, 30), Text = "trap!", W = 2, R = 900, G = 10, B = 10 };
    Check(PlayerLabels.Set(d, sent, "Ana") is null, "player labels: a player adds a label");
    var p1 = PlayerLabels.Find(d, "P1")!;
    Check(p1.CreatedBy == "Ana" && p1.ClientEditable && !p1.DmOnly && p1.W == 2 && p1.R == 255, "player labels: marked as the player's, visible, color clamped");
    Check(MapSerializer.FromJson(MapSerializer.ToJson(d)).Labels.Any(l => l.CreatedBy == "Ana" && l.Id == "P1"), "player labels: the creator is saved");
    var edit = new MapLabel { Id = "P1", Cell = new CellKey(3, 3), Text = "trap here", Align = LabelAlign.Left, FontSize = 20 };
    Check(PlayerLabels.Set(d, edit, "Bo") is null && p1.Text == "trap here" && p1.Cell == new CellKey(3, 3) && p1.Align == LabelAlign.Left && p1.FontSize == 20 && p1.CreatedBy == "Ana" && !p1.HasOwnColor,
        "player labels: another player edits it, the creator stays");
    var dmLabel = d.Labels.First(l => l.Text == "dm");
    dmLabel.Id = "D1";
    Check(PlayerLabels.Set(d, new MapLabel { Id = "D1", Text = "hacked" }, "Bo") == "only the DM edits that label" && dmLabel.Text == "dm", "player labels: a DM label is refused");
    Check(PlayerLabels.Delete(d, "D1") == "only the DM deletes that label", "player labels: deleting a DM label is refused");
    p1.DmOnly = true;
    Check(PlayerLabels.Set(d, edit, "Bo") is not null, "player labels: a label the DM hid can not be edited");
    p1.DmOnly = false;
    Check(PlayerLabels.Set(d, new MapLabel { Text = "x" }, "Bo") is not null, "player labels: a label without an id is refused");
    var big = new MapLabel { Id = "P2", Text = new string('a', 5000), W = 9999 };
    PlayerLabels.Set(d, big, "Bo");
    Check(PlayerLabels.Find(d, "P2")!.Text.Length == PlayerLabels.MaxText && PlayerLabels.Find(d, "P2")!.W == PlayerLabels.MaxSpan, "player labels: text and size are capped");

    // the players' copy: their own labels go anywhere, ids ride along
    p1.Cell = new CellKey(40, 40);
    var v = PlayerView.Build(d);
    Check(v.Labels.Any(l => l.Id == "P1" && l.ClientEditable) && v.Labels.Any(l => l.Id == "D1" && !l.ClientEditable), "player labels: the players' copy carries ids and the editable flag");
    dmLabel.Cell = new CellKey(40, 41);
    Check(!PlayerView.Build(d).Labels.Any(l => l.Id == "D1"), "player labels: a DM label off the revealed floor still stays back");
    var vw = MapSerializer.PackageFromWire(MapSerializer.PackageToWire(new PlayerPackage { Map = v }));
    Check(vw.Map.Labels.Any(l => l.Id == "P1" && l.ClientEditable && l.CreatedBy == "Ana"), "player labels: the flag crosses the wire");
    Check(PlayerLabels.Delete(d, "P1") is null && PlayerLabels.Find(d, "P1") is null, "player labels: a player deletes one");

    // copy keeps ids unique, a move keeps the id
    var m = new MapDocument();
    AddRoom(m, 0, 0, 3, 3);
    m.Labels.Add(new MapLabel { Cell = new CellKey(1, 1), Text = "a", Id = "X", ClientEditable = true });
    var snap = m.CaptureRegion(0, 0, 3, 3);
    m.PasteRegion(snap, 10, 10);
    Check(m.Labels.Count == 2 && m.Labels.Select(l => l.Id).Distinct().Count() == 2 && m.Labels.All(l => l.ClientEditable), "player labels: a pasted copy gets its own id");
    var m2 = new MapDocument();
    m2.PasteRegion(snap, 5, 5);
    Check(m2.Labels.Single().Id == "X", "player labels: a moved label keeps its id");
}

// --- multiplayer: player labels (canvas) ---
{
    using var h = new CanvasHarness();
    var c = h.Canvas;
    var r = new MapDocument();
    AddRoom(r, 0, 0, 5, 5);
    r.SetRevealed(r.FloorCells(), true);
    r.Labels.Add(new MapLabel { Cell = new CellKey(1, 1), Text = "dm", DmOnly = false, Id = "D" });
    r.Labels.Add(new MapLabel { Cell = new CellKey(3, 3), Text = "ours", DmOnly = false, Id = "P", ClientEditable = true });
    c.RemoteDocument = r;
    c.Mode = ToolMode.Label;
    var changed = new List<MapLabel>();
    var opened = new List<MapLabel>();
    c.RemoteLabelChanged += l => changed.Add(l);
    c.LabelConfigRequested += l => opened.Add(l);
    int commits = h.CommitCount;
    h.Click(1, 1);
    Check(opened.Count == 0 && h.LastStatus == "only the DM edits that label", "player labels canvas: a DM label does not open");
    h.Click(3, 3);
    Check(opened.Count == 1 && opened[0].Id == "P", "player labels canvas: a players' label opens");
    h.Drag(0, 4, 2, 4);
    var made = c.Document.Labels.LastOrDefault();
    Check(changed.Count == 1 && made is not null && made.W == 3 && made.ClientEditable && made.Id.Length > 0 && !made.DmOnly, "player labels canvas: a drag adds a label and sends it");
    Check(h.CommitCount == commits && h.Doc.Labels.Count == 0, "player labels canvas: the own map is untouched");
    c.Mode = ToolMode.Room;
    h.Drag(0, 0, 2, 2);
    Check(c.Document.Cells.Count == r.Cells.Count, "player labels canvas: other tools still do nothing");
    c.RemoteDocument = null;
}

// --- multiplayer: player labels (two windows) ---
{
    _ = new CanvasHarness();
    string step = "construct";
    try
    {
        bool WaitFor(Func<bool> cond, int ms = 4000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (cond()) return true;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                System.Threading.Thread.Sleep(20);
            }
            return cond();
        }
        var t = typeof(DungeonMapper.Views.MainWindow);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object? CallOn(object w, string name, params object?[] args) =>
            t.GetMethods(flags).First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(w, args);
        object? FieldOf(object w, string name) => t.GetField(name, flags)!.GetValue(w);
        var dm = new DungeonMapper.Views.MainWindow(); dm.Show();
        var pl = new DungeonMapper.Views.MainWindow(); pl.Show();
        var dmCanvas = (MapCanvas)FieldOf(dm, "Canvas")!;
        var plCanvas = (MapCanvas)FieldOf(pl, "Canvas")!;
        var dmDoc = dmCanvas.Document;
        AddRoom(dmDoc, 0, 0, 5, 5);
        dmDoc.SetRevealed(dmDoc.FloorCells(), true);
        dmDoc.Labels.Add(new MapLabel { Cell = new CellKey(1, 1), Text = "dm label", DmOnly = false });
        ((History)t.GetProperty("ActiveHistory", flags)!.GetValue(dm)!).Commit(dmDoc);

        step = "host";
        t.GetField("_hostPort", flags)!.SetValue(dm, 0);
        Check(CallOn(dm, "StartHosting") is null, "labels window: hosting starts");
        var host = (DungeonMapper.Net.SessionHost)FieldOf(dm, "_host")!;
        var client = Task.Run(() => DungeonMapper.Net.SessionClient.ConnectAsync("127.0.0.1", host.Port, "", "Dee")).GetAwaiter().GetResult();
        CallOn(pl, "EnterPlayerMode", client);
        Check(WaitFor(() => plCanvas.RemoteDocument?.Labels.Count == 1), "labels window: the player sees the DM's map");
        var labelBtn = (Avalonia.Controls.Button)FieldOf(pl, "ToolLabel")!;
        Check(labelBtn.IsEnabled, "labels window: the label tool is on for players");

        step = "player adds";
        CallOn(pl, "SetTool", ToolMode.Label);
        var mine = new MapLabel { Cell = new CellKey(4, 4), Text = "loot", Id = MapLabel.NewId(), ClientEditable = true, DmOnly = false };
        plCanvas.Document.Labels.Add(mine);
        CallOn(pl, "SendLabelToHost", mine);
        Check(WaitFor(() => dmDoc.Labels.Any(l => l.Text == "loot" && l.CreatedBy == "Dee")), "labels window: a player's label lands on the DM's map, marked with the player");
        CallOn(dm, "PublishNow");
        Check(WaitFor(() => plCanvas.Document.Labels.Count(l => l.Id == mine.Id) == 1), "labels window: it comes back once");

        step = "player edits in the view";
        var shownMine = plCanvas.Document.Labels.First(l => l.Id == mine.Id);
        CallOn(pl, "ShowLabelConfig", shownMine);
        shownMine.Text = "loot!!";
        CallOn(pl, "CommitLabel", shownMine);
        Check(WaitFor(() => dmDoc.Labels.Any(l => l.Text == "loot!!")), "labels window: an edit in the label view reaches the DM");
        CallOn(dm, "PublishNow");
        Check(WaitFor(() => plCanvas.Document.Labels.Any(l => l.Text == "loot!!")) && plCanvas.Document.Labels.Contains(shownMine), "labels window: the open label keeps its object over a DM update");

        step = "DM takes it back";
        var onDm = dmDoc.Labels.First(l => l.Text == "loot!!");
        onDm.ClientEditable = false;
        CallOn(dm, "PublishNow");
        Check(WaitFor(() => plCanvas.Document.Labels.Any(l => l.Text == "loot!!" && !l.ClientEditable)) && plCanvas.SelectedLabel is null, "labels window: a label the DM takes back closes for the player");
        onDm.ClientEditable = true;
        CallOn(dm, "PublishNow");
        Check(WaitFor(() => plCanvas.Document.Labels.Any(l => l.Text == "loot!!" && l.ClientEditable)), "labels window: and opens up again");

        step = "refused";
        var dmL = plCanvas.Document.Labels.First(l => l.Text == "dm label");
        dmL.Text = "hacked"; dmL.Id = "fake"; dmL.ClientEditable = true;
        CallOn(pl, "SendLabelToHost", dmL);
        Check(WaitFor(() => dmDoc.Labels.Count(l => l.Text == "hacked") == 1) && dmDoc.Labels.Any(l => l.Text == "dm label"),
            "labels window: a forged id only adds a new player label, the DM's label stays");

        step = "delete and undo";
        var status = (Avalonia.Controls.TextBlock)FieldOf(dm, "StatusText")!;
        Task.Run(() => client.SendLabelDelete(mine.Id)).Wait();
        Check(WaitFor(() => !dmDoc.Labels.Any(l => l.Text == "loot!!")), "labels window: a player deletes a label on the DM's map");
        Check(status.Text.Contains("Dee deleted a label"), "labels window: the DM's status bar says who");
        CallOn(dm, "DoUndo");
        Check(dmCanvas.Document.Labels.Any(l => l.Text == "loot!!"), "labels window: the DM can undo it");

        step = "DM view";
        CallOn(dm, "SetTool", ToolMode.Label);
        CallOn(dm, "ShowLabelConfig", dmCanvas.Document.Labels.First(l => l.Text == "dm label"));
        CallOn(pl, "LeavePlayerMode", "test");
        CallOn(dm, "StopHosting");
        dm.Close(); pl.Close();
    }
    catch (Exception ex)
    {
        Check(false, $"labels window: test failed at {step}: {(ex.InnerException ?? ex)}");
    }
}

// --- multiplayer: players' light ---
{
    using var h = new CanvasHarness();
    var c = h.Canvas;
    AddRoom(h.Doc, 0, 0, 14, 8);
    h.Doc.SetRevealed(h.Doc.FloorCells(), true);
    c.Mode = ToolMode.Fog;
    c.ResetFog();
    c.FogFeet = 20;
    h.MoveTo(3, 3);
    Check(h.Doc.PlayerLight is null, "light: following the cursor lights nothing for players");
    h.Click(3, 3);
    Check(h.Doc.PlayerLight == (3.5, 3.5, 4), "light: anchoring the fog lights that circle for players");
    c.FogFeet = 30;
    Check(h.Doc.PlayerLight == (3.5, 3.5, 6), "light: a radius change while anchored follows");
    c.Mode = ToolMode.Select; c.ResetFog();
    Check(h.Doc.PlayerLight == (3.5, 3.5, 6), "light: it stays when the tool changes");
    c.Mode = ToolMode.Fog;
    c.FogFeet = 60;
    Check(h.Doc.PlayerLight == (3.5, 3.5, 6), "light: an unanchored radius change does not move it");
    Check(!MapSerializer.ToJson(h.Doc).Contains("light"), "light: never saved");
    var hist = new History(); hist.Initialize(h.Doc);
    h.Doc.ApplyFrom(MapSerializer.FromJson(MapSerializer.ToJson(h.Doc)));
    Check(h.Doc.PlayerLight == (3.5, 3.5, 6), "light: undo leaves it alone");

    var v = PlayerView.Build(h.Doc);
    Check(v.PlayerLight == (3.5, 3.5, 6), "light: the players' copy carries it");
    var back = MapSerializer.PackageFromWire(MapSerializer.PackageToWire(new PlayerPackage { Map = v }));
    Check(back.Map.PlayerLight == (3.5, 3.5, 6), "light: it crosses the wire");
    Check(MapSerializer.PackageFromWire(MapSerializer.PackageToWire(new PlayerPackage { Map = new MapDocument() })).Map.PlayerLight is null, "light: none crosses as none");

    // drawn on the player's side: outside the circle is dimmer
    c.Mode = ToolMode.Measure;
    c.RemoteDocument = v;
    int Lum((byte r, byte g, byte b, byte a) p) => p.r + p.g + p.b;
    var bmp = h.Render();
    var inside = h.PixelAt(bmp, (int)h.ScreenOf(4, 3).X + 8, (int)h.ScreenOf(4, 3).Y + 8);
    var outside = h.PixelAt(bmp, (int)h.ScreenOf(13, 3).X + 8, (int)h.ScreenOf(13, 3).Y + 8);
    Check(Lum(inside) > Lum(outside), "light: unlit revealed floor is dimmer for players");
    c.RemoteDocument = null;
    c.Mode = ToolMode.Select;
    bmp = h.Render();
    outside = h.PixelAt(bmp, (int)h.ScreenOf(13, 3).X + 8, (int)h.ScreenOf(13, 3).Y + 8);
    Check(Lum(inside) == Lum(outside), "light: the DM's own view is not dimmed");
    c.ClearPlayerLight();
    Check(h.Doc.PlayerLight is null, "light: clear removes it");
}

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
