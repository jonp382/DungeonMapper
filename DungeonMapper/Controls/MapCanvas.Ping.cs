using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// pings (multiplayer): rings that spread from a cell for a few seconds with
// who pinged it. the window sends and receives them; this only draws.
public sealed partial class MapCanvas
{
    public const double PingSeconds = 3.5;
    private static readonly Color PingColor = Color.FromRgb(80, 220, 255);

    /// the cell under the cursor, null when the cursor is off the map
    public CellKey? CursorCell { get; private set; }

    private readonly List<(CellKey cell, string name, DateTime at)> _pings = new();
    private Avalonia.Threading.DispatcherTimer? _pingTimer;

    public int PingCount => _pings.Count;

    public void ShowPing(CellKey cell, string name)
    {
        _pings.Add((cell, name, DateTime.Now));
        if (_pingTimer is null)
        {
            _pingTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _pingTimer.Tick += (_, _) =>
            {
                _pings.RemoveAll(p => (DateTime.Now - p.at).TotalSeconds > PingSeconds);
                if (_pings.Count == 0) _pingTimer!.Stop();
                InvalidateVisual();
            };
        }
        _pingTimer.Start();
        InvalidateVisual();
    }

    private void DrawPings(DrawingContext ctx)
    {
        foreach (var (cell, name, at) in _pings)
        {
            double k = Math.Clamp((DateTime.Now - at).TotalSeconds / PingSeconds, 0, 1);
            var center = WorldToScreen(cell.X + 0.5, cell.Y + 0.5);
            byte a = (byte)(255 * (1 - k));
            for (int i = 0; i < 3; i++)
            {
                double phase = (k * 3 + i / 3.0) % 1; // three rings spreading out in turn
                double r = _scale * (0.3 + 1.4 * phase);
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(a * (1 - phase)), PingColor.R, PingColor.G, PingColor.B)), 3), center, r, r);
            }
            ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(a, PingColor.R, PingColor.G, PingColor.B)), null, center, 5, 5);
            var ft = new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Inter", FontStyle.Normal, FontWeight.Bold), 13, Brushes.White);
            double w = ft.Width + 10, h = ft.Height + 4;
            var box = new Rect(center.X - w / 2, center.Y - _scale * 0.6 - h, w, h);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(200 * (1 - k)), 12, 12, 16)), new Pen(new SolidColorBrush(PingColor, 1 - k), 1), box, 4, 4);
            ctx.DrawText(ft, new Point(box.X + 5, box.Y + 2));
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        var hoverBefore = CursorCell;
        CursorCell = null;
        HoverTimers(hoverBefore);
    }
}
