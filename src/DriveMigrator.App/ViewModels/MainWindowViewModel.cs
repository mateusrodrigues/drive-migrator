using DriveMigrator.Core;

namespace DriveMigrator.App.ViewModels;

public sealed class MainWindowViewModel(IProviderRegistry providers) : ViewModelBase
{
    public IReadOnlyList<ICloudProvider> Providers => providers.Providers;

    public string Status => Providers.Count == 0
        ? "No cloud providers are registered yet."
        : $"{Providers.Count} cloud provider(s) available.";
}
