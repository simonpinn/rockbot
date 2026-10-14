using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace McpServer.Graph.Services;

/// <summary>
/// Graph REST transport shared by the mail, calendar and contacts clients. Every path it builds is
/// under the single configured mailbox; nothing here takes a user from a tool argument.
/// </summary>
public sealed class GraphApi(HttpClient http, IOptions<GraphMailOptions> options)
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string MailboxPath => $"users/{Uri.EscapeDataString(options.Value.Mailbox)}";

    /// <summary>GETs a JSON document. <paramref name="feature"/> names the permission in a 403 hint.</summary>
    public async Task<JsonDocument> GetAsync(string relativeUri, string feature, CancellationToken ct, params (string Name, string Value)[] headers) =>
        await SendAsync(HttpMethod.Get, relativeUri, null, feature, ct, headers)
        ?? throw new McpException("Microsoft Graph returned an empty response.");

    /// <summary>Sends a request. Returns the response document, or null for Graph's empty 202/204 replies.</summary>
    public async Task<JsonDocument?> SendAsync(
        HttpMethod method, string relativeUri, object? body, string feature, CancellationToken ct, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, relativeUri);
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, WriteOptions), Encoding.UTF8, "application/json");

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
            if (!response.IsSuccessStatusCode)
                throw new McpException(DescribeFailure(response.StatusCode, content, feature));
            return string.IsNullOrWhiteSpace(content) ? null : JsonDocument.Parse(content);
        }
    }

    private static string DescribeFailure(HttpStatusCode status, string content, string feature)
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
            HttpStatusCode.Forbidden => $" The app is not authorised for {feature} on this mailbox. If the Exchange role assignment " +
                                        "was created recently it can take up to 2 hours to apply; otherwise re-run deploy/graph/setup.sh.",
            HttpStatusCode.Unauthorized => " The Graph token was rejected; check the client secret has not expired.",
            HttpStatusCode.NotFound => " The item or folder was not found.",
            _ => ""
        };
        return $"Microsoft Graph returned {(int)status}{(detail == "" ? "" : $" ({detail})")}.{hint}";
    }

    /// <summary>
    /// The response's @odata.nextLink as a path relative to the Graph base address, or null when
    /// there is no next page. A link to anywhere else is ignored rather than followed with the token.
    /// </summary>
    public string? RelativeNextLink(JsonElement root)
    {
        var link = Str(root, "@odata.nextLink");
        var baseUri = http.BaseAddress?.ToString();
        return link is not null && baseUri is not null && link.StartsWith(baseUri, StringComparison.OrdinalIgnoreCase)
            ? link[baseUri.Length..]
            : null;
    }

    public static string Query(IEnumerable<(string Key, string Value)> parameters) =>
        "?" + string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    public static string? FormatAddress(JsonElement recipient)
    {
        if (recipient.ValueKind != JsonValueKind.Object || !recipient.TryGetProperty("emailAddress", out var e))
            return null;
        var name = Str(e, "name");
        var address = Str(e, "address");
        return string.IsNullOrEmpty(name) || name == address ? address : $"{name} <{address}>";
    }

    public static string? Str(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool Bool(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;

    public static IReadOnlyList<string> Strings(JsonElement e, string property) =>
        e.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).Where(s => s.Length > 0).ToList()
            : [];
}
