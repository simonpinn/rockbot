using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using static McpServer.Graph.Services.GraphApi;

namespace McpServer.Graph.Services;

public sealed record ContactSummary(
    string Id,
    string? DisplayName,
    IReadOnlyList<string> Emails,
    string? MobilePhone,
    IReadOnlyList<string> BusinessPhones,
    string? Company,
    string? JobTitle);

public sealed record ContactDetail(
    string Id,
    string? DisplayName,
    string? GivenName,
    string? Surname,
    IReadOnlyList<string> Emails,
    string? MobilePhone,
    IReadOnlyList<string> BusinessPhones,
    IReadOnlyList<string> HomePhones,
    string? Company,
    string? JobTitle,
    string? BusinessAddress,
    string? HomeAddress,
    string? Birthday,
    string? Notes);

/// <summary>Graph contact calls, always against the default contacts folder of the configured mailbox.</summary>
public sealed class GraphContactsClient(GraphApi api)
{
    private const string Feature = "contacts";
    private const string SummaryFields = "id,displayName,emailAddresses,mobilePhone,businessPhones,companyName,jobTitle";
    private const string DetailFields = SummaryFields +
        ",givenName,surname,homePhones,businessAddress,homeAddress,birthday,personalNotes";
    private const int MaxScanned = 500;

    private string Contacts => $"{api.MailboxPath}/contacts";

    public async Task<IReadOnlyList<ContactSummary>> ListContactsAsync(int top, int skip, CancellationToken ct)
    {
        var query = Query([
            ("$top", top.ToString(CultureInfo.InvariantCulture)),
            ("$skip", skip.ToString(CultureInfo.InvariantCulture)),
            ("$orderby", "displayName"),
            ("$select", SummaryFields)
        ]);
        using var doc = await api.GetAsync(Contacts + query, Feature, ct);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(ToSummary).ToList();
    }

    /// <summary>Contacts whose display name, first name, last name or company starts with <paramref name="prefix"/>.</summary>
    public async Task<IReadOnlyList<ContactSummary>> SearchByNameAsync(string prefix, int top, CancellationToken ct)
    {
        var literal = "'" + prefix.Replace("'", "''") + "'";
        var filter = string.Join(" or ",
            new[] { "displayName", "givenName", "surname", "companyName" }.Select(f => $"startswith({f},{literal})"));
        var query = Query([("$filter", filter), ("$top", top.ToString(CultureInfo.InvariantCulture)), ("$select", SummaryFields)]);

        using var doc = await api.GetAsync(Contacts + query, Feature, ct);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(ToSummary).ToList();
    }

    /// <summary>
    /// Contacts with an email address containing <paramref name="text"/>. Graph v1.0 can't filter contacts on
    /// part of an address, so this pages through the folder (up to 500 contacts) and matches here.
    /// </summary>
    public async Task<IReadOnlyList<ContactSummary>> SearchByEmailAsync(string text, int top, CancellationToken ct)
    {
        var matches = new List<ContactSummary>();
        string? next = Contacts + Query([("$top", "100"), ("$select", SummaryFields)]);
        var scanned = 0;

        while (next is not null && scanned < MaxScanned && matches.Count < top)
        {
            using var doc = await api.GetAsync(next, Feature, ct);
            foreach (var c in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                scanned++;
                var contact = ToSummary(c);
                if (contact.Emails.Any(e => e.Contains(text, StringComparison.OrdinalIgnoreCase)))
                    matches.Add(contact);
                if (matches.Count >= top)
                    break;
            }
            next = api.RelativeNextLink(doc.RootElement);
        }
        return matches;
    }

    public async Task<ContactDetail> GetContactAsync(string id, CancellationToken ct)
    {
        using var doc = await api.GetAsync($"{Contacts}/{Uri.EscapeDataString(id)}{Query([("$select", DetailFields)])}", Feature, ct);
        return ToDetail(doc.RootElement);
    }

    public async Task<ContactDetail> CreateContactAsync(object body, CancellationToken ct)
    {
        using var doc = await api.SendAsync(HttpMethod.Post, Contacts, body, Feature, ct)
            ?? throw new McpException("Microsoft Graph did not return the created contact.");
        return ToDetail(doc.RootElement);
    }

    public async Task<ContactDetail> UpdateContactAsync(string id, object patch, CancellationToken ct)
    {
        using var doc = await api.SendAsync(HttpMethod.Patch, $"{Contacts}/{Uri.EscapeDataString(id)}", patch, Feature, ct)
            ?? throw new McpException("Microsoft Graph did not return the updated contact.");
        return ToDetail(doc.RootElement);
    }

    private static ContactSummary ToSummary(JsonElement c) => new(
        Id: c.GetProperty("id").GetString()!,
        DisplayName: Str(c, "displayName"),
        Emails: Emails(c),
        MobilePhone: Str(c, "mobilePhone"),
        BusinessPhones: Strings(c, "businessPhones"),
        Company: Str(c, "companyName"),
        JobTitle: Str(c, "jobTitle"));

    private static ContactDetail ToDetail(JsonElement c) => new(
        Id: c.GetProperty("id").GetString()!,
        DisplayName: Str(c, "displayName"),
        GivenName: Str(c, "givenName"),
        Surname: Str(c, "surname"),
        Emails: Emails(c),
        MobilePhone: Str(c, "mobilePhone"),
        BusinessPhones: Strings(c, "businessPhones"),
        HomePhones: Strings(c, "homePhones"),
        Company: Str(c, "companyName"),
        JobTitle: Str(c, "jobTitle"),
        BusinessAddress: FormatPostal(c, "businessAddress"),
        HomeAddress: FormatPostal(c, "homeAddress"),
        Birthday: c.TryGetProperty("birthday", out var b) && b.ValueKind == JsonValueKind.String && b.TryGetDateTimeOffset(out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null,
        Notes: Str(c, "personalNotes"));

    private static IReadOnlyList<string> Emails(JsonElement c) =>
        c.TryGetProperty("emailAddresses", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => Str(e, "address")).OfType<string>().Where(a => a.Length > 0).ToList()
            : [];

    private static string? FormatPostal(JsonElement c, string property)
    {
        if (!c.TryGetProperty(property, out var a) || a.ValueKind != JsonValueKind.Object)
            return null;
        var parts = new[] { "street", "city", "state", "postalCode", "countryOrRegion" }
            .Select(p => Str(a, p)).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
