using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DungeonMapper.Controls;
using DungeonMapper.Models;

namespace DungeonMapper.Views;

// a small pawn beside the status gap and timer distance settings, drawn the
// way the map draws them (MapCanvas.StatusArcs / TimerCenter), so a value
// can be judged without zooming the map in (user)
public sealed class SettingsPawnPreview : Control
{
    private static readonly List<PawnStatus> Sample = new()
    {
        new PawnStatus("a", 230, 80, 80, 3),
        new PawnStatus("b", 80, 200, 110, 3),
        new PawnStatus("c", 90, 140, 240, 3),
    };

    public int Gap { get; set; } = 8;
    public int Distance { get; set; } = 50;
    /// draw the turn timers too (the timer distance row)
    public bool Timers { get; set; }

    public SettingsPawnPreview()
    {
        Width = 56;
        Height = 56;
    }

    public override void Render(DrawingContext ctx)
    {
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double r = 15, br = r * 0.12 / 0.42;
        ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(120, 120, 135)), null, c, r, r);
        ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(60, 60, 72)), null, c, r * 0.52, r * 0.52);
        foreach (var (geo, st) in MapCanvas.StatusArcs(c, r, Sample, Gap))
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb((byte)st.R, (byte)st.G, (byte)st.B)), st.Thickness), geo);
        if (!Timers) return;
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(230, 12, 12, 16)), 1);
        for (int i = 0; i < Sample.Count; i++)
        {
            var st = Sample[i];
            var at = MapCanvas.TimerCenter(c, r, br, i, Sample.Count, Distance);
            ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb((byte)st.R, (byte)st.G, (byte)st.B)), edge, at, br, br);
        }
    }
}
