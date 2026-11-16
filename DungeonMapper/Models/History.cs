using System;
using System.Collections.Generic;

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
    private int _index = -1; // index of the currently applied snapshot

    public void Initialize(MapDocument doc)
    {
        _snapshots.Clear();
        _snapshots.Add(MapSerializer.ToJson(doc));
        _index = 0;
    }

    /// Call after each completed user action. Discards the redo branch.
    public void Commit(MapDocument doc)
    {
        if (_index < 0) { Initialize(doc); return; }
        _snapshots.RemoveRange(_index + 1, _snapshots.Count - _index - 1);
        _snapshots.Add(MapSerializer.ToJson(doc));
        if (_snapshots.Count > MaxSnapshots) _snapshots.RemoveAt(0);
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
        _index--;
        return MapSerializer.FromJson(_snapshots[_index]);
    }

    public MapDocument? Redo()
    {
        if (!CanRedo) return null;
        _index++;
        return MapSerializer.FromJson(_snapshots[_index]);
    }
}
