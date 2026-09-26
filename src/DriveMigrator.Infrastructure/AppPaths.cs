namespace DriveMigrator.Infrastructure;

/// <summary>Where the app keeps its local state.</summary>
public sealed class AppPaths
{
    /// <summary>Environment variable that overrides <see cref="DataDirectory"/> (useful for development and tests).</summary>
    public const string DataDirectoryVariable = "DRIVEMIGRATOR_DATA_DIR";

    public AppPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    /// <summary>
    /// Per-user data directory: %LOCALAPPDATA%\DriveMigrator on Windows, ~/Library/Application Support/DriveMigrator
    /// on macOS, $XDG_DATA_HOME/DriveMigrator (~/.local/share) on Linux.
    /// </summary>
    public string DataDirectory { get; }

    public string AccountsFile => Path.Combine(DataDirectory, "accounts.json");

    /// <summary>SQLite database holding transfer jobs and their progress.</summary>
    public string TransferDatabase => Path.Combine(DataDirectory, "transfers.db");

    /// <summary>Holds DPAPI-encrypted secrets on Windows, and unprotected fallback secrets where no keyring exists.</summary>
    public string SecretsDirectory => Path.Combine(DataDirectory, "secrets");

    public static AppPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        return new AppPaths(!string.IsNullOrWhiteSpace(overridden)
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveMigrator"));
    }

    /// <summary>Creates <paramref name="path"/> if needed, readable only by the current user on Unix.</summary>
    public static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
