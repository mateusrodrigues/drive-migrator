using Avalonia;

namespace DriveMigrator.App;

internal static class Program
{
    // Avalonia is not initialised until AppMain runs; don't touch Avalonia types before then.
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
