using System;
using System.Collections.Generic;
using System.Linq;

namespace DungeonMapper.Models;

/// Snapshot-based undo/redo. Each user action ends with Commit(), which stores
/// a JSON snapshot of the document (deep clone via the serializer, already
/// round-trip verified). Undo/Redo return a restored document; the caller
/// applies it to the live instance with ApplyFrom so bound UI keeps its
/// reference. History is bounded: oldest snapshots are dropped.
public sealed class History
{
    public const int MaxSnapshots = 100;

    private readonly List<string> _snapshots = new();
    // hero status changes made by the step that led to each snapshot (null
    // for most steps). a hero's status lives in the pawn library, outside the
    // map, so the step carries it: undo puts back before, redo after, by hero
    // id wherever the hero is now
    private readonly List<List<HeroStatusChange>?> _heroChanges = new();
    private int _index = -1; // index of the currently applied snapshot

    /// what the last Undo or Redo asks the caller to put back in the pawn
    /// library (hero id, status). empty when the step changed no hero status
    public List<(string id, string status)> HeroRestore { get; private set; } = new();

    public void Initialize(MapDocument doc)
    {
        _snapshots.Clear();
        _heroChanges.Clear();
        _snapshots.Add(MapSerializer.ToJson(doc));
        _heroChanges.Add(null);
        _index = 0;
    }

    /// Call after each completed user action. Discards the redo branch.
    /// heroChanges: hero statuses this action changed in the pawn library
    public void Commit(MapDocument doc, List<HeroStatusChange>? heroChanges = null)
    {
        if (_index < 0) { Initialize(doc); return; }
        _snapshots.RemoveRange(_index + 1, _snapshots.Count - _index - 1);
        _heroChanges.RemoveRange(_index + 1, _heroChanges.Count - _index - 1);
        _snapshots.Add(MapSerializer.ToJson(doc));
        _heroChanges.Add(heroChanges is { Count: > 0 } ? heroChanges : null);
        if (_snapshots.Count > MaxSnapshots) { _snapshots.RemoveAt(0); _heroChanges.RemoveAt(0); }
        _index = _snapshots.Count - 1;
    }

    /// applies a change to every stored snapshot, used when something the
    /// documents refer to by name is renamed so undo and redo keep the link
    public void Rewrite(Action<MapDocument> change)
    {
        for (int i = 0; i < _snapshots.Count; i++)
        {
            var d = MapSerializer.FromJson(_snapshots[i]);
            change(d);
            _snapshots[i] = MapSerializer.ToJson(d);
        }
    }

    public bool CanUndo => _index > 0;
    public bool CanRedo => _index < _snapshots.Count - 1;

    /// Returns the document state to restore, or null if nothing to undo.
    public MapDocument? Undo()
    {
        if (!CanUndo) return null;
        // the step being undone led to the current snapshot
        var undone = _heroChanges[_index];
        HeroRestore = undone is null ? new() : undone.Select(c => (c.Id, c.Before)).Reverse().ToList();
        _index--;
        return MapSerializer.FromJson(_snapshots[_index]);
    }

    public MapDocument? Redo()
    {
        if (!CanRedo) return null;
        _index++;
        var redone = _heroChanges[_index];
        HeroRestore = redone is null ? new() : redone.Select(c => (c.Id, c.After)).ToList();
        return MapSerializer.FromJson(_snapshots[_index]);
    }
}

/// one hero's status change in an undo step (statuses by name)
public sealed record HeroStatusChange(string Id, string Before, string After);
