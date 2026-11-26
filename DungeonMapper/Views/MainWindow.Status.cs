using System;
using Avalonia.Media;
using Avalonia.Threading;
using DungeonMapper.Models;

namespace DungeonMapper.Views;

// the status bar message: colored by kind, and the bar flashes on each new
// message (strong for errors, warnings, saves and players' changes, faint for
// plain info) so it is noticed. the text keeps its color until the next one.
public partial class MainWindow
{
    private DispatcherTimer? _flashTimer;
    private double _flashStart;
    private int _flashTicks;
    private const int FlashHoldTicks = 6, FlashFadeTicks = 18; // 40 ms ticks: hold, then fade

    public static Color StatusColor(StatusKind kind) => kind switch
    {
        StatusKind.Error => Color.FromRgb(255, 90, 90),
        StatusKind.Warning => Color.FromRgb(255, 160, 60),
        StatusKind.Success => Color.FromRgb(110, 224, 138),
        StatusKind.Player => Color.FromRgb(236, 90, 220),
        _ => Colors.White,
    };

    /// the kind of the message on show
    public StatusKind StatusShown { get; private set; }

    private void SetStatus(string text, StatusKind kind = StatusKind.Info)
    {
        StatusText.Text = text;
        StatusShown = kind;
        var color = StatusColor(kind);
        StatusText.Foreground = new SolidColorBrush(color);
        StatusFlash.Background = new SolidColorBrush(color);
        _flashStart = kind == StatusKind.Info ? 0.16 : 0.5;
        StatusFlash.Opacity = _flashStart;
        _flashTicks = 0;
        _flashTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => FlashTick());
        _flashTimer.Start();
    }

    private void FlashTick()
    {
        _flashTicks++;
        if (_flashTicks <= FlashHoldTicks) return;
        double t = (double)(_flashTicks - FlashHoldTicks) / FlashFadeTicks;
        if (t >= 1) { StatusFlash.Opacity = 0; _flashTimer!.Stop(); return; }
        StatusFlash.Opacity = _flashStart * (1 - t);
    }
}
