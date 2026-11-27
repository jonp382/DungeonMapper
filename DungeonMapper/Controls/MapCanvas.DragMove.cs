using Avalonia;
using Avalonia.Input;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// drag and drop moving of a selected region (user, Nov 27): box a region,
// press inside it and drag. the contents follow as the ghost (R / Shift+R
// turn them, Esc cancels) and land on release, one undo step. a press
// without a drag is the tool's plain click. it rides on the armed move
// (MoveAt, the ghost, rotation, heroes); the user's clipboard is put back
// after, so a move never replaces what was copied.
public sealed partial class MapCanvas
{
    /// select tool: a region drag moves only the pawns in it
    public bool SelectPawnsOnly { get; set; }

    private bool _dragMoving;
    private CellKey _dragGrab;
    private (int x, int y) _dragOrigin;
    private (int x0, int y0, int x1, int y1) _dragRect;
    private MapDocument.RegionSnapshot? _clipboardBeforeMove;
    private bool _clipboardSaved;

    public bool DragMoving => _dragMoving;

    private bool InSelection(CellKey c) =>
        SelectionRect is var (x0, y0, x1, y1) && c.X >= x0 && c.X <= x1 && c.Y >= y0 && c.Y <= y1;

    /// a press inside the selected region: picks its contents up
    private bool BeginDragMove(PointerPressedEventArgs e, CellKey cell)
    {
        if (_moveArmed || !InSelection(cell) || SelectionRect is not var (x0, y0, x1, y1)) return false;
        // the pawn tool only ever moves pawns; a joined player only their own
        bool pawnsOnly = Mode == ToolMode.Pawn || SelectPawnsOnly;
        var snap = Document.CaptureRegion(x0, y0, x1, y1);
        if (_remote is not null) snap.Pawns.RemoveAll(p => !PlayerPawns.Controlled(Library.Find(p.TemplateId)));
        if (pawnsOnly) snap = PawnsOnlySnapshot(snap, out _);
        if (snap.Empty)
        {
            Status(pawnsOnly ? "no pawns in the selection" : "nothing to move in the selection", StatusKind.Warning);
            return false;
        }
        _clipboardBeforeMove = _clipboard;
        _clipboardSaved = true;
        _clipboard = snap;
        if (_remote is not null) _remoteMoveFrom = snap.Pawns.ConvertAll(p => p.Cell);
        _moveSourceDoc = _remote is null ? Document : null;
        _cutSource = null;
        _moveArmed = true;
        _armedPawnsOnly = pawnsOnly;
        _moveSourceRect = (x0, y0, x1, y1);
        _moveSourceCleared = false;
        _movePlaced = false;
        _rotTurns = 0;
        _dragMoving = true;
        _dragGrab = cell;
        _dragOrigin = (snap.OriginX, snap.OriginY);
        _dragRect = (x0, y0, x1, y1);
        _pasteCursor = new Point(snap.OriginX + 0.5, snap.OriginY + 0.5);
        e.Pointer.Capture(this);
        InvalidateVisual();
        return true;
    }

    private (int dx, int dy) DragDelta(Point world) =>
        ((int)System.Math.Floor(world.X) - _dragGrab.X, (int)System.Math.Floor(world.Y) - _dragGrab.Y);

    private bool DragMoveMoved(Point world)
    {
        if (!_dragMoving) return false;
        var (dx, dy) = DragDelta(world);
        var p = new Point(_dragOrigin.x + dx + 0.5, _dragOrigin.y + dy + 0.5);
        if (_pasteCursor != p) { _pasteCursor = p; InvalidateVisual(); }
        return true;
    }

    private bool DragMoveReleased(PointerReleasedEventArgs e)
    {
        if (!_dragMoving) return false;
        _dragMoving = false;
        e.Pointer.Capture(null);
        var world = ScreenToWorld(e.GetPosition(this));
        var (dx, dy) = DragDelta(world);
        var cell = new CellKey((int)System.Math.Floor(world.X), (int)System.Math.Floor(world.Y));
        if (dx == 0 && dy == 0 && _rotTurns == 0)
        {
            // no drag: put everything back and treat it as a click
            CancelMove();
            if (_remote is not null) RemoteBoxReleased(cell, true);
            else if (Mode == ToolMode.Select) SelectClick(world, cell);
            else if (Mode == ToolMode.Pawn) PawnToolClick(cell);
            InvalidateVisual();
            return true;
        }
        bool odd = _rotTurns % 2 != 0;
        var (x0, y0, x1, y1) = _dragRect;
        int w = (odd ? y1 - y0 : x1 - x0), h = (odd ? x1 - x0 : y1 - y0);
        var target = new CellKey(_dragOrigin.x + dx, _dragOrigin.y + dy);
        MoveAt(target);
        if (_moveArmed) CancelMove(); // a refused drop puts everything back
        else
        {
            // the moved region stays selected, ready to drag again
            int nx = x0 + dx, ny = y0 + dy;
            _selectStart = new CellKey(nx, ny);
            _selectEnd = new CellKey(nx + w, ny + h);
        }
        InvalidateVisual();
        return true;
    }
}
