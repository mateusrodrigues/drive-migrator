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
    private IHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        _host = AppHost.Build();
        _host.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = _host.Services.GetRequiredService<MainWindowViewModel>(),
            };

            // Keep the two lifetimes in step: closing the window stops the host, and a host
            // stop (Ctrl+C / SIGTERM) closes the window.
            _host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping
                .Register(() => Dispatcher.UIThread.Post(() => desktop.TryShutdown()));
            desktop.Exit += (_, _) =>
            {
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _host.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
