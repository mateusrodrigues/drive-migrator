namespace DriveMigrator.Core.Contacts;

/// <summary>A provider-neutral contact.</summary>
public sealed record Contact
{
    public required string DisplayName { get; init; }

    public string? GivenName { get; init; }

    public string? MiddleName { get; init; }

    public string? FamilyName { get; init; }

    public string? Nickname { get; init; }

    public string? Company { get; init; }

    public string? Department { get; init; }

    public string? JobTitle { get; init; }

    public IReadOnlyList<LabeledValue> Emails { get; init; } = [];

    public IReadOnlyList<LabeledValue> Phones { get; init; } = [];

    public IReadOnlyList<PostalAddress> Addresses { get; init; } = [];

    public IReadOnlyList<LabeledValue> Websites { get; init; } = [];

    public DateOnly? Birthday { get; init; }

    public string? Notes { get; init; }
}

/// <summary>A value with an optional label such as "home", "work" or "mobile".</summary>
public sealed record LabeledValue(string Value, string? Label = null);

public sealed record PostalAddress
{
    public string? Label { get; init; }

    public string? Street { get; init; }

    public string? City { get; init; }

    public string? Region { get; init; }

    public string? PostalCode { get; init; }

    public string? Country { get; init; }
}
