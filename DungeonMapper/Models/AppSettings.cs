using System;
using System.Collections.Generic;
using System.Linq;
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

/// A reusable pawn template in the library (pawn_library.json). Identified by a
/// stable Id; map pawns reference it by Id (hard reference). Colors inherit from
/// the affiliation unless ColorOverride is on. Extra fields can be added over time.
public sealed class PawnTemplate
{
    public string Id = "";
    public string Name = "";
    public string Affiliation = "Neutral";
    public bool ColorOverride = false;
    public int InnerR, InnerG, InnerB;
    public int OuterR, OuterG, OuterB;
    public string Status = "none";

    /// Affiliation default colors: outer bright, inner dark accent.
    public static ((int r, int g, int b) outer, (int r, int g, int b) inner) AffiliationColors(string aff) => aff switch
    {
        "Player"  => ((60, 120, 220), (30, 60, 160)),   // blue outer, dark blue inner
        "Ally"    => ((120, 220, 120), (30, 120, 40)),  // light green outer, dark green inner
        "Enemy"   => ((220, 120, 120), (120, 30, 30)),  // light red outer, dark red inner
        _ => ((230, 190, 90), (150, 110, 40)),          // yellow/orange outer, darker inner
    };

    /// Normalizes legacy affiliation names (Friendly->Ally, Hostile->Enemy).
    public static string NormalizeAffiliation(string a) => a switch
    {
        "Friendly" => "Ally",
        "Hostile" => "Enemy",
        _ => a,
    };

    public void ApplyAffiliationDefaults()
    {
        var (o, i) = AffiliationColors(NormalizeAffiliation(Affiliation));
        OuterR = o.r; OuterG = o.g; OuterB = o.b;
        InnerR = i.r; InnerG = i.g; InnerB = i.b;
    }

    /// Effective colors for rendering: when ColorOverride is off, resolve from the
    /// GLOBAL affiliation color table so editing a global affiliation propagates to
    /// every inheriting template at render time. When on, use the template's own
    /// stored colors.
    public ((int r, int g, int b) outer, (int r, int g, int b) inner) Effective(AppSettings s)
    {
        if (!ColorOverride)
        {
            var ac = s.FindAffiliationColor(Affiliation);
            return ((ac.OuterR, ac.OuterG, ac.OuterB), (ac.InnerR, ac.InnerG, ac.InnerB));
        }
        return ((OuterR, OuterG, OuterB), (InnerR, InnerG, InnerB));
    }
}

/// Global affiliation coloring: outer + inner color per affiliation name.
/// Configurable in the pawn management menu; templates inherit these unless
/// they have ColorOverride on.
public sealed class AffiliationColor
{
    public string Name = "Neutral";
    public int OuterR, OuterG, OuterB;
    public int InnerR, InnerG, InnerB;

    public AffiliationColor() { }
    public AffiliationColor(string name, (int r, int g, int b) outer, (int r, int g, int b) inner)
    {
        Name = name; OuterR = outer.r; OuterG = outer.g; OuterB = outer.b;
        InnerR = inner.r; InnerG = inner.g; InnerB = inner.b;
    }
}

/// App-wide settings: grid draw spacing, tool hotkeys, door categories,
/// default door/label properties. Persisted to settings.json next to the app.
public sealed class AppSettings
{
    // Grid is always 5ft (1 cell). This is how many cells between drawn grid
    // lines: 1 = every 5ft, 2 = every 10ft, etc. This is the MAJOR spacing.
    public int GridDrawSpacing = 1;

    // Gridline colors. Major lines sit at the GridDrawSpacing boundaries; minor
    // lines sit at every 5ft cell edge (system default). At 1x spacing (sp==1)
    // major and minor coincide, so only major is drawn. Defaults: major dark grey,
    // minor lighter grey, both visible against the flooring fill.
    public int MajorGridR = 30, MajorGridG = 30, MajorGridB = 35;
    public int MinorGridR = 90, MinorGridG = 90, MinorGridB = 100;
    // Toggle: when false, minor gridlines (every 5ft edge) are not drawn at all,
    // so at spacing 2 you can show only the 10ft major lines.
    public bool ShowMinorGrid = true;

    // Tool hotkeys, keyed by ToolMode name, value = Key enum name (e.g. "R").
    public Dictionary<string, string> ToolHotkeys = new();

    // Door categories shown in the door dropdown, each with a color.
    public List<DoorType> DoorTypes = new();

    // Pawn status styles shown in the pawn status dropdown, each with a border
    // color + thickness. "none" = no border. Addable via the UI like door types.
    public List<PawnStatus> PawnStatuses = new();

    // Global affiliation coloring (Player/Ally/Enemy/Neutral). Templates inherit
    // these unless ColorOverride is on. Configurable in the pawn management menu.
    public List<AffiliationColor> AffiliationColors = new();

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
        // status scaffolding: "none" is the hard default (black, thickness 1,
        // un-deletable), plus a few starter styles, addable via the UI.
        s.PawnStatuses = new List<PawnStatus>
        {
            new("none", 0, 0, 0, 1),
            new("paralyzed", 230, 120, 200, 3),  // pink + thick
            new("fear", 245, 245, 245, 3),        // white + thick
        };
        s.AffiliationColors = new List<AffiliationColor>
        {
            new("Player", (60, 120, 220), (30, 60, 160)),
            new("Ally", (120, 220, 120), (30, 120, 40)),
            new("Enemy", (220, 120, 120), (120, 30, 30)),
            new("Neutral", (230, 190, 90), (150, 110, 40)),
        };
        return s;
    }

    public DoorType FindDoorType(string name)
        => DoorTypes.Find(t => t.Name == name) ?? DoorTypes[0];

    public PawnStatus FindPawnStatus(string name)
        => PawnStatuses.Find(t => t.Name == name) ?? PawnStatuses[0];

    public AffiliationColor FindAffiliationColor(string name)
        => AffiliationColors.Find(c => c.Name == name)
           ?? AffiliationColors.Find(c => c.Name == "Neutral")
           ?? new AffiliationColor("Neutral", (230, 190, 90), (150, 110, 40));

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
            // "none" is the hard default status (black, thickness 1, un-deletable):
            // guarantee it is present even in settings.json written before it existed.
            if (!s.PawnStatuses.Any(st => st.Name == "none"))
                s.PawnStatuses.Insert(0, new PawnStatus("none", 0, 0, 0, 1));
            if (s.AffiliationColors.Count == 0) s.AffiliationColors = Default().AffiliationColors;
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

    public PawnTemplate? Find(string id) => Pawns.Find(p => p.Id == id);

    /// Adds a template, assigning a fresh Id if it has none, and replacing any
    /// existing entry with the same Id.
    public void Add(PawnTemplate t)
    {
        if (string.IsNullOrEmpty(t.Id)) t.Id = NewId();
        var existing = Pawns.Find(p => p.Id == t.Id);
        if (existing is not null) Pawns[Pawns.IndexOf(existing)] = t;
        else Pawns.Add(t);
    }

    public void Remove(string id) => Pawns.RemoveAll(p => p.Id == id);

    /// Ids are stable across sessions: a counter persisted with the library.
    private int _nextId = 1;
    public string NewId() => $"p{_nextId++}";

    /// Backfills Ids for templates loaded from a legacy library (no Id field).
    public void EnsureIds()
    {
        foreach (var p in Pawns)
            if (string.IsNullOrEmpty(p.Id)) p.Id = NewId();
    }
}
