using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// the measure tool (range finder). reading only: nothing here touches the
// document, commits history or counts as an unsaved change. see Agent.md
// "Range Finder Design".
//  - line: drag between two points; ctrl+click / ctrl+drag adds a leg from the
//    last end point (waypoints)
//  - radius: drag out from a pawn, or shift+drag from any point. whole cells,
//    walls ignored. cells whose center is inside or on the circle are caught.
//    dragging from a pawn onto another pawn measures the line between them.
// a measurement stays until esc, a tool switch, a new one, or ClearMeasure.
public sealed partial class MapCanvas
{
    public enum MeasureKind { None, Line, Radius }

    /// ends snap to cell corners instead of cell centers (panel chip)
    public bool MeasureCorners { get; set; }

    /// the measurement changed (panel readout)
    public event Action? MeasureChanged;

    private MeasureKind _measureKind;
    // line: world points, at least two once started
    private readonly List<Point> _measurePts = new();
    // radius: center (world) and radius in whole cells
    private Point _measureCenter;
    private int _measureRadius;
    private CellKey? _measureFromPawn; // the radius started on this pawn's cell
    private bool _measuring;           // a left drag is in progress

    public MeasureKind MeasureMode => _measureKind;
    public bool HasMeasure => _measureKind != MeasureKind.None;
    public IReadOnlyList<Point> MeasurePoints => _measurePts;
    public Point MeasureCenter => _measureCenter;
    public int MeasureRadius => _measureRadius;

    public void ClearMeasure()
    {
        bool had = HasMeasure;
        _measureKind = MeasureKind.None;
        _measurePts.Clear();
        _measureRadius = 0;
        _measureFromPawn = null;
        _measuring = false;
        if (had) { MeasureChanged?.Invoke(); InvalidateVisual(); }
    }

    private Point MeasureSnap(Point world) => MeasureCorners
        ? new Point(Math.Round(world.X), Math.Round(world.Y))
        : new Point(Math.Floor(world.X) + 0.5, Math.Floor(world.Y) + 0.5);

    private static Point CellCenter(CellKey c) => new(c.X + 0.5, c.Y + 0.5);

    // ---- distances ----

    public static double Dist(Point a, Point b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    /// the last leg: horizontal, vertical, straight line (cells)
    public (double h, double v, double straight) MeasureLeg()
    {
        if (_measurePts.Count < 2) return (0, 0, 0);
        var a = _measurePts[^2];
        var b = _measurePts[^1];
        return (Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y), Dist(a, b));
    }

    /// the whole path's straight line length (cells)
    public double MeasurePathTotal()
    {
        double t = 0;
        for (int i = 1; i < _measurePts.Count; i++) t += Dist(_measurePts[i - 1], _measurePts[i]);
        return t;
    }

    public int MeasureLegCount => Math.Max(0, _measurePts.Count - 1);

    /// cells caught by the radius: center inside or on the circle
    public static List<CellKey> CellsInRadius(Point center, int radius)
    {
        var list = new List<CellKey>();
        if (radius <= 0) return list;
        double r2 = (double)radius * radius + 1e-9;
        int x0 = (int)Math.Floor(center.X - radius - 1), x1 = (int)Math.Ceiling(center.X + radius + 1);
        int y0 = (int)Math.Floor(center.Y - radius - 1), y1 = (int)Math.Ceiling(center.Y + radius + 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double dx = x + 0.5 - center.X, dy = y + 0.5 - center.Y;
                if (dx * dx + dy * dy <= r2) list.Add(new CellKey(x, y));
            }
        return list;
    }

    /// pawns on caught cells (radius only), the source pawn included
    public List<Pawn> MeasureCaughtPawns()
    {
        if (_measureKind != MeasureKind.Radius) return new();
        var res = new List<Pawn>();
        foreach (var c in CellsInRadius(_measureCenter, _measureRadius))
            if (Document.Pawns.TryGetValue(c, out var p)) res.Add(p);
        return res;
    }

    /// "10 cells (50 ft)", "4.2 cells (21.2 ft)"
    public static string FormatCells(double cells)
    {
        double c = Math.Round(cells, 1), ft = Math.Round(cells * 5, 1);
        string n(double v) => v.ToString(v == Math.Floor(v) ? "0" : "0.0", CultureInfo.InvariantCulture);
        return $"{n(c)} {(c == 1 ? "cell" : "cells")} ({n(ft)} ft)";
    }

    /// the readout lines shown on the map and in the panel
    public List<string> MeasureReadout()
    {
        var lines = new List<string>();
        if (_measureKind == MeasureKind.Line && _measurePts.Count >= 2)
        {
            var (h, v, s) = MeasureLeg();
            lines.Add(FormatCells(s));
            lines.Add($"horizontal {FormatCells(h)}");
            lines.Add($"vertical {FormatCells(v)}");
            if (MeasureLegCount > 1) lines.Add($"path total {FormatCells(MeasurePathTotal())}, {MeasureLegCount} legs");
        }
        else if (_measureKind == MeasureKind.Radius)
        {
            lines.Add($"radius {FormatCells(_measureRadius)}");
            int n = MeasureCaughtPawns().Count;
            lines.Add(n == 1 ? "1 pawn inside" : $"{n} pawns inside");
        }
        return lines;
    }

    // ---- pointer ----

    private bool MeasurePressed(PointerPressedEventArgs e, Point world, CellKey cell)
    {
        if (Mode != ToolMode.Measure) return false;
        var mods = e.KeyModifiers;
        if (mods.HasFlag(KeyModifiers.Control) && _measureKind == MeasureKind.Line && _measurePts.Count >= 2)
        {
            // waypoint: a new leg from the last end point
            _measurePts.Add(MeasureSnap(world));
        }
        else if (!mods.HasFlag(KeyModifiers.Shift) && Document.Pawns.ContainsKey(cell))
        {
            _measureKind = MeasureKind.Radius;
            _measurePts.Clear();
            _measureCenter = CellCenter(cell);
            _measureFromPawn = cell;
            _measureRadius = 0;
        }
        else if (mods.HasFlag(KeyModifiers.Shift))
        {
            _measureKind = MeasureKind.Radius;
            _measurePts.Clear();
            _measureCenter = MeasureSnap(world);
            _measureFromPawn = null;
            _measureRadius = 0;
        }
        else
        {
            _measureKind = MeasureKind.Line;
            _measurePts.Clear();
            var p = MeasureSnap(world);
            _measurePts.Add(p);
            _measurePts.Add(p);
            _measureFromPawn = null;
        }
        _measuring = true;
        e.Pointer.Capture(this);
        MeasureChanged?.Invoke();
        InvalidateVisual();
        return true;
    }

    // dragging from a pawn onto another pawn: the pawn to pawn line
    private CellKey? MeasurePawnTarget(Point world)
    {
        if (_measureFromPawn is not CellKey src) return null;
        var c = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
        return c != src && Document.Pawns.ContainsKey(c) ? c : null;
    }

    private bool MeasureMoved(Point world)
    {
        if (Mode != ToolMode.Measure || !_measuring) return false;
        if (_measureFromPawn is null && _measureKind == MeasureKind.Line)
        {
            _measurePts[^1] = MeasureSnap(world);
        }
        else if (_measureFromPawn is null)
        {
            _measureRadius = (int)Math.Round(Dist(_measureCenter, world));
        }
        else
        {
            // from a pawn: radius, or a line while over another pawn
            if (MeasurePawnTarget(world) is CellKey t)
            {
                // shown as a line while over the other pawn
                _measureKind = MeasureKind.Line;
                _measurePts.Clear();
                _measurePts.Add(_measureCenter);
                _measurePts.Add(CellCenter(t));
            }
            else
            {
                _measureKind = MeasureKind.Radius;
                _measurePts.Clear();
                _measureRadius = (int)Math.Round(Dist(_measureCenter, world));
            }
        }
        MeasureChanged?.Invoke();
        InvalidateVisual();
        return true;
    }

    private bool MeasureReleased(PointerReleasedEventArgs e)
    {
        if (Mode != ToolMode.Measure || !_measuring) return false;
        MeasureMoved(ScreenToWorld(e.GetPosition(this)));
        _measuring = false;
        // the pawn to pawn line is a plain line from here on
        if (_measureKind == MeasureKind.Line) _measureFromPawn = null;
        e.Pointer.Capture(null);
        MeasureChanged?.Invoke();
        InvalidateVisual();
        return true;
    }

    // ---- drawing ----

    private Color MeasureColor => Color.FromRgb((byte)Settings.MeasureR, (byte)Settings.MeasureG, (byte)Settings.MeasureB);

    private void DrawMeasureOverlay(DrawingContext ctx)
    {
        if (_measureKind == MeasureKind.None) return;
        var col = MeasureColor;
        var brush = new SolidColorBrush(col);
        var pen = new Pen(brush, 3);
        Point label;

        if (_measureKind == MeasureKind.Line && _measurePts.Count >= 2)
        {
            // the current leg's horizontal and vertical parts, dashed
            var a = _measurePts[^2];
            var b = _measurePts[^1];
            var dash = new Pen(new SolidColorBrush(col, 0.6), 1.5) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
            ctx.DrawLine(dash, WorldToScreen(a.X, a.Y), WorldToScreen(b.X, a.Y));
            ctx.DrawLine(dash, WorldToScreen(b.X, a.Y), WorldToScreen(b.X, b.Y));
            for (int i = 1; i < _measurePts.Count; i++)
                ctx.DrawLine(pen, WorldToScreen(_measurePts[i - 1].X, _measurePts[i - 1].Y), WorldToScreen(_measurePts[i].X, _measurePts[i].Y));
            foreach (var p in _measurePts)
                ctx.DrawEllipse(brush, null, WorldToScreen(p.X, p.Y), 4.5, 4.5);
            label = WorldToScreen(b.X, b.Y);
        }
        else
        {
            var center = WorldToScreen(_measureCenter.X, _measureCenter.Y);
            var cells = CellsInRadius(_measureCenter, _measureRadius);
            byte alpha = (byte)Math.Clamp(Settings.MeasureShadeOpacity * 255 / 100, 0, 255);
            if (alpha > 0)
            {
                var shade = new SolidColorBrush(Color.FromArgb(alpha, col.R, col.G, col.B));
                foreach (var c in cells)
                {
                    var p0 = WorldToScreen(c.X, c.Y);
                    ctx.DrawRectangle(shade, null, new Rect(p0.X, p0.Y, _scale, _scale));
                }
            }
            // caught pawns get a ring
            var ring = new Pen(brush, 3);
            foreach (var c in cells)
                if (Document.Pawns.ContainsKey(c))
                {
                    var pc = WorldToScreen(c.X + 0.5, c.Y + 0.5);
                    ctx.DrawEllipse(null, ring, pc, _scale * 0.5, _scale * 0.5);
                }
            double r = _measureRadius * _scale;
            if (r > 0) ctx.DrawEllipse(null, pen, center, r, r);
            ctx.DrawEllipse(brush, null, center, 4.5, 4.5);
            label = new Point(center.X + r, center.Y);
            if (r > 0) ctx.DrawLine(new Pen(brush, 1.5), center, label);
        }

        DrawMeasureLabel(ctx, label, MeasureReadout(), col);
    }

    // large, high contrast readout next to the end point (shown on a tv)
    private void DrawMeasureLabel(DrawingContext ctx, Point at, List<string> lines, Color col)
    {
        if (lines.Count == 0) return;
        var tf = new Typeface("Inter", FontStyle.Normal, FontWeight.Bold);
        var fts = lines.Select((t, i) => new FormattedText(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            tf, i == 0 ? 20 : 15, i == 0 ? new SolidColorBrush(col) : Brushes.White)).ToList();
        double w = fts.Max(f => f.Width) + 16, h = fts.Sum(f => f.Height) + 10;
        double x = at.X + 14, y = at.Y + 14;
        // keep it on screen
        if (x + w > Bounds.Width) x = at.X - 14 - w;
        if (y + h > Bounds.Height) y = at.Y - 14 - h;
        x = Math.Max(2, x); y = Math.Max(2, y);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(225, 12, 12, 16)), new Pen(new SolidColorBrush(col), 1.5),
            new Rect(x, y, w, h), 6, 6);
        double ty = y + 5;
        foreach (var f in fts) { ctx.DrawText(f, new Point(x + 8, ty)); ty += f.Height; }
    }
}
