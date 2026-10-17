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

/// A project holds one or more named maps and tracks which is active. The canvas
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

    public void SetActive(int index)
    {
        if (index >= 0 && index < Maps.Count) ActiveIndex = index;
    }
}
