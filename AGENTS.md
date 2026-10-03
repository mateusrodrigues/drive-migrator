# AGENTS.md

Guidance for coding agents working in this repository. For what the app is and how it's laid out, read
[README.md](README.md) first — this file covers how to work on it.

## Commands

Requires the .NET 10 SDK (pinned in `global.json`).

```bash
dotnet build DriveMigrator.slnx
dotnet test DriveMigrator.slnx
dotnet test tests/DriveMigrator.Engine.Tests          # one project
dotnet test DriveMigrator.slnx --filter "FullyQualifiedName~MailTransferTests"
dotnet run --project src/DriveMigrator.App
```

CI (`.github/workflows/ci.yml`) builds and tests in Release on Ubuntu, Windows and macOS. A change isn't done until
`dotnet build` and `dotnet test` pass for the whole solution.

## Safety: never touch real accounts

The developer's own Google and Microsoft accounts may be signed in on this machine (tokens in the OS keyring,
`accounts.json` in the default data directory).

- Don't run the app, or any script, against the default data directory in a way that could upload, create,
  move or delete anything in a real account. Avoid reading real file listings too.
- Test with `FakeCloudProvider` and the in-memory stores in `tests/DriveMigrator.Testing`, canned HTTP responses
  (`FakeHttpHandler` in the provider tests), and a throwaway `DRIVEMIGRATOR_DATA_DIR`.
- When something needs a real-account check, write down precise manual test steps for the developer instead.

## Code conventions

- `TreatWarningsAsErrors` is on with `AnalysisLevel=latest-recommended` and `EnforceCodeStyleInBuild`, so analyzer
  and `.editorconfig` style violations fail the build. Fix them; don't suppress them without a reason.
- Package versions live only in `Directory.Packages.props` (central package management). Project files reference
  packages without a `Version`.
- File-scoped namespaces, nullable enabled, implicit usings. Public types and non-obvious members get `///` XML doc
  summaries in the same terse style as the existing code.
- Keep `DriveMigrator.Core` free of cloud SDK dependencies. Provider-specific code belongs in
  `DriveMigrator.Providers.*`; the engine only talks to Core abstractions.
- Data crosses providers only through neutral formats (file streams, raw MIME, `CalendarEvent`, `Contact`). Don't
  add provider-to-provider code paths.
- App code is MVVM with CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`). Views stay thin; logic
  goes in view models so it can be unit-tested.

## Adding to a provider or capability

1. Extend the capability interface in `src/DriveMigrator.Core/<Area>/` if the neutral contract needs it.
2. Implement it in both `DriveMigrator.Providers.Google` and `DriveMigrator.Providers.Microsoft`
   (Microsoft calls go through `Graph/GraphClient.cs`).
3. Mirror it in the matching fake in `tests/DriveMigrator.Testing` so engine and UI tests can exercise it.
4. Add provider tests with `FakeHttpHandler` that assert on the recorded requests, plus engine tests for the
   transfer path (`tests/DriveMigrator.Engine.Tests`).

If a new Google API or Graph permission is needed, update the scopes in `docs/setup-google.md` or
`docs/setup-microsoft.md`.

## Tests

- xUnit v3 (`TestContext.Current.CancellationToken` for cancellation). Test names use underscores
  (`Method_Condition_Result`), which is why `CA1707` is off for test projects.
- `DriveMigrator.App.Tests` runs Avalonia headlessly (`[AvaloniaFact]`). `WindowRenderingTests` renders every window
  in light and dark mode to catch XAML and binding errors. Set `DRIVEMIGRATOR_SCREENSHOTS=<dir>` to save the frames
  as PNGs and look at them after any UI change.
- The README screenshots in `docs/screenshots/` come from those renders; refresh them when the UI changes visibly.

## UI and design system

- The theme lives in `src/DriveMigrator.App/Themes/`: `Tokens.axaml` (design tokens as resource keys, light and
  dark), `ControlThemes.axaml` (restyled stock controls) and `Controls.axaml`. The 16px glyph set is
  `Controls/Icon.cs`. Use the tokens and existing control classes rather than hard-coded colors or sizes.
- The design system export (tokens, component specs, previews, app icon sources) is kept locally in `design/` at
  the repo root and is not committed. If it's present, treat it as the source of truth for UI work.

## Product rules

- Copies never delete or modify anything in the source account.
- The copy destination is the node selected in the destination tree, falling back to the root.
- Users bring their own OAuth client IDs (Settings → Credentials); the app ships none.
- Choices with side effects are made per transfer job in the Transfer options dialog (Google-native document
  export formats, Office-to-native conversion, whether calendar attendees get invitations, name conflicts).

## Packaging

Snap packaging for Linux is in progress; other installers are out of scope for now.

- The app ID is `tech.mateus.DriveMigrator`. Don't change it: launchers, store listings and AppStream key on it.
- `packaging/linux/` holds the format-neutral desktop integration: the freedesktop `.desktop` entry, the AppStream
  `.metainfo.xml` and the SVG icon, all named after the app ID. Packaging formats install them into the standard
  `usr/share/{applications,metainfo,icons/hicolor}` paths rather than keeping their own copies.
- `snap/snapcraft.yaml` adopts version, summary and description from the metainfo (`adopt-info` + `parse-info`)
  and finds the desktop file and icon through `common-id`. Bump the version by adding a `<release>` to the
  metainfo, not in `snapcraft.yaml`.
- `StartupWMClass=DriveMigrator` matches Avalonia's default X11 `WM_CLASS` (the entry assembly name). Keep
  them in sync if the assembly is renamed or `X11PlatformOptions.WmClass` is set.
- The snap app uses the `gnome` extension alongside `dotnet10`: core24 ships no desktop libraries, and SkiaSharp
  (fontconfig, freetype), Avalonia (X11) and the keyring (libsecret) come from the GNOME content snap. Plugs
  beyond the extension's: `network`, `network-bind` (OAuth loopback redirect) and `password-manager-service`.
- After editing them, validate with `desktop-file-validate packaging/linux/*.desktop` and
  `appstreamcli validate --no-net packaging/linux/*.metainfo.xml`. Build the snap with `snapcraft` from the repo
  root; the snap sets `DRIVEMIGRATOR_DATA_DIR` to `$SNAP_USER_DATA`.

## Commits

Imperative, sentence-case subject with no prefix (e.g. "Add contacts migration between Google Contacts and
Outlook"), then a wrapped body explaining what changed and why.
