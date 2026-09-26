using System.Globalization;
using System.Text;
using DriveMigrator.Core.Contacts;

namespace DriveMigrator.Providers.Microsoft.Contacts;

/// <summary>
/// Maps between the neutral <see cref="Contact"/> and Graph's contact, which has fixed slots: up to three emails,
/// one mobile plus business and home phone lists, home/business/other addresses and one web page. Values with no
/// slot are written to the notes rather than dropped.
/// </summary>
internal static class GraphContactMapper
{
    internal const int MaxEmails = 3;

    /// <summary>Outlook's year for birthdays whose year is unknown.</summary>
    internal const int NoYear = 1604;

    public static Contact ToContact(GraphContact graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var phones = new List<LabeledValue>();
        if (!string.IsNullOrWhiteSpace(graph.MobilePhone))
        {
            phones.Add(new LabeledValue(graph.MobilePhone, "mobile"));
        }

        phones.AddRange((graph.BusinessPhones ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => new LabeledValue(p, "work")));
        phones.AddRange((graph.HomePhones ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => new LabeledValue(p, "home")));

        var addresses = new List<PostalAddress>();
        AddAddress(addresses, graph.HomeAddress, "home");
        AddAddress(addresses, graph.BusinessAddress, "work");
        AddAddress(addresses, graph.OtherAddress, "other");

        return new Contact
        {
            DisplayName = FirstNonEmpty(graph.DisplayName, Join(graph.GivenName, graph.Surname), graph.EmailAddresses?.FirstOrDefault()?.Address) ?? "(no name)",
            GivenName = Empty(graph.GivenName),
            MiddleName = Empty(graph.MiddleName),
            FamilyName = Empty(graph.Surname),
            Nickname = Empty(graph.NickName),
            Company = Empty(graph.CompanyName),
            Department = Empty(graph.Department),
            JobTitle = Empty(graph.JobTitle),
            Emails = [.. (graph.EmailAddresses ?? []).Where(e => !string.IsNullOrWhiteSpace(e.Address)).Select(e => new LabeledValue(e.Address!))],
            Phones = phones,
            Addresses = addresses,
            Websites = string.IsNullOrWhiteSpace(graph.BusinessHomePage) ? [] : [new LabeledValue(graph.BusinessHomePage, "work")],
            Birthday = ParseBirthday(graph.Birthday),
            Notes = Empty(graph.PersonalNotes),
        };
    }

    public static Dictionary<string, object> ToGraph(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var overflow = new List<string>();
        var json = new Dictionary<string, object> { ["displayName"] = contact.DisplayName };
        Set(json, "givenName", contact.GivenName);
        Set(json, "middleName", contact.MiddleName);
        Set(json, "surname", contact.FamilyName);
        Set(json, "nickName", contact.Nickname);
        Set(json, "companyName", contact.Company);
        Set(json, "department", contact.Department);
        Set(json, "jobTitle", contact.JobTitle);

        json["emailAddresses"] = contact.Emails.Take(MaxEmails)
            .Select(e => new Dictionary<string, object> { ["address"] = e.Value, ["name"] = contact.DisplayName }).ToList();
        overflow.AddRange(contact.Emails.Skip(MaxEmails).Select(e => $"Email{Label(e)}: {e.Value}"));

        string? mobile = null;
        var business = new List<string>();
        var home = new List<string>();
        foreach (var phone in contact.Phones)
        {
            switch (phone.Label?.ToUpperInvariant())
            {
                case "MOBILE" or "CELL" when mobile is null:
                    mobile = phone.Value;
                    break;
                case "WORK" or "BUSINESS" or "WORKMOBILE" or "WORKFAX":
                    business.Add(phone.Value);
                    break;
                default:
                    home.Add(phone.Value);
                    break;
            }
        }

        Set(json, "mobilePhone", mobile);
        json["businessPhones"] = business;
        json["homePhones"] = home;

        var slots = new Dictionary<string, PostalAddress>();
        foreach (var address in contact.Addresses)
        {
            var slot = address.Label?.ToUpperInvariant() switch
            {
                "HOME" => "homeAddress",
                "WORK" or "BUSINESS" => "businessAddress",
                _ => "otherAddress",
            };
            if (!slots.TryAdd(slot, address) && !slots.TryAdd("otherAddress", address))
            {
                overflow.Add($"Address{LabelOf(address.Label)}: {FormatAddress(address)}");
            }
        }

        foreach (var (slot, address) in slots)
        {
            json[slot] = new Dictionary<string, object?>
            {
                ["street"] = address.Street,
                ["city"] = address.City,
                ["state"] = address.Region,
                ["postalCode"] = address.PostalCode,
                ["countryOrRegion"] = address.Country,
            };
        }

        if (contact.Websites.Count > 0)
        {
            json["businessHomePage"] = contact.Websites[0].Value;
            overflow.AddRange(contact.Websites.Skip(1).Select(w => $"Website{Label(w)}: {w.Value}"));
        }

        if (contact.Birthday is { } birthday)
        {
            json["birthday"] = birthday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00Z";
        }

        var notes = new StringBuilder(contact.Notes);
        if (overflow.Count > 0)
        {
            if (notes.Length > 0)
            {
                notes.AppendLine().AppendLine();
            }

            notes.Append(string.Join(Environment.NewLine, overflow));
        }

        Set(json, "personalNotes", notes.Length == 0 ? null : notes.ToString());
        return json;
    }

    private static DateOnly? ParseBirthday(string? value)
        => value is { Length: >= 10 } && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static void AddAddress(List<PostalAddress> addresses, GraphAddress? address, string label)
    {
        if (address is { IsEmpty: false })
        {
            addresses.Add(new PostalAddress
            {
                Label = label,
                Street = Empty(address.Street),
                City = Empty(address.City),
                Region = Empty(address.State),
                PostalCode = Empty(address.PostalCode),
                Country = Empty(address.CountryOrRegion),
            });
        }
    }

    private static string FormatAddress(PostalAddress a)
        => string.Join(", ", new[] { a.Street, a.City, a.Region, a.PostalCode, a.Country }.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string Label(LabeledValue value) => LabelOf(value.Label);

    private static string LabelOf(string? label) => string.IsNullOrWhiteSpace(label) ? string.Empty : $" ({label})";

    private static void Set(Dictionary<string, object> json, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            json[name] = value;
        }
    }

    private static string? Join(string? first, string? last) => Empty(string.Join(' ', new[] { first, last }.Where(p => !string.IsNullOrWhiteSpace(p))));

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
