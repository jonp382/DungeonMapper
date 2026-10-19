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

/// A pawn status style: a name shown in the dropdown, an outer-border RGB color,
/// and a border thickness. "none" = no status border. Statuses are addable via the
/// UI like door types, so new effects (paralyzed, fear, ...) can be created.
public sealed class PawnStatus
{
    public string Name = "none";
    public int R = 255, G = 255, B = 255;
    public int Thickness = 0;

    public PawnStatus() { }
    public PawnStatus(string name, int r, int g, int b, int thickness)
    { Name = name; R = r; G = g; B = b; Thickness = thickness; }
}

/// A reusable pawn template stored in a separate JSON database (pawn_library.json),
/// so named pawns ("Thorin") and generic ones ("Skeleton") can be placed repeatedly.
public sealed class PawnTemplate
{
    public string Name = "";
    public string Class = "Neutral";
    public int InnerR, InnerG, InnerB;
    public int OuterR, OuterG, OuterB;
    public string Status = "none";
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

    // Pawn status styles shown in the pawn status dropdown, each with a border
    // color + thickness. "none" = no border. Addable via the UI like door types.
    public List<PawnStatus> PawnStatuses = new();

    // New doors take these properties automatically (item 3).
    public string DefaultDoorType = "standard";
    public string DefaultDoorState = "closed";

    // New labels start with this text so a placed label is visible (item 1).
    public string DefaultLabelText = "label";

    // New pawns take these properties automatically.
    public string DefaultPawnClass = "Neutral";
    public string DefaultPawnName = "";
    public string DefaultPawnStatus = "none";
    // "" = use class defaults; "r,g,b" = explicit color for the next placed pawn.
    public string DefaultPawnInner = "";
    public string DefaultPawnOuter = "";

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
            ["Pawn"] = "P",
        };
        // no "locked" door type: locked is a door STATE, a type is redundant.
        s.DoorTypes = new List<DoorType>
        {
            new("standard", 245, 220, 60),
            new("portcullis", 180, 180, 190),
            new("bricked", 160, 100, 60),
            new("magical", 180, 120, 240),
        };
        // status scaffolding: a few starter styles, addable via the UI.
        s.PawnStatuses = new List<PawnStatus>
        {
            new("none", 255, 255, 255, 0),
            new("paralyzed", 230, 120, 200, 3),  // pink + thick
            new("fear", 245, 245, 245, 3),        // white + thick
        };
        return s;
    }

    public DoorType FindDoorType(string name)
        => DoorTypes.Find(t => t.Name == name) ?? DoorTypes[0];

    public PawnStatus FindPawnStatus(string name)
        => PawnStatuses.Find(t => t.Name == name) ?? PawnStatuses[0];

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
            // settings.json written before a hotkey existed: fill in missing keys
            foreach (var (k, v) in Default().ToolHotkeys)
                if (!s.ToolHotkeys.ContainsKey(k)) s.ToolHotkeys[k] = v;
            if (s.DoorTypes.Count == 0) s.DoorTypes = Default().DoorTypes;
            if (s.PawnStatuses.Count == 0) s.PawnStatuses = Default().PawnStatuses;
            return s;
        }
        catch { return Default(); }
    }
}

/// The reusable pawn database, stored in pawn_library.json next to the app.
/// A list of PawnTemplate entries; new pawns added via the UI persist here for reuse.
public sealed class PawnLibrary
{
    public List<PawnTemplate> Pawns = new();

    public string LibraryPath()
    {
        var dir = AppContext.BaseDirectory;
        return System.IO.Path.Combine(dir, "pawn_library.json");
    }

    public void Save()
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        System.IO.File.WriteAllText(LibraryPath(), JsonSerializer.Serialize(this, opts));
    }

    public static PawnLibrary Load()
    {
        var path = new PawnLibrary().LibraryPath();
        if (!System.IO.File.Exists(path)) return new PawnLibrary();
        try
        {
            var l = JsonSerializer.Deserialize<PawnLibrary>(System.IO.File.ReadAllText(path));
            return l ?? new PawnLibrary();
        }
        catch { return new PawnLibrary(); }
    }

    public PawnTemplate? Find(string name) => Pawns.Find(p => p.Name == name);

    /// Adds a template, replacing any existing entry with the same name.
    public void Add(PawnTemplate t)
    {
        var existing = Pawns.Find(p => p.Name == t.Name);
        if (existing is not null) Pawns[Pawns.IndexOf(existing)] = t;
        else Pawns.Add(t);
    }
}
