using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// map objects on the canvas: drawing, the object tool (drag to place the
// armed type, click to configure, drag the handles of the configured object
// to resize it) and the picture cache. see Models/MapObjects.cs.
public sealed partial class MapCanvas
{
    /// the open campaign's object types
    public ObjectLibrary ObjectTypes { get; set; } = new();
    /// the type the object tool places, null when none is armed
    public ObjectType? ArmedObject { get; set; }
    /// the object whose config view is open. it gets resize handles while the
    /// object or select tool is active. an id, so undo and redo (which swap the object
    /// instances) keep it.
    public string? SelectedObjectId { get; set; }

    public event Action<MapObject>? ObjectConfigRequested;
    /// an object was placed, resized or erased with the canvas (legend counts)
    public event Action? ObjectsChanged;

    private MapObject? SelectedObject => SelectedObjectId is null ? null : Document.FindObject(SelectedObjectId);

    // ---- pictures ----
    // keyed by picture key. a key is a hash of the picture, so a changed
    // picture is a new key and never hits a stale bitmap
    private readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> _objectImages = new();

    public Avalonia.Media.Imaging.Bitmap? LoadObjectImage(string pictureId)
    {
        if (string.IsNullOrEmpty(pictureId)) return null;
        if (_objectImages.TryGetValue(pictureId, out var cached)) return cached;
        var b64 = ObjectTypes.Picture(pictureId);
        if (b64 is null) return null; // not cached: the pool may get it later
        Avalonia.Media.Imaging.Bitmap? bmp = null;
        try
        {
            using var ms = new System.IO.MemoryStream(Convert.FromBase64String(b64));
            bmp = new Avalonia.Media.Imaging.Bitmap(ms);
        }
        catch { }
        if (_objectImages.Count > 200) { foreach (var b in _objectImages.Values) b?.Dispose(); _objectImages.Clear(); }
        _objectImages[pictureId] = bmp;
        return bmp;
    }

    // ---- drawing ----

    private static Color Hex(string hex)
    {
        var (r, g, b) = ObjectLook.Rgb(hex);
        return Color.FromRgb((byte)r, (byte)g, (byte)b);
    }

    private void DrawObjects(DrawingContext ctx, Point topLeft, Point bottomRight)
    {
        foreach (var o in Document.Objects)
        {
            if (o.X + o.W < topLeft.X || o.X > bottomRight.X || o.Y + o.H < topLeft.Y || o.Y > bottomRight.Y) continue;
            var look = o.EffectiveLook(ObjectTypes);
            var tl = WorldToScreen(o.X, o.Y);
            DrawObjectShape(ctx, new Rect(tl.X, tl.Y, o.W * _scale, o.H * _scale), look, CaptionText(look, o.DisplayName(ObjectTypes)), 1);
        }
    }

    private static string CaptionText(ObjectLook look, string name) => look.Caption switch
    {
        ObjectCaption.Name => name,
        ObjectCaption.Initials => ObjectLook.Initials(name),
        _ => "",
    };

    /// Draws one object look into a screen rect (the cells it covers). Public
    /// through DrawObjectPreview so the side panel and the menus draw the same
    /// swatch as the map.
    private void DrawObjectShape(DrawingContext ctx, Rect cells, ObjectLook look, string caption, double opacity)
    {
        double scale = Math.Min(cells.Width, cells.Height);
        var r = cells.Deflate(Math.Max(1, scale * 0.06));
        if (r.Width < 2 || r.Height < 2) return;
        double rad = Math.Min(r.Width, r.Height) * 0.18;
        var rr = new RoundedRect(r, rad);
        var a = Hex(look.ColorA);
        var b = Hex(look.ColorB);
        // stripes and checker squares keep a size tied to the zoom, not the object
        double unit = Math.Max(4, _scale * 0.5);
        using var faded = ctx.PushOpacity(opacity);

        switch (look.Fill)
        {
            case ObjectFill.Solid:
                ctx.DrawRectangle(new SolidColorBrush(a), null, rr);
                break;
            case ObjectFill.Linear:
            {
                var (s, e) = look.Direction switch
                {
                    GradientDirection.Horizontal => (new RelativePoint(0, 0.5, RelativeUnit.Relative), new RelativePoint(1, 0.5, RelativeUnit.Relative)),
                    GradientDirection.Diagonal => (new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 1, RelativeUnit.Relative)),
                    _ => (new RelativePoint(0.5, 0, RelativeUnit.Relative), new RelativePoint(0.5, 1, RelativeUnit.Relative)),
                };
                var brush = new LinearGradientBrush { StartPoint = s, EndPoint = e };
                brush.GradientStops.Add(new GradientStop(a, 0));
                brush.GradientStops.Add(new GradientStop(b, 1));
                ctx.DrawRectangle(brush, null, rr);
                break;
            }
            case ObjectFill.Radial:
            {
                // center color a fading to b at the edge
                var brush = new RadialGradientBrush();
                brush.GradientStops.Add(new GradientStop(a, 0));
                brush.GradientStops.Add(new GradientStop(b, 1));
                ctx.DrawRectangle(brush, null, rr);
                break;
            }
            case ObjectFill.Stripes:
            {
                ctx.DrawRectangle(new SolidColorBrush(b), null, rr);
                using (ctx.PushClip(rr))
                {
                    var pen = new Pen(new SolidColorBrush(a), unit * 0.35);
                    for (double x = r.Left - r.Height; x < r.Right + unit; x += unit * 0.7)
                        ctx.DrawLine(pen, new Point(x, r.Bottom), new Point(x + r.Height, r.Top));
                }
                break;
            }
            case ObjectFill.Checker:
            {
                ctx.DrawRectangle(new SolidColorBrush(b), null, rr);
                using (ctx.PushClip(rr))
                {
                    var brush = new SolidColorBrush(a);
                    double sq = unit * 0.5;
                    int nx = (int)Math.Ceiling(r.Width / sq), ny = (int)Math.Ceiling(r.Height / sq);
                    for (int i = 0; i < nx; i++)
                        for (int j = 0; j < ny; j++)
                            if ((i + j) % 2 == 0)
                                ctx.FillRectangle(brush, new Rect(r.Left + i * sq, r.Top + j * sq, sq, sq));
                }
                break;
            }
            case ObjectFill.Outline:
                // no fill, the border is the object. drawn below whatever the
                // border toggle says
                break;
            case ObjectFill.Picture:
            {
                // without a backing, transparent parts of the picture show the map
                if (look.PictureBacking) ctx.DrawRectangle(new SolidColorBrush(a), null, rr);
                var bmp = LoadObjectImage(look.PictureId);
                if (bmp is not null)
                {
                    var src = new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height);
                    Rect dest = r;
                    if (!look.PictureStretch)
                    {
                        double side = Math.Min(r.Width, r.Height);
                        dest = new Rect(r.Center.X - side / 2, r.Center.Y - side / 2, side, side);
                    }
                    using (ctx.PushClip(rr))
                        ctx.DrawImage(bmp, src, dest);
                }
                break;
            }
        }

        // border thickness 1 to 10, relative to the object's cell size so it
        // keeps its look when zooming. 2 is the original border
        int bw = Math.Clamp(look.BorderWidth, ObjectLook.MinBorderWidth, ObjectLook.MaxBorderWidth);
        if (look.Fill == ObjectFill.Outline)
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Hex(look.BorderColor)), Math.Max(1, scale * 0.04 * bw)), rr);
        else if (look.Border)
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Hex(look.BorderColor)), Math.Max(1, scale * 0.025 * bw)), rr);

        if (!string.IsNullOrEmpty(caption)) DrawCaption(ctx, r, caption, look);
    }

    // centered text sized to fit the object, light or dark against the fill
    private static void DrawCaption(DrawingContext ctx, Rect r, string text, ObjectLook look)
    {
        double fs = Math.Min(r.Height * 0.42, 22);
        if (fs < 6) return;
        var bg = look.Fill switch
        {
            ObjectFill.Outline or ObjectFill.Picture => Color.FromRgb(40, 40, 48),
            ObjectFill.Radial => Hex(look.ColorA),
            ObjectFill.Linear => Color.FromRgb((byte)((Hex(look.ColorA).R + Hex(look.ColorB).R) / 2),
                (byte)((Hex(look.ColorA).G + Hex(look.ColorB).G) / 2), (byte)((Hex(look.ColorA).B + Hex(look.ColorB).B) / 2)),
            _ => Hex(look.ColorA),
        };
        bool light = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B < 140;
        var fg = light ? Color.FromRgb(246, 246, 250) : Color.FromRgb(20, 20, 26);
        var shade = light ? Color.FromArgb(200, 0, 0, 0) : Color.FromArgb(160, 255, 255, 255);
        FormattedText Make(double size, Color c) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Inter", FontStyle.Normal, FontWeight.SemiBold), size, new SolidColorBrush(c));
        var ft = Make(fs, fg);
        if (ft.Width > r.Width * 0.9)
        {
            fs *= r.Width * 0.9 / ft.Width;
            if (fs < 6) return;
            ft = Make(fs, fg);
        }
        var at = new Point(r.Center.X - ft.Width / 2, r.Center.Y - ft.Height / 2);
        ctx.DrawText(Make(fs, shade), new Point(at.X + 1, at.Y + 1));
        ctx.DrawText(ft, at);
    }

    // ghost while dragging a new object, and the handles of the configured one
    private void DrawObjectOverlay(DrawingContext ctx)
    {
        if (!BoxTool(ToolMode.Object)) return;
        if (Mode == ToolMode.Object && _objPlacing && ArmedObject is not null)
        {
            var (x0, y0, x1, y1) = NormRect(_objStart, _objEnd);
            var tl = WorldToScreen(x0, y0);
            var rect = new Rect(tl.X, tl.Y, (x1 - x0 + 1) * _scale, (y1 - y0 + 1) * _scale);
            DrawObjectShape(ctx, rect, ArmedObject.Look, CaptionText(ArmedObject.Look, ArmedObject.Name), 0.6);
            ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(250, 250, 250)), 2), rect);
            DrawSizeRuler(ctx, x1 - x0 + 1, y1 - y0 + 1, WorldToScreen(_objEnd.X + 0.5, _objEnd.Y));
        }
        var sel = SelectedObject;
        if (sel is null) return;
        var (rx, ry, rw, rh) = _objResizing ? _resizeRect : (sel.X, sel.Y, sel.W, sel.H);
        var p = WorldToScreen(rx, ry);
        var box = new Rect(p.X, p.Y, rw * _scale, rh * _scale);
        if (_objResizing)
        {
            var look = sel.EffectiveLook(ObjectTypes);
            DrawObjectShape(ctx, box, look, CaptionText(look, sel.DisplayName(ObjectTypes)), 0.6);
            if (_resizeHandle != MoveHandle) DrawSizeRuler(ctx, rw, rh, new Point(box.Center.X, box.Top));
        }
        DrawBoxHandles(ctx, box);
    }

    // feet label above a point, like the room tool's
    private void DrawSizeRuler(DrawingContext ctx, int wCells, int hCells, Point anchor)
    {
        var ft = new FormattedText($"{wCells * 5}x{hCells * 5}ft", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Inter"), Math.Max(11, _scale * 0.4), LabelBrush);
        var tx = anchor.X - ft.Width / 2;
        var ty = anchor.Y - ft.Height - 4;
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(180, 20, 20, 25)), new Rect(tx - 3, ty - 1, ft.Width + 6, ft.Height + 2));
        ctx.DrawText(ft, new Point(tx, ty));
    }

    // ---- object tool input ----

    private const double HandleSize = 9;
    private bool _objPlacing, _objResizing;
    private CellKey _objStart, _objEnd;
    // handle: x side -1 left, 0 none, 1 right; y side the same
    private (int sx, int sy) _resizeHandle;
    private (int x, int y, int w, int h) _resizeRect;

    private static IEnumerable<((int sx, int sy) h, Point p)> Handles(Rect b)
    {
        for (int sx = -1; sx <= 1; sx++)
            for (int sy = -1; sy <= 1; sy++)
            {
                if (sx == 0 && sy == 0) continue;
                double x = sx < 0 ? b.Left : sx > 0 ? b.Right : b.Center.X;
                double y = sy < 0 ? b.Top : sy > 0 ? b.Bottom : b.Center.Y;
                yield return ((sx, sy), new Point(x, y));
            }
    }

    /// a press on the open object's handles (resize) or dashed edge (move)
    private bool ObjectHandlePressed(PointerPressedEventArgs e)
    {
        if (!BoxTool(ToolMode.Object) || SelectedObject is not { } sel) return false;
        if (BoxHit(CellBox(sel.X, sel.Y, sel.W, sel.H), e.GetPosition(this)) is not { } h) return false;
        _objResizing = true;
        _resizeHandle = h;
        _resizeRect = (sel.X, sel.Y, sel.W, sel.H);
        _boxGrab = ScreenToWorld(e.GetPosition(this));
        e.Pointer.Capture(this);
        InvalidateVisual();
        return true;
    }

    private void ObjectPressed(PointerPressedEventArgs e, CellKey cell)
    {
        if (ObjectHandlePressed(e)) return;
        // shift places over an existing object instead of opening it
        bool force = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (!force && Document.ObjectAt(cell) is { } hit)
        {
            SelectedObjectId = hit.Id;
            ObjectConfigRequested?.Invoke(hit);
            InvalidateVisual();
            return;
        }
        if (ArmedObject is null)
        {
            Status("pick an object type in the object panel first", StatusKind.Warning);
            return;
        }
        _objPlacing = true;
        _objStart = _objEnd = cell;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    // true when the object tool used the move
    private bool ObjectMoved(Point world)
    {
        if (_objPlacing)
        {
            var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
            if (cell != _objEnd) { _objEnd = cell; InvalidateVisual(); }
            return true;
        }
        if (_objResizing && SelectedObject is { } sel)
        {
            if (_resizeHandle == MoveHandle)
            {
                var m = MovedRect((sel.X, sel.Y, sel.W, sel.H), world);
                if (m != _resizeRect) { _resizeRect = m; InvalidateVisual(); }
                return true;
            }
            // the dragged sides snap to the nearest grid line, at least one cell
            int left = sel.X, top = sel.Y, right = sel.X + sel.W, bottom = sel.Y + sel.H;
            int gx = (int)Math.Round(world.X), gy = (int)Math.Round(world.Y);
            if (_resizeHandle.sx < 0) left = Math.Min(gx, right - 1);
            if (_resizeHandle.sx > 0) right = Math.Max(gx, left + 1);
            if (_resizeHandle.sy < 0) top = Math.Min(gy, bottom - 1);
            if (_resizeHandle.sy > 0) bottom = Math.Max(gy, top + 1);
            var r = (left, top, right - left, bottom - top);
            if (r != _resizeRect) { _resizeRect = r; InvalidateVisual(); }
            return true;
        }
        return false;
    }

    // true when the object tool used the release
    private bool ObjectReleased(PointerReleasedEventArgs e)
    {
        if (_objPlacing)
        {
            _objPlacing = false;
            e.Pointer.Capture(null);
            if (ArmedObject is null) { InvalidateVisual(); return true; }
            var (x0, y0, x1, y1) = NormRect(_objStart, _objEnd);
            var o = new MapObject { TypeId = ArmedObject.Id, X = x0, Y = y0, W = x1 - x0 + 1, H = y1 - y0 + 1 };
            Document.Objects.Add(o);
            Status($"placed {(string.IsNullOrWhiteSpace(ArmedObject.Name) ? "object" : ArmedObject.Name)} ({o.W * 5}x{o.H * 5}ft)");
            Commit();
            ObjectsChanged?.Invoke();
            return true;
        }
        if (_objResizing)
        {
            _objResizing = false;
            e.Pointer.Capture(null);
            if (SelectedObject is { } sel && _resizeRect != (sel.X, sel.Y, sel.W, sel.H))
            {
                (sel.X, sel.Y, sel.W, sel.H) = _resizeRect;
                Status(_resizeHandle == MoveHandle ? $"moved to {sel.X},{sel.Y}" : $"resized to {sel.W * 5}x{sel.H * 5}ft");
                Commit();
                ObjectsChanged?.Invoke();
            }
            InvalidateVisual();
            return true;
        }
        return false;
    }

    /// removes objects touching the rect, for the erase tool and delete key
    private int EraseObjects(int x0, int y0, int x1, int y1) =>
        Document.Objects.RemoveAll(o => o.Touches(x0, y0, x1, y1));

    /// renders one look into a bitmap for the side panel and the menus
    public Avalonia.Media.Imaging.RenderTargetBitmap ObjectSwatch(ObjectLook look, string caption, int w, int h, int cellsW = 1, int cellsH = 1)
    {
        var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(w, h));
        double saved = _scale;
        _scale = Math.Min((double)w / cellsW, (double)h / cellsH);
        using (var ctx = bmp.CreateDrawingContext())
            DrawObjectShape(ctx, new Rect(0, 0, w, h), look, caption, 1);
        _scale = saved;
        return bmp;
    }
}
