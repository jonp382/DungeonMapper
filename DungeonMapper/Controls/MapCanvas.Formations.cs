using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// formations on the canvas: picking an area to capture one, and the armed
// formation (ghost at the cursor, R / Shift+R or shift+scroll rotates, a click
// places every member in one undo step, then it disarms). see
// Models/Formations.cs and Agent.md "Formation Manager Design".
public sealed partial class MapCanvas
{
    // ---- area pick (capture a formation) ----

    private Action<int, int, int, int>? _areaPicked;
    private bool _picking;
    private CellKey _pickStart, _pickEnd;

    public bool PickingArea => _areaPicked is not null;

    /// the next drag on the map picks a rectangle (x0, y0, x1, y1 in cells)
    /// and hands it to picked. Esc or a tool switch cancels.
    public void BeginAreaPick(Action<int, int, int, int> picked)
    {
        CancelFormation();
        _areaPicked = picked;
        _picking = false;
        InvalidateVisual();
    }

    public void CancelAreaPick()
    {
        _areaPicked = null;
        _picking = false;
        InvalidateVisual();
    }

    // ---- armed formation ----

    private List<(string id, int dx, int dy)>? _formation;
    private string _formationName = "";
    private Point? _formationCursor;

    public bool FormationArmed => _formation is not null;

    /// a formation was placed (for the pawn panel counts)
    public event Action? FormationDeployed;

    public void ArmFormation(string name, IEnumerable<(string templateId, int dx, int dy)> members)
    {
        CancelAreaPick();
        CancelMove();
        _formation = Formation.Rotate(members, 0);
        _formationName = string.IsNullOrWhiteSpace(name) ? "formation" : name;
        Status($"deploy {_formationName}: click to place {_formation.Count} pawns. r / shift+r rotates, esc cancels");
        InvalidateVisual();
    }

    public void CancelFormation()
    {
        if (_formation is null) return;
        _formation = null;
        InvalidateVisual();
    }

    public void RotateFormation(int turns)
    {
        if (_formation is null) return;
        _formation = Formation.Rotate(_formation, turns);
        InvalidateVisual();
    }

    // the cells the members land on with the box centered on the cursor cell
    private List<(string id, CellKey cell)> FormationTargets(CellKey cursor)
    {
        if (_formation is null || _formation.Count == 0) return new();
        int w = _formation.Max(m => m.dx) + 1, h = _formation.Max(m => m.dy) + 1;
        int ox = cursor.X - (w - 1) / 2, oy = cursor.Y - (h - 1) / 2;
        return _formation.Select(m => (m.id, new CellKey(ox + m.dx, oy + m.dy))).ToList();
    }

    // heroes in the formation move, so the cells they stand on now do not block
    private HashSet<string> FormationHeroes() =>
        _formation is null ? new() : _formation.Select(m => m.id).Where(id => Library.Find(id)?.Hero == true).ToHashSet();

    private List<CellKey> BlockedCells(List<(string id, CellKey cell)> targets)
    {
        var heroes = FormationHeroes();
        return targets.Where(t => Document.Pawns.TryGetValue(t.cell, out var occ) && !heroes.Contains(occ.TemplateId))
            .Select(t => t.cell).ToList();
    }

    /// Places the armed formation with its box centered on the cursor cell. A
    /// cell holding a pawn that is not moving with the formation blocks the
    /// whole deploy and it stays armed. Heroes leave their old spots (on any
    /// map) and keep their status, other members are new copies. Returns false
    /// when blocked.
    public bool DeployFormationAt(CellKey cursor)
    {
        if (_formation is null) return false;
        var targets = FormationTargets(cursor);
        var blocked = BlockedCells(targets);
        if (blocked.Count > 0)
        {
            Status($"can not deploy {_formationName}: {(blocked.Count == 1 ? "a cell already has" : $"{blocked.Count} cells already have")} a pawn. move the ghost and click again", StatusKind.Error);
            InvalidateVisual();
            return false;
        }
        if (_remote is not null)
        {
            // a joined player: the DM's app places them
            if (PlayerPawns.CheckDeploy(_remote, Library, targets) is { } why)
            {
                Status($"can not deploy {_formationName}: {why}", StatusKind.Error);
                return false;
            }
            _formation = null;
            InvalidateVisual();
            PlayerDeployRequested?.Invoke(targets);
            Status($"deploying {_formationName}: {targets.Count} pawns");
            return true;
        }
        // heroes come off the map (and the other maps) first, so a hero
        // standing where another member lands is not overwritten with its status
        var statuses = new Dictionary<string, string?>();
        var hidden = new Dictionary<string, bool>(); // heroes keep their dm only flag too
        foreach (var id in FormationHeroes())
        {
            foreach (var (c, p) in Document.Pawns.Where(kv => kv.Value.TemplateId == id).ToList())
            {
                if (!statuses.ContainsKey(id)) { statuses[id] = p.Status; hidden[id] = p.DmOnly; }
                Document.Pawns.Remove(c);
            }
            var other = TakeHeroFromOtherMaps?.Invoke(id);
            if (!statuses.ContainsKey(id) && other is not null) { statuses[id] = other.Status; hidden[id] = other.DmOnly; }
        }
        int outside = 0;
        foreach (var (id, cell) in targets)
        {
            Document.PlacePawn(new Pawn { Cell = cell, TemplateId = id, Status = statuses.GetValueOrDefault(id), DmOnly = hidden.GetValueOrDefault(id, true) });
            if (!Document.FloorAt(cell.X + 0.5, cell.Y + 0.5)) outside++;
        }
        string name = _formationName;
        _formation = null;
        Commit();
        Status($"deployed {name}: {targets.Count} pawns" + (outside > 0 ? $". warning: {outside} outside a room" : ""), outside > 0 ? StatusKind.Warning : StatusKind.Info);
        FormationDeployed?.Invoke();
        return true;
    }

    // ---- input, called first from the main handlers ----

    private bool FormationPressed(PointerPressedEventArgs e, CellKey cell)
    {
        if (_areaPicked is not null)
        {
            _picking = true;
            _pickStart = _pickEnd = cell;
            e.Pointer.Capture(this);
            InvalidateVisual();
            return true;
        }
        if (_formation is not null)
        {
            DeployFormationAt(cell);
            return true;
        }
        return false;
    }

    private bool FormationMoved(Point world)
    {
        var cell = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
        if (_picking)
        {
            if (cell != _pickEnd) { _pickEnd = cell; InvalidateVisual(); }
            return true;
        }
        if (_formation is not null)
        {
            _formationCursor = world;
            InvalidateVisual();
            return true;
        }
        return false;
    }

    private bool FormationReleased(PointerReleasedEventArgs e)
    {
        if (!_picking) return false;
        _picking = false;
        e.Pointer.Capture(null);
        var picked = _areaPicked;
        _areaPicked = null;
        var (x0, y0, x1, y1) = NormRect(_pickStart, _pickEnd);
        InvalidateVisual();
        picked?.Invoke(x0, y0, x1, y1);
        return true;
    }

    private void DrawFormationOverlay(DrawingContext ctx)
    {
        if (_picking)
        {
            var (x0, y0, x1, y1) = NormRect(_pickStart, _pickEnd);
            var tl = WorldToScreen(x0, y0);
            var rect = new Rect(tl.X, tl.Y, (x1 - x0 + 1) * _scale, (y1 - y0 + 1) * _scale);
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(50, 90, 127, 208)), rect);
            ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(90, 127, 208)), 2, new DashStyle(new double[] { 3, 2 }, 0)), rect);
        }
        if (_formation is not null && _formationCursor is Point pc)
        {
            var cursor = new CellKey((int)Math.Floor(pc.X), (int)Math.Floor(pc.Y));
            var targets = FormationTargets(cursor);
            var blocked = BlockedCells(targets).ToHashSet();
            var ghost = new SolidColorBrush(Color.FromArgb(60, 80, 200, 220));
            var red = new SolidColorBrush(Color.FromArgb(130, 220, 50, 50));
            foreach (var (id, cell) in targets)
            {
                var p0 = WorldToScreen(cell.X, cell.Y);
                ctx.FillRectangle(blocked.Contains(cell) ? red : ghost, new Rect(p0.X, p0.Y, _scale, _scale));
                DrawPawnGlyph(ctx, cell, id, null, 0.6);
            }
        }
    }
}
