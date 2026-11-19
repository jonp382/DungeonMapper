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
    // v3 only: polygons became plain walls plus a flood region in v4, so this
    // is read for old files and never written
    [JsonPropertyName("polygons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PolygonDto>? Polygons { get; set; }
    [JsonPropertyName("flood_regions")] public List<FloodRegionDto> FloodRegions { get; set; } = new();
    [JsonPropertyName("vertex_doors")] public List<VertexDoorDto> VertexDoors { get; set; } = new();
    // placed objects, in placement order (Models/MapObjects.cs). older files
    // have none, which loads as no objects
    [JsonPropertyName("objects")] public List<ObjectDto> Objects { get; set; } = new();
}

public sealed class ObjectDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type_id")] public string TypeId { get; set; } = "";
    // "x,y,w,h" in cells
    [JsonPropertyName("rect")] public string Rect { get; set; } = "";
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
    // the object's own look, absent when it uses its type's
    [JsonPropertyName("look")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObjectLook? Look { get; set; }
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
    // range in cells from the top-left cell, and text alignment across it
    [JsonPropertyName("w")] public int W { get; set; } = 1;
    [JsonPropertyName("h")] public int H { get; set; } = 1;
    [JsonPropertyName("align")] public string Align { get; set; } = "center";
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
    [JsonPropertyName("favorite")] public bool Favorite { get; set; }
    [JsonPropertyName("hero")] public bool Hero { get; set; }
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

/// Wire format for a campaign (full save): a list of named maps plus the
/// active index and the pawn library. "kind" marks it as a campaign; files
/// written before the field existed are told apart by their "maps" list.
public sealed class ProjectFileDto
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = MapSerializer.KindCampaign;
    [JsonPropertyName("version")] public int Version { get; set; } = 3;
    [JsonPropertyName("active")] public int Active { get; set; } = 0;
    [JsonPropertyName("maps")] public List<NamedMapDto> Maps { get; set; } = new();
    // saved pawn formations (Models/Formations.cs). older files have none
    [JsonPropertyName("formations")] public List<Formation> Formations { get; set; } = new();
    // the pawn library travels with the project file (v3). older project files
    // have no field and load with an empty embedded library.
    [JsonPropertyName("pawn_library")] public List<PawnTemplateDto> PawnLibrary { get; set; } = new();
    // the campaign's object types (a map file: the ones its objects use)
    [JsonPropertyName("object_library")] public List<ObjectType> ObjectLibrary { get; set; } = new();
    // each object picture once, by key (ObjectLook.PictureId)
    [JsonPropertyName("object_pictures")] public Dictionary<string, string> ObjectPictures { get; set; } = new();
    // the campaign's door presets, pawn statuses and affiliation colors. a
    // campaign file holds all of them, a map file the ones its map uses.
    // older files have none and get the built-in starters on load.
    [JsonPropertyName("door_types")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DoorType>? DoorTypes { get; set; }
    [JsonPropertyName("pawn_statuses")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PawnStatus>? PawnStatuses { get; set; }
    [JsonPropertyName("affiliation_colors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AffiliationColor>? AffiliationColors { get; set; }
}

/// Wire format for one exported map: the map, its name and the pawn
/// templates its pawns point at, so the file opens complete in any campaign.
public sealed class MapExportDto
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = MapSerializer.KindMap;
    [JsonPropertyName("version")] public int Version { get; set; } = MapSerializer.MapSpecVersion;
    [JsonPropertyName("name")] public string Name { get; set; } = "Map 1";
    [JsonPropertyName("map")] public MapFileDto Map { get; set; } = new();
    [JsonPropertyName("pawn_library")] public List<PawnTemplateDto> PawnLibrary { get; set; } = new();
    // the campaign's object types (a map file: the ones its objects use)
    [JsonPropertyName("object_library")] public List<ObjectType> ObjectLibrary { get; set; } = new();
    // each object picture once, by key (ObjectLook.PictureId)
    [JsonPropertyName("object_pictures")] public Dictionary<string, string> ObjectPictures { get; set; } = new();
    // the campaign's door presets, pawn statuses and affiliation colors. a
    // campaign file holds all of them, a map file the ones its map uses.
    // older files have none and get the built-in starters on load.
    [JsonPropertyName("door_types")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DoorType>? DoorTypes { get; set; }
    [JsonPropertyName("pawn_statuses")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PawnStatus>? PawnStatuses { get; set; }
    [JsonPropertyName("affiliation_colors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AffiliationColor>? AffiliationColors { get; set; }
}

/// What a file holds, read from its "kind" field or, for older files, its shape.
public enum SaveFileKind { Unknown, Campaign, Map, BareMap }

/// Maps read from a campaign or map file for import, plus the pawn templates
/// they reference (merged into the live library by the caller).
public sealed class ImportedMaps
{
    public List<MapEntry> Maps { get; } = new();
    public int Active { get; set; }
    public List<PawnTemplateDto> Library { get; set; } = new();
    public List<ObjectType> ObjectTypes { get; set; } = new();
    public List<Formation> Formations { get; set; } = new();
    public Dictionary<string, string> ObjectPictures { get; set; } = new();
    // the presets the file carries, each list null when it has none
    public PresetsWire Presets { get; set; } = new();
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
    // v4 drops it: a polygon is saved as walls, cells and a flood region.
    public const int MapSpecVersion = 4;
    // v3 adds the "pawn_library" field: the project's pawn library travels
    // inside the project file. v2 files load with an empty embedded library.
    // v4: the maps inside follow map spec v4 (no "polygons" field).
    public const int ProjectSpecVersion = 4;

    // "kind" values. a campaign is a full save (every map, the pawn library,
    // the active map); a map file is one exported map with the templates its
    // pawns use. older files have no kind and are told apart by shape.
    public const string KindCampaign = "campaign";
    public const string KindMap = "map";

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

    // legacy loader for v3 polygon rooms. each loop becomes a flood region
    // floor and its cut cells leave Cells, see MapDocument.AddLegacyPolygon.
    private static void AddPolygons(MapFileDto dto, MapDocument doc)
    {
        if (dto.Polygons is null) return;
        foreach (var d in dto.Polygons)
        {
            var loop = new List<(int x, int y)>();
            foreach (var s in d.Verts)
                if (TryParseCell(s, out var c)) loop.Add((c.X, c.Y));
            doc.AddLegacyPolygon(loop);
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
        => JsonSerializer.Serialize(ToDto(doc), DmJson.Default.MapFileDto);

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
        => FromDto(JsonSerializer.Deserialize(json, DmJson.Default.MapFileDto)
            ?? throw new InvalidDataException("map file is empty or not a map object"));

    // map porter helper: a map with a "polygons" field goes through the
    // document once so its polygons come out as walls, cells and flood
    // regions. a map without one is returned unchanged.
    public static MapFileDto PortPolygons(MapFileDto dto)
    {
        if (dto.Polygons is null) return dto;
        return ToDto(FromDto(dto));
    }

    public static void Save(MapDocument doc, string path) => File.WriteAllText(path, ToJson(doc));

    public static MapDocument Load(string path) => FromJson(File.ReadAllText(path));

    // what a campaign save would hold, in a form that only changes when the
    // content does: the active map is left out (switching maps is not a
    // change) and each map's lists are sorted, since cells, walls, doors and
    // the rest come out of hash sets in whatever order they were added. used
    // for the autosave skip and the save prompt on exit (MainWindow.SaveState.cs)
    public static string ProjectContentKey(MapProject project, PawnLibrary? library = null, CampaignPresets? presets = null, ObjectLibrary? objects = null)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(ProjectToJson(project, library, presets, objects, ignoreActive: true))!;
        if (root["maps"] is System.Text.Json.Nodes.JsonArray maps)
            foreach (var m in maps)
                if (m?["map"] is System.Text.Json.Nodes.JsonObject map)
                    foreach (var name in map.Select(p => p.Key).ToList())
                        if (map[name] is System.Text.Json.Nodes.JsonArray arr)
                        {
                            var items = arr.Select(n => n?.ToJsonString() ?? "null").OrderBy(s => s, StringComparer.Ordinal).ToList();
                            map[name] = new System.Text.Json.Nodes.JsonArray(items.Select(s => System.Text.Json.Nodes.JsonNode.Parse(s)).ToArray());
                        }
        return root.ToJsonString();
    }

    // ignoreActive writes 0 as the active map (ProjectContentKey)
    public static string ProjectToJson(MapProject project, PawnLibrary? library = null, CampaignPresets? presets = null, ObjectLibrary? objects = null, bool ignoreActive = false)
    {
        var dto = new ProjectFileDto { Version = ProjectSpecVersion, Active = ignoreActive ? 0 : project.ActiveIndex };
        foreach (var m in project.Maps)
            dto.Maps.Add(new NamedMapDto { Name = m.Name, Map = ToDto(m.Doc) });
        if (library is not null)
        {
            dto.PawnLibrary = LibraryToDtos(library);
            dto.Formations = library.Formations;
        }
        if (objects is not null)
        {
            dto.ObjectLibrary = objects.Types;
            dto.ObjectPictures = objects.UsedPictures(project.Maps.Select(m => m.Doc));
        }
        if (presets is not null)
        {
            var w = presets.ToWire();
            dto.DoorTypes = w.DoorTypes; dto.PawnStatuses = w.PawnStatuses; dto.AffiliationColors = w.AffiliationColors;
        }
        return JsonSerializer.Serialize(dto, DmJson.Default.ProjectFileDto);
    }

    /// Embedded library DTOs of a project file (empty for v2 files). The caller
    /// merges them into the live library and remaps the loaded pawns' ids.
    public static List<PawnTemplateDto> ExtractLibrary(string json)
    {
        var dto = JsonSerializer.Deserialize(json, DmJson.Default.ProjectFileDto);
        return dto?.PawnLibrary ?? new List<PawnTemplateDto>();
    }

    public static MapProject ProjectFromJson(string json)
    {
        var dto = JsonSerializer.Deserialize(json, DmJson.Default.ProjectFileDto)
            ?? throw new InvalidDataException("project file is empty or not a project object");
        var project = new MapProject();
        project.Maps.Clear();
        foreach (var m in dto.Maps)
            project.Maps.Add(new MapEntry { Name = string.IsNullOrWhiteSpace(m.Name) ? "Map" : m.Name, Doc = FromDto(m.Map) });
        if (project.Maps.Count == 0) project.Maps.Add(new MapEntry { Name = "Map 1" });
        project.ActiveIndex = (dto.Active >= 0 && dto.Active < project.Maps.Count) ? dto.Active : 0;
        return project;
    }

    /// Reads what a file holds. "kind" wins when present; otherwise a "maps"
    /// list means a campaign, a "map" object a map file and a "cells" list a
    /// bare map from before campaigns existed.
    public static SaveFileKind DetectKind(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return SaveFileKind.Unknown;
            if (root.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
            {
                var kind = k.GetString();
                if (string.Equals(kind, KindCampaign, StringComparison.OrdinalIgnoreCase)) return SaveFileKind.Campaign;
                if (string.Equals(kind, KindMap, StringComparison.OrdinalIgnoreCase)) return SaveFileKind.Map;
                return SaveFileKind.Unknown;
            }
            if (root.TryGetProperty("maps", out var ms) && ms.ValueKind == JsonValueKind.Array) return SaveFileKind.Campaign;
            if (root.TryGetProperty("map", out var m) && m.ValueKind == JsonValueKind.Object) return SaveFileKind.Map;
            if (root.TryGetProperty("cells", out var c) && c.ValueKind == JsonValueKind.Array) return SaveFileKind.BareMap;
            return SaveFileKind.Unknown;
        }
        catch (JsonException) { return SaveFileKind.Unknown; }
    }

    /// One map as a map file. Only the templates its pawns point at are
    /// embedded, so the file is complete without dragging the whole library.
    public static string MapToJson(MapEntry entry, PawnLibrary? library = null, CampaignPresets? presets = null, ObjectLibrary? objects = null)
    {
        var dto = new MapExportDto { Name = entry.Name, Map = ToDto(entry.Doc) };
        if (objects is not null)
        {
            var usedTypes = new HashSet<string>(entry.Doc.Objects.Select(o => o.TypeId));
            dto.ObjectLibrary = objects.Types.Where(t => usedTypes.Contains(t.Id)).ToList();
            dto.ObjectPictures = objects.UsedPictures([entry.Doc], dto.ObjectLibrary);
        }
        if (presets is not null)
        {
            var w = presets.UsedBy([entry.Doc], library).ToWire();
            dto.DoorTypes = w.DoorTypes; dto.PawnStatuses = w.PawnStatuses; dto.AffiliationColors = w.AffiliationColors;
        }
        if (library is not null)
        {
            var used = new HashSet<string>(entry.Doc.Pawns.Values.Select(p => p.TemplateId));
            var lib = new PawnLibrary();
            foreach (var t in library.Pawns)
                if (used.Contains(t.Id)) lib.Pawns.Add(t);
            dto.PawnLibrary = LibraryToDtos(lib);
        }
        return JsonSerializer.Serialize(dto, DmJson.Default.MapExportDto);
    }

    /// Reads every map a file holds: all maps of a campaign, the one map of a
    /// map file, or a bare map (named after fallbackName). Unknown files throw.
    public static ImportedMaps ImportFromJson(string json, string fallbackName)
    {
        var result = new ImportedMaps();
        switch (DetectKind(json))
        {
            case SaveFileKind.Campaign:
            {
                var project = ProjectFromJson(json);
                result.Maps.AddRange(project.Maps);
                result.Active = project.ActiveIndex;
                result.Library = ExtractLibrary(json);
                var pdto = JsonSerializer.Deserialize(json, DmJson.Default.ProjectFileDto);
                result.ObjectTypes = pdto?.ObjectLibrary ?? new();
                result.ObjectPictures = pdto?.ObjectPictures ?? new();
                result.Formations = (pdto?.Formations ?? new()).Where(f => f is not null).Select(f => f.Tidy()).ToList();
                result.Presets = CampaignPresets.ReadWire(json);
                result.Presets.Kind = null;
                break;
            }
            case SaveFileKind.Map:
            {
                var dto = JsonSerializer.Deserialize(json, DmJson.Default.MapExportDto)
                    ?? throw new InvalidDataException("map file is empty");
                var name = string.IsNullOrWhiteSpace(dto.Name) ? fallbackName : dto.Name;
                result.Maps.Add(new MapEntry { Name = name, Doc = FromDto(dto.Map) });
                result.Library = dto.PawnLibrary ?? new List<PawnTemplateDto>();
                result.ObjectTypes = dto.ObjectLibrary ?? new();
                result.ObjectPictures = dto.ObjectPictures ?? new();
                result.Presets = new PresetsWire { DoorTypes = dto.DoorTypes, PawnStatuses = dto.PawnStatuses, AffiliationColors = dto.AffiliationColors };
                break;
            }
            case SaveFileKind.BareMap:
                result.Maps.Add(new MapEntry { Name = fallbackName, Doc = FromJson(json) });
                break;
            default:
                throw new InvalidDataException("not a campaign or map file");
        }
        return result;
    }

    public static void SaveMapFile(MapEntry entry, string path, PawnLibrary? library = null, CampaignPresets? presets = null, ObjectLibrary? objects = null)
        => File.WriteAllText(path, MapToJson(entry, library, presets, objects));

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
                Favorite = t.Favorite,
                Hero = t.Hero,
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
                Favorite = d.Favorite,
                Hero = d.Hero,
            });
        }
        return list;
    }

    public static string LibraryToJson(PawnLibrary lib)
        => JsonSerializer.Serialize(new LibraryWire { pawn_library = LibraryToDtos(lib) }, DmJson.Default.LibraryWire);

    public static List<PawnTemplateDto> LibraryFromJson(string json)
    {
        var dto = JsonSerializer.Deserialize(json, DmJson.Default.ProjectFileDto);
        if (dto is not null && dto.PawnLibrary.Count > 0) return dto.PawnLibrary;
        var w = JsonSerializer.Deserialize(json, DmJson.Default.LibraryWire);
        return w?.pawn_library ?? new List<PawnTemplateDto>();
    }

    // an object types export file: {"kind":"object_types","object_library":[...]}.
    // import also reads the object_library of campaign and map files
    public const string KindObjectTypes = "object_types";
    internal sealed class ObjectTypesWire
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = KindObjectTypes;
        [JsonPropertyName("object_library")] public List<ObjectType> ObjectLibrary { get; set; } = new();
        [JsonPropertyName("object_pictures")] public Dictionary<string, string> ObjectPictures { get; set; } = new();
    }

    public static string ObjectTypesToJson(ObjectLibrary lib)
        => JsonSerializer.Serialize(new ObjectTypesWire { ObjectLibrary = lib.Types, ObjectPictures = lib.UsedPictures([]) }, DmJson.Default.ObjectTypesWire);

    internal sealed class LibraryWire
    {
        [JsonPropertyName("pawn_library")] public List<PawnTemplateDto> pawn_library { get; set; } = new();
    }

    public static void SaveProject(MapProject project, string path, PawnLibrary? library = null, CampaignPresets? presets = null, ObjectLibrary? objects = null)
        => File.WriteAllText(path, ProjectToJson(project, library, presets, objects));

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
            dto.Labels.Add(new LabelDto
            {
                Cell = Key(l.Cell), Text = l.Text, FontSize = l.FontSize, R = l.R, G = l.G, B = l.B,
                W = l.W, H = l.H, Align = l.Align.ToString().ToLowerInvariant(),
            });
        foreach (var (c, p) in doc.Pawns)
            dto.Pawns.Add(PawnToDto(c, p));
        foreach (var w in doc.AngledWalls)
        {
            dto.AngledWalls.Add(Key(w));
            foreach (var d in doc.GetAngledDoors(w))
                dto.AngledDoors.Add(new AngledDoorDto { Wall = Key(w), T = d.T, Type = d.Type, State = d.State.ToString().ToLowerInvariant() });
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
        foreach (var o in doc.Objects)
            dto.Objects.Add(new ObjectDto
            {
                Id = o.Id, TypeId = o.TypeId, Rect = $"{o.X},{o.Y},{o.W},{o.H}",
                Name = string.IsNullOrWhiteSpace(o.Name) ? null : o.Name, Look = o.Look?.Clone(),
            });
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
            doc.Labels.Add(new MapLabel
            {
                Cell = c, Text = l.Text, FontSize = l.FontSize, R = l.R, G = l.G, B = l.B,
                W = Math.Max(1, l.W), H = Math.Max(1, l.H),
                Align = Enum.TryParse<LabelAlign>(l.Align, ignoreCase: true, out var al) ? al : LabelAlign.Center,
            });
        }
        AddPawns(dto, doc);
        AddAngled(dto, doc);
        AddPolygons(dto, doc);
        AddFloodRegions(dto, doc);
        AddVertexDoors(dto, doc);
        AddObjects(dto, doc);
        return doc;
    }

    // a rect that does not parse or has no size is dropped. ids are kept so
    // undo and redo find the same object again, a missing or repeated id gets
    // a fresh one.
    private static void AddObjects(MapFileDto dto, MapDocument doc)
    {
        var ids = new HashSet<string>();
        foreach (var d in dto.Objects ?? new())
        {
            var parts = (d.Rect ?? "").Split(',');
            if (parts.Length != 4 || !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)
                || !int.TryParse(parts[2], out int w) || !int.TryParse(parts[3], out int h) || w < 1 || h < 1) continue;
            var o = new MapObject
            {
                TypeId = d.TypeId ?? "", X = x, Y = y, W = w, H = h,
                Name = string.IsNullOrWhiteSpace(d.Name) ? null : d.Name,
                Look = d.Look?.Tidy(),
            };
            if (!string.IsNullOrEmpty(d.Id) && ids.Add(d.Id)) o.Id = d.Id; else ids.Add(o.Id);
            doc.Objects.Add(o);
        }
    }

    /// the object types of a campaign or map file, tidied, for MergeAppend
    public static List<ObjectType> TidyObjectTypes(IEnumerable<ObjectType>? types)
    {
        var list = new List<ObjectType>();
        foreach (var t in types ?? [])
        {
            if (t is null || string.IsNullOrEmpty(t.Id)) continue;
            t.Name ??= "";
            t.Look = (t.Look ?? new ObjectLook()).Tidy();
            list.Add(t);
        }
        return list;
    }
}
