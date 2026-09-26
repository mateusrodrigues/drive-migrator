using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DriveMigrator.App.ViewModels;
using DriveMigrator.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DriveMigrator.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Only the real desktop app gets a host; headless UI tests use this class for its resources only.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var host = AppHost.Build();
            host.Start();

            var mainViewModel = host.Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = mainViewModel };
            desktop.MainWindow.Opened += async (_, _) => await mainViewModel.InitializeAsync();

            // Keep the two lifetimes in step: closing the window stops the host, and a host
            // stop (Ctrl+C / SIGTERM) closes the window.
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping
                .Register(() => Dispatcher.UIThread.Post(() => desktop.TryShutdown()));
            desktop.Exit += (_, _) =>
            {
                host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                host.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
