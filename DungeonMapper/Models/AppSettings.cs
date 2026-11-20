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
    // starred in the pawn panel: favorites get their own group at the top
    [JsonPropertyName("favorite")] public bool Favorite { get; set; }
    // a hero is one of a kind across the campaign: placing it again moves the
    // existing copy instead of adding a second one
    [JsonPropertyName("hero")] public bool Hero { get; set; }

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
    /// campaign's affiliation color table so editing an affiliation propagates to
    /// every inheriting template at render time. When on, use the template's own
    /// stored colors.
    public ((int r, int g, int b) outer, (int r, int g, int b) inner) Effective(CampaignPresets s)
    {
        if (!ColorOverride)
        {
            var ac = s.FindAffiliationColor(Affiliation);
            return ((ac.OuterR, ac.OuterG, ac.OuterB), (ac.InnerR, ac.InnerG, ac.InnerB));
        }
        return ((OuterR, OuterG, OuterB), (InnerR, InnerG, InnerB));
    }
}

/// Affiliation coloring of a campaign: outer + inner color per affiliation name.
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

/// App-wide settings: grid draw spacing, colors, tool hotkeys, default label
/// text. Persisted to settings.json next to the app. Door presets, pawn
/// statuses and affiliation colors belong to the campaign (CampaignPresets).
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
    // opacity of the major and minor lines drawn inside floored regions, in
    // percent. 0 hides them, 100 is the full line color.
    [JsonPropertyName("floor_grid_opacity")] public int FloorGridOpacity { get; set; } = 100;
    // empty space outside rooms shows a dot on each grid vertex instead of
    // lines. one color for both, major dots are drawn a little bigger.
    [JsonPropertyName("grid_dot_r")] public int GridDotR { get; set; } = 105;
    [JsonPropertyName("grid_dot_g")] public int GridDotG { get; set; } = 105;
    [JsonPropertyName("grid_dot_b")] public int GridDotB { get; set; } = 115;

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
    // wall line thickness in screen pixels, the same for every wall. render only,
    // walls do not store it.
    [JsonPropertyName("wall_thickness")] public int WallThickness { get; set; } = 3;
    public const int MinWallThickness = 1, MaxWallThickness = 20;
    // extra wall cleared past each end of a door glyph, in percent of a cell
    // edge (0 to 50, 50 = half a wall length). shows floor color so rooms read
    // as connected. 0 = the gap is just the glyph.
    [JsonPropertyName("door_wall_gap")] public int DoorWallGap { get; set; } = 0;
    public const int MaxDoorWallGap = 50;
    public const int MaxGridSpacing = 100, MaxAutosaveMinutes = 999, MaxFloorGridOpacity = 100;
    // single stair color: all stairs (up/down/left/right) render in this color.
    // directional coloring was removed; default is the old down-stair orange.
    [JsonPropertyName("stair_color_r")] public int StairColorR { get; set; } = 230;
    [JsonPropertyName("stair_color_g")] public int StairColorG { get; set; } = 160;
    [JsonPropertyName("stair_color_b")] public int StairColorB { get; set; } = 80;
    [JsonPropertyName("label_r")] public int LabelR { get; set; } = 250;
    [JsonPropertyName("label_g")] public int LabelG { get; set; } = 250;
    [JsonPropertyName("label_b")] public int LabelB { get; set; } = 250;
    // measure tool (range finder): line, circle and readout color, and how
    // strongly the cells caught by a radius are shaded (percent, 0 to 100)
    [JsonPropertyName("measure_r")] public int MeasureR { get; set; } = 255;
    [JsonPropertyName("measure_g")] public int MeasureG { get; set; } = 200;
    [JsonPropertyName("measure_b")] public int MeasureB { get; set; } = 40;
    [JsonPropertyName("measure_shade_opacity")] public int MeasureShadeOpacity { get; set; } = 35;
    public const int MaxMeasureShadeOpacity = 100;
    // fog tool: the dim over everything outside the vision radius (percent, 0 to 100)
    [JsonPropertyName("fog_r")] public int FogR { get; set; } = 0;
    [JsonPropertyName("fog_g")] public int FogG { get; set; } = 0;
    [JsonPropertyName("fog_b")] public int FogB { get; set; } = 0;
    [JsonPropertyName("fog_opacity")] public int FogOpacity { get; set; } = 30;
    public const int MaxFogOpacity = 100;

    // Autosave interval in minutes; 0 = autosave off. Writes to
    // maps/autosaves/ with a standardized name (index + datetime stamp).
    [JsonPropertyName("autosave_minutes")] public int AutosaveMinutes { get; set; } = 0;

    // Tool hotkeys, keyed by ToolMode name, value = Key enum name (e.g. "R").
    // FitHotkey is the one non tool entry (zoom to fit).
    public const string FitHotkey = "Fit";
    // also not a tool: deploys the last formation used (Models/Formations.cs)
    public const string FormationHotkey = "Formation";
    [JsonPropertyName("tool_hotkeys")] public Dictionary<string, string> ToolHotkeys { get; set; } = new();

    // New labels start with this text so a placed label is visible (item 1).
    [JsonPropertyName("default_label_text")] public string DefaultLabelText { get; set; } = "label";

    // pawn panel: draw each pawn's name in a pill under it on the map
    [JsonPropertyName("show_pawn_names")] public bool ShowPawnNames { get; set; } = false;

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
            ["Object"] = "O",
            // not a tool: zoom to fit. same table so it gets the hotkeys menu row
            ["Measure"] = "G",
            ["Fog"] = "V",
            [FitHotkey] = "Z",
            [FormationHotkey] = "I",
        };
        return s;
    }

    public string SettingsPath()
    {
        // settings live in a settings/ subfolder next to the exe.
        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "settings");
        return System.IO.Path.Combine(dir, "settings.json");
    }

    public void Save()
    {
        var path = SettingsPath();
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, ToJson());
    }

    public string ToJson() => JsonSerializer.Serialize(this, DmJson.Default.AppSettings);

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
        try { return Parse(System.IO.File.ReadAllText(path)) ?? Default(); }
        catch { return Default(); }
    }

    // the door presets, pawn statuses and affiliation colors an older
    // settings.json still holds, from before they moved into the campaign.
    // null when it has none of them.
    public static PresetsWire? LegacyPresets()
    {
        try
        {
            var path = new AppSettings().SettingsPath();
            if (!System.IO.File.Exists(path)) return null;
            var w = CampaignPresets.ReadWire(System.IO.File.ReadAllText(path));
            if (w.DoorTypes is null && w.PawnStatuses is null && w.AffiliationColors is null) return null;
            w.Kind = null;
            return w;
        }
        catch { return null; }
    }

    // reads settings json and fills in anything an older or hand edited file
    // is missing. returns null when the json is not a settings file at all
    // (no settings keys at its top level), so a map or campaign file picked
    // by mistake is refused instead of loading as all defaults. throws on
    // json that does not parse.
    public static AppSettings? Parse(string json)
    {
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var known = JsonKeys().ToHashSet();
            if (!doc.RootElement.EnumerateObject().Any(p => known.Contains(p.Name))) return null;
        }
        var s = JsonSerializer.Deserialize(json, DmJson.Default.AppSettings);
        if (s is null) return null;
        if (s.ToolHotkeys.Count == 0) s.ToolHotkeys = Default().ToolHotkeys;
        // settings.json written before a hotkey existed: fill in missing keys
        foreach (var (k, v) in Default().ToolHotkeys)
            if (!s.ToolHotkeys.ContainsKey(k)) s.ToolHotkeys[k] = v;
        return s;
    }

    // ---- settings import/export ----
    // settings files are app wide. door presets, pawn statuses and affiliation
    // colors belong to the campaign, so they never go into or come out of one.
    // an older settings file still holding them is read, the lists ignored.
    public const string KindSettings = "settings";

    // every json key AppSettings writes, from the source generated metadata
    // (no reflection, survives trimming)
    private static IEnumerable<string> JsonKeys()
        => DmJson.Default.AppSettings.Properties.Select(p => p.Name);

    public string ExportJson()
    {
        var node = JsonSerializer.SerializeToNode(this, DmJson.Default.AppSettings)!.AsObject();
        var outNode = new System.Text.Json.Nodes.JsonObject { ["kind"] = KindSettings };
        foreach (var (k, v) in node.ToList())
        {
            node.Remove(k);
            outNode[k] = v;
        }
        return outNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // reads a settings export. returns null when the file is not one: a
    // "kind" other than settings (campaign, map, ...), or no app wide setting
    // at its top level. throws on json that does not parse. numbers and
    // hotkeys a hand edited file could break are put back in range.
    public static AppSettings? ParseImport(string json)
    {
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("kind", out var k)
                && !(k.ValueKind == JsonValueKind.String && string.Equals(k.GetString(), KindSettings, StringComparison.OrdinalIgnoreCase)))
                return null;
            var appWide = JsonKeys().ToHashSet();
            if (!root.EnumerateObject().Any(p => appWide.Contains(p.Name))) return null;
        }
        var s = Parse(json);
        if (s is null) return null;
        var defaults = Default();
        s.GridDrawSpacing = Math.Clamp(s.GridDrawSpacing, 1, MaxGridSpacing);
        s.WallThickness = Math.Clamp(s.WallThickness, MinWallThickness, MaxWallThickness);
        s.AutosaveMinutes = Math.Clamp(s.AutosaveMinutes, 0, MaxAutosaveMinutes);
        s.FloorGridOpacity = Math.Clamp(s.FloorGridOpacity, 0, MaxFloorGridOpacity);
        s.DoorWallGap = Math.Clamp(s.DoorWallGap, 0, MaxDoorWallGap);
        s.MeasureShadeOpacity = Math.Clamp(s.MeasureShadeOpacity, 0, MaxMeasureShadeOpacity);
        s.FogOpacity = Math.Clamp(s.FogOpacity, 0, MaxFogOpacity);
        if (string.IsNullOrWhiteSpace(s.DefaultLabelText)) s.DefaultLabelText = defaults.DefaultLabelText;
        // hotkeys: only known tools, each a single letter a to z (what the
        // hotkeys menu allows); anything else falls back to the default
        var keys = new Dictionary<string, string>();
        foreach (var (tool, def) in defaults.ToolHotkeys)
        {
            var v = s.ToolHotkeys.GetValueOrDefault(tool, "");
            keys[tool] = v.Length == 1 && char.IsAsciiLetter(v[0]) ? v.ToUpperInvariant() : def;
        }
        s.ToolHotkeys = keys;
        return s;
    }

    // takes the app wide values from an imported settings object. the app
    // keeps one AppSettings that the canvas and panels hold on to, so an
    // import copies into it instead of swapping it out.
    public void CopyAppWideFrom(AppSettings other)
    {
        foreach (var p in DmJson.Default.AppSettings.Properties)
            if (p.Get is not null && p.Set is not null)
                p.Set(this, p.Get(other));
        ToolHotkeys = new Dictionary<string, string>(other.ToolHotkeys);
    }
}

/// The per-project pawn database, embedded in each project file.
/// A list of PawnTemplate entries. There is no global library: each project
/// file embeds its own library, and template edits persist with the project
/// file (Ctrl+S / autosave). Import/export files move libraries between projects.
public sealed class PawnLibrary
{
    [JsonPropertyName("pawns")] public List<PawnTemplate> Pawns { get; set; } = new();
    // saved formations of this campaign's pawns (Models/Formations.cs). they
    // travel in the campaign file, not in pawn library export files
    [JsonIgnore] public List<Formation> Formations { get; set; } = new();

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

    /// Map import: a template identical to one already in the library (same
    /// name, look, status and image) is reused instead of copied, so exporting
    /// a map and importing it back does not duplicate its pawns. Anything else
    /// is appended like MergeAppend. Returns the id remap.
    public Dictionary<string, string> MergeImport(List<PawnTemplate> templates)
    {
        var map = new Dictionary<string, string>();
        var fresh = new List<PawnTemplate>();
        foreach (var t in templates)
        {
            var same = Pawns.Find(p => SameLook(p, t));
            if (same is not null) { if (!string.IsNullOrEmpty(t.Id)) map[t.Id] = same.Id; }
            else fresh.Add(t);
        }
        foreach (var (k, v) in MergeAppend(fresh)) map[k] = v;
        return map;
    }

    /// How an incoming template compares to the library, for the import picker.
    public enum ImportKind { New, Have, NewLook }

    public ImportKind Classify(PawnTemplate t)
    {
        if (Pawns.Exists(p => SameLook(p, t))) return ImportKind.Have;
        if (Pawns.Exists(p => string.Equals(p.Name, t.Name, StringComparison.OrdinalIgnoreCase))) return ImportKind.NewLook;
        return ImportKind.New;
    }

    /// A copy of the chosen templates as their own library, for exporting a
    /// selection. Keeps library order.
    public PawnLibrary Subset(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids);
        var lib = new PawnLibrary();
        foreach (var p in Pawns) if (set.Contains(p.Id)) lib.Pawns.Add(p);
        lib.BumpNextId();
        return lib;
    }

    /// Recently placed template ids, newest first. Session only, not saved.
    [JsonIgnore] public List<string> Recent { get; } = new();
    public const int RecentMax = 5;

    public void NoteRecent(string id)
    {
        Recent.Remove(id);
        Recent.Insert(0, id);
        if (Recent.Count > RecentMax) Recent.RemoveRange(RecentMax, Recent.Count - RecentMax);
    }

    private static bool SameLook(PawnTemplate a, PawnTemplate b) =>
        a.Name == b.Name && a.Affiliation == b.Affiliation && a.ColorOverride == b.ColorOverride
        && a.InnerR == b.InnerR && a.InnerG == b.InnerG && a.InnerB == b.InnerB
        && a.OuterR == b.OuterR && a.OuterG == b.OuterG && a.OuterB == b.OuterB
        && a.Status == b.Status && a.ImageB64 == b.ImageB64 && a.ImageExt == b.ImageExt;

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
