namespace DriveMigrator.Infrastructure.Tests;

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("drivemigrator-tests-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
