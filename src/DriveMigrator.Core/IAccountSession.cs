namespace DriveMigrator.Core;

/// <summary>An authenticated account and the capabilities it can use.</summary>
public interface IAccountSession
{
    AccountInfo Account { get; }

    IReadOnlyList<ICapability> Capabilities { get; }
}

public static class AccountSessionExtensions
{
    public static T? GetCapability<T>(this IAccountSession session)
        where T : class, ICapability
        => session.Capabilities.OfType<T>().FirstOrDefault();

    public static ICapability? GetCapability(this IAccountSession session, CapabilityKind kind)
        => session.Capabilities.FirstOrDefault(c => c.Kind == kind);
}
