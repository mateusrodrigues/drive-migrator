namespace DriveMigrator.Core.Accounts;

/// <summary>Cached credentials are missing, expired or revoked; the user must sign in again.</summary>
public sealed class ReauthenticationRequiredException : Exception
{
    public ReauthenticationRequiredException()
    {
    }

    public ReauthenticationRequiredException(string message)
        : base(message)
    {
    }

    public ReauthenticationRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A provider cannot sign in because its client credentials (client ID etc.) have not been entered.</summary>
public sealed class ProviderNotConfiguredException : Exception
{
    public ProviderNotConfiguredException()
    {
    }

    public ProviderNotConfiguredException(string message)
        : base(message)
    {
    }

    public ProviderNotConfiguredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Re-authorizing an account signed in as a different account than the one being re-authorized.</summary>
public sealed class AccountMismatchException : Exception
{
    public AccountMismatchException()
    {
    }

    public AccountMismatchException(string message)
        : base(message)
    {
    }

    public AccountMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
