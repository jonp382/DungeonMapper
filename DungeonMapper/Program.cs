using Avalonia;
using System;

namespace DungeonMapper;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Force the CPU (Skia software) renderer. The default GPU renderer
            // fails to allocate when VRAM is saturated (e.g. a local LLM running),
            // producing a transparent/blank canvas. Software rendering has no VRAM
            // dependency.
            .UseSkia()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
