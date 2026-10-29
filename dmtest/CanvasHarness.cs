using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using System.Runtime.InteropServices;
using DungeonMapper.Controls;
using DungeonMapper.Models;

/// Headless Avalonia boot for driving the REAL MapCanvas pointer handlers
/// (press/move/release) without a window, screen, or xdotool. The canvas keeps
/// its startup transform (scale 40 px/cell, offset (-1,-1)) because AutoFit is
/// never called here, so a cell (cx,cy) center maps to window point
/// ((cx+1.5)*40, (cy+1.5)*40). The app is set up once per process; harness
/// instances share it and each gets a fresh canvas + document.
public sealed class HeadlessTestApp : Application
{
    public HeadlessTestApp() { Styles.Add(new FluentTheme()); }
}

public sealed class CanvasHarness : IDisposable
{
    private static bool _appStarted;
    private readonly Window _window;

    public MapCanvas Canvas { get; }
    public MapDocument Doc { get; }
    public Window Window => _window;
    public List<string> Statuses { get; } = new();

    public CanvasHarness()
    {
        if (!_appStarted)
        {
            AppBuilder.Configure<HeadlessTestApp>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .SetupWithoutStarting();
            _appStarted = true;
        }
        Doc = new MapDocument();
        Canvas = new MapCanvas();
        Canvas.Document = Doc;
        Canvas.StatusMessage += s => Statuses.Add(s);
        _window = new Window { Content = Canvas };
        _window.Show();
        _window.ApplyTemplate();
        _window.Measure(new Size(1000, 700));
        _window.Arrange(new Rect(0, 0, 1000, 700));
    }

    /// Window point at the center of cell (cx, cy) under the startup transform.
    public Point ScreenOf(int cx, int cy) => new((cx + 1.5) * 40, (cy + 1.5) * 40);

    public void Press(int cx, int cy) => _window.MouseDown(ScreenOf(cx, cy), MouseButton.Left, RawInputModifiers.None);
    public void MoveTo(int cx, int cy) => _window.MouseMove(ScreenOf(cx, cy), RawInputModifiers.None);
    public void Release(int cx, int cy) => _window.MouseUp(ScreenOf(cx, cy), MouseButton.Left, RawInputModifiers.None);

    /// press at (x0,y0), move to (x1,y1), release at (x1,y1)
    public void Drag(int x0, int y0, int x1, int y1)
    {
        Press(x0, y0);
        MoveTo(x1, y1);
        Release(x1, y1);
    }

    /// press + release on the same cell (a plain click)
    public void Click(int cx, int cy)
    {
        Press(cx, cy);
        Release(cx, cy);
    }

    /// Window point for a raw world coordinate under the startup transform
    /// (scale 40, offset (-1,-1)): screen = (world + 1) * 40.
    public Point ScreenWorld(double wx, double wy) => new((wx + 1) * 40, (wy + 1) * 40);

    public void PressW(double wx, double wy) => _window.MouseDown(ScreenWorld(wx, wy), MouseButton.Left, RawInputModifiers.None);
    public void ReleaseW(double wx, double wy) => _window.MouseUp(ScreenWorld(wx, wy), MouseButton.Left, RawInputModifiers.None);
    public void ClickW(double wx, double wy) { PressW(wx, wy); ReleaseW(wx, wy); }

    public string LastStatus => Statuses.Count > 0 ? Statuses[^1] : "(none)";

    /// Render the window to a bitmap. With UseHeadlessDrawing = false the
    /// headless platform runs the software renderer, so RenderTargetBitmap
    /// produces real pixels.
    public RenderTargetBitmap Render(int w = 1000, int h = 700)
    {
        var bmp = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        bmp.Render(_window);
        return bmp;
    }

    /// Read one pixel: CopyPixels a 1x1 region into an opaque Bgra8888 target
    /// and read the framebuffer. Transparent source pixels flatten to black.
    public (byte r, byte g, byte b, byte a) PixelAt(RenderTargetBitmap bmp, int x, int y)
    {
        var target = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using var lockInfo = target.Lock();
        bmp.CopyPixels(new PixelRect(x, y, 1, 1), lockInfo.Address, 4, 4);
        IntPtr addr = lockInfo.Address;
        byte b = Marshal.ReadByte(addr, 0);
        byte g = Marshal.ReadByte(addr, 1);
        byte r = Marshal.ReadByte(addr, 2);
        byte a = Marshal.ReadByte(addr, 3);
        return (r, g, b, a);
    }

    public void Dispose() => _window.Close();
}
