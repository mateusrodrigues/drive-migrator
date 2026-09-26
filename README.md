# Drive Migrator

A cross-platform desktop app (Windows, Linux, macOS) for migrating data between cloud services —
currently Google (Drive, Gmail, Calendar, Contacts) and Microsoft (OneDrive, Outlook Mail, Calendar,
Contacts; personal and work/school accounts). Built with .NET 10 and Avalonia.

## Layout

| Path | Purpose |
|---|---|
| `src/DriveMigrator.Core` | Provider-neutral abstractions: providers, accounts, capabilities, node tree, payload models. No cloud SDK dependencies. |
| `src/DriveMigrator.App` | Avalonia desktop app (MVVM with CommunityToolkit.Mvvm, DI via Microsoft.Extensions.Hosting). |
| `tests/DriveMigrator.Testing` | `FakeCloudProvider`: an in-memory provider implementing every capability, for tests and UI development. |
| `tests/DriveMigrator.Core.Tests` | Unit tests. |

## Architecture in brief

- An `ICloudProvider` (one per service) signs accounts in and hands back an `IAccountSession`.
- A session exposes capabilities: `IDriveCapability`, `IMailCapability`, `ICalendarCapability`, `IContactsCapability`.
- Every capability shares one browsing contract (`GetChildrenAsync`, `CreateContainerAsync`, `FindChildAsync`)
  over `MigrationNode`s, so the UI renders any of them as a tree.
- Data moves through neutral formats — file streams, raw MIME for mail, and `CalendarEvent` / `Contact`
  records — so the engine pairs any reader with any writer without provider-to-provider code.
- New services plug in with `services.AddCloudProvider<TProvider>()`.

## Build and run

Requires the .NET 10 SDK (pinned in `global.json`).

```bash
dotnet build DriveMigrator.slnx
dotnet test DriveMigrator.slnx
dotnet run --project src/DriveMigrator.App
```
