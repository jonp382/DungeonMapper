using System;
using System.Collections.Generic;

namespace DungeonMapper.Models;

/// A named map inside a project. No vertical layering (per DM: stairs go up/down
/// between separate maps, never overlapping layers), so each map is a standalone
/// document with a display name.
public sealed class MapEntry
{
    public string Name { get; set; } = "Map 1";
    public MapDocument Doc { get; set; } = new();
}

/// A project (a "campaign" in the ui and the file "kind") holds one or more named maps and tracks which is active. The canvas
/// shows the active map; switching maps swaps the canvas Document.
public sealed class MapProject
{
    public List<MapEntry> Maps { get; set; } = new();
    public int ActiveIndex { get; set; } = 0;

    public MapProject()
    {
        Maps.Add(new MapEntry { Name = "Map 1" });
    }

    public MapDocument Active => Maps[ActiveIndex].Doc;
    public string ActiveName => Maps[ActiveIndex].Name;

    public MapEntry AddMap(string name)
    {
        var e = new MapEntry { Name = name };
        Maps.Add(e);
        ActiveIndex = Maps.Count - 1;
        return e;
    }

    public void RemoveMap(int index)
    {
        if (Maps.Count <= 1) return; // keep at least one map
        Maps.RemoveAt(index);
        if (ActiveIndex >= Maps.Count) ActiveIndex = Maps.Count - 1;
    }

    /// A name not used by any map: "Cave" stays "Cave" when free, else
    /// becomes "Cave (2)", "Cave (3)" and so on. skip is a map index to ignore
    /// (the map being renamed).
    public string UniqueName(string name, int skip = -1)
    {
        bool Taken(string n)
        {
            for (int i = 0; i < Maps.Count; i++)
                if (i != skip && string.Equals(Maps[i].Name, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        if (!Taken(name)) return name;
        for (int n = 2; ; n++)
            if (!Taken($"{name} ({n})")) return $"{name} ({n})";
    }

    /// Moves a map up or down the list, keeping the same map active.
    public void MoveMap(int from, int to)
    {
        if (from < 0 || from >= Maps.Count || to < 0 || to >= Maps.Count || from == to) return;
        var active = Maps[ActiveIndex];
        var e = Maps[from];
        Maps.RemoveAt(from);
        Maps.Insert(to, e);
        ActiveIndex = Maps.IndexOf(active);
    }

    public void SetActive(int index)
    {
        if (index >= 0 && index < Maps.Count) ActiveIndex = index;
    }
}
