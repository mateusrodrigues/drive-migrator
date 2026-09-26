namespace DriveMigrator.Core.Accounts;

public enum AccountStatus
{
    Connected,
    NeedsReauthorization,
    Error,
}

/// <summary>A persisted account and, when it could be restored, its live session.</summary>
public sealed record ConnectedAccount(AccountInfo Info, AccountStatus Status, IAccountSession? Session = null, string? Error = null);
