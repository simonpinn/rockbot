using System.Net;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace McpServer.Graph.Services;

public sealed record MessageSummary(
    string Id,
    string? Subject,
    string? From,
    DateTimeOffset? Received,
    bool IsRead,
    bool HasAttachments,
    string? Preview);

public sealed record MessageDetail(
    string Id,
    string? Subject,
    string? From,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    DateTimeOffset? Received,
    bool IsRead,
    bool HasAttachments,
    string Body,
    bool BodyTruncated);

/// <summary>Read-only Graph REST calls, always against the single configured mailbox.</summary>
public sealed class GraphMailClient(HttpClient http, IOptions<GraphMailOptions> options)
{
    private const string SummaryFields = "id,subject,from,receivedDateTime,isRead,hasAttachments,bodyPreview";
    private const string DetailFields = "id,subject,from,toRecipients,ccRecipients,receivedDateTime,isRead,hasAttachments,body";

    private string MailboxPath => $"users/{Uri.EscapeDataString(options.Value.Mailbox)}";

    public async Task<IReadOnlyList<MessageSummary>> ListMessagesAsync(string folder, int top, bool unreadOnly, CancellationToken ct)
    {
        var query = new List<(string, string)>
        {
            ("$top", top.ToString()),
            ("$select", SummaryFields),
            ("$orderby", "receivedDateTime desc")
        };
        // Graph rejects $orderby on a property absent from $filter, hence the always-true date clause.
        if (unreadOnly)
            query.Add(("$filter", "receivedDateTime ge 1900-01-01T00:00:00Z and isRead eq false"));

        using var doc = await GetAsync($"{MailboxPath}/mailFolders/{Uri.EscapeDataString(folder)}/messages{Query(query)}", textBody: false, ct);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<MessageSummary>> SearchMessagesAsync(string search, int top, CancellationToken ct)
    {
        var kql = "\"" + search.Replace("\"", string.Empty) + "\"";
        var query = Query([("$search", kql), ("$top", top.ToString()), ("$select", SummaryFields)]);

        using var doc = await GetAsync($"{MailboxPath}/messages{query}", textBody: false, ct);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(ToSummary).ToList();
    }

    public async Task<MessageDetail> GetMessageAsync(string id, CancellationToken ct)
    {
        using var doc = await GetAsync($"{MailboxPath}/messages/{Uri.EscapeDataString(id)}{Query([("$select", DetailFields)])}", textBody: true, ct);
        var m = doc.RootElement;

        var body = m.TryGetProperty("body", out var b) && b.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var max = options.Value.MaxBodyChars;
        var truncated = body.Length > max;

        return new MessageDetail(
            Id: m.GetProperty("id").GetString()!,
            Subject: Str(m, "subject"),
            From: m.TryGetProperty("from", out var from) ? FormatAddress(from) : null,
            To: Recipients(m, "toRecipients"),
            Cc: Recipients(m, "ccRecipients"),
            Received: m.TryGetProperty("receivedDateTime", out var r) ? r.GetDateTimeOffset() : null,
            IsRead: Bool(m, "isRead"),
            HasAttachments: Bool(m, "hasAttachments"),
            Body: truncated ? body[..max] : body,
            BodyTruncated: truncated);
    }

    private async Task<JsonDocument> GetAsync(string relativeUri, bool textBody, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);
        if (textBody)
            request.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new McpException($"could not authenticate to Microsoft Graph; check the GRAPH_* settings: {ex.Message}", ex);
        }

        using (response)
        {
            var content = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                return JsonDocument.Parse(content);

            throw new McpException(DescribeFailure(response.StatusCode, content));
        }
    }

    private static string DescribeFailure(HttpStatusCode status, string content)
    {
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("error", out var error))
                detail = $"{Str(error, "code")}: {Str(error, "message")}";
        }
        catch (JsonException)
        {
        }

        var hint = status switch
        {
            HttpStatusCode.Forbidden => " The app is not authorised for this mailbox. If the Exchange role assignment " +
                                        "was created recently it can take up to 2 hours to apply; otherwise re-run deploy/graph/setup.sh.",
            HttpStatusCode.Unauthorized => " The Graph token was rejected; check the client secret has not expired.",
            HttpStatusCode.NotFound => " The message or folder was not found.",
            _ => ""
        };
        return $"Microsoft Graph returned {(int)status}{(detail == "" ? "" : $" ({detail})")}.{hint}";
    }

    private static string Query(IEnumerable<(string Key, string Value)> parameters) =>
        "?" + string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    private static MessageSummary ToSummary(JsonElement m) => new(
        Id: m.GetProperty("id").GetString()!,
        Subject: Str(m, "subject"),
        From: m.TryGetProperty("from", out var from) ? FormatAddress(from) : null,
        Received: m.TryGetProperty("receivedDateTime", out var r) ? r.GetDateTimeOffset() : null,
        IsRead: Bool(m, "isRead"),
        HasAttachments: Bool(m, "hasAttachments"),
        Preview: Str(m, "bodyPreview"));

    private static IReadOnlyList<string> Recipients(JsonElement m, string property) =>
        m.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(FormatAddress).OfType<string>().ToList()
            : [];

    private static string? FormatAddress(JsonElement recipient)
    {
        if (recipient.ValueKind != JsonValueKind.Object || !recipient.TryGetProperty("emailAddress", out var e))
            return null;
        var name = Str(e, "name");
        var address = Str(e, "address");
        return string.IsNullOrEmpty(name) || name == address ? address : $"{name} <{address}>";
    }

    private static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;
}
