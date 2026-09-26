# Drive Migrator

A cross-platform desktop app (Windows, Linux, macOS) for migrating data between cloud services —
currently Google (Drive, Gmail, Calendar, Contacts) and Microsoft (OneDrive, Outlook Mail, Calendar,
Contacts; personal and work/school accounts). Built with .NET 10 and Avalonia.

## Layout

| Path | Purpose |
|---|---|
| `src/DriveMigrator.Core` | Provider-neutral abstractions: providers, accounts, capabilities, node tree, payload models. No cloud SDK dependencies. |
| `src/DriveMigrator.Infrastructure` | Local persistence: OS keychain secret store (DPAPI / macOS Keychain / libsecret), account list. |
| `src/DriveMigrator.Providers.Google` | Google provider (sign-in via Google.Apis.Auth; APIs added per capability). |
| `src/DriveMigrator.Providers.Microsoft` | Microsoft provider (sign-in via MSAL, personal and work/school accounts). |
| `src/DriveMigrator.App` | Avalonia desktop app (MVVM with CommunityToolkit.Mvvm, DI via Microsoft.Extensions.Hosting). |
| `tests/DriveMigrator.Testing` | `FakeCloudProvider` and in-memory stores for tests and UI development. |
| `tests/*.Tests` | Unit tests, plus headless Avalonia UI tests in `DriveMigrator.App.Tests`. |

## Architecture in brief

- An `ICloudProvider` (one per service) signs accounts in and hands back an `IAccountSession`.
- A session exposes capabilities: `IDriveCapability`, `IMailCapability`, `ICalendarCapability`, `IContactsCapability`.
- Every capability shares one browsing contract (`GetChildrenAsync`, `CreateContainerAsync`, `FindChildAsync`)
  over `MigrationNode`s, so the UI renders any of them as a tree.
- Data moves through neutral formats — file streams, raw MIME for mail, and `CalendarEvent` / `Contact`
  records — so the engine pairs any reader with any writer without provider-to-provider code.
- New services plug in with `services.AddCloudProvider<TProvider>()`.
- `AccountManager` restores, adds, re-authorizes and removes accounts. Tokens live in the OS credential store;
  only the account list (no secrets) is written to `accounts.json` in the app data directory.

## Credentials

Drive Migrator doesn't ship with OAuth clients. You register your own and enter them in **Settings → Credentials**:

- [Google setup](docs/setup-google.md)
- [Microsoft setup](docs/setup-microsoft.md)

## Build and run

Requires the .NET 10 SDK (pinned in `global.json`).

```bash
dotnet build DriveMigrator.slnx
dotnet test DriveMigrator.slnx
dotnet run --project src/DriveMigrator.App
```

Set `DRIVEMIGRATOR_DATA_DIR` to use a separate data directory (handy during development). Set
`DRIVEMIGRATOR_SCREENSHOTS` to a directory when running the tests to save rendered window images there.
