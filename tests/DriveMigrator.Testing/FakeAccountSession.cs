using DriveMigrator.Core;

namespace DriveMigrator.Testing;

public sealed class FakeAccountSession : IAccountSession
{
    internal FakeAccountSession(AccountInfo account, IReadOnlySet<CapabilityKind> supportedCapabilities)
    {
        Account = account;
        ICapability[] all = [Drive, Mail, Calendar, Contacts];
        Capabilities = [.. all.Where(c => supportedCapabilities.Contains(c.Kind))];
    }

    public AccountInfo Account { get; }

    public IReadOnlyList<ICapability> Capabilities { get; }

    public FakeDriveCapability Drive { get; } = new();

    public FakeMailCapability Mail { get; } = new();

    public FakeCalendarCapability Calendar { get; } = new();

    public FakeContactsCapability Contacts { get; } = new();
}
