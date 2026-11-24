using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace DungeonMapper.Models;

/// how a picture sits in its box when the shapes differ
public enum ImageFit { Fit, Fill, Stretch }

/// An image placed on the map: a rectangle of cells showing a picture file
/// from the images folder (AppSettings.ImagesFolder, a shared drive). The
/// map keeps only the reference (path inside the folder + content hash),
/// never the picture. Treated like labels: drawn over doors, under pawns.
public sealed class MapImage
{
    /// image pixels per cell when placed with a click (a drag sets the box).
    /// a code constant on purpose, not a setting
    public const int PixelsPerCell = 100;
    /// a box side never goes past this many cells
    public const int MaxCells = 500;

    public string Id { get; set; } = NewId();
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 1;
    public int H { get; set; } = 1;
    /// path inside the images folder, forward slashes
    public string Path { get; set; } = "";
    /// content hash (ImageStore.HashFile), finds a renamed or moved file
    public string Hash { get; set; } = "";
    /// the picture's size in pixels when it was picked, unturned. keeps the
    /// shape known while the file is missing
    public int PixelW { get; set; }
    public int PixelH { get; set; }
    public ImageFit Fit { get; set; } = ImageFit.Fit;
    /// resizing keeps the box close to the picture's shape
    public bool KeepShape { get; set; } = true;
    /// quarter turns clockwise, 0..3
    private int _turns;
    public int Turns { get => _turns; set => _turns = ((value % 4) + 4) % 4; }
    /// hidden from players even on revealed cells (multiplayer)
    public bool DmOnly { get; set; } = true;
    /// multiplayer: the joined name of the player who placed it, null = the DM
    public string? CreatedBy { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public string FileName => System.IO.Path.GetFileName(Path);

    public bool Contains(CellKey c) => c.X >= X && c.X < X + W && c.Y >= Y && c.Y < Y + H;
    public bool Inside(int x0, int y0, int x1, int y1) => X >= x0 && Y >= y0 && X + W - 1 <= x1 && Y + H - 1 <= y1;
    public bool Touches(int x0, int y0, int x1, int y1) => X <= x1 && X + W - 1 >= x0 && Y <= y1 && Y + H - 1 >= y0;

    /// the picture's width over height as drawn (after turning). 1 when unknown
    public double Aspect
    {
        get
        {
            if (PixelW <= 0 || PixelH <= 0) return 1;
            return Turns % 2 == 0 ? (double)PixelW / PixelH : (double)PixelH / PixelW;
        }
    }

    /// the box a click places: the picture at PixelsPerCell, at least 1 cell
    public static (int w, int h) NaturalCells(int pixelW, int pixelH, int turns = 0)
    {
        if (turns % 2 != 0) (pixelW, pixelH) = (pixelH, pixelW);
        int w = Math.Clamp((int)Math.Round((double)pixelW / PixelsPerCell), 1, MaxCells);
        int h = Math.Clamp((int)Math.Round((double)pixelH / PixelsPerCell), 1, MaxCells);
        return (w, h);
    }

    public MapImage Copy(bool newId = false) => new()
    {
        Id = newId ? NewId() : Id,
        X = X, Y = Y, W = W, H = H, Path = Path, Hash = Hash, PixelW = PixelW, PixelH = PixelH,
        Fit = Fit, KeepShape = KeepShape, Turns = Turns, DmOnly = DmOnly, CreatedBy = CreatedBy,
    };

    /// copies everything but the id from another image (player edits)
    public void SetFrom(MapImage o)
    {
        X = o.X; Y = o.Y; W = o.W; H = o.H; Path = o.Path; Hash = o.Hash; PixelW = o.PixelW; PixelH = o.PixelH;
        Fit = o.Fit; KeepShape = o.KeepShape; Turns = o.Turns; DmOnly = o.DmOnly; CreatedBy = o.CreatedBy;
    }

    /// keeps a box near the picture's shape. the side the user dragged wins,
    /// the other follows (at least 1 cell). widthLeads: the width was dragged
    public static (int w, int h) Shaped(int w, int h, double aspect, bool widthLeads)
    {
        if (aspect <= 0) return (w, h);
        if (widthLeads) h = Math.Clamp((int)Math.Round(w / aspect), 1, MaxCells);
        else w = Math.Clamp((int)Math.Round(h * aspect), 1, MaxCells);
        return (w, h);
    }
}

/// The images folder: turning references into files and files into
/// references. The folder is per machine (each player picks their own path
/// to the shared drive), so maps only hold paths inside it.
public static class ImageStore
{
    public static readonly string[] Patterns = { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

    /// the images folder of this machine, "" when not set
    public static string Folder { get; set; } = "";

    public static bool FolderOk => Folder.Length > 0 && Directory.Exists(Folder);

    /// a short content hash: the first 16 hex digits of the file's sha256
    public static string HashFile(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs))[..16].ToLowerInvariant();
    }

    /// A stored path made safe: forward slashes, no empty or dot segments,
    /// never rooted. null when it tries to leave the folder (it comes from
    /// someone else's file or from the network).
    public static string? CleanPath(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return null;
        var s = rel.Replace('\\', '/');
        if (s.StartsWith('/') || s.Contains(':')) return null;
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p == ".." || p == ".")) return null;
        return string.Join('/', parts);
    }

    /// the path of a file inside the folder, or null when it is outside
    public static string? Relative(string file)
    {
        if (!FolderOk) return null;
        var root = System.IO.Path.GetFullPath(Folder);
        var full = System.IO.Path.GetFullPath(file);
        var rel = System.IO.Path.GetRelativePath(root, full);
        if (rel == "." || rel.StartsWith("..") || System.IO.Path.IsPathRooted(rel)) return null;
        return CleanPath(rel);
    }

    /// The file an image points at: its path (letter case ignored, so a map
    /// made on Windows works on Linux), else a file with the same content
    /// hash anywhere in the folder. null when not found.
    public static string? Resolve(string path, string hash)
    {
        if (!FolderOk) return null;
        var rel = CleanPath(path);
        if (rel is not null)
        {
            var exact = System.IO.Path.Combine(Folder, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (File.Exists(exact)) return exact;
            if (FindIgnoringCase(rel) is { } ci) return ci;
        }
        if (hash.Length > 0 && FindByHash(hash) is { } byHash) return byHash;
        return null;
    }

    private static string? FindIgnoringCase(string rel)
    {
        string dir = Folder;
        var parts = rel.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            IEnumerable<string> entries;
            try { entries = last ? Directory.EnumerateFiles(dir) : Directory.EnumerateDirectories(dir); }
            catch { return null; }
            var hit = entries.FirstOrDefault(e => string.Equals(System.IO.Path.GetFileName(e), parts[i], StringComparison.OrdinalIgnoreCase));
            if (hit is null) return null;
            dir = hit;
        }
        return dir;
    }

    // hash -> file, built once per folder (hashing a whole shared drive is
    // slow, so only when a path misses). Forget() drops it
    private static Dictionary<string, string>? _hashIndex;
    private static string _hashIndexFolder = "";
    private static readonly object _lock = new();

    private static string? FindByHash(string hash)
    {
        Dictionary<string, string> index;
        lock (_lock)
        {
            if (_hashIndex is null || _hashIndexFolder != Folder)
            {
                var built = new Dictionary<string, string>();
                foreach (var f in ImageFiles())
                {
                    try { built.TryAdd(HashFile(f), f); } catch { }
                }
                _hashIndex = built;
                _hashIndexFolder = Folder;
            }
            index = _hashIndex;
        }
        if (index.TryGetValue(hash, out var file) && File.Exists(file)) return file;
        return null;
    }

    /// drop the hash index (folder changed, files changed, "reload images")
    public static void Forget()
    {
        lock (_lock) _hashIndex = null;
    }

    /// every picture file in the folder and its subfolders
    public static IEnumerable<string> ImageFiles()
    {
        if (!FolderOk) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var f in files)
            if (Extensions.Contains(System.IO.Path.GetExtension(f))) yield return f;
    }

    /// Copies a picture into the top of the folder and returns the copy. An
    /// identical file already there is reused; a different file with the
    /// same name gets " (2)", " (3)"... added.
    public static string CopyIn(string source)
    {
        if (!FolderOk) throw new InvalidOperationException("no images folder set");
        var hash = HashFile(source);
        var name = System.IO.Path.GetFileNameWithoutExtension(source);
        var ext = System.IO.Path.GetExtension(source);
        for (int n = 1; ; n++)
        {
            var dest = System.IO.Path.Combine(Folder, n == 1 ? name + ext : $"{name} ({n}){ext}");
            if (!File.Exists(dest))
            {
                File.Copy(source, dest);
                return dest;
            }
            try { if (HashFile(dest) == hash) return dest; } catch { }
        }
    }

    /// the pixel size of a picture file from its header (no full decode)
    public static (int w, int h) PixelSize(string file)
    {
        using var codec = SkiaSharp.SKCodec.Create(file);
        if (codec is null) throw new InvalidDataException("not a picture this app can read");
        return (codec.Info.Width, codec.Info.Height);
    }

    /// Builds an image for a picked file inside the folder: reference, hash,
    /// pixel size, the click size. Throws when the file is unreadable.
    public static MapImage FromFile(string file)
    {
        var rel = Relative(file) ?? throw new InvalidOperationException("the file is not in the images folder");
        var (pw, ph) = PixelSize(file);
        var (w, h) = MapImage.NaturalCells(pw, ph);
        return new MapImage { Path = rel, Hash = HashFile(file), PixelW = pw, PixelH = ph, W = w, H = h };
    }
}
