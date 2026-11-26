using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// status turn timers (turn tracking): a filled circle in each status's color
// just outside the pawn's ring, at the middle of that status's arc, with the
// turns left inside (∞ for a permanent one, or none: a setting). only on the
// map and only zoomed in near max, where they are readable; drawn after every
// pawn so a neighbour never covers them. reading only.
public sealed partial class MapCanvas
{
    /// pixels per cell from which the timers show (the zoom goes to MaxScale)
    public const double TimerMinScale = 120;

    /// what a timer shows: the turns, "99+" past 99, ∞ for permanent (null =
    /// no timer: a permanent one with the setting off)
    public static string? TimerText(int turns, bool showPermanent) =>
        turns < 0 ? (showPermanent ? "∞" : null) : turns > 99 ? "99+" : turns.ToString(CultureInfo.InvariantCulture);

    /// where timer i of n sits, around a pawn centered at center with ring
    /// radius r and timer radius br (screen units). distance, percent: 0 on
    /// the ring, 50 just touching it from outside, 100 a timer radius clear
    public static Point TimerCenter(Point center, double r, double br, int i, int n, int distance = 50)
    {
        double a = ArcMiddle(i, n) * Math.PI / 180, d = r + br * Math.Clamp(distance, 0, 100) / 50.0;
        return new Point(center.X + d * Math.Cos(a), center.Y + d * Math.Sin(a));
    }

    public bool TimersShown => Settings.ShowStatusTimers && _scale >= TimerMinScale;

    private void DrawStatusTimers(DrawingContext ctx, Point topLeft, Point bottomRight)
    {
        if (!TimersShown) return;
        double r = _scale * 0.42, br = _scale * 0.12;
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(230, 12, 12, 16)), Math.Max(1.5, _scale * 0.012));
        foreach (var (c, p) in Document.Pawns)
        {
            if (p.Statuses.Count == 0) continue;
            if (c.X + 2 < topLeft.X || c.X - 1 > bottomRight.X || c.Y + 2 < topLeft.Y || c.Y - 1 > bottomRight.Y) continue;
            var ring = RingStatuses(Presets, p.Statuses);
            var center = WorldToScreen(c.X + 0.5, c.Y + 0.5);
            for (int i = 0; i < ring.Count; i++)
            {
                var st = ring[i];
                var entry = p.Statuses.First(e => e.Name == st.Name);
                if (TimerText(entry.Turns, Settings.ShowPermanentTimers) is not { } text) continue;
                var at = TimerCenter(center, r, br, i, ring.Count, Settings.TimerDistance);
                var fill = Color.FromRgb((byte)st.R, (byte)st.G, (byte)st.B);
                ctx.DrawEllipse(new SolidColorBrush(fill), edge, at, br, br);
                // dark text on light colors, white on dark ones
                double lum = 0.299 * st.R + 0.587 * st.G + 0.114 * st.B;
                double fs = br * (text.Length <= 1 ? 1.25 : text.Length == 2 ? 1.0 : 0.75);
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Inter", FontStyle.Normal, FontWeight.Bold), fs, lum > 150 ? Brushes.Black : Brushes.White);
                ctx.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
            }
        }
    }
}
