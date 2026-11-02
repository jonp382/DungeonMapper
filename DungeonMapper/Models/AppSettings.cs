using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DungeonMapper.Models;

/// A configurable door category: a name shown in the dropdown and an RGB color.
public sealed class DoorType
{
    [JsonPropertyName("name")] public string Name { get; set; } = "standard";
    [JsonPropertyName("r")] public int R { get; set; } = 245;
    [JsonPropertyName("g")] public int G { get; set; } = 220;
    [JsonPropertyName("b")] public int B { get; set; } = 60;

    public DoorType() { }
    public DoorType(string name, int r, int g, int b) { Name = name; R = r; G = g; B = b; }
}

/// A pawn status style: a name shown in the dropdown, an outer-border RGB color,
/// and a border thickness. "none" = no status border. Statuses are addable via the
/// UI like door types, so new effects (paralyzed, fear, ...) can be created.
public sealed class PawnStatus
{
    [JsonPropertyName("name")] public string Name { get; set; } = "none";
    [JsonPropertyName("r")] public int R { get; set; } = 255;
    [JsonPropertyName("g")] public int G { get; set; } = 255;
    [JsonPropertyName("b")] public int B { get; set; } = 255;
    [JsonPropertyName("thickness")] public int Thickness { get; set; } = 0;

    public PawnStatus() { }
    public PawnStatus(string name, int r, int g, int b, int thickness)
    { Name = name; R = r; G = g; B = b; Thickness = thickness; }
}

/// A reusable pawn template in the project's library. Identified by a
/// stable Id; map pawns reference it by Id (hard reference). Colors inherit from
/// the affiliation unless ColorOverride is on. Extra fields can be added over time.
public sealed class PawnTemplate
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("affiliation")] public string Affiliation { get; set; } = "Neutral";
    [JsonPropertyName("color_override")] public bool ColorOverride { get; set; } = false;
    [JsonPropertyName("inner_r")] public int InnerR { get; set; }
    [JsonPropertyName("inner_g")] public int InnerG { get; set; }
    [JsonPropertyName("inner_b")] public int InnerB { get; set; }
    [JsonPropertyName("outer_r")] public int OuterR { get; set; }
    [JsonPropertyName("outer_g")] public int OuterG { get; set; }
    [JsonPropertyName("outer_b")] public int OuterB { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "none";
    // optional image for unique pawns: base64 bytes carried on the template, so
    // the image travels inside the project file and import/export files. When
    // set, the image replaces the two-tone circles entirely, drawn square
    // (center-cropped) to fit the cell. ImageExt is the original extension
    // (".png"/".jpg"), kept for decoding and re-export.
    [JsonPropertyName("image_b64")] public string ImageB64 { get; set; } = "";
    [JsonPropertyName("image_ext")] public string ImageExt { get; set; } = "";

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
    [JsonPropertyName("name")] public string Name { get; set; } = "Neutral";
    [JsonPropertyName("outer_r")] public int OuterR { get; set; }
    [JsonPropertyName("outer_g")] public int OuterG { get; set; }
    [JsonPropertyName("outer_b")] public int OuterB { get; set; }
    [JsonPropertyName("inner_r")] public int InnerR { get; set; }
    [JsonPropertyName("inner_g")] public int InnerG { get; set; }
    [JsonPropertyName("inner_b")] public int InnerB { get; set; }

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
    [JsonPropertyName("grid_draw_spacing")] public int GridDrawSpacing { get; set; } = 1;

    // Gridline colors. Major lines sit at the GridDrawSpacing boundaries; minor
    // lines sit at every 5ft cell edge (system default). At 1x spacing (sp==1)
    // major and minor coincide, so only major is drawn. Defaults: major dark grey,
    // minor lighter grey, both visible against the flooring fill.
    [JsonPropertyName("major_grid_r")] public int MajorGridR { get; set; } = 30;
    [JsonPropertyName("major_grid_g")] public int MajorGridG { get; set; } = 30;
    [JsonPropertyName("major_grid_b")] public int MajorGridB { get; set; } = 35;
    [JsonPropertyName("minor_grid_r")] public int MinorGridR { get; set; } = 90;
    [JsonPropertyName("minor_grid_g")] public int MinorGridG { get; set; } = 90;
    [JsonPropertyName("minor_grid_b")] public int MinorGridB { get; set; } = 100;
    // Toggle: when false, minor gridlines (every 5ft edge) are not drawn at all,
    // so at spacing 2 you can show only the 10ft major lines.
    [JsonPropertyName("show_minor_grid")] public bool ShowMinorGrid { get; set; } = true;

    // Canvas element colors (settings menu). Defaults match the original hardcoded
    // brushes in MapCanvas. The old static GridLine/FaintGridLine brushes were dead
    // code (grid rendering uses the MajorGrid/MinorGrid settings colors), removed.
    [JsonPropertyName("background_r")] public int BackgroundR { get; set; } = 20;
    [JsonPropertyName("background_g")] public int BackgroundG { get; set; } = 20;
    [JsonPropertyName("background_b")] public int BackgroundB { get; set; } = 25;
    [JsonPropertyName("cell_fill_r")] public int CellFillR { get; set; } = 60;
    [JsonPropertyName("cell_fill_g")] public int CellFillG { get; set; } = 75;
    [JsonPropertyName("cell_fill_b")] public int CellFillB { get; set; } = 105;
    [JsonPropertyName("wall_r")] public int WallR { get; set; } = 235;
    [JsonPropertyName("wall_g")] public int WallG { get; set; } = 235;
    [JsonPropertyName("wall_b")] public int WallB { get; set; } = 240;
    // single stair color: all stairs (up/down/left/right) render in this color.
    // directional coloring was removed; default is the old down-stair orange.
    [JsonPropertyName("stair_color_r")] public int StairColorR { get; set; } = 230;
    [JsonPropertyName("stair_color_g")] public int StairColorG { get; set; } = 160;
    [JsonPropertyName("stair_color_b")] public int StairColorB { get; set; } = 80;
    [JsonPropertyName("label_r")] public int LabelR { get; set; } = 250;
    [JsonPropertyName("label_g")] public int LabelG { get; set; } = 250;
    [JsonPropertyName("label_b")] public int LabelB { get; set; } = 250;

    // Autosave interval in minutes; 0 = autosave off. Writes to
    // maps/autosaves/ with a standardized name (index + datetime stamp).
    [JsonPropertyName("autosave_minutes")] public int AutosaveMinutes { get; set; } = 0;

    // Tool hotkeys, keyed by ToolMode name, value = Key enum name (e.g. "R").
    [JsonPropertyName("tool_hotkeys")] public Dictionary<string, string> ToolHotkeys { get; set; } = new();

    // Door categories shown in the door dropdown, each with a color.
    [JsonPropertyName("door_types")] public List<DoorType> DoorTypes { get; set; } = new();

    // Pawn status styles shown in the pawn status dropdown, each with a border
    // color + thickness. "none" = no border. Addable via the UI like door types.
    [JsonPropertyName("pawn_statuses")] public List<PawnStatus> PawnStatuses { get; set; } = new();

    // Global affiliation coloring (Player/Ally/Enemy/Neutral). Templates inherit
    // these unless ColorOverride is on. Configurable in the pawn management menu.
    [JsonPropertyName("affiliation_colors")] public List<AffiliationColor> AffiliationColors { get; set; } = new();

    // New doors take these properties automatically (item 3).
    [JsonPropertyName("default_door_type")] public string DefaultDoorType { get; set; } = "standard";
    [JsonPropertyName("default_door_state")] public string DefaultDoorState { get; set; } = "closed";

    // New labels start with this text so a placed label is visible (item 1).
    [JsonPropertyName("default_label_text")] public string DefaultLabelText { get; set; } = "label";

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
            ["Polygon"] = "N",
            ["Line"] = "I",
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
        // settings live in a settings/ subfolder next to the exe.
        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "settings");
        return System.IO.Path.Combine(dir, "settings.json");
    }

    public void Save()
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        var path = SettingsPath();
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, JsonSerializer.Serialize(this, opts));
    }

    public static AppSettings Load()
    {
        var path = new AppSettings().SettingsPath();
        // one-time migration: a settings.json at the exe root (pre-subfolder
        // layout) is moved into settings/ so existing settings survive.
        var legacy = System.IO.Path.Combine(AppContext.BaseDirectory, "settings.json");
        if (!System.IO.File.Exists(path) && System.IO.File.Exists(legacy))
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.Move(legacy, path);
        }
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

/// The per-project pawn database, embedded in each project file.
/// A list of PawnTemplate entries. There is no global library: each project
/// file embeds its own library, and template edits persist with the project
/// file (Ctrl+S / autosave). Import/export files move libraries between projects.
public sealed class PawnLibrary
{
    [JsonPropertyName("pawns")] public List<PawnTemplate> Pawns { get; set; } = new();

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

    /// Appends imported templates: entries whose id is free keep their id,
    /// entries colliding with the live library get a fresh id (the import
    /// becomes a new entry; existing references are untouched). Returns the
    /// id remap (old id -> new id) so the caller can re-point pawn references.
    public Dictionary<string, string> MergeAppend(List<PawnTemplate> templates)
    {
        var map = new Dictionary<string, string>();
        foreach (var t in templates)
        {
            string oldId = t.Id;
            string newId = (!string.IsNullOrEmpty(oldId) && Find(oldId) is null) ? oldId : NewId();
            t.Id = newId;
            Pawns.Add(t);
            if (!string.IsNullOrEmpty(oldId)) map[oldId] = newId;
        }
        BumpNextId();
        return map;
    }

    /// Overwrites: deletes every current template, then appends the imported
    /// ones (fresh ids for collisions, same rule as append). Pawns pointing at
    /// deleted templates become broken references (X render) - the user accepted
    /// this for overwrite mode.
    public Dictionary<string, string> MergeOverwrite(List<PawnTemplate> templates)
    {
        Pawns.Clear();
        return MergeAppend(templates);
    }

    /// Id counter for fresh ids. Not persisted to disk (the project file stores
    /// template DTOs, not the counter); BumpNextId keeps it ahead of embedded ids.
    [JsonPropertyName("next_id")] public int NextId { get; private set; } = 1;
    // New ids skip ids already in use: a library whose counter lags behind its
    // live ids (legacy file, manual adds) must never re-issue a live id.
    public string NewId()
    {
        string id;
        do { id = $"p{NextId++}"; } while (Find(id) is not null);
        return id;
    }

    /// Keeps the id counter ahead of every existing template id, so a
    /// legacy library (ids p1..pN, no counter field) never re-issues a live id.
    public void BumpNextId()
    {
        foreach (var p in Pawns)
            if (p.Id.StartsWith("p") && int.TryParse(p.Id[1..], out int n) && n >= NextId) NextId = n + 1;
    }
}
