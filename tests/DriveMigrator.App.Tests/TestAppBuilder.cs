using Avalonia;
using Avalonia.Headless;
using DriveMigrator.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace DriveMigrator.App.Tests;

/// <summary>Headless Avalonia with real Skia rendering, so windows can be captured as images.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
