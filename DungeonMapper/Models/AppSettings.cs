using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DungeonMapper.Models;

/// A configurable door category: a name shown in the dropdown and an RGB color.
public sealed class DoorType
{
    public string Name = "standard";
    public int R = 245, G = 220, B = 60;

    public DoorType() { }
    public DoorType(string name, int r, int g, int b) { Name = name; R = r; G = g; B = b; }
}

/// App-wide settings: grid draw spacing, tool hotkeys, door categories,
/// default door/label properties. Persisted to settings.json next to the app.
public sealed class AppSettings
{
    // Grid is always 5ft (1 cell). This is how many cells between drawn grid
    // lines: 1 = every 5ft, 2 = every 10ft, etc.
    public int GridDrawSpacing = 1;

    // Tool hotkeys, keyed by ToolMode name, value = Key enum name (e.g. "R").
    public Dictionary<string, string> ToolHotkeys = new();

    // Door categories shown in the door dropdown, each with a color.
    public List<DoorType> DoorTypes = new();

    // New doors take these properties automatically (item 3).
    public string DefaultDoorType = "standard";
    public string DefaultDoorState = "closed";

    // New labels start with this text so a placed label is visible (item 1).
    public string DefaultLabelText = "label";

    public static AppSettings Default()
    {
        var s = new AppSettings();
        s.ToolHotkeys = new Dictionary<string, string>
        {
            ["Select"] = "S",
            ["Move"] = "M",
            ["Room"] = "R",
            ["Floor"] = "F",
            ["Wall"] = "W",
            ["Door"] = "D",
            ["Stair"] = "T",
            ["Label"] = "L",
            ["Erase"] = "E",
        };
        // no "locked" door type: locked is a door STATE, a type is redundant.
        s.DoorTypes = new List<DoorType>
        {
            new("standard", 245, 220, 60),
            new("portcullis", 180, 180, 190),
            new("bricked", 160, 100, 60),
            new("magical", 180, 120, 240),
        };
        return s;
    }

    public DoorType FindDoorType(string name)
        => DoorTypes.Find(t => t.Name == name) ?? DoorTypes[0];

    public string SettingsPath()
    {
        var dir = AppContext.BaseDirectory;
        return System.IO.Path.Combine(dir, "settings.json");
    }

    public void Save()
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        System.IO.File.WriteAllText(SettingsPath(), JsonSerializer.Serialize(this, opts));
    }

    public static AppSettings Load()
    {
        var path = new AppSettings().SettingsPath();
        if (!System.IO.File.Exists(path)) return Default();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(path));
            if (s is null) return Default();
            if (s.ToolHotkeys.Count == 0) s.ToolHotkeys = Default().ToolHotkeys;
            if (s.DoorTypes.Count == 0) s.DoorTypes = Default().DoorTypes;
            return s;
        }
        catch { return Default(); }
    }
}
