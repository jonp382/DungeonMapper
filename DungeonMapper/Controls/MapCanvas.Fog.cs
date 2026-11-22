using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// the fog tool (vision radius). reading only, like the measure tool: nothing
// here touches the document, commits history or counts as an unsaved change.
// see Agent.md "Fog of War Design".
//  - the whole map is dimmed (settings: fog color and opacity) while the fog
//    tool is selected and FogOn is set
//  - the center follows the cursor; a click anchors it, a click elsewhere
//    moves the anchor, ReleaseFogAnchor (esc) goes back to following
//  - the center snaps to the closest cell center or cell corner; anywhere on
//    a pawn's cell snaps to the pawn
//  - whole cells whose center is inside or on the circle are revealed
//    (CellsInRadius, same as the measure tool), walls ignored
public sealed partial class MapCanvas
{
    public const int FogMaxFeet = 120;
    public const int FogStepFeet = 10;
    public const int FogDefaultFeet = 30;

    /// the panel checkbox. the fog only ever shows with the fog tool selected
    public bool FogOn { get; set; } = true;

    private int _fogFeet = FogDefaultFeet;
    /// vision radius in feet, 0 to 120 in steps of 10 (session only)
    public int FogFeet
    {
        get => _fogFeet;
        set
        {
            int v = Math.Clamp((int)Math.Round(value / (double)FogStepFeet) * FogStepFeet, 0, FogMaxFeet);
            if (v == _fogFeet) return;
            _fogFeet = v;
            FogChanged?.Invoke();
            InvalidateVisual();
        }
    }

    /// radius, center or anchor changed (panel slider and readout)
    public event Action? FogChanged;

    private Point? _fogAnchor; // clicked center
    private Point? _fogHover;  // center under the cursor, null when it left the map
    private bool _fogDragging;

    public bool FogActive => Mode == ToolMode.Fog && FogOn;
    public bool FogAnchored => _fogAnchor is not null;
    /// the reveal center in world units (anchor first, then the cursor)
    public Point? FogCenter => _fogAnchor ?? _fogHover;
    public int FogRadiusCells => _fogFeet / 5;

    /// cells revealed around the current center
    public List<CellKey> FogRevealed() =>
        FogCenter is Point c ? CellsInRadius(c, FogRadiusCells) : new List<CellKey>();

    /// back to following the cursor. true when there was an anchor
    public bool ReleaseFogAnchor()
    {
        if (_fogAnchor is null) return false;
        _fogAnchor = null;
        FogChanged?.Invoke();
        InvalidateVisual();
        return true;
    }

    /// a tool switch: the anchor goes, the radius stays for the session
    public void ResetFog()
    {
        _fogAnchor = null;
        _fogHover = null;
        _fogDragging = false;
        FogOn = true;
    }

    /// shift+scroll: +1 grows the radius by one step, -1 shrinks it
    public void StepFog(int dir) => FogFeet = _fogFeet + Math.Sign(dir) * FogStepFeet;

    /// the closest cell center or cell corner, or the pawn on that cell
    public Point FogSnap(Point world)
    {
        var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
        var center = CellCenter(cell);
        if (Document.Pawns.ContainsKey(cell)) return center;
        var corner = new Point(Math.Round(world.X), Math.Round(world.Y));
        return Dist(world, corner) < Dist(world, center) ? corner : center;
    }

    // ---- pointer ----

    private bool FogPressed(PointerPressedEventArgs e, Point world)
    {
        if (Mode != ToolMode.Fog) return false;
        _fogAnchor = FogSnap(world);
        _fogDragging = true;
        e.Pointer.Capture(this);
        FogChanged?.Invoke();
        InvalidateVisual();
        return true;
    }

    private bool FogMoved(Point world)
    {
        if (Mode != ToolMode.Fog) return false;
        var p = FogSnap(world);
        if (_fogDragging)
        {
            // dragging with the button down moves the anchor along
            if (_fogAnchor != p) { _fogAnchor = p; FogChanged?.Invoke(); InvalidateVisual(); }
        }
        else if (_fogHover != p)
        {
            _fogHover = p;
            if (_fogAnchor is null) { FogChanged?.Invoke(); InvalidateVisual(); }
        }
        return true;
    }

    private bool FogReleased(PointerReleasedEventArgs e)
    {
        if (Mode != ToolMode.Fog || !_fogDragging) return false;
        _fogDragging = false;
        e.Pointer.Capture(null);
        return true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        CursorCell = null;
        // the cursor left the map: without an anchor everything is dimmed
        if (_fogHover is null) return;
        _fogHover = null;
        if (Mode == ToolMode.Fog && _fogAnchor is null) { FogChanged?.Invoke(); InvalidateVisual(); }
    }

    // ---- drawing ----

    private Color FogColor => Color.FromRgb((byte)Settings.FogR, (byte)Settings.FogG, (byte)Settings.FogB);

    // the dim over the whole canvas with the revealed cells cut out. the
    // revealed cells of a circle are one run per row, so each row is a
    // rectangle and the even odd rule punches them out of the screen rect.
    private void DrawFog(DrawingContext ctx)
    {
        if (!FogActive) return;
        var cells = FogRevealed();
        byte alpha = (byte)Math.Clamp(Settings.FogOpacity * 255 / 100, 0, 255);
        if (alpha > 0)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.SetFillRule(FillRule.EvenOdd);
                void Rect(double x0, double y0, double x1, double y1)
                {
                    g.BeginFigure(new Point(x0, y0), true);
                    g.LineTo(new Point(x1, y0));
                    g.LineTo(new Point(x1, y1));
                    g.LineTo(new Point(x0, y1));
                    g.EndFigure(true);
                }
                Rect(0, 0, Bounds.Width, Bounds.Height);
                foreach (var row in cells.GroupBy(c => c.Y))
                {
                    int x0 = row.Min(c => c.X), x1 = row.Max(c => c.X) + 1;
                    var a = WorldToScreen(x0, row.Key);
                    var b = WorldToScreen(x1, row.Key + 1);
                    Rect(a.X, a.Y, b.X, b.Y);
                }
            }
            var col = FogColor;
            ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(alpha, col.R, col.G, col.B)), null, geo);
        }

        if (FogCenter is not Point c) return;
        // a thin ring at the edge and the radius at the center, in the measure color
        var mc = MeasureColor;
        var brush = new SolidColorBrush(mc);
        var center = WorldToScreen(c.X, c.Y);
        double r = FogRadiusCells * _scale;
        if (r > 0) ctx.DrawEllipse(null, new Pen(new SolidColorBrush(mc, 0.8), 1.5), center, r, r);
        ctx.DrawEllipse(brush, null, center, _fogAnchor is null ? 3 : 4.5, _fogAnchor is null ? 3 : 4.5);
        var ft = new FormattedText($"{_fogFeet} ft", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Inter", FontStyle.Normal, FontWeight.Bold), 14, Brushes.White);
        double w = ft.Width + 10, h = ft.Height + 4;
        var box = new Rect(center.X - w / 2, center.Y + 8, w, h);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 12, 12, 16)), new Pen(brush, 1), box, 4, 4);
        ctx.DrawText(ft, new Point(box.X + 5, box.Y + 2));
    }
}
