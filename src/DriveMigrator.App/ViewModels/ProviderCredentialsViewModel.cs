using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveMigrator.App.Services;
using DriveMigrator.Core;
using DriveMigrator.Core.Security;

namespace DriveMigrator.App.ViewModels;

/// <summary>The credential form for one provider (e.g. the OAuth client ID of the user's own app registration).</summary>
public sealed partial class ProviderCredentialsViewModel(
    ICloudProvider provider,
    ProviderCredentialStore store,
    IDialogService dialogs,
    Func<ProviderCredentialsViewModel, Task> saved) : ViewModelBase
{
    public ICloudProvider Provider { get; } = provider;

    public string DisplayName => Provider.DisplayName;

    public ObservableCollection<CredentialFieldViewModel> Fields { get; } =
        [.. provider.CredentialFields.Select(f => new CredentialFieldViewModel(f))];

    /// <summary>Whether the app carries a guide for creating this provider's OAuth app.</summary>
    public bool HasGuide { get; } = SetupGuides.Exists(provider.Id);

    [ObservableProperty]
    public partial bool IsConfigured { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public async Task LoadAsync()
    {
        var values = await store.GetAsync(Provider.Id);
        foreach (var field in Fields)
        {
            field.Value = values.GetValueOrDefault(field.Field.Key) ?? string.Empty;
        }

        IsConfigured = await store.IsConfiguredAsync(Provider);
    }

    [RelayCommand]
    private Task ShowGuideAsync() => dialogs.ShowSetupGuideAsync(Provider.Id);

    [RelayCommand]
    private async Task SaveAsync()
    {
        await store.SetAsync(Provider.Id, Fields.ToDictionary(f => f.Field.Key, f => f.Value));
        IsConfigured = await store.IsConfiguredAsync(Provider);
        StatusMessage = IsConfigured ? "Saved." : "Saved, but required fields are still empty.";
        await saved(this);
    }
}

public sealed partial class CredentialFieldViewModel(CredentialField field) : ViewModelBase
{
    public CredentialField Field { get; } = field;

    public string Label => Field.IsRequired ? Field.Label : $"{Field.Label} (optional)";

    public string? Watermark => Field.DefaultValue;

    public char PasswordChar => Field.IsSecret ? '•' : default;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;
}
