using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// images on the map (Models/MapImages.cs): drawing from the images folder,
// the image tool (click or drag to place the armed picture, click an image to
// open it, drag the handles of the open one to resize it). treated like
// labels: drawn over doors, under labels and pawns.
public sealed partial class MapCanvas
{
    /// the picture the image tool places (a template: path, hash, pixel size), null when none
    public MapImage? ArmedImage { get; set; }
    /// the image whose view is open (handles in the image tool). an id, so
    /// undo, redo and new maps from the DM keep it
    public string? SelectedImageId { get; set; }

    public event Action<MapImage>? ImageConfigRequested;
    /// an image was placed, resized or erased with the canvas (panel counts)
    public event Action? ImagesChanged;
    /// joined: an image the player placed or resized, for the DM
    public event Action<MapImage>? RemoteImageChanged;

    public MapImage? SelectedImage => SelectedImageId is null ? null : Document.FindImage(SelectedImageId);

    // ---- loading ----
    // pictures are decoded off the ui thread (a shared drive can be slow) and
    // kept by reference. a reference that can not be found is remembered as
    // missing until ReloadImages
    private sealed class ImageEntry
    {
        public Bitmap? Bitmap;
        public bool Loading = true;
        public string? Error;
    }

    private readonly Dictionary<string, ImageEntry> _images = new();
    /// the longest side a picture is decoded at
    public const int MaxImagePixels = 4096;

    private static string ImageKey(MapImage im) => im.Path + "|" + im.Hash;

    /// drops every loaded picture and the folder's hash index: the folder or
    /// its files changed
    public void ReloadImages()
    {
        foreach (var e in _images.Values) e.Bitmap?.Dispose();
        _images.Clear();
        ImageStore.Forget();
        InvalidateVisual();
    }

    /// the loaded picture, or null with why (loading, missing, no folder)
    public (Bitmap? bitmap, string? why) ImageState(MapImage im)
    {
        if (!ImageStore.FolderOk) return (null, ImageStore.Folder.Length == 0 ? "no images folder set" : "images folder not found");
        var key = ImageKey(im);
        if (!_images.TryGetValue(key, out var e))
        {
            e = new ImageEntry();
            _images[key] = e;
            string path = im.Path, hash = im.Hash;
            _ = Task.Run(() =>
            {
                Bitmap? bmp = null;
                string? error = null;
                try
                {
                    var file = ImageStore.Resolve(path, hash);
                    if (file is null) error = "missing";
                    else
                    {
                        var (w, h) = ImageStore.PixelSize(file);
                        using var fs = System.IO.File.OpenRead(file);
                        bmp = Math.Max(w, h) <= MaxImagePixels
                            ? new Bitmap(fs)
                            : w >= h
                                ? Bitmap.DecodeToWidth(fs, MaxImagePixels, BitmapInterpolationMode.HighQuality)
                                : Bitmap.DecodeToHeight(fs, MaxImagePixels, BitmapInterpolationMode.HighQuality);
                    }
                }
                catch (Exception ex) { error = "unreadable: " + ex.Message; }
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_images.TryGetValue(key, out var now) || now != e) { bmp?.Dispose(); return; }
                    e.Bitmap = bmp;
                    e.Error = error;
                    e.Loading = false;
                    InvalidateVisual();
                    ImagesLoaded?.Invoke();
                });
            });
        }
        if (e.Loading) return (null, "loading…");
        return (e.Bitmap, e.Bitmap is null ? e.Error ?? "missing" : null);
    }

    /// a picture finished loading (the open image view shows it)
    public event Action? ImagesLoaded;

    // ---- drawing ----

    private void DrawImages(DrawingContext ctx, Point topLeft, Point bottomRight)
    {
        foreach (var im in Document.Images)
        {
            if (im.X + im.W < topLeft.X || im.X > bottomRight.X || im.Y + im.H < topLeft.Y || im.Y > bottomRight.Y) continue;
            var tl = WorldToScreen(im.X, im.Y);
            DrawImageIn(ctx, im, new Rect(tl.X, tl.Y, im.W * _scale, im.H * _scale), 1);
        }
    }

    /// draws an image's picture into a screen box with its fit and turns, or
    /// a dashed placeholder with the file name and why it is not shown
    private void DrawImageIn(DrawingContext ctx, MapImage im, Rect box, double opacity)
    {
        var (bmp, why) = ImageState(im);
        using var faded = ctx.PushOpacity(opacity);
        if (bmp is null)
        {
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(120, 30, 30, 38)), box);
            ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(200, 200, 210)), 1.5, new DashStyle(new double[] { 4, 3 }, 0)), box.Deflate(1));
            double fs = Math.Clamp(_scale * 0.3, 8, 14);
            if (box.Width > 30 && box.Height > 2 * fs)
            {
                var text = im.FileName + "\n" + why;
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Inter"), fs,
                    new SolidColorBrush(Color.FromRgb(220, 220, 228)))
                { MaxTextWidth = Math.Max(10, box.Width - 8), MaxTextHeight = Math.Max(10, box.Height - 4), TextAlignment = TextAlignment.Center };
                using (ctx.PushClip(box))
                    ctx.DrawText(ft, new Point(box.X + 4, box.Center.Y - ft.Height / 2));
            }
            return;
        }
        // the picture's size as drawn (turned), then fitted into the box
        double iw = bmp.PixelSize.Width, ih = bmp.PixelSize.Height;
        bool odd = im.Turns % 2 != 0;
        double tw = odd ? ih : iw, th = odd ? iw : ih;
        double dw, dh;
        switch (im.Fit)
        {
            case ImageFit.Stretch: dw = box.Width; dh = box.Height; break;
            case ImageFit.Fill:
            {
                double s = Math.Max(box.Width / tw, box.Height / th);
                dw = tw * s; dh = th * s;
                break;
            }
            default:
            {
                double s = Math.Min(box.Width / tw, box.Height / th);
                dw = tw * s; dh = th * s;
                break;
            }
        }
        using (ctx.PushClip(box))
        using (ctx.PushTransform(Matrix.CreateRotation(im.Turns * Math.PI / 2) * Matrix.CreateTranslation(box.Center.X, box.Center.Y)))
        {
            // unturned, the drawn rect swaps back on odd turns
            double rw = odd ? dh : dw, rh = odd ? dw : dh;
            ctx.DrawImage(bmp, new Rect(0, 0, iw, ih), new Rect(-rw / 2, -rh / 2, rw, rh));
        }
    }

    /// a small preview of an image for the side panel (picture or placeholder)
    public RenderTargetBitmap ImageSwatch(MapImage im, int w, int h)
    {
        var bmp = new RenderTargetBitmap(new PixelSize(w, h));
        double saved = _scale;
        _scale = Math.Min(w, h);
        var copy = im.Copy();
        copy.Fit = ImageFit.Fit;
        using (var ctx = bmp.CreateDrawingContext())
            DrawImageIn(ctx, copy, new Rect(0, 0, w, h), 1);
        _scale = saved;
        return bmp;
    }

    // ghost while placing, handles on the open image
    private void DrawImageOverlay(DrawingContext ctx)
    {
        if (Mode != ToolMode.Image) return;
        var accent = new SolidColorBrush(Color.FromRgb(90, 127, 208));
        if (_imgPlacing && ArmedImage is not null)
        {
            var (x, y, w, h) = PlacingRect();
            var tl = WorldToScreen(x, y);
            var rect = new Rect(tl.X, tl.Y, w * _scale, h * _scale);
            DrawImageIn(ctx, ArmedImage, rect, 0.6);
            ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(250, 250, 250)), 2), rect);
            DrawSizeRuler(ctx, w, h, new Point(rect.Center.X, rect.Top));
        }
        var sel = SelectedImage;
        if (sel is null) return;
        var (rx, ry, rw, rh) = _imgResizing ? _imgRect : (sel.X, sel.Y, sel.W, sel.H);
        var p = WorldToScreen(rx, ry);
        var box = new Rect(p.X, p.Y, rw * _scale, rh * _scale);
        if (_imgResizing)
        {
            var ghost = sel.Copy();
            DrawImageIn(ctx, ghost, box, 0.6);
            DrawSizeRuler(ctx, rw, rh, new Point(box.Center.X, box.Top));
        }
        ctx.DrawRectangle(new Pen(accent, 2, new DashStyle(new double[] { 3, 2 }, 0)), box);
        foreach (var (_, hp) in Handles(box))
        {
            var hr = new Rect(hp.X - HandleSize / 2, hp.Y - HandleSize / 2, HandleSize, HandleSize);
            ctx.FillRectangle(Brushes.White, hr);
            ctx.DrawRectangle(new Pen(accent, 1.5), hr);
        }
    }

    // ---- image tool input ----

    private bool _imgPlacing, _imgResizing;
    private CellKey _imgStart, _imgEnd;
    private (int sx, int sy) _imgHandle;
    private (int x, int y, int w, int h) _imgRect;

    /// the rect a placement covers: a click places the picture at its own
    /// size (MapImage.PixelsPerCell), a drag the dragged box
    private (int x, int y, int w, int h) PlacingRect()
    {
        if (_imgStart == _imgEnd && ArmedImage is { } a)
            return (_imgStart.X, _imgStart.Y, a.W, a.H);
        var (x0, y0, x1, y1) = NormRect(_imgStart, _imgEnd);
        return (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private void ImagePressed(PointerPressedEventArgs e, CellKey cell)
    {
        var screen = e.GetPosition(this);
        if (SelectedImage is { } sel && (_remote is null || PlayerImages.Editable(sel)))
        {
            var p = WorldToScreen(sel.X, sel.Y);
            var box = new Rect(p.X, p.Y, sel.W * _scale, sel.H * _scale);
            foreach (var (h, hp) in Handles(box))
                if (Math.Abs(screen.X - hp.X) <= HandleSize && Math.Abs(screen.Y - hp.Y) <= HandleSize)
                {
                    _imgResizing = true;
                    _imgHandle = h;
                    _imgRect = (sel.X, sel.Y, sel.W, sel.H);
                    e.Pointer.Capture(this);
                    InvalidateVisual();
                    return;
                }
        }
        // shift places over an existing image instead of opening it
        bool force = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (!force && Document.ImageAt(cell) is { } hit)
        {
            if (_remote is not null && !PlayerImages.Editable(hit))
            {
                Status("only the DM edits that image", StatusKind.Error);
                return;
            }
            SelectedImageId = hit.Id;
            ImageConfigRequested?.Invoke(hit);
            InvalidateVisual();
            return;
        }
        if (ArmedImage is null)
        {
            Status("choose a picture in the image panel first", StatusKind.Warning);
            return;
        }
        _imgPlacing = true;
        _imgStart = _imgEnd = cell;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    // true when the image tool used the move
    private bool ImageMoved(Point world)
    {
        if (_imgPlacing)
        {
            var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
            if (cell != _imgEnd) { _imgEnd = cell; InvalidateVisual(); }
            return true;
        }
        if (_imgResizing && SelectedImage is { } sel)
        {
            var r = ResizedRect(sel, _imgHandle, world);
            if (r != _imgRect) { _imgRect = r; InvalidateVisual(); }
            return true;
        }
        return false;
    }

    /// The box a handle drag gives: the dragged sides snap to grid lines, at
    /// least one cell. With keep shape the other side follows the picture's
    /// shape, anchored at the side or corner opposite the handle.
    public static (int x, int y, int w, int h) ResizedRect(MapImage im, (int sx, int sy) handle, Point world)
    {
        int left = im.X, top = im.Y, right = im.X + im.W, bottom = im.Y + im.H;
        int gx = (int)Math.Round(world.X), gy = (int)Math.Round(world.Y);
        if (handle.sx < 0) left = Math.Min(gx, right - 1);
        if (handle.sx > 0) right = Math.Max(gx, left + 1);
        if (handle.sy < 0) top = Math.Min(gy, bottom - 1);
        if (handle.sy > 0) bottom = Math.Max(gy, top + 1);
        int w = Math.Min(right - left, MapImage.MaxCells), h = Math.Min(bottom - top, MapImage.MaxCells);
        if (im.KeepShape && im.PixelW > 0 && im.PixelH > 0)
        {
            // a side handle leads with its side, a corner with the side that
            // changed the most for its size
            bool widthLeads = handle.sy == 0 || (handle.sx != 0
                && Math.Abs(w - im.W) / (double)im.W >= Math.Abs(h - im.H) / (double)im.H);
            (w, h) = MapImage.Shaped(w, h, im.Aspect, widthLeads);
            if (handle.sx < 0) left = right - w; else right = left + w;
            if (handle.sy < 0) top = bottom - h; else bottom = top + h;
        }
        return (left, top, right - left, bottom - top);
    }

    // true when the image tool used the release
    private bool ImageReleased(PointerReleasedEventArgs e)
    {
        if (_imgPlacing)
        {
            _imgPlacing = false;
            e.Pointer.Capture(null);
            if (ArmedImage is null) { InvalidateVisual(); return true; }
            var (x, y, w, h) = PlacingRect();
            var im = ArmedImage.Copy(newId: true);
            (im.X, im.Y, im.W, im.H) = (x, y, w, h);
            im.Fit = ImageFit.Fit;
            im.KeepShape = true;
            im.Turns = 0;
            im.DmOnly = _remote is null; // new items start DM only, players' are always shown
            im.CreatedBy = null;
            Document.Images.Add(im);
            SelectedImageId = im.Id;
            Status($"placed {im.FileName} ({w * 5}x{h * 5}ft)");
            if (_remote is not null) { RemoteImageChanged?.Invoke(im); InvalidateVisual(); }
            else Commit();
            ImagesChanged?.Invoke();
            ImageConfigRequested?.Invoke(im);
            return true;
        }
        if (_imgResizing)
        {
            _imgResizing = false;
            e.Pointer.Capture(null);
            if (SelectedImage is { } sel && _imgRect != (sel.X, sel.Y, sel.W, sel.H))
            {
                (sel.X, sel.Y, sel.W, sel.H) = _imgRect;
                Status($"image resized to {sel.W * 5}x{sel.H * 5}ft");
                Commit();
                ImagesChanged?.Invoke();
                ImageConfigRequested?.Invoke(sel);
            }
            InvalidateVisual();
            return true;
        }
        return false;
    }

    /// removes images touching the rect, for the erase tool and delete key
    private int EraseImages(int x0, int y0, int x1, int y1) =>
        Document.Images.RemoveAll(o => o.Touches(x0, y0, x1, y1));
}
