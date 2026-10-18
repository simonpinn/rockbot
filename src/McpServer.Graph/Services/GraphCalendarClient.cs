using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using static McpServer.Graph.Services.GraphApi;

namespace McpServer.Graph.Services;

public sealed record EventSummary(
    string Id,
    string? Subject,
    string? Start,
    string? End,
    bool IsAllDay,
    string? Location,
    string? Organizer,
    bool IsOrganizer,
    int Attendees,
    string? MyResponse,
    string? ShowAs,
    bool IsCancelled,
    string? OnlineMeetingUrl);

public sealed record EventDetail(
    string Id,
    string? Subject,
    string? Start,
    string? End,
    bool IsAllDay,
    string? Location,
    string? Organizer,
    bool IsOrganizer,
    IReadOnlyList<string> Attendees,
    string? MyResponse,
    string? ShowAs,
    bool IsCancelled,
    string? OnlineMeetingUrl,
    string? WebLink,
    string Body,
    bool BodyTruncated);

/// <summary>What delete and respond need to know about an event before acting on it.</summary>
public sealed record EventRole(bool IsOrganizer, int Attendees);

/// <summary>A busy block, in the configured time zone.</summary>
public sealed record BusyBlock(DateTime Start, DateTime End);

/// <summary>Graph calendar calls, always against the default calendar of the configured mailbox.</summary>
public sealed class GraphCalendarClient(GraphApi api, GraphTimeZone zone, IOptions<GraphMailOptions> options)
{
    private const string Feature = "calendar";
    private const string SummaryFields =
        "id,subject,start,end,isAllDay,location,organizer,isOrganizer,attendees,responseStatus,showAs,isCancelled,onlineMeeting";
    private const string DetailFields = SummaryFields + ",webLink,body";
    private const int MaxBusyEvents = 1_000;

    private string Events => $"{api.MailboxPath}/events";

    // Graph returns event times in this zone, so what the agent sees matches what it sends.
    private (string, string) TimeZoneHeader => ("Prefer", $"outlook.timezone=\"{zone.GraphName}\"");

    public async Task<IReadOnlyList<EventSummary>> ListEventsAsync(DateTime start, DateTime end, int top, CancellationToken ct)
    {
        var query = Query([
            ("startDateTime", Instant(start)),
            ("endDateTime", Instant(end)),
            ("$top", top.ToString(CultureInfo.InvariantCulture)),
            ("$orderby", "start/dateTime"),
            ("$select", SummaryFields)
        ]);
        using var doc = await api.GetAsync($"{api.MailboxPath}/calendarView{query}", Feature, ct, TimeZoneHeader);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(ToSummary).ToList();
    }

    /// <summary>Every event in the range that blocks time: not cancelled and not shown as free.</summary>
    public async Task<IReadOnlyList<BusyBlock>> ListBusyAsync(DateTime start, DateTime end, CancellationToken ct)
    {
        var busy = new List<BusyBlock>();
        string? next = $"{api.MailboxPath}/calendarView" + Query([
            ("startDateTime", Instant(start)),
            ("endDateTime", Instant(end)),
            ("$top", "100"),
            ("$select", "start,end,showAs,isCancelled")
        ]);
        var seen = 0;

        while (next is not null)
        {
            using var doc = await api.GetAsync(next, Feature, ct, TimeZoneHeader);
            foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (++seen > MaxBusyEvents)
                    throw new McpException($"more than {MaxBusyEvents} events in that range; pick a shorter one");
                if (Bool(e, "isCancelled") || Str(e, "showAs") is "free" or "workingElsewhere")
                    continue;
                if (ReadTime(e, "start") is { } s && ReadTime(e, "end") is { } f)
                    busy.Add(new BusyBlock(s, f));
            }
            next = api.RelativeNextLink(doc.RootElement);
        }
        return busy;
    }

    public async Task<EventDetail> GetEventAsync(string id, CancellationToken ct)
    {
        using var doc = await api.GetAsync($"{Events}/{Uri.EscapeDataString(id)}{Query([("$select", DetailFields)])}", Feature, ct,
            TimeZoneHeader, ("Prefer", "outlook.body-content-type=\"text\""));
        var e = doc.RootElement;

        var body = e.TryGetProperty("body", out var b) ? Str(b, "content") ?? "" : "";
        var max = options.Value.MaxBodyChars;
        var truncated = body.Length > max;
        var summary = ToSummary(e);

        return new EventDetail(
            summary.Id, summary.Subject, summary.Start, summary.End, summary.IsAllDay, summary.Location,
            summary.Organizer, summary.IsOrganizer, Attendees(e), summary.MyResponse, summary.ShowAs,
            summary.IsCancelled, summary.OnlineMeetingUrl, Str(e, "webLink"),
            truncated ? body[..max] : body, truncated);
    }

    public async Task<EventRole> GetRoleAsync(string id, CancellationToken ct)
    {
        using var doc = await api.GetAsync($"{Events}/{Uri.EscapeDataString(id)}{Query([("$select", "isOrganizer,attendees")])}", Feature, ct);
        var e = doc.RootElement;
        return new EventRole(Bool(e, "isOrganizer"), AttendeeCount(e));
    }

    public async Task<EventSummary> CreateEventAsync(object body, CancellationToken ct)
    {
        using var doc = await api.SendAsync(HttpMethod.Post, Events, body, Feature, ct, TimeZoneHeader)
            ?? throw new McpException("Microsoft Graph did not return the created event.");
        return ToSummary(doc.RootElement);
    }

    public async Task<EventSummary> UpdateEventAsync(string id, object patch, CancellationToken ct)
    {
        using var doc = await api.SendAsync(HttpMethod.Patch, $"{Events}/{Uri.EscapeDataString(id)}", patch, Feature, ct, TimeZoneHeader)
            ?? throw new McpException("Microsoft Graph did not return the updated event.");
        return ToSummary(doc.RootElement);
    }

    /// <summary>Organiser only: cancels the meeting and sends attendees a cancellation.</summary>
    public async Task CancelEventAsync(string id, string? comment, CancellationToken ct) =>
        (await api.SendAsync(HttpMethod.Post, $"{Events}/{Uri.EscapeDataString(id)}/cancel", new { comment = comment ?? "" }, Feature, ct))?.Dispose();

    public async Task DeleteEventAsync(string id, CancellationToken ct) =>
        (await api.SendAsync(HttpMethod.Delete, $"{Events}/{Uri.EscapeDataString(id)}", null, Feature, ct))?.Dispose();

    /// <param name="action">accept, decline or tentativelyAccept.</param>
    public async Task RespondAsync(string id, string action, string? comment, bool sendResponse, CancellationToken ct) =>
        (await api.SendAsync(HttpMethod.Post, $"{Events}/{Uri.EscapeDataString(id)}/{action}",
            new { comment = comment ?? "", sendResponse }, Feature, ct))?.Dispose();

    /// <summary>A wall-clock time in the configured zone, as an instant with its offset.</summary>
    private string Instant(DateTime local)
    {
        var offset = zone.Info.IsInvalidTime(local) ? zone.Info.BaseUtcOffset : zone.Info.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a dateTimeTimeZone property as wall-clock time in the configured zone.</summary>
    private DateTime? ReadTime(JsonElement e, string property)
    {
        if (!e.TryGetProperty(property, out var value) || Str(value, "dateTime") is not { } text
            || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return null;

        time = DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
        var source = GraphTimeZone.Resolve(Str(value, "timeZone")) ?? TimeZoneInfo.Utc;
        return source.Id == zone.Info.Id || source.HasSameRules(zone.Info)
            ? time
            : TimeZoneInfo.ConvertTime(time, source, zone.Info);
    }

    private string? FormatTime(JsonElement e, string property, bool allDay) =>
        ReadTime(e, property)?.ToString(allDay ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    private EventSummary ToSummary(JsonElement e)
    {
        var allDay = Bool(e, "isAllDay");
        return new EventSummary(
            Id: e.GetProperty("id").GetString()!,
            Subject: Str(e, "subject"),
            Start: FormatTime(e, "start", allDay),
            End: FormatTime(e, "end", allDay),
            IsAllDay: allDay,
            Location: e.TryGetProperty("location", out var l) ? NullIfEmpty(Str(l, "displayName")) : null,
            Organizer: e.TryGetProperty("organizer", out var o) ? FormatAddress(o) : null,
            IsOrganizer: Bool(e, "isOrganizer"),
            Attendees: AttendeeCount(e),
            MyResponse: e.TryGetProperty("responseStatus", out var r) ? Str(r, "response") : null,
            ShowAs: Str(e, "showAs"),
            IsCancelled: Bool(e, "isCancelled"),
            OnlineMeetingUrl: e.TryGetProperty("onlineMeeting", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, "joinUrl") : null);
    }

    private static int AttendeeCount(JsonElement e) =>
        e.TryGetProperty("attendees", out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0;

    private static IReadOnlyList<string> Attendees(JsonElement e) =>
        e.TryGetProperty("attendees", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
                .Select(a => (Address: FormatAddress(a), Response: a.TryGetProperty("status", out var s) ? Str(s, "response") : null))
                .Where(a => a.Address is not null)
                .Select(a => a.Response is null or "none" ? a.Address! : $"{a.Address} ({a.Response})")
                .ToList()
            : [];

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
