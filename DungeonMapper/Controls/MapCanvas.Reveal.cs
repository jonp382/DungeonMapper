using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

public enum RevealTarget { Cells, Items }
public enum RevealShape { Box, Room, Circle }

// the reveal tool (multiplayer, see Agent.md "Multiplayer Design"). the DM
// decides what players see:
//  - cells: show or hide floor cells by box (a click is one cell), room (a
//    click fills to the walls, doors count as walls) or circle (drag out from
//    a center that snaps to the closest cell center, corner or pawn)
//  - items: a box marks labels, doors, objects and pawns (filtered by kind)
//    DM-only or visible
// every change is one undo step. while the tool is selected, unrevealed floor
// is dimmed (dim color and darkness) and DM-only items get a dashed outline.
public sealed partial class MapCanvas
{
    public RevealTarget RevealTarget { get; set; } = RevealTarget.Cells;
    public RevealShape RevealShape { get; set; } = RevealShape.Box;
    /// true shows (reveal / visible), false hides (unreveal / DM-only)
    public bool RevealShow { get; set; } = true;
    public RevealItems RevealKinds { get; set; } = RevealItems.All;

    /// a reveal or visibility change landed (panel counts)
    public event Action? RevealChanged;

    private bool _revealDragging;
    private CellKey _revealStart, _revealEnd;
    private Point _revealCenter;
    private int _revealRadius;
    private CellKey? _revealHover;

    private static readonly Color DmOnlyColor = Color.FromRgb(236, 90, 220);

    /// the cells the current drag (or hover, for room) would change
    public List<CellKey> RevealPreviewCells()
    {
        if (RevealTarget != RevealTarget.Cells) return new();
        if (RevealShape == RevealShape.Room)
            return _revealHover is CellKey h ? Document.RevealRoomCells(h).ToList() : new();
        if (!_revealDragging) return new();
        if (RevealShape == RevealShape.Circle) return CellsInRadius(_revealCenter, _revealRadius);
        var (x0, y0, x1, y1) = NormRect(_revealStart, _revealEnd);
        var list = new List<CellKey>();
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++) list.Add(new CellKey(x, y));
        return list;
    }

    // ---- pointer ----

    private bool RevealPressed(PointerPressedEventArgs e, Point world, CellKey cell)
    {
        if (Mode != ToolMode.Reveal) return false;
        if (RevealTarget == RevealTarget.Cells && RevealShape == RevealShape.Room)
        {
            ApplyReveal(Document.RevealRoomCells(cell), "room");
            return true;
        }
        _revealDragging = true;
        _revealStart = _revealEnd = cell;
        _revealCenter = SnapCenter(world);
        _revealRadius = 0;
        e.Pointer.Capture(this);
        InvalidateVisual();
        return true;
    }

    private bool RevealMoved(Point world)
    {
        if (Mode != ToolMode.Reveal) return false;
        var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
        if (!_revealDragging)
        {
            // room mode previews the room under the cursor
            if (RevealTarget == RevealTarget.Cells && RevealShape == RevealShape.Room && _revealHover != cell)
            {
                _revealHover = cell;
                InvalidateVisual();
            }
            return false; // let hover handling elsewhere run
        }
        _revealEnd = cell;
        _revealRadius = (int)Math.Round(Dist(_revealCenter, world));
        InvalidateVisual();
        return true;
    }

    private bool RevealReleased(PointerReleasedEventArgs e)
    {
        if (Mode != ToolMode.Reveal || !_revealDragging) return false;
        e.Pointer.Capture(null);
        if (RevealTarget == RevealTarget.Items)
        {
            var (x0, y0, x1, y1) = NormRect(_revealStart, _revealEnd);
            _revealDragging = false;
            int n = Document.SetDmOnly(x0, y0, x1, y1, RevealKinds, !RevealShow);
            if (n > 0) Commit();
            Status(n == 0
                ? "no items changed in that area"
                : $"{n} {(n == 1 ? "item" : "items")} now {(RevealShow ? "visible to players" : "DM only")}", n == 0 ? StatusKind.Warning : StatusKind.Info);
            RevealChanged?.Invoke();
            InvalidateVisual();
            return true;
        }
        var cells = RevealPreviewCells();
        _revealDragging = false;
        ApplyReveal(cells, RevealShape == RevealShape.Circle ? "circle" : "area");
        return true;
    }

    private void ApplyReveal(IEnumerable<CellKey> cells, string what)
    {
        int n = Document.SetRevealed(cells, RevealShow);
        if (n > 0) Commit();
        Status(n == 0
            ? $"nothing to {(RevealShow ? "reveal" : "hide")} in that {what}"
            : $"{(RevealShow ? "revealed" : "hid")} {n} {(n == 1 ? "cell" : "cells")}", n == 0 ? StatusKind.Warning : StatusKind.Info);
        RevealChanged?.Invoke();
        InvalidateVisual();
    }

    /// whole map: reveal or hide every floor cell. returns how many changed
    public int RevealAll(bool show)
    {
        int n = show ? Document.SetRevealed(Document.FloorCells(), true)
                     : Document.SetRevealed(Document.Revealed.ToList(), false);
        if (n > 0) Commit();
        RevealChanged?.Invoke();
        InvalidateVisual();
        return n;
    }

    /// whole map: marks every item of the chosen kinds DM-only or visible
    public int SetAllDmOnly(bool dmOnly)
    {
        int n = 0;
        foreach (var item in Document.AllItems(RevealKinds))
            if (MapDocument.IsDmOnly(item) != dmOnly) { MapDocument.SetDmOnly(item, dmOnly); n++; }
        if (n > 0) Commit();
        RevealChanged?.Invoke();
        InvalidateVisual();
        return n;
    }

    /// a tool switch drops a drag in progress
    public void ResetReveal()
    {
        _revealDragging = false;
        _revealHover = null;
    }

    // ---- drawing ----

    // one rect per run of cells in a row, filled with the even odd rule off
    private void FillCellRuns(DrawingContext ctx, IEnumerable<CellKey> cells, IBrush brush, IPen? pen = null)
    {
        var geo = new StreamGeometry();
        bool any = false;
        using (var g = geo.Open())
        {
            foreach (var row in cells.GroupBy(c => c.Y))
            {
                var xs = row.Select(c => c.X).Distinct().OrderBy(x => x).ToList();
                int i = 0;
                while (i < xs.Count)
                {
                    int j = i;
                    while (j + 1 < xs.Count && xs[j + 1] == xs[j] + 1) j++;
                    var a = WorldToScreen(xs[i], row.Key);
                    var b = WorldToScreen(xs[j] + 1, row.Key + 1);
                    g.BeginFigure(a, true);
                    g.LineTo(new Point(b.X, a.Y));
                    g.LineTo(b);
                    g.LineTo(new Point(a.X, b.Y));
                    g.EndFigure(true);
                    any = true;
                    i = j + 1;
                }
            }
        }
        if (any) ctx.DrawGeometry(brush, pen, geo);
    }

    private void DrawRevealOverlay(DrawingContext ctx)
    {
        if (Mode != ToolMode.Reveal) return;

        // unrevealed floor, dimmed with the dim settings
        var hidden = Document.FloorCells().Where(c => !Document.Revealed.Contains(c));
        byte alpha = (byte)Math.Clamp(Settings.DimOpacity * 255 / 100, 0, 255);
        var dim = DimColor;
        if (alpha > 0) FillCellRuns(ctx, hidden, new SolidColorBrush(Color.FromArgb(alpha, dim.R, dim.G, dim.B)));

        // DM-only items: a dashed outline
        var pen = new Pen(new SolidColorBrush(DmOnlyColor), 2, new DashStyle(new double[] { 3, 2 }, 0));
        void Box(double x0, double y0, double x1, double y1, double inset)
        {
            var a = WorldToScreen(x0, y0);
            var b = WorldToScreen(x1, y1);
            double k = inset * _scale;
            ctx.DrawRectangle(null, pen, new Rect(a.X + k, a.Y + k, Math.Max(1, b.X - a.X - 2 * k), Math.Max(1, b.Y - a.Y - 2 * k)), 3, 3);
        }
        void Dot(double wx, double wy)
        {
            var p = WorldToScreen(wx, wy);
            double r = Math.Max(6, _scale * 0.3);
            ctx.DrawEllipse(null, pen, p, r, r);
        }
        foreach (var l in Document.Labels) if (l.DmOnly) Box(l.Cell.X, l.Cell.Y, l.Cell.X + l.W, l.Cell.Y + l.H, 0.06);
        foreach (var o in Document.Objects) if (o.DmOnly) Box(o.X, o.Y, o.X + o.W, o.Y + o.H, 0.02);
        foreach (var im in Document.Images) if (im.DmOnly) Box(im.X, im.Y, im.X + im.W, im.Y + im.H, 0.04);
        foreach (var (c, p) in Document.Pawns)
            if (p.DmOnly)
            {
                var ctr = WorldToScreen(c.X + 0.5, c.Y + 0.5);
                double r = _scale * 0.48;
                ctx.DrawEllipse(null, pen, ctr, r, r);
            }
        foreach (var (e, d) in Document.Doors)
            if (d.DmOnly)
            {
                if (e.Side == Side.North) Dot(e.Cell.X + 0.5, e.Cell.Y);
                else Dot(e.Cell.X + 1, e.Cell.Y + 0.5); // normalized keys are north or east
            }
        foreach (var (k, d) in Document.VertexDoors) if (d.DmOnly) Dot(k.X, k.Y);
        foreach (var (w, doors) in Document.AngledDoors)
            foreach (var d in doors)
                if (d.DmOnly) { var (px, py) = MapDocument.AngledDoorPoint(w, d); Dot(px, py); }

        // what the drag (or room hover) will change: green shows, red hides
        var tint = RevealShow ? Color.FromRgb(90, 220, 120) : Color.FromRgb(235, 80, 80);
        if (RevealTarget == RevealTarget.Cells)
        {
            var cells = RevealPreviewCells();
            if (cells.Count > 0)
                FillCellRuns(ctx, cells, new SolidColorBrush(tint, 0.3), new Pen(new SolidColorBrush(tint, 0.9), 1));
            if (_revealDragging && RevealShape == RevealShape.Circle && _revealRadius > 0)
            {
                var ctr = WorldToScreen(_revealCenter.X, _revealCenter.Y);
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(tint), 1.5), ctr, _revealRadius * _scale, _revealRadius * _scale);
            }
        }
        else if (_revealDragging)
        {
            var (x0, y0, x1, y1) = NormRect(_revealStart, _revealEnd);
            var a = WorldToScreen(x0, y0);
            var b = WorldToScreen(x1 + 1, y1 + 1);
            ctx.DrawRectangle(new SolidColorBrush(tint, 0.15), new Pen(new SolidColorBrush(tint), 1.5, new DashStyle(new double[] { 4, 3 }, 0)),
                new Rect(a, b));
        }
    }

    /// the dim over hidden floor (reveal tool) and outside the sight region
    private Color DimColor => Color.FromRgb((byte)Settings.DimR, (byte)Settings.DimG, (byte)Settings.DimB);

    /// the closest cell center or cell corner, or the pawn on that cell (the
    /// reveal circle's center)
    public Point SnapCenter(Point world)
    {
        var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
        var center = CellCenter(cell);
        if (Document.Pawns.ContainsKey(cell)) return center;
        var corner = new Point(Math.Round(world.X), Math.Round(world.Y));
        return Dist(world, corner) < Dist(world, center) ? corner : center;
    }
}
