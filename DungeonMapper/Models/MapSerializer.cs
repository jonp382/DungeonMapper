using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DungeonMapper.Models;

/// Wire format for MapDocument. Keys are "x,y" strings so the JSON stays
/// lightweight and hand-editable when shared between players.
/// Version field allows future format changes (e.g. multi-map stairs).

public sealed class MapFileDto
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("cells")] public List<string> Cells { get; set; } = new();
    [JsonPropertyName("walls")] public List<string> Walls { get; set; } = new();
    [JsonPropertyName("doors")] public List<DoorDto> Doors { get; set; } = new();
    [JsonPropertyName("stairs")] public List<StairDto> Stairs { get; set; } = new();
    [JsonPropertyName("labels")] public List<LabelDto> Labels { get; set; } = new();
    [JsonPropertyName("pawns")] public List<PawnDto> Pawns { get; set; } = new();
    [JsonPropertyName("angled_walls")] public List<string> AngledWalls { get; set; } = new();
    [JsonPropertyName("angled_doors")] public List<AngledDoorDto> AngledDoors { get; set; } = new();
    [JsonPropertyName("polygons")] public List<PolygonDto> Polygons { get; set; } = new();
    [JsonPropertyName("flood_regions")] public List<FloodRegionDto> FloodRegions { get; set; } = new();
    [JsonPropertyName("vertex_doors")] public List<VertexDoorDto> VertexDoors { get; set; } = new();
}

public sealed class PolygonDto
{
    // closed vertex loop of LATTICE points "x,y" (cell corners); the exact
    // polygon floor shape. v1 files have no polygons field.
    [JsonPropertyName("verts")] public List<string> Verts { get; set; } = new();
}

public sealed class FloodRegionDto
{
    // closed rings of world points "x,y" (cell corners and diagonal cut
    // points): ring 0 outer, rings 1+ holes. The exact flood floor geometry.
    // v2 files have no flood_regions field.
    [JsonPropertyName("rings")] public List<List<string>> Rings { get; set; } = new();
}

public sealed class AngledDoorDto
{
    [JsonPropertyName("wall")] public string Wall { get; set; } = "";
    [JsonPropertyName("t")] public double T { get; set; } = 0.5;
    [JsonPropertyName("type")] public string Type { get; set; } = "standard";
    [JsonPropertyName("state")] public string State { get; set; } = "closed";
}

public sealed class VertexDoorDto
{
    // lattice vertex "x,y" (cell corner) + orientation; the door is centered on
    // the vertex, straddling the two collinear unit edges (2.5ft each side).
    [JsonPropertyName("vertex")] public string Vertex { get; set; } = "";
    [JsonPropertyName("orientation")] public string Orientation { get; set; } = "horizontal";
    [JsonPropertyName("type")] public string Type { get; set; } = "standard";
    [JsonPropertyName("state")] public string State { get; set; } = "closed";
}

public sealed class DoorDto
{
    [JsonPropertyName("edge")] public string Edge { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "standard";
    [JsonPropertyName("state")] public string State { get; set; } = "closed";
}

public sealed class StairDto
{
    [JsonPropertyName("cell")] public string Cell { get; set; } = "";
    [JsonPropertyName("direction")] public string Direction { get; set; } = "down";
    [JsonPropertyName("targetMap")] public string? TargetMap { get; set; }
}

public sealed class LabelDto
{
    [JsonPropertyName("cell")] public string Cell { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    // per-label overrides; 0/-1 = inherit (auto font size / settings label color).
    [JsonPropertyName("font_size")] public int FontSize { get; set; } = 0;
    [JsonPropertyName("r")] public int R { get; set; } = -1;
    [JsonPropertyName("g")] public int G { get; set; } = -1;
    [JsonPropertyName("b")] public int B { get; set; } = -1;
}

public sealed class PawnTemplateDto
{
    // a library template embedded in a project file (or in a library export
    // file). image_b64 carries the pawn image bytes so shared files are
    // complete; image_ext is the original extension (".png"/".jpg").
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("affiliation")] public string Affiliation { get; set; } = "Neutral";
    [JsonPropertyName("color_override")] public bool ColorOverride { get; set; } = false;
    [JsonPropertyName("inner_r")] public int InnerR { get; set; }
    [JsonPropertyName("inner_g")] public int InnerG { get; set; }
    [JsonPropertyName("inner_b")] public int InnerB { get; set; }
    [JsonPropertyName("outer_r")] public int OuterR { get; set; }
    [JsonPropertyName("outer_g")] public int OuterG { get; set; }
    [JsonPropertyName("outer_b")] public int OuterB { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "none";
    [JsonPropertyName("image_b64")] public string ImageB64 { get; set; } = "";
    [JsonPropertyName("image_ext")] public string ImageExt { get; set; } = "";
}

public sealed class PawnDto
{
    [JsonPropertyName("cell")] public string Cell { get; set; } = "";
    [JsonPropertyName("template_id")] public string TemplateId { get; set; } = "";
    // legacy fields (pre-hard-reference map files); ignored on load
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("class")] public string Class { get; set; } = "";
    [JsonPropertyName("inner")] public string Inner { get; set; } = "";
    [JsonPropertyName("outer")] public string Outer { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
}

/// Wire format for a multi-map project: a list of named maps plus the active index.
public sealed class ProjectFileDto
{
    [JsonPropertyName("version")] public int Version { get; set; } = 3;
    [JsonPropertyName("active")] public int Active { get; set; } = 0;
    [JsonPropertyName("maps")] public List<NamedMapDto> Maps { get; set; } = new();
    // the pawn library travels with the project file (v3). older project files
    // have no field and load with an empty embedded library.
    [JsonPropertyName("pawn_library")] public List<PawnTemplateDto> PawnLibrary { get; set; } = new();
}

public sealed class NamedMapDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "Map 1";
    [JsonPropertyName("map")] public MapFileDto Map { get; set; } = new();
}

public static class MapSerializer
{
    // JSON spec versions. Tracked inside every saved file and here in the app.
    // Increment ONLY when the wire format changes in a way old maps cannot load
    // as-is; app-only additive changes with backwards-compatible defaults do NOT
    // increment (the map porter app ports old files to the latest spec).
    // v2 adds the "polygons" field (polygon rooms stored as vertex loops).
    public const int MapSpecVersion = 3;
    // v3 adds the "pawn_library" field: the project's pawn library travels
    // inside the project file. v2 files load with an empty embedded library.
    public const int ProjectSpecVersion = 3;

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
    };

    // "x,y" <-> CellKey
    private static string Key(CellKey c) => $"{c.X},{c.Y}";
    private static string Key(EdgeKey e) => $"{e.Cell.X},{e.Cell.Y},{e.Side.ToString().ToLowerInvariant()}";

    // "ax,ay,bx,by" <-> AngledWall
    private static string Key(AngledWall w) => $"{w.Ax},{w.Ay},{w.Bx},{w.By}";
    private static bool TryParseAngled(string s, out AngledWall w)
    {
        w = default;
        var parts = s.Split(',');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[0], out int ax) || !int.TryParse(parts[1], out int ay)
            || !int.TryParse(parts[2], out int bx) || !int.TryParse(parts[3], out int by)) return false;
        w = new AngledWall(ax, ay, bx, by);
        return true;
    }

    // shared loader for angled walls + doors. Walls keep their stored orientation;
    // a door matches its wall in either endpoint order, and doors whose wall is
    // absent are dropped (door implies wall).
    private static void AddAngled(MapFileDto dto, MapDocument doc)
    {
        foreach (var s in dto.AngledWalls)
            if (TryParseAngled(s, out var w)) doc.AngledWalls.Add(w);
        foreach (var d in dto.AngledDoors)
        {
            if (!TryParseAngled(d.Wall, out var w)) continue;
            var wall = doc.AngledWalls.FirstOrDefault(x =>
                (x.Ax == w.Ax && x.Ay == w.Ay && x.Bx == w.Bx && x.By == w.By) ||
                (x.Ax == w.Bx && x.Ay == w.By && x.Bx == w.Ax && x.By == w.Ay));
            if (wall == default) continue; // door implies wall
            var list = doc.AngledDoors.TryGetValue(wall, out var l) ? l : (doc.AngledDoors[wall] = new List<AngledDoor>());
            var door = new AngledDoor { T = d.T, Type = string.IsNullOrWhiteSpace(d.Type) ? "standard" : d.Type };
            if (Enum.TryParse<DoorState>(d.State, ignoreCase: true, out var st)) door.State = st;
            list.Add(door);
        }
    }

    // shared loader for polygon rooms. v1 files have no polygons field (the
    // list stays empty), so an old file loads with no polygons; the map porter
    // app ports v1 files to the v2 shape.
    private static void AddPolygons(MapFileDto dto, MapDocument doc)
    {
        foreach (var d in dto.Polygons)
        {
            var loop = new List<(int x, int y)>();
            foreach (var s in d.Verts)
                if (TryParseCell(s, out var c)) loop.Add((c.X, c.Y));
            if (loop.Count >= 3) doc.Polygons.Add(new PolygonRoom { Verts = loop });
        }
    }

    // shared loader for flood region floors. v2 files have no flood_regions
    // field, so they load with none. Rings are world points (doubles, diagonal
    // cut points included); rings with fewer than 3 points are dropped.
    private static void AddFloodRegions(MapFileDto dto, MapDocument doc)
    {
        foreach (var d in dto.FloodRegions)
        {
            var rings = new List<List<(double x, double y)>>();
            foreach (var ring in d.Rings)
            {
                var r = new List<(double, double)>();
                foreach (var s in ring)
                {
                    var parts = s.Split(',');
                    if (parts.Length != 2) continue;
                    if (!double.TryParse(parts[0], out double vx) || !double.TryParse(parts[1], out double vy)) continue;
                    r.Add((vx, vy));
                }
                if (r.Count >= 3) rings.Add(r);
            }
            if (rings.Count >= 1) doc.FloodRegions.Add(new FloodRegion { Rings = rings });
        }
    }

    // shared loader for vertex doors. Door implies walls: a vertex door whose
    // straddled walls are missing is dropped, same rule as edge doors. Old
    // files have no vertex_doors field, so they load with none.
    private static void AddVertexDoors(MapFileDto dto, MapDocument doc)
    {
        foreach (var d in dto.VertexDoors)
        {
            if (!TryParseCell(d.Vertex, out var c)) continue;
            bool horizontal = !string.Equals(d.Orientation, "vertical", StringComparison.OrdinalIgnoreCase);
            var key = new VertexDoorKey(c.X, c.Y, horizontal);
            var straddles = MapDocument.VertexStraddles(key);
            if (!doc.HasWall(straddles[0]) || !doc.HasWall(straddles[1])) continue; // door implies walls
            var door = new VertexDoor { Type = string.IsNullOrWhiteSpace(d.Type) ? "standard" : d.Type };
            if (Enum.TryParse<DoorState>(d.State, ignoreCase: true, out var st)) door.State = st;
            doc.VertexDoors[key] = door;
        }
    }

    private static bool TryParseCell(string s, out CellKey cell)
    {
        cell = default;
        var parts = s.Split(',');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)) return false;
        cell = new CellKey(x, y);
        return true;
    }

    private static bool TryParseEdge(string s, out EdgeKey edge)
    {
        edge = default;
        var parts = s.Split(',');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)) return false;
        if (!Enum.TryParse<Side>(parts[2], ignoreCase: true, out var side)) return false;
        edge = EdgeKey.Normalize(new CellKey(x, y), side);
        return true;
    }

    // "r,g,b" <-> (r,g,b)
    private static string Color(int r, int g, int b) => $"{r},{g},{b}";
    private static bool TryParseColor(string s, out (int r, int g, int b) c)
    {
        c = default;
        var parts = s.Split(',');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out int r) || !int.TryParse(parts[1], out int g) || !int.TryParse(parts[2], out int b)) return false;
        c = (r, g, b);
        return true;
    }

    public static string ToJson(MapDocument doc)
    {
        var dto = new MapFileDto { Version = MapSpecVersion };
        foreach (var c in doc.Cells) dto.Cells.Add(Key(c));
        foreach (var (e, w) in doc.Walls) if (w) dto.Walls.Add(Key(e));
        foreach (var (e, d) in doc.Doors)
            dto.Doors.Add(new DoorDto { Edge = Key(e), Type = d.Type, State = d.State.ToString().ToLowerInvariant() });
        foreach (var (c, s) in doc.Stairs)
            dto.Stairs.Add(new StairDto { Cell = Key(c), Direction = s.Direction.ToString().ToLowerInvariant(), TargetMap = s.TargetMap });
        foreach (var l in doc.Labels)
            dto.Labels.Add(new LabelDto { Cell = Key(l.Cell), Text = l.Text, FontSize = l.FontSize, R = l.R, G = l.G, B = l.B });
        foreach (var (c, p) in doc.Pawns)
            dto.Pawns.Add(PawnToDto(c, p));
        foreach (var w in doc.AngledWalls)
        {
            dto.AngledWalls.Add(Key(w));
            foreach (var d in doc.GetAngledDoors(w))
                dto.AngledDoors.Add(new AngledDoorDto { Wall = Key(w), T = d.T, Type = d.Type, State = d.State.ToString().ToLowerInvariant() });
        }
        foreach (var p in doc.Polygons)
        {
            var pd = new PolygonDto();
            foreach (var (vx, vy) in p.Verts) pd.Verts.Add($"{vx},{vy}");
            dto.Polygons.Add(pd);
        }
        foreach (var fr in doc.FloodRegions)
        {
            var fd = new FloodRegionDto();
            foreach (var ring in fr.Rings)
            {
                var r = new List<string>();
                foreach (var (vx, vy) in ring) r.Add($"{vx},{vy}");
                fd.Rings.Add(r);
            }
            dto.FloodRegions.Add(fd);
        }
        foreach (var (k, d) in doc.VertexDoors)
            dto.VertexDoors.Add(new VertexDoorDto { Vertex = $"{k.X},{k.Y}", Orientation = k.Horizontal ? "horizontal" : "vertical", Type = d.Type, State = d.State.ToString().ToLowerInvariant() });
        return JsonSerializer.Serialize(dto, Opts);
    }

    private static PawnDto PawnToDto(CellKey c, Pawn p) => new PawnDto
    {
        Cell = Key(c),
        TemplateId = p.TemplateId,
        Status = p.Status ?? "",
    };

    private static void AddPawns(MapFileDto dto, MapDocument doc)
    {
        foreach (var d in dto.Pawns)
        {
            if (!TryParseCell(d.Cell, out var c)) continue;
            doc.Pawns[c] = new Pawn { Cell = c, TemplateId = d.TemplateId, Status = string.IsNullOrEmpty(d.Status) ? null : d.Status };
        }
    }

    public static MapDocument FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<MapFileDto>(json)
            ?? throw new InvalidDataException("map file is empty or not a map object");
        var doc = new MapDocument();
        foreach (var s in dto.Cells)
            if (TryParseCell(s, out var c)) doc.Cells.Add(c);
        foreach (var s in dto.Walls)
            if (TryParseEdge(s, out var e)) doc.SetWall(e, true);
        foreach (var d in dto.Doors)
        {
            if (!TryParseEdge(d.Edge, out var e)) continue;
            if (!doc.HasWall(e)) continue; // door implies wall; drop invalid doors
            var door = new Door { Type = string.IsNullOrWhiteSpace(d.Type) ? "standard" : d.Type };
            door.State = Enum.TryParse<DoorState>(d.State, ignoreCase: true, out var st) ? st : DoorState.Closed;
            doc.Doors[EdgeKey.Normalize(e.Cell, e.Side)] = door;
        }
        foreach (var s in dto.Stairs)
        {
            if (!TryParseCell(s.Cell, out var c)) continue;
            var stair = new Stair
            {
                Direction = Enum.TryParse<StairDirection>(s.Direction, ignoreCase: true, out var dir) ? dir : StairDirection.Down,
                TargetMap = s.TargetMap,
            };
            doc.Stairs[c] = stair;
        }
        foreach (var l in dto.Labels)
        {
            if (!TryParseCell(l.Cell, out var c)) continue;
            doc.Labels.Add(new MapLabel { Cell = c, Text = l.Text, FontSize = l.FontSize, R = l.R, G = l.G, B = l.B });
        }
        AddPawns(dto, doc);
        AddAngled(dto, doc);
        AddPolygons(dto, doc);
        AddFloodRegions(dto, doc);
        AddVertexDoors(dto, doc);
        return doc;
    }

    public static void Save(MapDocument doc, string path) => File.WriteAllText(path, ToJson(doc));

    public static MapDocument Load(string path) => FromJson(File.ReadAllText(path));

    public static string ProjectToJson(MapProject project, PawnLibrary? library = null)
    {
        var dto = new ProjectFileDto { Version = ProjectSpecVersion, Active = project.ActiveIndex };
        foreach (var m in project.Maps)
            dto.Maps.Add(new NamedMapDto { Name = m.Name, Map = ToDto(m.Doc) });
        if (library is not null) dto.PawnLibrary = LibraryToDtos(library);
        return JsonSerializer.Serialize(dto, Opts);
    }

    /// Embedded library DTOs of a project file (empty for v2 files). The caller
    /// merges them into the live library and remaps the loaded pawns' ids.
    public static List<PawnTemplateDto> ExtractLibrary(string json)
    {
        var dto = JsonSerializer.Deserialize<ProjectFileDto>(json);
        return dto?.PawnLibrary ?? new List<PawnTemplateDto>();
    }

    public static MapProject ProjectFromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<ProjectFileDto>(json)
            ?? throw new InvalidDataException("project file is empty or not a project object");
        var project = new MapProject();
        project.Maps.Clear();
        foreach (var m in dto.Maps)
            project.Maps.Add(new MapEntry { Name = string.IsNullOrWhiteSpace(m.Name) ? "Map" : m.Name, Doc = FromDto(m.Map) });
        if (project.Maps.Count == 0) project.Maps.Add(new MapEntry { Name = "Map 1" });
        project.ActiveIndex = (dto.Active >= 0 && dto.Active < project.Maps.Count) ? dto.Active : 0;
        return project;
    }

    // ---- pawn library wire format (shared by project embedding and import/export files) ----

    public static List<PawnTemplateDto> LibraryToDtos(PawnLibrary lib)
    {
        var list = new List<PawnTemplateDto>();
        foreach (var t in lib.Pawns)
            list.Add(new PawnTemplateDto
            {
                Id = t.Id, Name = t.Name, Affiliation = t.Affiliation,
                ColorOverride = t.ColorOverride,
                InnerR = t.InnerR, InnerG = t.InnerG, InnerB = t.InnerB,
                OuterR = t.OuterR, OuterG = t.OuterG, OuterB = t.OuterB,
                Status = t.Status,
                ImageB64 = t.ImageB64, ImageExt = t.ImageExt,
            });
        return list;
    }

    public static List<PawnTemplate> LibraryFromDtos(List<PawnTemplateDto> dtos)
    {
        var list = new List<PawnTemplate>();
        foreach (var d in dtos)
        {
            if (string.IsNullOrEmpty(d.Id) && string.IsNullOrEmpty(d.Name)) continue;
            list.Add(new PawnTemplate
            {
                Id = d.Id, Name = d.Name, Affiliation = d.Affiliation,
                ColorOverride = d.ColorOverride,
                InnerR = d.InnerR, InnerG = d.InnerG, InnerB = d.InnerB,
                OuterR = d.OuterR, OuterG = d.OuterG, OuterB = d.OuterB,
                Status = string.IsNullOrEmpty(d.Status) ? "none" : d.Status,
                ImageB64 = d.ImageB64, ImageExt = d.ImageExt,
            });
        }
        return list;
    }

    public static string LibraryToJson(PawnLibrary lib)
        => JsonSerializer.Serialize(new { pawn_library = LibraryToDtos(lib) }, Opts);

    public static List<PawnTemplateDto> LibraryFromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<ProjectFileDto>(json);
        if (dto is not null && dto.PawnLibrary.Count > 0) return dto.PawnLibrary;
        var w = JsonSerializer.Deserialize<LibraryWire>(json);
        return w?.pawn_library ?? new List<PawnTemplateDto>();
    }

    private sealed class LibraryWire
    {
        [JsonPropertyName("pawn_library")] public List<PawnTemplateDto> pawn_library { get; set; } = new();
    }

    public static void SaveProject(MapProject project, string path, PawnLibrary? library = null)
        => File.WriteAllText(path, ProjectToJson(project, library));

    public static MapProject LoadProject(string path) => ProjectFromJson(File.ReadAllText(path));

    // build a MapFileDto from a document (shared by ToJson and ProjectToJson)
    private static MapFileDto ToDto(MapDocument doc)
    {
        var dto = new MapFileDto { Version = MapSpecVersion };
        foreach (var c in doc.Cells) dto.Cells.Add(Key(c));
        foreach (var (e, w) in doc.Walls) if (w) dto.Walls.Add(Key(e));
        foreach (var (e, d) in doc.Doors)
            dto.Doors.Add(new DoorDto { Edge = Key(e), Type = d.Type, State = d.State.ToString().ToLowerInvariant() });
        foreach (var (c, s) in doc.Stairs)
            dto.Stairs.Add(new StairDto { Cell = Key(c), Direction = s.Direction.ToString().ToLowerInvariant(), TargetMap = s.TargetMap });
        foreach (var l in doc.Labels)
            dto.Labels.Add(new LabelDto { Cell = Key(l.Cell), Text = l.Text, FontSize = l.FontSize, R = l.R, G = l.G, B = l.B });
        foreach (var (c, p) in doc.Pawns)
            dto.Pawns.Add(PawnToDto(c, p));
        foreach (var w in doc.AngledWalls)
        {
            dto.AngledWalls.Add(Key(w));
            foreach (var d in doc.GetAngledDoors(w))
                dto.AngledDoors.Add(new AngledDoorDto { Wall = Key(w), T = d.T, Type = d.Type, State = d.State.ToString().ToLowerInvariant() });
        }
        foreach (var p in doc.Polygons)
        {
            var pd = new PolygonDto();
            foreach (var (vx, vy) in p.Verts) pd.Verts.Add($"{vx},{vy}");
            dto.Polygons.Add(pd);
        }
        foreach (var fr in doc.FloodRegions)
        {
            var fd = new FloodRegionDto();
            foreach (var ring in fr.Rings)
            {
                var r = new List<string>();
                foreach (var (vx, vy) in ring) r.Add($"{vx},{vy}");
                fd.Rings.Add(r);
            }
            dto.FloodRegions.Add(fd);
        }
        foreach (var (k, d) in doc.VertexDoors)
            dto.VertexDoors.Add(new VertexDoorDto { Vertex = $"{k.X},{k.Y}", Orientation = k.Horizontal ? "horizontal" : "vertical", Type = d.Type, State = d.State.ToString().ToLowerInvariant() });
        return dto;
    }

    private static MapDocument FromDto(MapFileDto dto)
    {
        var doc = new MapDocument();
        foreach (var s in dto.Cells)
            if (TryParseCell(s, out var c)) doc.Cells.Add(c);
        foreach (var s in dto.Walls)
            if (TryParseEdge(s, out var e)) doc.SetWall(e, true);
        foreach (var d in dto.Doors)
        {
            if (!TryParseEdge(d.Edge, out var e)) continue;
            if (!doc.HasWall(e)) continue;
            var door = new Door { Type = string.IsNullOrWhiteSpace(d.Type) ? "standard" : d.Type };
            door.State = Enum.TryParse<DoorState>(d.State, ignoreCase: true, out var st) ? st : DoorState.Closed;
            doc.Doors[EdgeKey.Normalize(e.Cell, e.Side)] = door;
        }
        foreach (var s in dto.Stairs)
        {
            if (!TryParseCell(s.Cell, out var c)) continue;
            var stair = new Stair
            {
                Direction = Enum.TryParse<StairDirection>(s.Direction, ignoreCase: true, out var dir) ? dir : StairDirection.Down,
                TargetMap = s.TargetMap,
            };
            doc.Stairs[c] = stair;
        }
        foreach (var l in dto.Labels)
        {
            if (!TryParseCell(l.Cell, out var c)) continue;
            doc.Labels.Add(new MapLabel { Cell = c, Text = l.Text, FontSize = l.FontSize, R = l.R, G = l.G, B = l.B });
        }
        AddPawns(dto, doc);
        AddAngled(dto, doc);
        AddPolygons(dto, doc);
        AddFloodRegions(dto, doc);
        AddVertexDoors(dto, doc);
        return doc;
    }
}
