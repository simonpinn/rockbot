using System.ComponentModel;
using System.Text.Json;
using McpServer.Graph.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using static McpServer.Graph.Tools.ToolArgs;

namespace McpServer.Graph.Tools;

[McpServerToolType]
public sealed class ContactsTools(GraphContactsClient client)
{
    private const int MaxTop = 50;
    private const int MaxSkip = 5_000;
    private const int MaxQuery = 100;
    private const int MaxEmails = 3;     // Outlook contacts hold at most three addresses.
    private const int MaxPhones = 5;
    private const int MaxName = 255;
    private const int MaxPhone = 64;
    private const int MaxNotes = 10_000;

    private const string SummaryShape = "{ id, displayName, emails, mobilePhone, businessPhones, company, jobTitle }";
    private const string DetailShape =
        "{ id, displayName, givenName, surname, emails, mobilePhone, businessPhones, homePhones, company, jobTitle, " +
        "businessAddress, homeAddress, birthday, notes }";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [McpServerTool(Name = "list_contacts", ReadOnly = true)]
    [Description("Lists contacts in the configured mailbox's default contacts folder, sorted by display name. " +
                 "Returns a JSON array of " + SummaryShape + "." + UntrustedNote)]
    public Task<string> ListContactsAsync(
        [Description("Number of contacts to return, 1-50. Defaults to 25.")] int top = 25,
        [Description("Number of contacts to skip, for paging. Defaults to 0.")] int skip = 0,
        CancellationToken cancellationToken = default) => Run(async () =>
    {
        RequireTop(top, MaxTop);
        if (skip is < 0 or > MaxSkip)
            throw new McpException($"skip must be between 0 and {MaxSkip}");
        return await client.ListContactsAsync(top, skip, cancellationToken);
    });

    [McpServerTool(Name = "search_contacts", ReadOnly = true)]
    [Description("Finds contacts in the configured mailbox. Text without '@' matches the start of the display name, first name, " +
                 "last name or company; text with '@' matches part of an email address. " +
                 "Returns a JSON array of " + SummaryShape + "." + UntrustedNote)]
    public Task<string> SearchContactsAsync(
        [Description("Name, company or email text, e.g. 'Alice', 'Contoso' or '@contoso.com'. Max 100 characters.")] string query,
        [Description("Number of contacts to return, 1-50. Defaults to 25.")] int top = 25,
        CancellationToken cancellationToken = default) => Run(async () =>
    {
        var text = RequireText(query, nameof(query), MaxQuery);
        RequireTop(top, MaxTop);
        return text.Contains('@')
            ? await client.SearchByEmailAsync(text, top, cancellationToken)
            : await client.SearchByNameAsync(text, top, cancellationToken);
    });

    [McpServerTool(Name = "get_contact", ReadOnly = true)]
    [Description("Gets one contact with all its details. Returns JSON " + DetailShape + "." + UntrustedNote)]
    public Task<string> GetContactAsync(
        [Description("Contact id, as returned by list_contacts or search_contacts.")] string id,
        CancellationToken cancellationToken = default) => Run(async () =>
    {
        RequireGraphId(id, nameof(id));
        return await client.GetContactAsync(id, cancellationToken);
    });

    [McpServerTool(Name = "create_contact", Destructive = false)]
    [Description("Adds a contact to the configured mailbox's default contacts folder. Needs a name or an email address. " +
                 "Returns the created contact as JSON " + DetailShape + ".")]
    public Task<string> CreateContactAsync(
        [Description("First name.")] string? given_name = null,
        [Description("Last name.")] string? surname = null,
        [Description("Name as shown in lists. Defaults to first and last name, the company, or the first email address.")] string? display_name = null,
        [Description("Email addresses, at most 3.")] string[]? emails = null,
        [Description("Mobile phone number.")] string? mobile_phone = null,
        [Description("Business phone numbers.")] string[]? business_phones = null,
        [Description("Company name.")] string? company = null,
        [Description("Job title.")] string? job_title = null,
        [Description("Notes, as plain text.")] string? notes = null,
        CancellationToken cancellationToken = default) => Run(async () =>
    {
        var changes = Changes(given_name, surname, display_name, emails, mobile_phone, business_phones, company, job_title, notes);
        if (!changes.ContainsKey("displayName"))
        {
            var name = $"{changes.GetValueOrDefault("givenName")} {changes.GetValueOrDefault("surname")}".Trim();
            var fallback = name.Length > 0 ? name
                : changes.GetValueOrDefault("companyName") as string
                ?? (changes.GetValueOrDefault("emailAddresses") as EmailAddress[])?.FirstOrDefault()?.Address;
            if (string.IsNullOrEmpty(fallback))
                throw new McpException("give at least a name, a company or an email address");
            changes["displayName"] = fallback;
        }
        return await client.CreateContactAsync(changes, cancellationToken);
    });

    [McpServerTool(Name = "update_contact", Destructive = false)]
    [Description("Changes a contact in the configured mailbox; only the arguments given are changed, and an empty string clears a text field. " +
                 "emails and business_phones replace the existing lists. Returns the updated contact as JSON " + DetailShape + ".")]
    public Task<string> UpdateContactAsync(
        [Description("Contact id, as returned by list_contacts or search_contacts.")] string id,
        [Description("First name.")] string? given_name = null,
        [Description("Last name.")] string? surname = null,
        [Description("Name as shown in lists.")] string? display_name = null,
        [Description("The complete new list of email addresses, at most 3.")] string[]? emails = null,
        [Description("Mobile phone number.")] string? mobile_phone = null,
        [Description("The complete new list of business phone numbers.")] string[]? business_phones = null,
        [Description("Company name.")] string? company = null,
        [Description("Job title.")] string? job_title = null,
        [Description("Notes, as plain text; replaces the existing notes.")] string? notes = null,
        CancellationToken cancellationToken = default) => Run(async () =>
    {
        RequireGraphId(id, nameof(id));
        var changes = Changes(given_name, surname, display_name, emails, mobile_phone, business_phones, company, job_title, notes);
        if (changes.Count == 0)
            throw new McpException("nothing to update; give at least one field to change");
        return await client.UpdateContactAsync(id, changes, cancellationToken);
    });

    private sealed record EmailAddress(string Address, string? Name);

    private static Dictionary<string, object?> Changes(
        string? givenName, string? surname, string? displayName, string[]? emails, string? mobilePhone,
        string[]? businessPhones, string? company, string? jobTitle, string? notes)
    {
        var changes = new Dictionary<string, object?>();
        void Text(string key, string? value, string name, int max)
        {
            if (OptionalText(value, name, max) is { } text)
                changes[key] = text;
        }

        Text("givenName", givenName, "given_name", MaxName);
        Text("surname", surname, "surname", MaxName);
        Text("displayName", displayName, "display_name", MaxName);
        Text("mobilePhone", mobilePhone, "mobile_phone", MaxPhone);
        Text("companyName", company, "company", MaxName);
        Text("jobTitle", jobTitle, "job_title", MaxName);
        Text("personalNotes", notes, "notes", MaxNotes);

        if (emails is not null)
        {
            var name = changes.GetValueOrDefault("displayName") as string;
            changes["emailAddresses"] = RequireEmails(emails, "emails", MaxEmails)
                .Select(address => new EmailAddress(address, name ?? address))
                .ToArray();
        }
        if (businessPhones is not null)
        {
            if (businessPhones.Length > MaxPhones)
                throw new McpException($"business_phones may hold at most {MaxPhones} numbers");
            changes["businessPhones"] = businessPhones.Select(p => RequireText(p, "business_phones", MaxPhone)).ToArray();
        }

        // Empty display names would make the contact unfindable in Outlook.
        if (changes.GetValueOrDefault("displayName") is "")
            changes.Remove("displayName");
        return changes;
    }

    private static async Task<string> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return JsonSerializer.Serialize(await action(), JsonOptions);
        }
        catch (Exception ex) when (ex is not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
