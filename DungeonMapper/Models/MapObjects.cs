using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DungeonMapper.Models;

// map objects: statues, paintings, reliefs and the like. a campaign keeps a
// library of object types (like the pawn library), each with a default look.
// a placed object is a type id plus a rectangle of cells, any size. it uses
// its type's look unless it carries its own copy. see Agent.md "Object
// System Design".

[JsonConverter(typeof(JsonStringEnumConverter<ObjectFill>))]
public enum ObjectFill { Solid, Linear, Radial, Stripes, Checker, Outline, Picture }

[JsonConverter(typeof(JsonStringEnumConverter<GradientDirection>))]
public enum GradientDirection { Horizontal, Vertical, Diagonal }

[JsonConverter(typeof(JsonStringEnumConverter<ObjectCaption>))]
public enum ObjectCaption { None, Name, Initials }

/// How an object is drawn: a rounded rectangle with a fill, an optional
/// border and an optional caption. Colors are "#rrggbb". Color B is the second
/// color of gradients, stripes and the checker. The picture is a key into the
/// campaign's picture pool (ObjectLibrary.Pictures), so a picture shared by any
/// number of types and objects is saved once.
public sealed class ObjectLook
{
    [JsonPropertyName("fill")] public ObjectFill Fill { get; set; } = ObjectFill.Solid;
    [JsonPropertyName("color_a")] public string ColorA { get; set; } = DefaultA;
    [JsonPropertyName("color_b")] public string ColorB { get; set; } = DefaultB;
    [JsonPropertyName("direction")] public GradientDirection Direction { get; set; } = GradientDirection.Vertical;
    [JsonPropertyName("border")] public bool Border { get; set; } = true;
    [JsonPropertyName("border_color")] public string BorderColor { get; set; } = DefaultBorder;
    // 1 to 10. scales with the zoom, 2 is the original border
    [JsonPropertyName("border_width")] public int BorderWidth { get; set; } = 2;
    [JsonPropertyName("caption")] public ObjectCaption Caption { get; set; } = ObjectCaption.None;
    // picture: true stretches it over the whole object, false keeps it square
    // in the middle over the fill color
    [JsonPropertyName("picture_stretch")] public bool PictureStretch { get; set; } = true;
    // false: transparent parts of the picture show the map through. true:
    // color A fills the shape behind the picture
    [JsonPropertyName("picture_backing")] public bool PictureBacking { get; set; } = false;
    // key into ObjectLibrary.Pictures, "" for none
    [JsonPropertyName("picture")] public string PictureId { get; set; } = "";
    // files saved before the picture pool kept the base64 in every look.
    // read only: ObjectLibrary.AdoptLegacy moves it into the pool on load
    [JsonPropertyName("image_b64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyImageB64 { get; set; }

    public const string DefaultA = "#8c8c96", DefaultB = "#4a4a54", DefaultBorder = "#d8d8e0";
    public const int MinBorderWidth = 1, MaxBorderWidth = 10;

    public ObjectLook Clone() => (ObjectLook)MemberwiseClone();

    public bool Same(ObjectLook o) =>
        Fill == o.Fill && ColorA == o.ColorA && ColorB == o.ColorB && Direction == o.Direction
        && Border == o.Border && BorderColor == o.BorderColor && Caption == o.Caption
        && PictureStretch == o.PictureStretch && PictureBacking == o.PictureBacking && PictureId == o.PictureId && BorderWidth == o.BorderWidth;

    /// bad colors from a hand edited file fall back to the defaults
    public ObjectLook Tidy()
    {
        ColorA = HexOr(ColorA, DefaultA);
        ColorB = HexOr(ColorB, DefaultB);
        BorderColor = HexOr(BorderColor, DefaultBorder);
        PictureId ??= "";
        BorderWidth = Math.Clamp(BorderWidth, MinBorderWidth, MaxBorderWidth);
        if (!Enum.IsDefined(Fill)) Fill = ObjectFill.Solid;
        if (!Enum.IsDefined(Direction)) Direction = GradientDirection.Vertical;
        if (!Enum.IsDefined(Caption)) Caption = ObjectCaption.None;
        return this;
    }

    public static (int r, int g, int b) Rgb(string hex)
    {
        hex = HexOr(hex, "#000000");
        return (Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16));
    }

    public static string Hex((int r, int g, int b) c) =>
        $"#{Math.Clamp(c.r, 0, 255):x2}{Math.Clamp(c.g, 0, 255):x2}{Math.Clamp(c.b, 0, 255):x2}";

    private static string HexOr(string? s, string fallback)
    {
        if (s is null || s.Length != 7 || s[0] != '#') return fallback;
        for (int i = 1; i < 7; i++) if (!Uri.IsHexDigit(s[i])) return fallback;
        return s.ToLowerInvariant();
    }

    /// up to two letters: the first letter of the first two words, or the
    /// first two letters of a single word
    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "";
        if (words.Length == 1) return words[0].Length >= 2 ? words[0][..2].ToUpperInvariant() : words[0].ToUpperInvariant();
        return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
    }
}

/// An object type in the campaign library, e.g. "Statue". Placed objects
/// point at it by Id.
public sealed class ObjectType
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("favorite")] public bool Favorite { get; set; }
    [JsonPropertyName("look")] public ObjectLook Look { get; set; } = new();

    public ObjectType Clone() => new() { Id = Id, Name = Name, Favorite = Favorite, Look = Look.Clone() };
}

/// The campaign's object types. Travels inside the campaign file
/// ("object_library") and with exported maps (only the types they use).
public sealed class ObjectLibrary
{
    [JsonPropertyName("objects")] public List<ObjectType> Types { get; set; } = new();
    // picture key -> base64 png. keys are a hash of the picture, so the same
    // picture always gets the same key, in this campaign or any other
    public Dictionary<string, string> Pictures { get; set; } = new();

    public ObjectType? Find(string id) => Types.Find(t => t.Id == id);

    public string? Picture(string id) => !string.IsNullOrEmpty(id) && Pictures.TryGetValue(id, out var b64) ? b64 : null;

    public static string PictureKey(string b64)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(b64));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// stores a picture once and returns its key
    public string AddPicture(string b64)
    {
        var key = PictureKey(b64);
        Pictures.TryAdd(key, b64);
        return key;
    }

    public void AddPictures(IReadOnlyDictionary<string, string>? pictures)
    {
        if (pictures is null) return;
        // keyed again from the content, so a hand edited key can not point at
        // the wrong picture
        foreach (var b64 in pictures.Values) if (!string.IsNullOrEmpty(b64)) AddPicture(b64);
    }

    /// looks from an older file carry their picture inline: move it into the pool
    public void AdoptLegacy(IEnumerable<ObjectLook> looks)
    {
        foreach (var l in looks)
        {
            if (!string.IsNullOrEmpty(l.LegacyImageB64)) l.PictureId = AddPicture(l.LegacyImageB64);
            l.LegacyImageB64 = null;
        }
    }

    /// the pictures the types and the given maps' objects point at, by key, to save
    public Dictionary<string, string> UsedPictures(IEnumerable<MapDocument> docs, IEnumerable<ObjectType>? types = null)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in types ?? Types) keys.Add(t.Look.PictureId);
        foreach (var d in docs)
            foreach (var o in d.Objects)
                if (o.Look is not null) keys.Add(o.Look.PictureId);
        var used = new Dictionary<string, string>();
        foreach (var k in keys) if (Picture(k) is { } b64) used[k] = b64;
        return used;
    }

    public ObjectType Add(ObjectType t)
    {
        if (string.IsNullOrEmpty(t.Id) || Types.Exists(x => x.Id == t.Id)) t.Id = NewId();
        Types.Add(t);
        return t;
    }

    public void Remove(string id) => Types.RemoveAll(t => t.Id == id);

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// adds types from another campaign or a map file. a type whose id is
    /// already here with the same name and look is reused, a clash gets a new
    /// id. returns old id -> id in this library, for remapping placed objects.
    public Dictionary<string, string> MergeAppend(IEnumerable<ObjectType> incoming)
    {
        var map = new Dictionary<string, string>();
        foreach (var t in incoming)
        {
            if (string.IsNullOrEmpty(t.Id)) continue;
            var have = Find(t.Id);
            if (have is not null && have.Name == t.Name && have.Look.Same(t.Look)) { map[t.Id] = t.Id; continue; }
            // another campaign made the same type under another id
            var twin = Types.Find(x => x.Name == t.Name && x.Look.Same(t.Look));
            if (twin is not null) { map[t.Id] = twin.Id; continue; }
            var copy = t.Clone();
            copy.Look.Tidy();
            if (have is not null) copy.Id = NewId();
            Types.Add(copy);
            map[t.Id] = copy.Id;
        }
        return map;
    }
}

/// An object placed on a map: a rectangle of W x H cells with its top-left
/// cell at (X, Y). Name, when set, is shown instead of the type name. Look,
/// when set, is this object's own copy and the type's look is ignored.
public sealed class MapObject
{
    public string Id { get; set; } = ObjectLibrary.NewId();
    public string TypeId { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 1;
    public int H { get; set; } = 1;
    public string? Name { get; set; }
    public ObjectLook? Look { get; set; }

    public bool Contains(CellKey c) => c.X >= X && c.X < X + W && c.Y >= Y && c.Y < Y + H;

    /// the rectangle lies inside [x0,y0]-[x1,y1] (cells, inclusive)
    public bool Inside(int x0, int y0, int x1, int y1) => X >= x0 && Y >= y0 && X + W - 1 <= x1 && Y + H - 1 <= y1;

    public bool Touches(int x0, int y0, int x1, int y1) => X <= x1 && X + W - 1 >= x0 && Y <= y1 && Y + H - 1 >= y0;

    public MapObject Copy(bool newId = false) => new()
    {
        Id = newId ? ObjectLibrary.NewId() : Id,
        TypeId = TypeId, X = X, Y = Y, W = W, H = H, Name = Name, Look = Look?.Clone(),
    };

    /// the look to draw: its own, else the type's, else a plain default
    public ObjectLook EffectiveLook(ObjectLibrary? lib) => Look ?? lib?.Find(TypeId)?.Look ?? new ObjectLook();

    /// the text a name caption shows: its own name, else the type name
    public string DisplayName(ObjectLibrary? lib) =>
        !string.IsNullOrWhiteSpace(Name) ? Name! : lib?.Find(TypeId)?.Name ?? "";
}
