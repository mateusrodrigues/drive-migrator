using System.Text.Json;
using DriveMigrator.Core.Accounts;

namespace DriveMigrator.Infrastructure;

/// <summary>Keeps the connected-account list in a JSON file. Contains no secrets.</summary>
public sealed class JsonAccountStore(AppPaths paths) : IAccountStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task<IReadOnlyList<AccountInfo>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.AccountsFile))
        {
            return [];
        }

        var stream = File.OpenRead(paths.AccountsFile);
        await using (stream.ConfigureAwait(false))
        {
            var document = await JsonSerializer.DeserializeAsync<AccountsDocument>(stream, Options, cancellationToken).ConfigureAwait(false);
            return document?.Accounts ?? [];
        }
    }

    public async Task SaveAsync(IReadOnlyList<AccountInfo> accounts, CancellationToken cancellationToken = default)
    {
        AppPaths.EnsurePrivateDirectory(paths.DataDirectory);

        // Write to a temp file and swap it in so a crash never leaves a truncated list behind.
        var temp = paths.AccountsFile + ".tmp";
        var stream = File.Create(temp);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, new AccountsDocument([.. accounts]), Options, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, paths.AccountsFile, overwrite: true);
    }

    private sealed record AccountsDocument(List<AccountInfo> Accounts)
    {
        public int Version { get; init; } = 1;
    }
}
