using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DungeonMapper.Models;

/// Door presets, pawn statuses and affiliation colors of one campaign. They
/// travel inside the campaign file (and a map file carries the ones its map
/// uses), so every campaign keeps its own. "standard", "none" and the four
/// fixed affiliations are always present.
public sealed class CampaignPresets
{
    [JsonPropertyName("door_types")] public List<DoorType> DoorTypes { get; set; } = new();
    [JsonPropertyName("pawn_statuses")] public List<PawnStatus> PawnStatuses { get; set; } = new();
    [JsonPropertyName("affiliation_colors")] public List<AffiliationColor> AffiliationColors { get; set; } = new();

    public const string StandardDoor = "standard";
    public const string NoStatus = "none";
    public static readonly string[] FixedAffiliations = ["Player", "Ally", "Enemy", "Neutral"];

    // export file kinds, one per list
    public const string KindDoors = "door_presets";
    public const string KindStatuses = "pawn_statuses";
    public const string KindAffiliations = "affiliations";

    public enum Part { Doors, Statuses, Affiliations }

    // the starters a new campaign gets
    public static List<DoorType> DefaultDoorTypes() =>
    [
        // no "locked" door type: locked is a door STATE, a type is redundant.
        new(StandardDoor, 245, 220, 60),
        new("portcullis", 180, 180, 190),
        new("bricked", 160, 100, 60),
        new("magical", 180, 120, 240),
    ];

    // "none" is the hard default (black, thickness 1, un-deletable)
    public static List<PawnStatus> DefaultPawnStatuses() =>
    [
        new(NoStatus, 0, 0, 0, 1),
        new("paralyzed", 230, 120, 200, 3),  // pink + thick
        new("fear", 245, 245, 245, 3),        // white + thick
    ];

    public static List<AffiliationColor> DefaultAffiliationColors() =>
    [
        new("Player", (60, 120, 220), (30, 60, 160)),
        new("Ally", (120, 220, 120), (30, 120, 40)),
        new("Enemy", (220, 120, 120), (120, 30, 30)),
        new("Neutral", (230, 190, 90), (150, 110, 40)),
    ];

    public static CampaignPresets Default() => new()
    {
        DoorTypes = DefaultDoorTypes(),
        PawnStatuses = DefaultPawnStatuses(),
        AffiliationColors = DefaultAffiliationColors(),
    };

    /// Puts a list read from a file in order: blank and repeated names are
    /// dropped (first one wins), numbers are put back in range and the fixed
    /// entries are added when missing.
    public CampaignPresets Normalize()
    {
        Tidy();
        if (!DoorTypes.Any(t => t.Name == StandardDoor)) DoorTypes.Insert(0, DefaultDoorTypes()[0]);
        if (!PawnStatuses.Any(s => s.Name == NoStatus)) PawnStatuses.Insert(0, DefaultPawnStatuses()[0]);
        var defs = DefaultAffiliationColors();
        for (int i = 0; i < FixedAffiliations.Length; i++)
            if (!AffiliationColors.Any(a => a.Name == FixedAffiliations[i]))
                AffiliationColors.Insert(Math.Min(i, AffiliationColors.Count), defs[i]);
        return this;
    }

    /// Drops blank and repeated names (first one wins) and puts numbers back
    /// in range. Adds nothing.
    public CampaignPresets Tidy()
    {
        DoorTypes = Clean(DoorTypes, t => t.Name, t => { t.R = C(t.R); t.G = C(t.G); t.B = C(t.B); });
        PawnStatuses = Clean(PawnStatuses, s => s.Name, s => { s.R = C(s.R); s.G = C(s.G); s.B = C(s.B); s.Thickness = Math.Clamp(s.Thickness, 0, MaxThickness); });
        AffiliationColors = Clean(AffiliationColors, a => a.Name, a =>
        {
            a.OuterR = C(a.OuterR); a.OuterG = C(a.OuterG); a.OuterB = C(a.OuterB);
            a.InnerR = C(a.InnerR); a.InnerG = C(a.InnerG); a.InnerB = C(a.InnerB);
        });
        return this;
    }

    public const int MaxThickness = 20;
    private static int C(int v) => Math.Clamp(v, 0, 255);

    private static List<T> Clean<T>(List<T>? items, Func<T, string> name, Action<T> fix) where T : class
    {
        var seen = new HashSet<string>();
        var list = new List<T>();
        foreach (var t in items ?? new List<T>())
        {
            if (t is null || string.IsNullOrWhiteSpace(name(t)) || !seen.Add(name(t))) continue;
            fix(t);
            list.Add(t);
        }
        return list;
    }

    public DoorType FindDoorType(string name)
        => DoorTypes.Find(t => t.Name == name) ?? DoorTypes.Find(t => t.Name == StandardDoor) ?? DefaultDoorTypes()[0];

    public PawnStatus FindPawnStatus(string name)
        => PawnStatuses.Find(t => t.Name == name) ?? PawnStatuses.Find(t => t.Name == NoStatus) ?? DefaultPawnStatuses()[0];

    public AffiliationColor FindAffiliationColor(string name)
        => AffiliationColors.Find(c => c.Name == name)
           ?? AffiliationColors.Find(c => c.Name == "Neutral")
           ?? DefaultAffiliationColors()[3];

    public CampaignPresets Clone() => new()
    {
        DoorTypes = DoorTypes.Select(Copy).ToList(),
        PawnStatuses = PawnStatuses.Select(Copy).ToList(),
        AffiliationColors = AffiliationColors.Select(Copy).ToList(),
    };

    public static DoorType Copy(DoorType t) => new(t.Name, t.R, t.G, t.B);
    public static PawnStatus Copy(PawnStatus s) => new(s.Name, s.R, s.G, s.B, s.Thickness);
    public static AffiliationColor Copy(AffiliationColor a) => new(a.Name, (a.OuterR, a.OuterG, a.OuterB), (a.InnerR, a.InnerG, a.InnerB));

    public static bool SameLook(DoorType a, DoorType b) => a.Name == b.Name && a.R == b.R && a.G == b.G && a.B == b.B;
    public static bool SameLook(PawnStatus a, PawnStatus b) => a.Name == b.Name && a.R == b.R && a.G == b.G && a.B == b.B && a.Thickness == b.Thickness;
    public static bool SameLook(AffiliationColor a, AffiliationColor b) =>
        a.Name == b.Name && a.OuterR == b.OuterR && a.OuterG == b.OuterG && a.OuterB == b.OuterB
        && a.InnerR == b.InnerR && a.InnerG == b.InnerG && a.InnerB == b.InnerB;

    // import picker: have = same name and look, new look = the name is taken
    // by a different look (skipped, never overwritten), new = name is free
    public PawnLibrary.ImportKind Classify(DoorType t) => Classify(DoorTypes, t, x => x.Name, SameLook);
    public PawnLibrary.ImportKind Classify(PawnStatus s) => Classify(PawnStatuses, s, x => x.Name, SameLook);
    public PawnLibrary.ImportKind Classify(AffiliationColor a) => Classify(AffiliationColors, a, x => x.Name, SameLook);

    private static PawnLibrary.ImportKind Classify<T>(List<T> have, T t, Func<T, string> name, Func<T, T, bool> same)
    {
        var hit = have.Find(x => name(x) == name(t));
        if (hit is null) return PawnLibrary.ImportKind.New;
        return same(hit, t) ? PawnLibrary.ImportKind.Have : PawnLibrary.ImportKind.NewLook;
    }

    /// Adds every entry of other whose name this campaign does not have yet.
    /// Names already here keep their look. Returns how many were added.
    public int MergeMissing(CampaignPresets other)
        => AddMissing(DoorTypes, other.DoorTypes, t => t.Name, Copy)
         + AddMissing(PawnStatuses, other.PawnStatuses, s => s.Name, Copy)
         + AddMissing(AffiliationColors, other.AffiliationColors, a => a.Name, Copy);

    public int AddMissing(IEnumerable<DoorType> items) => AddMissing(DoorTypes, items, t => t.Name, Copy);
    public int AddMissing(IEnumerable<PawnStatus> items) => AddMissing(PawnStatuses, items, s => s.Name, Copy);
    public int AddMissing(IEnumerable<AffiliationColor> items) => AddMissing(AffiliationColors, items, a => a.Name, Copy);

    private static int AddMissing<T>(List<T> into, IEnumerable<T> items, Func<T, string> name, Func<T, T> copy)
    {
        int n = 0;
        foreach (var t in items)
        {
            if (string.IsNullOrWhiteSpace(name(t)) || into.Exists(x => name(x) == name(t))) continue;
            into.Add(copy(t));
            n++;
        }
        return n;
    }

    /// The entries the given maps use, in campaign order: door types of their
    /// doors, statuses of their pawns and of the templates those pawns use,
    /// and the affiliations of those templates. A map file carries these.
    public CampaignPresets UsedBy(IEnumerable<MapDocument> docs, PawnLibrary? library)
    {
        var doors = new HashSet<string>();
        var statuses = new HashSet<string>();
        var affs = new HashSet<string>();
        foreach (var doc in docs)
        {
            foreach (var d in doc.AllDoors()) doors.Add(d.Type);
            foreach (var p in doc.Pawns.Values)
            {
                if (!string.IsNullOrEmpty(p.Status)) statuses.Add(p.Status);
                if (library?.Find(p.TemplateId) is { } t)
                {
                    affs.Add(PawnTemplate.NormalizeAffiliation(t.Affiliation));
                }
            }
        }
        return new CampaignPresets
        {
            DoorTypes = DoorTypes.Where(t => doors.Contains(t.Name)).Select(Copy).ToList(),
            PawnStatuses = PawnStatuses.Where(s => statuses.Contains(s.Name)).Select(Copy).ToList(),
            AffiliationColors = AffiliationColors.Where(a => affs.Contains(a.Name)).Select(Copy).ToList(),
        };
    }

    /// A campaign opened from its file. A list the file has is used as it
    /// is, a list it lacks (files from before presets moved into the
    /// campaign) starts from the built-in starters.
    public static CampaignPresets ForCampaign(PresetsWire? w) => new CampaignPresets
    {
        // an empty list is treated as missing: a saved campaign never has one
        DoorTypes = w?.DoorTypes is { Count: > 0 } d ? d : DefaultDoorTypes(),
        PawnStatuses = w?.PawnStatuses is { Count: > 0 } st ? st : DefaultPawnStatuses(),
        AffiliationColors = w?.AffiliationColors is { Count: > 0 } a ? a : DefaultAffiliationColors(),
    }.Normalize();

    /// A new campaign opened from a map file: the map's own entries first,
    /// then the built-in starters it does not already have.
    public static CampaignPresets ForMapFile(PresetsWire? w)
    {
        var p = new CampaignPresets
        {
            DoorTypes = w?.DoorTypes ?? new(),
            PawnStatuses = w?.PawnStatuses ?? new(),
            AffiliationColors = w?.AffiliationColors ?? new(),
        }.Normalize();
        p.MergeMissing(Default());
        return p;
    }

    /// The lists as they sit in a file, each null when the file lacks it.
    public PresetsWire ToWire(string? kind = null) => new()
    {
        Kind = kind,
        DoorTypes = DoorTypes.Select(Copy).ToList(),
        PawnStatuses = PawnStatuses.Select(Copy).ToList(),
        AffiliationColors = AffiliationColors.Select(Copy).ToList(),
    };

    /// Reads the preset lists from any json that holds them: a campaign, a
    /// map file, a presets export or an old settings.json. Lists the file
    /// lacks are null. Throws on json that does not parse.
    public static PresetsWire ReadWire(string json)
    {
        using (var doc = JsonDocument.Parse(json))
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return new PresetsWire();
        return JsonSerializer.Deserialize(json, DmJson.Default.PresetsWire) ?? new PresetsWire();
    }

    /// One list as its own export file. The other lists are left out.
    public string ExportJson(Part part)
    {
        var w = new PresetsWire();
        switch (part)
        {
            case Part.Doors: w.Kind = KindDoors; w.DoorTypes = DoorTypes.Select(Copy).ToList(); break;
            case Part.Statuses: w.Kind = KindStatuses; w.PawnStatuses = PawnStatuses.Select(Copy).ToList(); break;
            default: w.Kind = KindAffiliations; w.AffiliationColors = AffiliationColors.Select(Copy).ToList(); break;
        }
        return JsonSerializer.Serialize(w, DmJson.Default.PresetsWire);
    }

    /// The entries of a presets file for the import picker, tidied the way a
    /// loaded campaign is but without adding the fixed entries. Lists the
    /// file lacks come back empty. Throws on json that does not parse.
    public static CampaignPresets ReadImport(string json) => FromWire(ReadWire(json));

    /// The lists a file carried, tidied, without adding the fixed entries.
    public static CampaignPresets FromWire(PresetsWire w)
    {
        return new CampaignPresets
        {
            DoorTypes = w.DoorTypes ?? new(),
            PawnStatuses = w.PawnStatuses ?? new(),
            AffiliationColors = w.AffiliationColors ?? new(),
        }.Tidy();
    }
}

/// The preset lists on the wire. Each list is written only when set, so a
/// presets export holds just its own list and older readers ignore them.
public sealed class PresetsWire
{
    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }

    [JsonPropertyName("door_types")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DoorType>? DoorTypes { get; set; }

    [JsonPropertyName("pawn_statuses")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PawnStatus>? PawnStatuses { get; set; }

    [JsonPropertyName("affiliation_colors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AffiliationColor>? AffiliationColors { get; set; }
}
