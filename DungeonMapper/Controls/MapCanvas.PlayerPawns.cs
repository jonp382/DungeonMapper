using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// the session view (multiplayer, player side) and the players' pawns (the
// Player affiliation, see PlayerPawns). the move and pawn tools drag them,
// the pawn tool places the armed one, opens a placed one (status, on a click
// without a drag) and deploys formations. nothing changes here: the request goes to the DM, whose app
// checks it, applies it to the real map and sends the map back. every other
// pawn is the DM's.
public sealed partial class MapCanvas
{
    private CellKey? _playerFrom, _playerOver;

    /// a joined session: the map the host sent, drawn read only in place of
    /// this app's own map. null leaves the session view
    public MapDocument? RemoteDocument
    {
        get => _remote;
        set
        {
            _remote = value;
            if (value is null) { _playerFrom = _playerOver = null; ArmedPawn = null; CancelFormation(); CancelAreaPick(); }
            InvalidateVisual();
        }
    }

    /// the player dragged one of their pawns (from, to); checked, not applied
    public event Action<CellKey, CellKey>? PlayerMoveRequested;
    /// the player placed one of their pawns (template id, cell); checked, not applied
    public event Action<string, CellKey>? PlayerPlaceRequested;
    /// the player deployed a formation (template id, cell each); checked, not applied
    public event Action<List<(string id, CellKey cell)>>? PlayerDeployRequested;

    private bool PlayerPawnsActive => _remote is not null && Mode is ToolMode.Move or ToolMode.Pawn;
    /// the tools a joined player edits with (the measure tool only reads)
    private bool JoinedTool => _remote is not null && Mode is ToolMode.Move or ToolMode.Label or ToolMode.Pawn or ToolMode.Image;
    /// a joined player added or changed a label (shown here, not yet kept)
    public event Action<MapLabel>? RemoteLabelChanged;

    private bool PlayerPawnPressed(PointerPressedEventArgs e, CellKey cell)
    {
        if (_remote is null) return false;
        if (Mode == ToolMode.Pawn)
        {
            if (_remote.Pawns.TryGetValue(cell, out var placed))
            {
                // dragged like in the move tool; a click opens it (on release)
                if (PlayerPawns.Controlled(Library.Find(placed.TemplateId))) { _playerFrom = _playerOver = cell; e.Pointer.Capture(this); }
                else StatusMessage?.Invoke("only the DM changes that pawn");
            }
            else if (ArmedPawn is { } t)
            {
                if (PlayerPawns.CheckPlace(_remote, Library, t.Id, cell) is { } why) StatusMessage?.Invoke(why);
                else PlayerPlaceRequested?.Invoke(t.Id, cell);
            }
            else StatusMessage?.Invoke("select a pawn in the pawn menu first");
            return true;
        }
        if (Mode != ToolMode.Move) return false;
        if (_remote.Pawns.TryGetValue(cell, out var p))
        {
            if (!PlayerPawns.Controlled(Library.Find(p.TemplateId))) { StatusMessage?.Invoke("only the DM moves that pawn"); return true; }
            _playerFrom = _playerOver = cell;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        return true; // nothing else edits in a session
    }

    private bool PlayerPawnMoved(Point world)
    {
        if (!PlayerPawnsActive || _playerFrom is null) return false;
        var c = new CellKey((int)Math.Floor(world.X), (int)Math.Floor(world.Y));
        if (_playerOver != c) { _playerOver = c; InvalidateVisual(); }
        return true;
    }

    private bool PlayerPawnReleased(PointerReleasedEventArgs e)
    {
        if (!PlayerPawnsActive || _playerFrom is not CellKey from) return false;
        e.Pointer.Capture(null);
        var to = _playerOver ?? from;
        _playerFrom = _playerOver = null;
        InvalidateVisual();
        if (to == from)
        {
            if (Mode == ToolMode.Pawn && _remote!.Pawns.TryGetValue(from, out var placed)) PawnConfigRequested?.Invoke(placed);
            return true;
        }
        if (PlayerPawns.CheckMove(_remote!, Library, from, to) is { } why) StatusMessage?.Invoke(why);
        else PlayerMoveRequested?.Invoke(from, to);
        return true;
    }

    private static readonly Color PlayerPawnColor = Color.FromRgb(120, 230, 160);

    // while dragging: a ring on the cell under the cursor
    private void DrawPlayerPawnDrag(DrawingContext ctx)
    {
        if (_remote is null || _playerFrom is not CellKey from || _playerOver is not CellKey over || over == from) return;
        var a = WorldToScreen(from.X + 0.5, from.Y + 0.5);
        var c = WorldToScreen(over.X + 0.5, over.Y + 0.5);
        var pen = new Pen(new SolidColorBrush(PlayerPawnColor), 2);
        ctx.DrawLine(new Pen(new SolidColorBrush(PlayerPawnColor, 0.85), 2, new DashStyle(new double[] { 3, 2 }, 0)), a, c);
        ctx.DrawEllipse(new SolidColorBrush(PlayerPawnColor, 0.25), pen, c, _scale * 0.45, _scale * 0.45);
    }
}
