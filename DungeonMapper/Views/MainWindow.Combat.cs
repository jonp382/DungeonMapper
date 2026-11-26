using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using DungeonMapper.Models;

namespace DungeonMapper.Views;

// turn tracking (Agent.md "Pawn Status and Turn Tracking Design"): start
// combat on the open map, pass turns (timed statuses count down, the ones at
// 0 wear off with floating text), end combat (timed statuses go). each is
// one undo step. players only see the turn number; their app shows the
// "wore off" text by comparing the maps the DM sends.
public partial class MainWindow
{
    private void InitCombat()
    {
        BtnStartCombat.Click += (_, _) => { StartCombat(); FocusCanvas(); };
        BtnPassTurn.Click += (_, _) => { PassTurn(); FocusCanvas(); };
        BtnEndCombat.Click += (_, _) => { EndCombat(); FocusCanvas(); };
        RefreshCombatUi();
    }

    /// the buttons and the turn number follow the open map (or the map the
    /// DM sends, while joined: the number only)
    private void RefreshCombatUi()
    {
        bool joined = Joined;
        int turn = joined ? Canvas.RemoteDocument?.CombatTurn ?? 0 : _doc.CombatTurn;
        BtnStartCombat.IsVisible = !joined && turn == 0;
        BtnPassTurn.IsVisible = !joined && turn > 0;
        BtnEndCombat.IsVisible = !joined && turn > 0;
        TurnLabel.IsVisible = turn > 0;
        TurnLabel.Text = $"turn {turn}";
    }

    private void StartCombat()
    {
        if (Joined || TurnTracker.InCombat(_doc)) return;
        TurnTracker.Start(_doc);
        ActiveHistory.Commit(_doc);
        AfterCombatChange(Enumerable.Empty<TurnTracker.WoreOff>(), "combat started: turn 1");
    }

    private void PassTurn()
    {
        if (Joined || !TurnTracker.InCombat(_doc)) return;
        var gone = TurnTracker.Pass(_doc);
        TickHeroesElsewhere(clear: false);
        ActiveHistory.Commit(_doc);
        AfterCombatChange(gone, $"turn {_doc.CombatTurn}" + (gone.Count > 0 ? $": {Statuses(gone.Count)} wore off" : ""));
    }

    private void EndCombat()
    {
        if (Joined || !TurnTracker.InCombat(_doc)) return;
        var gone = TurnTracker.End(_doc);
        TickHeroesElsewhere(clear: true);
        ActiveHistory.Commit(_doc);
        AfterCombatChange(gone, "combat ended" + (gone.Count > 0 ? $": {Statuses(gone.Count)} wore off" : ""));
    }

    private static string Statuses(int n) => n == 1 ? "1 status" : $"{n} statuses";

    // heroes on the other maps count down too, so a hero's timed status keeps
    // running wherever the hero is. each map changed gets an undo step of its
    // own; floating text only shows on the open map
    private void TickHeroesElsewhere(bool clear)
    {
        foreach (var m in _project.Maps)
        {
            if (m.Doc == _doc) continue;
            var heroes = m.Doc.Pawns.Values.Where(p => ActiveLibrary.Find(p.TemplateId)?.Hero == true && p.Statuses.Any(s => s.Turns > 0)).ToList();
            if (heroes.Count == 0) continue;
            if (clear) TurnTracker.ClearTimed(heroes); else TurnTracker.Tick(heroes);
            HistoryFor(m).Commit(m.Doc);
        }
    }

    private void AfterCombatChange(System.Collections.Generic.IEnumerable<TurnTracker.WoreOff> gone, string status)
    {
        Canvas.Refresh();
        Canvas.ShowWoreOff(gone);
        PawnViewsChanged();
        ReopenConfigView();
        RefreshMaps();
        RefreshCombatUi();
        SetStatus(status);
    }

    private bool IsPassTurnKey(Key k) =>
        System.Enum.TryParse<Key>(_settings.ToolHotkeys.GetValueOrDefault(AppSettings.PassTurnHotkey, ""), out var pk) && pk == k;
}
