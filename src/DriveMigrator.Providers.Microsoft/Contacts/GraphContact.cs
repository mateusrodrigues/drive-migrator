namespace DriveMigrator.Providers.Microsoft.Contacts;

// Subsets of Graph's contactFolder and contact resources.
internal sealed record ContactFolder(string Id, string DisplayName);

internal sealed record GraphContact
{
    public string? Id { get; init; }

    public string? DisplayName { get; init; }

    public string? GivenName { get; init; }

    public string? MiddleName { get; init; }

    public string? Surname { get; init; }

    public string? NickName { get; init; }

    public string? CompanyName { get; init; }

    public string? Department { get; init; }

    public string? JobTitle { get; init; }

    public List<GraphEmail>? EmailAddresses { get; init; }

    public List<string>? BusinessPhones { get; init; }

    public List<string>? HomePhones { get; init; }

    public string? MobilePhone { get; init; }

    public GraphAddress? HomeAddress { get; init; }

    public GraphAddress? BusinessAddress { get; init; }

    public GraphAddress? OtherAddress { get; init; }

    /// <summary>Kept as text: only the date part is meaningful.</summary>
    public string? Birthday { get; init; }

    public string? PersonalNotes { get; init; }

    public string? BusinessHomePage { get; init; }
}

internal sealed record GraphEmail(string? Name, string? Address);

internal sealed record GraphAddress(string? Street, string? City, string? State, string? PostalCode, string? CountryOrRegion)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Street) && string.IsNullOrWhiteSpace(City) && string.IsNullOrWhiteSpace(State)
        && string.IsNullOrWhiteSpace(PostalCode) && string.IsNullOrWhiteSpace(CountryOrRegion);
}
