using Avalonia;
using System;

namespace DungeonMapper;

sealed class Program
{
    /// true when this run draws with the CPU (the cpu_renderer setting as
    /// it was at startup; the settings menu says when that differs)
    public static bool CpuRendering { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    // (reading settings.json first is plain file and json work, safe here)
    [STAThread]
    public static void Main(string[] args)
    {
        try { CpuRendering = Models.AppSettings.Load().CpuRenderer; } catch { CpuRendering = false; }
        BuildAvaloniaApp(CpuRendering).StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(false);

    // the GPU renderer is Avalonia's default (OpenGL on Linux, DirectX through
    // ANGLE on Windows), falling back to the CPU when the GPU can not start.
    // UsePlatformDetect already picks Skia for drawing. cpu forces the CPU
    // renderer, for a machine where the GPU one fails while running
    public static AppBuilder BuildAvaloniaApp(bool cpu)
    {
        var b = AppBuilder.Configure<App>().UsePlatformDetect();
        if (cpu)
            b = b.With(new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Software } })
                 .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } });
        return b
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
    }
}
