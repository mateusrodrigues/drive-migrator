using DriveMigrator.Core.Accounts;
using DriveMigrator.Core.Security;
using Microsoft.Extensions.DependencyInjection;

namespace DriveMigrator.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers local persistence (secret store, account list) and account management.</summary>
    public static IServiceCollection AddDriveMigratorInfrastructure(this IServiceCollection services, AppPaths? paths = null)
    {
        services.AddSingleton(paths ?? AppPaths.Default());
        services.AddSingleton<ISecretStore>(sp => OsSecretStore.Create(sp.GetRequiredService<AppPaths>()));
        services.AddSingleton<IAccountStore, JsonAccountStore>();
        services.AddSingleton<ProviderCredentialStore>();
        services.AddSingleton<AccountManager>();
        return services;
    }
}
