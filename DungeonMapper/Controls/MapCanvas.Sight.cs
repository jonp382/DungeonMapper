using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// the sight tool (line of sight). DM side only, reading only except the pawn
// drag, which is an ordinary pawn move. see Agent.md "Line of Sight Design".
//  - click a pawn or an empty cell: sight from that cell's center, out to
//    the range. esc or a tool switch clears it
//  - the visible region is tinted green, behind one pawn orange, behind two
//    or more red. everything else is dimmed
//  - dragging a pawn moves it for real; the region follows the ghost
//  - hovering a cell draws the line to it with what is in the way
//  - the geometry is Models/Sight.cs, cached until the map, the viewer or
//    the range changes
public sealed partial class MapCanvas
{
    private CellKey? _sightCell;       // the viewer's cell
    private MapDocument? _sightDoc;    // the map it was set on
    private bool _sightDragging;       // button held on an empty cell
    private CellKey? _sightHover;      // cell under the cursor for the line
    private int _sightFeet = Sight.DefaultFeet;

    /// range, origin or region changed (panel readout)
    public event Action? SightChanged;

    public bool HasSight => _sightCell is not null && Mode == ToolMode.Sight;

    /// range in feet, 1 to 240 (session only)
    public int SightFeet
    {
        get => _sightFeet;
        set
        {
            int v = Math.Clamp(value, Sight.MinFeet, Sight.MaxFeet);
            if (v == _sightFeet) return;
            _sightFeet = v;
            SightChanged?.Invoke();
            InvalidateVisual();
        }
    }

    public double SightRangeCells => _sightFeet / 5.0;

    /// shift+scroll: one 5 ft step, landing on a multiple of 5
    public void StepSight(int dir)
    {
        int step = Sight.StepFeet;
        int v = dir > 0 ? (_sightFeet / step + 1) * step : ((_sightFeet + step - 1) / step - 1) * step;
        SightFeet = Math.Clamp(v, step, Sight.MaxFeet);
    }

    /// the viewer's cell, or null (panel readout)
    public CellKey? SightCell => ViewerCell();

    /// esc and tool switches. true when something was showing
    public bool ClearSight()
    {
        bool had = _sightCell is not null;
        _sightCell = null;
        _sightDoc = null;
        _sightDragging = false;
        _sightCache = null;
        if (had) { SightChanged?.Invoke(); InvalidateVisual(); }
        return had;
    }

    /// shows sight from a cell (tests and the panel)
    public void ShowSight(CellKey cell)
    {
        _sightCell = cell;
        _sightDoc = Document;
        SightChanged?.Invoke();
        InvalidateVisual();
    }

    // the viewer: the ghost cell while a pawn is dragged, otherwise the set
    // cell. a map switch drops it
    private CellKey? ViewerCell()
    {
        if (Mode != ToolMode.Sight || _sightCell is null) return null;
        if (!ReferenceEquals(_sightDoc, Document)) return null;
        if (_pawnDragging && _pawnDragPawn is not null) return _pawnDragEnd;
        return _sightCell;
    }

    // other pawns' centers as the viewer sees them. while dragging, the
    // dragged pawn is lifted and a pawn under the ghost swaps to its start
    private List<(double x, double y)> SightPawns(CellKey viewer)
    {
        var list = new List<(double, double)>();
        bool drag = _pawnDragging && _pawnDragPawn is not null;
        foreach (var c in Document.Pawns.Keys)
        {
            var at = c;
            if (drag && c == _pawnDragStart) continue;
            if (drag && c == _pawnDragEnd) at = _pawnDragStart;
            if (at == viewer) continue;
            list.Add((at.X + 0.5, at.Y + 0.5));
        }
        return list;
    }

    // ---- cache ----

    private sealed class SightResult
    {
        public required List<SightSegment> Segments;
        public required List<(double x, double y)> Region;
        public required List<List<(double x, double y)>> Shadows;
        public required List<(int a, int b)> Pairs; // shadows that overlap
        public required List<(double x, double y)> Pawns;
    }
    private SightResult? _sightCache;
    private (long print, CellKey viewer, int feet, bool drag, CellKey start)? _sightKey;
    private (long print, List<SightSegment> segs)? _sightSegs;

    private SightResult? SightNow()
    {
        if (ViewerCell() is not CellKey viewer) return null;
        long print = Sight.Fingerprint(Document);
        bool drag = _pawnDragging && _pawnDragPawn is not null;
        var key = (print, viewer, _sightFeet, drag, drag ? _pawnDragStart : default);
        if (_sightCache is not null && _sightKey == key) return _sightCache;

        if (_sightSegs is not { } cached || cached.print != print)
            _sightSegs = (print, Sight.Blockers(Document));
        var segs = _sightSegs.Value.segs;
        double ox = viewer.X + 0.5, oy = viewer.Y + 0.5, r = SightRangeCells;
        var near = Sight.Near(segs, ox, oy, r);
        var region = Sight.Visible(ox, oy, r, near);
        var pawns = SightPawns(viewer);
        var shadows = new List<List<(double, double)>>();
        var spans = new List<(double, double)>();
        foreach (var (px, py) in pawns)
        {
            if (Math.Sqrt((px - ox) * (px - ox) + (py - oy) * (py - oy)) > r + Sight.PawnRadius) continue;
            var sh = Sight.Shadow(ox, oy, px, py, r);
            if (sh is null) continue;
            shadows.Add(sh);
            spans.Add(Sight.ShadowSpan(ox, oy, px, py));
        }
        var pairs = new List<(int, int)>();
        for (int i = 0; i < spans.Count; i++)
            for (int j = i + 1; j < spans.Count; j++)
                if (Sight.SpansOverlap(spans[i], spans[j])) pairs.Add((i, j));
        _sightCache = new SightResult { Segments = near, Region = region, Shadows = shadows, Pairs = pairs, Pawns = pawns };
        _sightKey = key;
        return _sightCache;
    }

    /// what a straight line from the viewer to a cell's center meets (the
    /// hover line), or null with no viewer
    public (SightLine result, int pawns)? SightLineTo(CellKey target)
    {
        if (ViewerCell() is not CellKey viewer || SightNow() is not { } s) return null;
        return Sight.Line(viewer.X + 0.5, viewer.Y + 0.5, target.X + 0.5, target.Y + 0.5, SightRangeCells, s.Segments, s.Pawns);
    }

    // ---- pointer ----

    private bool SightPressed(PointerPressedEventArgs e, CellKey cell)
    {
        if (Mode != ToolMode.Sight) return false;
        _sightCell = cell;
        _sightDoc = Document;
        _sightHover = null;
        // a pawn drags like in the select tool, the region follows the ghost
        if (!BeginPawnDrag(e, cell))
        {
            _sightDragging = true;
            e.Pointer.Capture(this);
        }
        SightChanged?.Invoke();
        InvalidateVisual();
        return true;
    }

    private bool SightMoved(Point world)
    {
        if (Mode != ToolMode.Sight || _pawnDragging) return false;
        var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
        if (_sightDragging)
        {
            // an empty cell held down: the viewer follows the cursor
            if (_sightCell != cell) { _sightCell = cell; SightChanged?.Invoke(); InvalidateVisual(); }
            return true;
        }
        if (_sightHover != cell)
        {
            _sightHover = cell;
            if (_sightCell is not null) InvalidateVisual();
        }
        return true;
    }

    private bool SightReleased(PointerReleasedEventArgs e)
    {
        if (Mode != ToolMode.Sight) return false;
        if (_sightDragging)
        {
            _sightDragging = false;
            e.Pointer.Capture(null);
            return true;
        }
        if (!_pawnDragging || _pawnDragPawn is null) return false;
        _pawnDragging = false;
        e.Pointer.Capture(null);
        var pawn = _pawnDragPawn;
        _pawnDragPawn = null;
        var target = _pawnDragEnd;
        // a release on the start cell only shows its sight, no pawn config
        if (target != _pawnDragStart) LandPawn(pawn, _pawnDragStart, target);
        _sightCell = target;
        _sightDoc = Document;
        SightChanged?.Invoke();
        InvalidateVisual();
        return true;
    }

    // ---- drawing ----

    private static Color SightColor(int r, int g, int b) => Color.FromRgb((byte)r, (byte)g, (byte)b);
    private Color SightClearColor => SightColor(Settings.SightClearR, Settings.SightClearG, Settings.SightClearB);
    private Color SightOneColor => SightColor(Settings.SightOneR, Settings.SightOneG, Settings.SightOneB);
    private Color SightTwoColor => SightColor(Settings.SightTwoR, Settings.SightTwoG, Settings.SightTwoB);

    private StreamGeometry ScreenPolygon(List<(double x, double y)> pts)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        if (pts.Count < 3) return geo;
        g.BeginFigure(WorldToScreen(pts[0].x, pts[0].y), true);
        for (int i = 1; i < pts.Count; i++) g.LineTo(WorldToScreen(pts[i].x, pts[i].y));
        g.EndFigure(true);
        return geo;
    }

    // the tint, drawn under the pawns. opaque colors in one see-through
    // layer, so a red patch replaces the orange under it instead of mixing
    private void DrawSightTint(DrawingContext ctx)
    {
        if (SightNow() is not { } s || s.Region.Count < 3) return;
        double alpha = Math.Clamp(Settings.SightOpacity, 0, AppSettings.MaxSightOpacity) / 100.0;
        if (alpha <= 0) return;
        var region = ScreenPolygon(s.Region);
        var shadows = s.Shadows.Select(ScreenPolygon).ToList();
        var one = new SolidColorBrush(SightOneColor);
        var two = new SolidColorBrush(SightTwoColor);
        using (ctx.PushOpacity(alpha))
        using (ctx.PushGeometryClip(region))
        {
            ctx.DrawGeometry(new SolidColorBrush(SightClearColor), null, region);
            foreach (var sh in shadows) ctx.DrawGeometry(one, null, sh);
            foreach (var (a, b) in s.Pairs)
                using (ctx.PushGeometryClip(shadows[a]))
                    ctx.DrawGeometry(two, null, shadows[b]);
        }
    }

    // everything outside the region dimmed (dim color, sight dim setting),
    // over the whole map, plus a thin range ring and the viewer's dot
    private void DrawSightDim(DrawingContext ctx)
    {
        if (ViewerCell() is not CellKey viewer || SightNow() is not { } s) return;
        byte alpha = (byte)Math.Clamp(Settings.SightDim * 255 / 100, 0, 255);
        if (alpha > 0 && s.Region.Count >= 3)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.SetFillRule(FillRule.EvenOdd);
                g.BeginFigure(new Point(0, 0), true);
                g.LineTo(new Point(Bounds.Width, 0));
                g.LineTo(new Point(Bounds.Width, Bounds.Height));
                g.LineTo(new Point(0, Bounds.Height));
                g.EndFigure(true);
                g.BeginFigure(WorldToScreen(s.Region[0].x, s.Region[0].y), true);
                for (int i = 1; i < s.Region.Count; i++) g.LineTo(WorldToScreen(s.Region[i].x, s.Region[i].y));
                g.EndFigure(true);
            }
            var col = DimColor;
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(alpha, col.R, col.G, col.B)), null, geo);
        }
        var center = WorldToScreen(viewer.X + 0.5, viewer.Y + 0.5);
        var mc = MeasureColor;
        double rr = SightRangeCells * _scale;
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(mc, 0.6), 1.5), center, rr, rr);
        if (!Document.Pawns.ContainsKey(viewer) || _pawnDragging)
            ctx.DrawEllipse(new SolidColorBrush(mc), null, center, 4, 4);
    }

    // the line from the viewer to the hovered cell's center, in the color of
    // what it meets, with a short label
    private void DrawSightLine(DrawingContext ctx)
    {
        if (_pawnDragging || _sightDragging || _sightHover is not CellKey target) return;
        if (ViewerCell() is not CellKey viewer || target == viewer) return;
        if (SightLineTo(target) is not { } line) return;
        var (col, text) = line.result switch
        {
            SightLine.Clear => (SightClearColor, "clear"),
            SightLine.OnePawn => (SightOneColor, "1 pawn in the way"),
            SightLine.TwoPawns => (SightTwoColor, $"{line.pawns} pawns in the way"),
            SightLine.Blocked => (Color.FromRgb(150, 150, 160), "blocked"),
            _ => (Color.FromRgb(150, 150, 160), "out of range"),
        };
        var a = WorldToScreen(viewer.X + 0.5, viewer.Y + 0.5);
        var b = WorldToScreen(target.X + 0.5, target.Y + 0.5);
        var pen = new Pen(new SolidColorBrush(col), 2.5, line.result is SightLine.Blocked or SightLine.OutOfRange ? new DashStyle(new double[] { 3, 2 }, 0) : null);
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 4.5), a, b);
        ctx.DrawLine(pen, a, b);
        ctx.DrawEllipse(new SolidColorBrush(col), null, b, 4, 4);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Inter", FontStyle.Normal, FontWeight.Bold), 13, Brushes.White);
        double w = ft.Width + 10, h = ft.Height + 4;
        var box = new Rect(b.X + 10, b.Y - h - 6, w, h);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 12, 12, 16)), new Pen(new SolidColorBrush(col), 1), box, 4, 4);
        ctx.DrawText(ft, new Point(box.X + 5, box.Y + 2));
    }
}
