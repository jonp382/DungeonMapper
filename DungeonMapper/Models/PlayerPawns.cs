using System;
using System.Linq;

namespace DungeonMapper.Models;

/// multiplayer: the pawns a joined player may place and move, heroes with the
/// Player affiliation. every other pawn (Ally, Enemy, Neutral, and Player
/// pawns that are not heroes) is the DM's. the player's app checks with these
/// to refuse early; the DM's app checks again on the real map and applies.
public static class PlayerPawns
{
    public static bool Controlled(PawnTemplate? t) =>
        t is not null && t.Hero && PawnTemplate.NormalizeAffiliation(t.Affiliation) == "Player";

    // where a player may put a pawn: revealed floor with no pawn on it
    private static string? CheckTarget(MapDocument doc, CellKey to)
    {
        if (!doc.Revealed.Contains(to) || !doc.IsFloorCell(to)) return "pawns can only go onto revealed floor";
        if (doc.Pawns.ContainsKey(to)) return "that cell already has a pawn";
        return null;
    }

    /// why a player can not move the pawn at from to to, null when they can.
    /// the pawn must be one the players see (visible, on revealed floor)
    public static string? CheckMove(MapDocument doc, PawnLibrary lib, CellKey from, CellKey to)
    {
        if (!doc.Pawns.TryGetValue(from, out var p) || p.DmOnly || !doc.Revealed.Contains(from)) return "there is no pawn there";
        if (!Controlled(lib.Find(p.TemplateId))) return "only the DM moves that pawn";
        if (from == to) return "that pawn is already there";
        return CheckTarget(doc, to);
    }

    /// why a player can not place the template at to, null when they can
    public static string? CheckPlace(MapDocument doc, PawnLibrary lib, string templateId, CellKey to)
    {
        if (!Controlled(lib.Find(templateId))) return "only the DM places that pawn";
        return CheckTarget(doc, to);
    }

    /// moves the pawn (status kept, visible). call CheckMove first
    public static void ApplyMove(MapDocument doc, CellKey from, CellKey to)
    {
        var p = doc.Pawns[from];
        doc.Pawns.Remove(from);
        doc.Pawns[to] = new Pawn { Cell = to, TemplateId = p.TemplateId, Status = p.Status, DmOnly = false };
    }

    /// places the hero, visible. a hero is one of a kind: copies on this map
    /// go, and takeElsewhere removes it from the other maps; the status of a
    /// copy is kept. true when an existing copy moved. call CheckPlace first
    public static bool ApplyPlace(MapDocument doc, string templateId, CellKey to, Func<string, Pawn?>? takeElsewhere)
    {
        Pawn? old = null;
        foreach (var (c, p) in doc.Pawns.Where(kv => kv.Value.TemplateId == templateId).ToList())
        {
            old ??= p;
            doc.Pawns.Remove(c);
        }
        var other = takeElsewhere?.Invoke(templateId);
        old ??= other;
        doc.Pawns[to] = new Pawn { Cell = to, TemplateId = templateId, Status = old?.Status, DmOnly = false };
        return old is not null;
    }
}
