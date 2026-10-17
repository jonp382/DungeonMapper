using System;
using System.Collections.Generic;
using System.IO;
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
}

/// Wire format for a multi-map project: a list of named maps plus the active index.
public sealed class ProjectFileDto
{
    [JsonPropertyName("version")] public int Version { get; set; } = 2;
    [JsonPropertyName("active")] public int Active { get; set; } = 0;
    [JsonPropertyName("maps")] public List<NamedMapDto> Maps { get; set; } = new();
}

public sealed class NamedMapDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "Map 1";
    [JsonPropertyName("map")] public MapFileDto Map { get; set; } = new();
}

public static class MapSerializer
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
    };

    // "x,y" <-> CellKey
    private static string Key(CellKey c) => $"{c.X},{c.Y}";
    private static string Key(EdgeKey e) => $"{e.Cell.X},{e.Cell.Y},{e.Side.ToString().ToLowerInvariant()}";

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

    public static string ToJson(MapDocument doc)
    {
        var dto = new MapFileDto();
        foreach (var c in doc.Cells) dto.Cells.Add(Key(c));
        foreach (var (e, w) in doc.Walls) if (w) dto.Walls.Add(Key(e));
        foreach (var (e, d) in doc.Doors)
            dto.Doors.Add(new DoorDto { Edge = Key(e), Type = d.Type, State = d.State.ToString().ToLowerInvariant() });
        foreach (var (c, s) in doc.Stairs)
            dto.Stairs.Add(new StairDto { Cell = Key(c), Direction = s.Direction.ToString().ToLowerInvariant(), TargetMap = s.TargetMap });
        foreach (var l in doc.Labels)
            dto.Labels.Add(new LabelDto { Cell = Key(l.Cell), Text = l.Text });
        return JsonSerializer.Serialize(dto, Opts);
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
            doc.Labels.Add(new MapLabel { Cell = c, Text = l.Text });
        }
        return doc;
    }

    public static void Save(MapDocument doc, string path) => File.WriteAllText(path, ToJson(doc));

    public static MapDocument Load(string path) => FromJson(File.ReadAllText(path));

    public static string ProjectToJson(MapProject project)
    {
        var dto = new ProjectFileDto { Active = project.ActiveIndex };
        foreach (var m in project.Maps)
            dto.Maps.Add(new NamedMapDto { Name = m.Name, Map = ToDto(m.Doc) });
        return JsonSerializer.Serialize(dto, Opts);
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

    public static void SaveProject(MapProject project, string path) => File.WriteAllText(path, ProjectToJson(project));

    public static MapProject LoadProject(string path) => ProjectFromJson(File.ReadAllText(path));

    // build a MapFileDto from a document (shared by ToJson and ProjectToJson)
    private static MapFileDto ToDto(MapDocument doc)
    {
        var dto = new MapFileDto();
        foreach (var c in doc.Cells) dto.Cells.Add(Key(c));
        foreach (var (e, w) in doc.Walls) if (w) dto.Walls.Add(Key(e));
        foreach (var (e, d) in doc.Doors)
            dto.Doors.Add(new DoorDto { Edge = Key(e), Type = d.Type, State = d.State.ToString().ToLowerInvariant() });
        foreach (var (c, s) in doc.Stairs)
            dto.Stairs.Add(new StairDto { Cell = Key(c), Direction = s.Direction.ToString().ToLowerInvariant(), TargetMap = s.TargetMap });
        foreach (var l in doc.Labels)
            dto.Labels.Add(new LabelDto { Cell = Key(l.Cell), Text = l.Text });
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
            doc.Labels.Add(new MapLabel { Cell = c, Text = l.Text });
        }
        return doc;
    }
}
