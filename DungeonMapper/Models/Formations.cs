using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DungeonMapper.Models;

// formations: a saved set of pawns and where they stand relative to each
// other, deployed onto the map in one click. kept with the pawn library in the
// campaign file ("formations"). see Agent.md "Formation Manager Design".

public sealed class FormationMember
{
    [JsonPropertyName("template_id")] public string TemplateId { get; set; } = "";
    // cells from the top-left of the formation's bounding box
    [JsonPropertyName("dx")] public int Dx { get; set; }
    [JsonPropertyName("dy")] public int Dy { get; set; }
}

public sealed class Formation
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("members")] public List<FormationMember> Members { get; set; } = new();

    public int Width => Members.Count == 0 ? 0 : Members.Max(m => m.Dx) + 1;
    public int Height => Members.Count == 0 ? 0 : Members.Max(m => m.Dy) + 1;

    /// the pawns in a map area, top-left of their bounding box at 0,0, in
    /// reading order. empty when the area has no pawns
    public static List<FormationMember> Capture(IEnumerable<Pawn> pawns)
    {
        var list = pawns.ToList();
        if (list.Count == 0) return new();
        int x0 = list.Min(p => p.Cell.X), y0 = list.Min(p => p.Cell.Y);
        return list.OrderBy(p => p.Cell.Y).ThenBy(p => p.Cell.X)
            .Select(p => new FormationMember { TemplateId = p.TemplateId, Dx = p.Cell.X - x0, Dy = p.Cell.Y - y0 })
            .ToList();
    }

    /// offsets turned counterclockwise by quarter turns (R = -1 clockwise),
    /// then moved back so the top-left of the box is 0,0
    public static List<(string id, int dx, int dy)> Rotate(IEnumerable<(string id, int dx, int dy)> members, int turns)
    {
        turns = ((turns % 4) + 4) % 4;
        var r = members.Select(m =>
        {
            int x = m.dx, y = m.dy;
            for (int i = 0; i < turns; i++) (x, y) = (y, -x);
            return (m.id, x, y);
        }).ToList();
        if (r.Count == 0) return r;
        int mx = r.Min(m => m.x), my = r.Min(m => m.y);
        return r.Select(m => (m.id, m.x - mx, m.y - my)).ToList();
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// a name not used by another formation: "formation", "formation 2", ...
    public static string UniqueName(IEnumerable<Formation> existing, string baseName = "formation")
    {
        var names = new HashSet<string>(existing.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (int i = 2; ; i++) if (!names.Contains($"{baseName} {i}")) return $"{baseName} {i}";
    }

    /// drops members with no template id and gives a missing id a new one
    public Formation Tidy()
    {
        if (string.IsNullOrEmpty(Id)) Id = NewId();
        Name ??= "";
        Members = (Members ?? new()).Where(m => m is not null && !string.IsNullOrEmpty(m.TemplateId)).ToList();
        return this;
    }
}
