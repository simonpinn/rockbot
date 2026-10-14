using System.Text.Json;
using Microsoft.Extensions.Options;
using static McpServer.Graph.Services.GraphApi;

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
public sealed class GraphMailClient(GraphApi api, IOptions<GraphMailOptions> options)
{
    private const string SummaryFields = "id,subject,from,receivedDateTime,isRead,hasAttachments,bodyPreview";
    private const string DetailFields = "id,subject,from,toRecipients,ccRecipients,receivedDateTime,isRead,hasAttachments,body";

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

        using var doc = await GetAsync($"{api.MailboxPath}/mailFolders/{Uri.EscapeDataString(folder)}/messages{Query(query)}", textBody: false, ct);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<MessageSummary>> SearchMessagesAsync(string search, int top, CancellationToken ct)
    {
        var kql = "\"" + search.Replace("\"", string.Empty) + "\"";
        var query = Query([("$search", kql), ("$top", top.ToString()), ("$select", SummaryFields)]);

        using var doc = await GetAsync($"{api.MailboxPath}/messages{query}", textBody: false, ct);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(ToSummary).ToList();
    }

    public async Task<MessageDetail> GetMessageAsync(string id, CancellationToken ct)
    {
        using var doc = await GetAsync($"{api.MailboxPath}/messages/{Uri.EscapeDataString(id)}{Query([("$select", DetailFields)])}", textBody: true, ct);
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

    private static MessageSummary ToSummary(JsonElement m) => new(
        Id: m.GetProperty("id").GetString()!,
        Subject: Str(m, "subject"),
        From: m.TryGetProperty("from", out var from) ? FormatAddress(from) : null,
        Received: m.TryGetProperty("receivedDateTime", out var r) ? r.GetDateTimeOffset() : null,
        IsRead: Bool(m, "isRead"),
        HasAttachments: Bool(m, "hasAttachments"),
        Preview: Str(m, "bodyPreview"));

    private Task<JsonDocument> GetAsync(string relativeUri, bool textBody, CancellationToken ct) =>
        textBody
            ? api.GetAsync(relativeUri, "mail", ct, ("Prefer", "outlook.body-content-type=\"text\""))
            : api.GetAsync(relativeUri, "mail", ct);

    private static IReadOnlyList<string> Recipients(JsonElement m, string property) =>
        m.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(FormatAddress).OfType<string>().ToList()
            : [];
}
