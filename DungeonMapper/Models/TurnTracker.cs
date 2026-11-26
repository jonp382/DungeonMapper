using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonMapper.Models;

/// Turn tracking (Agent.md "Pawn Status and Turn Tracking Design"). Each
/// status on a pawn has turns left (-1 = permanent). A map's combat starts at
/// turn 1; passing a turn counts every timed status down by one and removes
/// the ones that reach 0; ending combat removes every timed status. The
/// caller commits one undo step and shows the "wore off" text.
public static class TurnTracker
{
    public const int MaxTurns = 999;

    /// a status's turns: -1 (permanent) or 1 to MaxTurns; 0 and other
    /// negatives become permanent
    public static int CleanTurns(int turns) => turns >= 1 ? Math.Min(turns, MaxTurns) : -1;

    /// one status that wore off: where the pawn stands and which status
    public readonly record struct WoreOff(CellKey Cell, string Name);

    public static bool InCombat(MapDocument doc) => doc.CombatTurn > 0;

    public static void Start(MapDocument doc) => doc.CombatTurn = 1;

    /// counts down the timed statuses of these pawns by one turn; the ones
    /// reaching 0 are removed and returned
    public static List<WoreOff> Tick(IEnumerable<Pawn> pawns)
    {
        var gone = new List<WoreOff>();
        foreach (var p in pawns)
        {
            foreach (var s in p.Statuses.Where(s => s.Turns > 0)) s.Turns--;
            foreach (var s in p.Statuses.Where(s => s.Turns == 0)) gone.Add(new WoreOff(p.Cell, s.Name));
            p.Statuses.RemoveAll(s => s.Turns == 0);
        }
        return gone;
    }

    /// removes every timed status of these pawns (permanent ones stay)
    public static List<WoreOff> ClearTimed(IEnumerable<Pawn> pawns)
    {
        var gone = new List<WoreOff>();
        foreach (var p in pawns)
        {
            foreach (var s in p.Statuses.Where(s => s.Turns > 0)) gone.Add(new WoreOff(p.Cell, s.Name));
            p.Statuses.RemoveAll(s => s.Turns > 0);
        }
        return gone;
    }

    /// the next turn on this map: its pawns tick, the counter goes up
    public static List<WoreOff> Pass(MapDocument doc)
    {
        if (!InCombat(doc)) return new();
        var gone = Tick(doc.Pawns.Values);
        doc.CombatTurn++;
        return gone;
    }

    /// ends this map's combat: timed statuses go, the counter resets
    public static List<WoreOff> End(MapDocument doc)
    {
        var gone = ClearTimed(doc.Pawns.Values);
        doc.CombatTurn = 0;
        return gone;
    }

    /// what disappeared between two copies of a map the players were sent
    /// (their app shows the "wore off" text): timed statuses of pawns on the
    /// same cell that are gone, when the turn moved on or combat ended
    public static List<WoreOff> Diff(MapDocument before, MapDocument after)
    {
        var gone = new List<WoreOff>();
        if (before.CombatTurn == 0 || after.CombatTurn == before.CombatTurn) return gone;
        foreach (var (c, p) in before.Pawns)
        {
            if (!after.Pawns.TryGetValue(c, out var q) || q.TemplateId != p.TemplateId) continue;
            foreach (var s in p.Statuses.Where(s => s.Turns > 0))
                if (!q.HasStatus(s.Name)) gone.Add(new WoreOff(c, s.Name));
        }
        return gone;
    }
}
