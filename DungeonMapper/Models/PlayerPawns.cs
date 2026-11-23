using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonMapper.Models;

/// multiplayer: the pawns a joined player may place, move, set the status of
/// and keep formations of: every pawn with the Player affiliation, hero or
/// not. Ally, Enemy and Neutral pawns are the DM's. the player's app checks
/// with these to refuse early; the DM's app checks again on the real map
/// (with its own library) and applies.
public static class PlayerPawns
{
    public const string HiddenHero = "the DM has that hero hidden";

    public static bool Controlled(PawnTemplate? t) =>
        t is not null && PawnTemplate.NormalizeAffiliation(t.Affiliation) == "Player";

    /// a pawn the players see: not DM only, on a revealed cell
    public static bool Visible(MapDocument doc, Pawn p) => !p.DmOnly && doc.Revealed.Contains(p.Cell);

    // where a player may put a pawn: revealed floor with no pawn on it
    private static string? CheckTarget(MapDocument doc, CellKey to)
    {
        if (!doc.Revealed.Contains(to) || !doc.IsFloorCell(to)) return "pawns can only go onto revealed floor";
        if (doc.Pawns.ContainsKey(to)) return "that cell already has a pawn";
        return null;
    }

    /// a hero is one of a kind: placing it moves the copy the players see.
    /// a copy they can not see (DM only, unrevealed, or on another map:
    /// onOtherMap) is the DM's secret, so placing it is refused
    private static bool HeroHidden(MapDocument doc, string templateId, bool onOtherMap) =>
        onOtherMap || doc.Pawns.Values.Any(p => p.TemplateId == templateId && !Visible(doc, p));

    /// why a player can not move the pawn at from to to, null when they can
    public static string? CheckMove(MapDocument doc, PawnLibrary lib, CellKey from, CellKey to)
    {
        if (!doc.Pawns.TryGetValue(from, out var p) || !Visible(doc, p)) return "there is no pawn there";
        if (!Controlled(lib.Find(p.TemplateId))) return "only the DM moves that pawn";
        if (from == to) return "that pawn is already there";
        return CheckTarget(doc, to);
    }

    /// why a player can not place the template at to, null when they can.
    /// onOtherMap: the template is a hero placed on another map (the DM's
    /// app knows; the player's passes false)
    public static string? CheckPlace(MapDocument doc, PawnLibrary lib, string templateId, CellKey to, bool onOtherMap = false)
    {
        var t = lib.Find(templateId);
        if (!Controlled(t)) return "only the DM places that pawn";
        if (t!.Hero && HeroHidden(doc, templateId, onOtherMap)) return HiddenHero;
        return CheckTarget(doc, to);
    }

    /// why a player can not set the status of the pawn at the cell (null =
    /// back to the template's), null when they can
    public static string? CheckStatus(MapDocument doc, PawnLibrary lib, CampaignPresets presets, CellKey at, string? status)
    {
        if (!doc.Pawns.TryGetValue(at, out var p) || !Visible(doc, p)) return "there is no pawn there";
        if (!Controlled(lib.Find(p.TemplateId))) return "only the DM changes that pawn";
        if (status is not null && !presets.PawnStatuses.Any(s => s.Name == status)) return "that status does not exist";
        return null;
    }

    /// why a player can not deploy these pawns (template id, cell), null when
    /// they can. a cell may hold a hero of the deploy, which moves.
    /// onOtherMap tells which heroes are on another map (the DM's app)
    public static string? CheckDeploy(MapDocument doc, PawnLibrary lib, IReadOnlyList<(string id, CellKey cell)> pawns, Func<string, bool>? onOtherMap = null)
    {
        if (pawns.Count == 0) return "no pawns to deploy";
        if (pawns.Select(p => p.cell).Distinct().Count() != pawns.Count) return "two pawns on one cell";
        var heroes = new HashSet<string>();
        foreach (var (id, _) in pawns)
        {
            var t = lib.Find(id);
            if (!Controlled(t)) return "only the DM places some of those pawns";
            if (t!.Hero)
            {
                if (HeroHidden(doc, id, onOtherMap?.Invoke(id) ?? false)) return HiddenHero;
                heroes.Add(id);
            }
        }
        foreach (var (_, cell) in pawns)
        {
            if (!doc.Revealed.Contains(cell) || !doc.IsFloorCell(cell)) return "pawns can only go onto revealed floor";
            if (doc.Pawns.TryGetValue(cell, out var occ) && !heroes.Contains(occ.TemplateId)) return "a cell already has a pawn";
        }
        return null;
    }

    /// moves the pawn (status kept, visible). call CheckMove first
    public static void ApplyMove(MapDocument doc, CellKey from, CellKey to)
    {
        var p = doc.Pawns[from];
        doc.Pawns.Remove(from);
        doc.Pawns[to] = new Pawn { Cell = to, TemplateId = p.TemplateId, Status = p.Status, DmOnly = false };
    }

    /// places the pawn, visible. a hero moves from where it stands on this
    /// map, keeping its status; other pawns are a new copy. true when a hero
    /// moved. call CheckPlace first
    public static bool ApplyPlace(MapDocument doc, PawnTemplate t, CellKey to) =>
        ApplyDeploy(doc, new[] { t }, new[] { (t.Id, to) }) > 0;

    /// places all of them in one go (heroes come off their old cells first,
    /// status kept). returns how many heroes moved. call CheckDeploy first
    public static int ApplyDeploy(MapDocument doc, IEnumerable<PawnTemplate> templates, IReadOnlyList<(string id, CellKey cell)> pawns)
    {
        var heroes = templates.Where(t => t.Hero).Select(t => t.Id).ToHashSet();
        var statuses = new Dictionary<string, string?>();
        foreach (var (c, p) in doc.Pawns.Where(kv => heroes.Contains(kv.Value.TemplateId)).ToList())
        {
            if (!pawns.Any(x => x.id == p.TemplateId)) continue;
            statuses.TryAdd(p.TemplateId, p.Status);
            doc.Pawns.Remove(c);
        }
        foreach (var (id, cell) in pawns)
            doc.Pawns[cell] = new Pawn { Cell = cell, TemplateId = id, Status = statuses.GetValueOrDefault(id), DmOnly = false };
        return statuses.Count;
    }

    /// why a player can not keep this formation (all members are theirs)
    public static string? CheckFormation(PawnLibrary lib, Formation f)
    {
        if (string.IsNullOrEmpty(f.Id)) return "that formation has no id";
        if (f.Members.Count == 0) return "a formation needs pawns";
        if (f.Members.Any(m => !Controlled(lib.Find(m.TemplateId)))) return "formations can only hold player pawns";
        return null;
    }
}
