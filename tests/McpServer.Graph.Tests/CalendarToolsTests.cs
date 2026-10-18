using System.Net;
using System.Text.Json;

namespace McpServer.Graph.Tests;

[TestClass]
public sealed class CalendarToolsTests : GraphServerTestBase
{
    private const string Sydney = "AUS Eastern Standard Time";

    private static string Event(string id, string start, string end, string showAs = "busy", bool cancelled = false,
        bool allDay = false, string zone = Sydney)
    {
        var e = new
        {
            id,
            subject = $"Meeting {id}",
            start = new { dateTime = start, timeZone = zone },
            end = new { dateTime = end, timeZone = zone },
            isAllDay = allDay,
            location = new { displayName = "Room 1" },
            organizer = new { emailAddress = new { name = "Alice", address = "alice@example.com" } },
            isOrganizer = false,
            attendees = new[] { new { emailAddress = new { address = "me@contoso.com" }, status = new { response = "accepted" } } },
            responseStatus = new { response = "accepted" },
            showAs,
            isCancelled = cancelled,
            onlineMeeting = new { joinUrl = "https://teams.example/join" }
        };
        return JsonSerializer.Serialize(e);
    }

    private static string Page(params string[] events) => $$"""{"value":[{{string.Join(",", events)}}]}""";

    /// <summary>Replaces the closing brace of a JSON object with <paramref name="tail"/>.</summary>
    private static string WithExtra(string json, string tail) => json[..json.LastIndexOf('}')] + tail;

    private static string QueryOf(HttpRequestMessage request) => Uri.UnescapeDataString(request.RequestUri!.Query);

    [TestMethod]
    public async Task ListEvents_QueriesCalendarViewOfConfiguredMailboxInConfiguredZone()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(Page(Event("e1", "2026-10-20T14:00:00.0000000", "2026-10-20T14:30:00.0000000")));

        var result = await CallOkAsync("list_events", new() { ["start"] = "2026-10-20", ["end"] = "2026-10-21", ["top"] = 5 });

        var request = Graph.Single;
        Assert.AreEqual("/v1.0/users/me%40contoso.com/calendarView", request.RequestUri!.AbsolutePath);
        var query = QueryOf(request);
        // A date-only end includes that whole day; offsets are Sydney daylight time.
        StringAssert.Contains(query, "startDateTime=2026-10-20T00:00:00+11:00");
        StringAssert.Contains(query, "endDateTime=2026-10-22T00:00:00+11:00");
        StringAssert.Contains(query, "$top=5");
        StringAssert.Contains(query, "$orderby=start/dateTime");
        CollectionAssert.Contains(request.Headers.GetValues("Prefer").ToList(), $"outlook.timezone=\"{Sydney}\"");

        Assert.AreEqual(TimeZone, result.GetProperty("timeZone").GetString());
        var e = result.GetProperty("events")[0];
        Assert.AreEqual("e1", e.GetProperty("id").GetString());
        Assert.AreEqual("2026-10-20T14:00", e.GetProperty("start").GetString());
        Assert.AreEqual("2026-10-20T14:30", e.GetProperty("end").GetString());
        Assert.AreEqual("Room 1", e.GetProperty("location").GetString());
        Assert.AreEqual("Alice <alice@example.com>", e.GetProperty("organizer").GetString());
        Assert.AreEqual(1, e.GetProperty("attendees").GetInt32());
        Assert.AreEqual("accepted", e.GetProperty("myResponse").GetString());
        Assert.AreEqual("https://teams.example/join", e.GetProperty("onlineMeetingUrl").GetString());
    }

    [TestMethod]
    public async Task ListEvents_NoRange_IsSevenDaysFromStartOfToday()
    {
        await CallOkAsync("list_events");

        var query = System.Web.HttpUtility.ParseQueryString(Graph.Single.RequestUri!.Query);
        var start = DateTimeOffset.Parse(query["startDateTime"]!);
        var end = DateTimeOffset.Parse(query["endDateTime"]!);
        Assert.AreEqual(TimeSpan.Zero, start.TimeOfDay);
        Assert.AreEqual(7, (end.DateTime - start.DateTime).TotalDays);
    }

    [TestMethod]
    public async Task ListEvents_ConvertsTimesFromAnotherZone()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(Page(Event("e1", "2026-10-20T03:00:00", "2026-10-20T04:00:00", zone: "UTC")));

        var result = await CallOkAsync("list_events", new() { ["start"] = "2026-10-20", ["end"] = "2026-10-20" });

        Assert.AreEqual("2026-10-20T14:00", result.GetProperty("events")[0].GetProperty("start").GetString());
    }

    [TestMethod]
    [DataRow("2026-10-21", "2026-10-20", "end must be after start")]
    [DataRow("2026-10-01", "2026-12-31", "at most 62 days")]
    [DataRow("next tuesday", null, "ISO 8601")]
    public async Task ListEvents_BadRange_IsErrorWithoutCallingGraph(string start, string? end, string message)
    {
        var result = await CallAsync("list_events", new() { ["start"] = start, ["end"] = end });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), message);
        Assert.AreEqual(0, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task GetEvent_ReturnsAttendeesAndPlainTextBody()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(WithExtra(Event("e1", "2026-10-20T14:00:00", "2026-10-20T15:00:00"),
            ""","webLink":"https://outlook/e1","body":{"contentType":"text","content":"Agenda"}}"""));

        var result = await CallOkAsync("get_event", new() { ["id"] = "e1" });

        var request = Graph.Single;
        Assert.AreEqual("/v1.0/users/me%40contoso.com/events/e1", request.RequestUri!.AbsolutePath);
        var prefer = request.Headers.GetValues("Prefer").ToList();
        CollectionAssert.Contains(prefer, "outlook.body-content-type=\"text\"");
        CollectionAssert.Contains(prefer, $"outlook.timezone=\"{Sydney}\"");
        Assert.AreEqual("me@contoso.com (accepted)", result.GetProperty("attendees")[0].GetString());
        Assert.AreEqual("Agenda", result.GetProperty("body").GetString());
        Assert.AreEqual("https://outlook/e1", result.GetProperty("webLink").GetString());
    }

    [TestMethod]
    public async Task CreateEvent_PostsEventInConfiguredZoneWithAttendeesAndTeams()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(Event("new", "2026-10-20T14:00:00", "2026-10-20T14:30:00"), HttpStatusCode.Created);

        var result = await CallOkAsync("create_event", new()
        {
            ["subject"] = "Planning",
            ["start"] = "2026-10-20T14:00",
            ["end"] = "2026-10-20T14:30",
            ["location"] = "Cafe",
            ["body"] = "Bring notes",
            ["attendees"] = new[] { "bob@example.com", "BOB@example.com", "carol@example.com" },
            ["online_meeting"] = true
        });

        var request = Graph.Single;
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("/v1.0/users/me%40contoso.com/events", request.RequestUri!.AbsolutePath);
        var body = Graph.BodyOf(0);
        Assert.AreEqual("Planning", body.GetProperty("subject").GetString());
        Assert.AreEqual("2026-10-20T14:00:00", body.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.AreEqual(Sydney, body.GetProperty("start").GetProperty("timeZone").GetString());
        Assert.AreEqual("2026-10-20T14:30:00", body.GetProperty("end").GetProperty("dateTime").GetString());
        Assert.IsFalse(body.GetProperty("isAllDay").GetBoolean());
        Assert.AreEqual("Cafe", body.GetProperty("location").GetProperty("displayName").GetString());
        Assert.AreEqual("text", body.GetProperty("body").GetProperty("contentType").GetString());
        var attendees = body.GetProperty("attendees");
        Assert.AreEqual(2, attendees.GetArrayLength(), "duplicate addresses are collapsed");
        Assert.AreEqual("bob@example.com", attendees[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.AreEqual("required", attendees[0].GetProperty("type").GetString());
        Assert.IsTrue(body.GetProperty("isOnlineMeeting").GetBoolean());
        Assert.AreEqual("teamsForBusiness", body.GetProperty("onlineMeetingProvider").GetString());

        Assert.AreEqual("new", result.GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task CreateEvent_AllDay_EndIsTheDayAfterTheLastDay()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(Event("new", "2026-10-20T00:00:00", "2026-10-22T00:00:00", allDay: true));

        var result = await CallOkAsync("create_event", new()
        {
            ["subject"] = "Leave", ["start"] = "2026-10-20", ["end"] = "2026-10-21", ["is_all_day"] = true
        });

        var body = Graph.BodyOf(0);
        Assert.IsTrue(body.GetProperty("isAllDay").GetBoolean());
        Assert.AreEqual("2026-10-20T00:00:00", body.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.AreEqual("2026-10-22T00:00:00", body.GetProperty("end").GetProperty("dateTime").GetString());
        Assert.IsFalse(body.TryGetProperty("attendees", out _));
        Assert.AreEqual("2026-10-20", result.GetProperty("start").GetString());
    }

    [TestMethod]
    public async Task CreateEvent_ExplicitTimeZone_IsSentAsWindowsName()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(Event("new", "2026-10-20T09:00:00", "2026-10-20T10:00:00"));

        await CallOkAsync("create_event", new()
        {
            ["subject"] = "Call", ["start"] = "2026-10-20T09:00", ["end"] = "2026-10-20T10:00", ["time_zone"] = "Europe/London"
        });

        Assert.AreEqual("GMT Standard Time", Graph.BodyOf(0).GetProperty("start").GetProperty("timeZone").GetString());
    }

    [TestMethod]
    [DataRow("2026-10-20T15:00", "2026-10-20T14:00", null, null, "end must be after start")]
    [DataRow("2026-10-20T14:00", "2026-10-20T15:00", "not-an-address", null, "not a valid email address")]
    [DataRow("2026-10-20T14:00", "2026-10-20T15:00", null, "Mars/Olympus", "not a known time zone")]
    public async Task CreateEvent_BadInput_IsErrorWithoutCallingGraph(string start, string end, string? attendee, string? zone, string message)
    {
        var arguments = new Dictionary<string, object?> { ["subject"] = "x", ["start"] = start, ["end"] = end };
        if (attendee is not null)
            arguments["attendees"] = new[] { attendee };
        if (zone is not null)
            arguments["time_zone"] = zone;

        var result = await CallAsync("create_event", arguments);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), message);
        Assert.AreEqual(0, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task UpdateEvent_PatchesOnlyTheGivenFields()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(Event("e1", "2026-10-20T16:00:00", "2026-10-20T16:30:00"));

        await CallOkAsync("update_event", new() { ["id"] = "e1", ["start"] = "2026-10-20T16:00", ["end"] = "2026-10-20T16:30" });

        var request = Graph.Single;
        Assert.AreEqual(HttpMethod.Patch, request.Method);
        Assert.AreEqual("/v1.0/users/me%40contoso.com/events/e1", request.RequestUri!.AbsolutePath);
        var names = Graph.BodyOf(0).EnumerateObject().Select(p => p.Name).ToArray();
        CollectionAssert.AreEquivalent(new[] { "start", "end", "isAllDay" }, names);
    }

    [TestMethod]
    [DataRow("start", "2026-10-20T16:00", "start and end must be given together")]
    [DataRow("time_zone", "UTC", "time_zone only applies")]
    public async Task UpdateEvent_PartialTimes_IsError(string argument, string value, string message)
    {
        var result = await CallAsync("update_event", new() { ["id"] = "e1", [argument] = value });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), message);
        Assert.AreEqual(0, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task UpdateEvent_NothingGiven_IsError()
    {
        var result = await CallAsync("update_event", new() { ["id"] = "e1" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "nothing to update");
    }

    [TestMethod]
    public async Task DeleteEvent_OrganisedMeetingWithAttendees_IsCancelled()
    {
        Graph.Respond = r => r.Method == HttpMethod.Get
            ? FakeGraphHandler.Json("""{"isOrganizer":true,"attendees":[{},{}]}""")
            : new HttpResponseMessage(HttpStatusCode.Accepted);

        var result = await CallOkAsync("delete_event", new() { ["id"] = "e1", ["comment"] = "Sorry" });

        Assert.AreEqual(2, Graph.Requests.Count);
        var cancel = Graph.Requests[1];
        Assert.AreEqual(HttpMethod.Post, cancel.Method);
        Assert.AreEqual("/v1.0/users/me%40contoso.com/events/e1/cancel", cancel.RequestUri!.AbsolutePath);
        Assert.AreEqual("Sorry", Graph.BodyOf(1).GetProperty("comment").GetString());
        StringAssert.Contains(result.GetProperty("result").GetString(), "2 attendee(s)");
    }

    [TestMethod]
    [DataRow(true, 0)]
    [DataRow(false, 3)]
    public async Task DeleteEvent_OtherwiseIsDeleted(bool organizer, int attendees)
    {
        var list = string.Join(",", Enumerable.Repeat("{}", attendees));
        Graph.Respond = r => r.Method == HttpMethod.Get
            ? FakeGraphHandler.Json($$"""{"isOrganizer":{{(organizer ? "true" : "false")}},"attendees":[{{list}}]}""")
            : new HttpResponseMessage(HttpStatusCode.NoContent);

        var result = await CallOkAsync("delete_event", new() { ["id"] = "e1" });

        Assert.AreEqual(HttpMethod.Delete, Graph.Requests[1].Method);
        Assert.AreEqual("/v1.0/users/me%40contoso.com/events/e1", Graph.Requests[1].RequestUri!.AbsolutePath);
        Assert.AreEqual("deleted", result.GetProperty("result").GetString());
    }

    [TestMethod]
    [DataRow("accept", "accept")]
    [DataRow("tentative", "tentativelyAccept")]
    [DataRow("Decline", "decline")]
    public async Task RespondToEvent_PostsTheMatchingAction(string response, string action)
    {
        Graph.Respond = r => r.Method == HttpMethod.Get
            ? FakeGraphHandler.Json("""{"isOrganizer":false,"attendees":[{}]}""")
            : new HttpResponseMessage(HttpStatusCode.Accepted);

        await CallOkAsync("respond_to_event", new() { ["id"] = "e1", ["response"] = response, ["send_response"] = false });

        var post = Graph.Requests[1];
        Assert.AreEqual($"/v1.0/users/me%40contoso.com/events/e1/{action}", post.RequestUri!.AbsolutePath);
        Assert.IsFalse(Graph.BodyOf(1).GetProperty("sendResponse").GetBoolean());
    }

    [TestMethod]
    public async Task RespondToEvent_OwnEvent_IsError()
    {
        Graph.Respond = _ => FakeGraphHandler.Json("""{"isOrganizer":true,"attendees":[]}""");

        var result = await CallAsync("respond_to_event", new() { ["id"] = "e1", ["response"] = "accept" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "organises this event");
        Assert.AreEqual(1, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task FindFreeTime_SkipsFreeCancelledAndWeekendTimeAndFollowsPaging()
    {
        // Mon 2026-10-19 and Tue 2026-10-20, working hours 08:00-18:00.
        var first = Page(
            Event("a", "2026-10-19T09:00:00", "2026-10-19T10:00:00"),
            Event("b", "2026-10-19T09:30:00", "2026-10-19T12:00:00"),     // overlaps a
            Event("c", "2026-10-19T13:00:00", "2026-10-19T14:00:00", showAs: "free"));
        first = WithExtra(first, ""","@odata.nextLink":"https://graph.microsoft.com/v1.0/users/me%40contoso.com/calendarView?$skiptoken=2"}""");
        var second = Page(
            Event("d", "2026-10-19T15:00:00", "2026-10-19T18:00:00", cancelled: true),
            Event("e", "2026-10-20T00:00:00", "2026-10-21T00:00:00", showAs: "oof", allDay: true));
        Graph.Respond = r => FakeGraphHandler.Json(r.RequestUri!.Query.Contains("skiptoken") ? second : first);

        var result = await CallOkAsync("find_free_time", new() { ["start"] = "2026-10-19", ["end"] = "2026-10-20", ["duration_minutes"] = 60 });

        Assert.AreEqual(2, Graph.Requests.Count);
        var slots = result.GetProperty("slots").EnumerateArray()
            .Select(s => $"{s.GetProperty("start").GetString()}-{s.GetProperty("end").GetString()}:{s.GetProperty("minutes").GetInt32()}")
            .ToArray();
        CollectionAssert.AreEqual(new[] { "2026-10-19T08:00-2026-10-19T09:00:60", "2026-10-19T12:00-2026-10-19T18:00:360" }, slots,
            string.Join(" | ", slots));
    }

    [TestMethod]
    public async Task FindFreeTime_WeekendsIncludedWhenNotLimitedToWorkingHours()
    {
        var result = await CallOkAsync("find_free_time", new()
        {
            ["start"] = "2026-10-24T10:00", ["end"] = "2026-10-24T12:00", ["working_hours_only"] = false
        });

        var slot = result.GetProperty("slots")[0];
        Assert.AreEqual("2026-10-24T10:00", slot.GetProperty("start").GetString());
        Assert.AreEqual(120, slot.GetProperty("minutes").GetInt32());
    }

    [TestMethod]
    public async Task FindFreeTime_IgnoresNextLinkToAnotherHost()
    {
        Graph.Respond = _ => FakeGraphHandler.Json("""{"value":[],"@odata.nextLink":"https://evil.example/steal"}""");

        await CallOkAsync("find_free_time", new() { ["start"] = "2026-10-19", ["end"] = "2026-10-19" });

        Assert.AreEqual(1, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task GraphForbidden_NamesCalendarPermission()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(
            """{"error":{"code":"ErrorAccessDenied","message":"Access is denied"}}""", HttpStatusCode.Forbidden);

        var result = await CallAsync("list_events");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "not authorised for calendar");
        StringAssert.Contains(TextOf(result), "2 hours");
    }
}
