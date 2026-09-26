using DriveMigrator.Core.Contacts;
using Google.Apis.PeopleService.v1.Data;
using GoogleDate = Google.Apis.PeopleService.v1.Data.Date;

namespace DriveMigrator.Providers.Google.Contacts;

/// <summary>Maps between the neutral <see cref="Contact"/> and the People API's Person.</summary>
internal static class PersonMapper
{
    /// <summary>Fields read for a full contact.</summary>
    internal const string FullFields = "names,nicknames,emailAddresses,phoneNumbers,addresses,organizations,birthdays,biographies,urls";

    /// <summary>Fields read for listing (name and email only).</summary>
    internal const string ListFields = "names,emailAddresses";

    /// <summary>The year used for birthdays without one (Outlook's convention).</summary>
    internal const int NoYear = 1604;

    public static Contact ToContact(Person person)
    {
        ArgumentNullException.ThrowIfNull(person);
        var name = person.Names?.FirstOrDefault();
        var organization = person.Organizations?.FirstOrDefault();
        var emails = (person.EmailAddresses ?? []).Where(e => !string.IsNullOrWhiteSpace(e.Value)).Select(e => new LabeledValue(e.Value, Empty(e.Type))).ToList();
        return new Contact
        {
            DisplayName = Empty(name?.DisplayName) ?? Empty(name?.UnstructuredName) ?? emails.FirstOrDefault()?.Value ?? "(no name)",
            GivenName = Empty(name?.GivenName),
            MiddleName = Empty(name?.MiddleName),
            FamilyName = Empty(name?.FamilyName),
            Nickname = Empty(person.Nicknames?.FirstOrDefault()?.Value),
            Company = Empty(organization?.Name),
            Department = Empty(organization?.Department),
            JobTitle = Empty(organization?.Title),
            Emails = emails,
            Phones = [.. (person.PhoneNumbers ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => new LabeledValue(p.Value, Empty(p.Type)))],
            Addresses = [.. (person.Addresses ?? []).Select(a => new PostalAddress
            {
                Label = Empty(a.Type),
                Street = Empty(a.StreetAddress) ?? (a.City is null && a.PostalCode is null ? Empty(a.FormattedValue) : null),
                City = Empty(a.City),
                Region = Empty(a.Region),
                PostalCode = Empty(a.PostalCode),
                Country = Empty(a.Country),
            })],
            Websites = [.. (person.Urls ?? []).Where(u => !string.IsNullOrWhiteSpace(u.Value)).Select(u => new LabeledValue(u.Value, Empty(u.Type)))],
            Birthday = person.Birthdays?.Select(b => b.Date).FirstOrDefault(d => d?.Month is > 0 && d.Day is > 0) is { } date
                ? new DateOnly(date.Year is > 0 ? date.Year.Value : NoYear, date.Month!.Value, date.Day!.Value)
                : null,
            Notes = Empty(person.Biographies?.FirstOrDefault()?.Value),
        };
    }

    public static Person ToPerson(Contact contact, string? groupResourceName)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var structured = contact.GivenName is not null || contact.FamilyName is not null;
        var person = new Person
        {
            Names =
            [
                structured
                    ? new Name { GivenName = contact.GivenName, MiddleName = contact.MiddleName, FamilyName = contact.FamilyName }
                    : new Name { UnstructuredName = contact.DisplayName },
            ],
            EmailAddresses = [.. contact.Emails.Select(e => new EmailAddress { Value = e.Value, Type = e.Label })],
            PhoneNumbers = [.. contact.Phones.Select(p => new PhoneNumber { Value = p.Value, Type = p.Label })],
            Addresses = [.. contact.Addresses.Select(a => new Address
            {
                Type = a.Label,
                StreetAddress = a.Street,
                City = a.City,
                Region = a.Region,
                PostalCode = a.PostalCode,
                Country = a.Country,
            })],
            Urls = [.. contact.Websites.Select(w => new Url { Value = w.Value, Type = w.Label })],
        };

        if (contact.Nickname is not null)
        {
            person.Nicknames = [new Nickname { Value = contact.Nickname }];
        }

        if (contact.Company is not null || contact.Department is not null || contact.JobTitle is not null)
        {
            person.Organizations = [new Organization { Name = contact.Company, Department = contact.Department, Title = contact.JobTitle }];
        }

        if (contact.Birthday is { } birthday)
        {
            person.Birthdays = [new Birthday { Date = new GoogleDate { Year = birthday.Year == NoYear ? null : birthday.Year, Month = birthday.Month, Day = birthday.Day } }];
        }

        if (contact.Notes is not null)
        {
            person.Biographies = [new Biography { Value = contact.Notes, ContentType = "TEXT_PLAIN" }];
        }

        if (groupResourceName is not null)
        {
            person.Memberships = [new Membership { ContactGroupMembership = new ContactGroupMembership { ContactGroupResourceName = groupResourceName } }];
        }

        return person;
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
