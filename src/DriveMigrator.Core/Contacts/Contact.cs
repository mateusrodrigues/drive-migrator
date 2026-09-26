namespace DriveMigrator.Core.Contacts;

/// <summary>A provider-neutral contact. Equality compares all fields, including list contents.</summary>
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

    /// <summary>
    /// Identifies "the same person" across services: the first email address, or the display name when there is
    /// none. Case-insensitive.
    /// </summary>
    public string MatchKey => MatchKeyFor(Emails is [var first, ..] ? first.Value : null, DisplayName);

    public bool Equals(Contact? other)
        => other is not null
            && DisplayName == other.DisplayName
            && GivenName == other.GivenName
            && MiddleName == other.MiddleName
            && FamilyName == other.FamilyName
            && Nickname == other.Nickname
            && Company == other.Company
            && Department == other.Department
            && JobTitle == other.JobTitle
            && Emails.SequenceEqual(other.Emails)
            && Phones.SequenceEqual(other.Phones)
            && Addresses.SequenceEqual(other.Addresses)
            && Websites.SequenceEqual(other.Websites)
            && Birthday == other.Birthday
            && Notes == other.Notes;

    public override int GetHashCode() => HashCode.Combine(DisplayName, Emails.Count, Phones.Count, Birthday);

    public static string MatchKeyFor(string? email, string? displayName)
        => string.IsNullOrWhiteSpace(email)
            ? "name:" + (displayName ?? string.Empty).Trim().ToUpperInvariant()
            : "email:" + email.Trim().ToUpperInvariant();
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
