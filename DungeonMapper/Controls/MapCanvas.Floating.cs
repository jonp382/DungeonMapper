using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using DungeonMapper.Models;

namespace DungeonMapper.Controls;

// floating text over a pawn (turn tracking: "fear wore off!"), like combat
// text in a game: it rises and fades for a couple of seconds. texts on the same
// cell at the same time stack upward. reading only: nothing is saved.
public sealed partial class MapCanvas
{
    public const double FloatSeconds = 2.2;

    private readonly List<(CellKey cell, string text, Color color, int slot, DateTime at)> _floats = new();
    private Avalonia.Threading.DispatcherTimer? _floatTimer;

    public int FloatingCount => _floats.Count;
    public IEnumerable<string> FloatingTexts => _floats.Select(f => f.text);

    public void ShowFloatingText(CellKey cell, string text, Color color)
    {
        var now = DateTime.Now;
        int slot = _floats.Count(f => f.cell == cell && (now - f.at).TotalSeconds < 0.5);
        _floats.Add((cell, text, color, slot, now));
        if (_floatTimer is null)
        {
            _floatTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _floatTimer.Tick += (_, _) =>
            {
                _floats.RemoveAll(f => (DateTime.Now - f.at).TotalSeconds > FloatSeconds);
                if (_floats.Count == 0) _floatTimer!.Stop();
                InvalidateVisual();
            };
        }
        _floatTimer.Start();
        InvalidateVisual();
    }

    /// "<status> wore off!" over each pawn, in the status's color
    public void ShowWoreOff(IEnumerable<TurnTracker.WoreOff> gone)
    {
        foreach (var g in gone)
        {
            var st = Presets.FindPawnStatus(g.Name);
            var color = st.Name == g.Name ? Color.FromRgb((byte)st.R, (byte)st.G, (byte)st.B) : Colors.White;
            ShowFloatingText(g.Cell, $"{g.Name} wore off!", color);
        }
    }

    private void DrawFloating(DrawingContext ctx)
    {
        foreach (var (cell, text, color, slot, at) in _floats)
        {
            double k = Math.Clamp((DateTime.Now - at).TotalSeconds / FloatSeconds, 0, 1);
            double fade = k < 0.6 ? 1 : 1 - (k - 0.6) / 0.4;
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Inter", FontStyle.Normal, FontWeight.Bold), 15, new SolidColorBrush(color, fade));
            var outline = ft.BuildGeometry(new Point(0, 0));
            var top = WorldToScreen(cell.X + 0.5, cell.Y);
            double y = top.Y - 6 - slot * (ft.Height + 2) - k * _scale * 0.9 - ft.Height;
            var origin = new Point(top.X - ft.Width / 2, y);
            // a dark edge so the text reads over any floor
            if (outline is not null)
                using (ctx.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y)))
                    ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(220 * fade), 10, 10, 14)), 3), outline);
            ctx.DrawText(ft, origin);
        }
    }
}
