using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// sandbox pawns (multiplayer, player side): with the move tool a joined
// player drags pawns around to try things out. the moves live only here, are
// never sent, and are laid over every map the DM sends until the player
// resets them. a move whose pawn the DM moved away, or whose spot the DM
// filled, drops off.
public sealed partial class MapCanvas
{
    private MapDocument? _remoteBase;
    private readonly List<(CellKey from, CellKey to)> _sandbox = new();
    private CellKey? _sandboxFrom, _sandboxOver;

    /// the player's moves: from a cell in the DM's map to where they put it
    public IReadOnlyList<(CellKey from, CellKey to)> SandboxMoves => _sandbox;
    /// the moves changed (panel count)
    public event Action? SandboxChanged;

    /// a joined session: the map the host sent, drawn read only (with the
    /// player's sandbox moves on top) in place of this app's own map. null
    /// leaves the session view and forgets the moves
    public MapDocument? RemoteDocument
    {
        get => _remoteBase;
        set
        {
            _remoteBase = value;
            if (value is null && _sandbox.Count > 0) { _sandbox.Clear(); SandboxChanged?.Invoke(); }
            RebuildRemote();
        }
    }

    private void RebuildRemote()
    {
        if (_remoteBase is null) { _remote = null; InvalidateVisual(); return; }
        var d = new MapDocument();
        d.ApplyFrom(_remoteBase);
        d.PlayerLight = _remoteBase.PlayerLight;
        var kept = new List<(CellKey, CellKey)>();
        foreach (var (f, t) in _sandbox)
        {
            if (!d.Pawns.TryGetValue(f, out var p) || d.Pawns.ContainsKey(t)) continue;
            d.Pawns.Remove(f);
            d.Pawns[t] = new Pawn { Cell = t, TemplateId = p.TemplateId, Status = p.Status, DmOnly = false };
            kept.Add((f, t));
        }
        _remote = d;
        if (kept.Count != _sandbox.Count)
        {
            _sandbox.Clear();
            _sandbox.AddRange(kept);
            SandboxChanged?.Invoke();
        }
        InvalidateVisual();
    }

    /// moves the pawn shown at from to to. false (and a status) when it can not
    public bool SandboxMove(CellKey from, CellKey to)
    {
        if (_remote is null || from == to) return false;
        if (!_remote.Pawns.ContainsKey(from)) return false;
        if (_remote.Pawns.ContainsKey(to)) { StatusMessage?.Invoke("that cell already has a pawn"); return false; }
        if (!_remote.IsFloorCell(to)) { StatusMessage?.Invoke("pawns can only go onto the map"); return false; }
        int i = _sandbox.FindIndex(m => m.to == from);
        if (i >= 0)
        {
            var f = _sandbox[i].from;
            if (f == to) _sandbox.RemoveAt(i); else _sandbox[i] = (f, to);
        }
        else _sandbox.Add((from, to));
        RebuildRemote();
        SandboxChanged?.Invoke();
        return true;
    }

    public void ResetSandbox()
    {
        if (_sandbox.Count == 0) return;
        _sandbox.Clear();
        RebuildRemote();
        SandboxChanged?.Invoke();
    }

    private bool SandboxActive => _remote is not null && Mode == ToolMode.Move;

    private bool SandboxPressed(PointerPressedEventArgs e, CellKey cell)
    {
        if (!SandboxActive) return false;
        if (_remote!.Pawns.ContainsKey(cell))
        {
            _sandboxFrom = _sandboxOver = cell;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        return true; // nothing else edits in a session
    }

    private bool SandboxMoved(Point world)
    {
        if (!SandboxActive || _sandboxFrom is null) return false;
        var c = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
        if (_sandboxOver != c) { _sandboxOver = c; InvalidateVisual(); }
        return true;
    }

    private bool SandboxReleased(PointerReleasedEventArgs e)
    {
        if (!SandboxActive || _sandboxFrom is not CellKey from) return false;
        e.Pointer.Capture(null);
        var to = _sandboxOver ?? from;
        _sandboxFrom = _sandboxOver = null;
        SandboxMove(from, to);
        InvalidateVisual();
        return true;
    }

    private static readonly Color SandboxColor = Color.FromRgb(120, 230, 160);

    // each moved pawn: a dashed ring where the DM has it and a line to where
    // the player put it. while dragging, a ring on the cell under the cursor
    private void DrawSandbox(DrawingContext ctx)
    {
        if (_remote is null) return;
        var pen = new Pen(new SolidColorBrush(SandboxColor, 0.85), 2, new DashStyle(new double[] { 3, 2 }, 0));
        foreach (var (f, t) in _sandbox)
        {
            var a = WorldToScreen(f.X + 0.5, f.Y + 0.5);
            var b = WorldToScreen(t.X + 0.5, t.Y + 0.5);
            ctx.DrawEllipse(null, pen, a, _scale * 0.4, _scale * 0.4);
            ctx.DrawLine(pen, a, b);
            ctx.DrawEllipse(null, pen, b, _scale * 0.5, _scale * 0.5);
        }
        if (_sandboxFrom is CellKey from && _sandboxOver is CellKey over && over != from)
        {
            var c = WorldToScreen(over.X + 0.5, over.Y + 0.5);
            ctx.DrawEllipse(new SolidColorBrush(SandboxColor, 0.25), new Pen(new SolidColorBrush(SandboxColor), 2), c, _scale * 0.45, _scale * 0.45);
        }
    }
}
