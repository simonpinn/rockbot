using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace McpServer.Trello.Services;

public sealed record Label(string Id, string? Name, string? Color);
public sealed record BoardSummary(string Id, string Name, string? Url, DateTimeOffset? LastActivity);
public sealed record ListSummary(string Id, string Name);
public sealed record BoardDetail(string Id, string Name, string? Url, IReadOnlyList<ListSummary> Lists, IReadOnlyList<Label> Labels);

public sealed record CardSummary(
    string Id, string Name, string BoardId, string ListId,
    DateTimeOffset? Due, bool DueComplete, IReadOnlyList<Label> Labels, string? Url, DateTimeOffset? LastActivity);

public sealed record ChecklistItem(string Name, bool Complete);
public sealed record Checklist(string Name, IReadOnlyList<ChecklistItem> Items);
public sealed record Comment(string? Author, DateTimeOffset? Date, string Text);

public sealed record CardDetail(
    string Id, string Name, string Description, bool DescriptionTruncated,
    string BoardId, string? BoardName, string ListId, string? ListName,
    DateTimeOffset? Due, bool DueComplete, IReadOnlyList<Label> Labels, IReadOnlyList<string> Members,
    IReadOnlyList<Checklist> Checklists, IReadOnlyList<Comment> RecentComments, string? Url);

public sealed record SearchResult(IReadOnlyList<BoardSummary> Boards, IReadOnlyList<CardSummary> Cards);

public sealed record CardChanges(
    string? Name = null, string? Description = null, DateTimeOffset? Due = null, bool ClearDue = false,
    bool? DueComplete = null, string? ListId = null, string? Position = null);

/// <summary>Trello REST calls. Every call that touches a board checks it against the allowlist first.</summary>
public sealed class TrelloClient(HttpClient http, IOptions<TrelloOptions> options)
{
    private const string CardFields = "name,idBoard,idList,due,dueComplete,labels,shortUrl,dateLastActivity";
    private const string BoardFields = "name,url,dateLastActivity";

    private IReadOnlySet<string> AllowedBoards => options.Value.AllowedBoards;

    public async Task<IReadOnlyList<BoardSummary>> ListBoardsAsync(CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, "members/me/boards", [("filter", "open"), ("fields", BoardFields)], null, ct);
        return doc.RootElement.EnumerateArray().Select(ToBoardSummary).Where(b => IsAllowed(b.Id)).ToList();
    }

    public async Task<BoardDetail> GetBoardAsync(string boardId, CancellationToken ct)
    {
        EnsureAllowed(boardId);
        using var doc = await SendAsync(HttpMethod.Get, $"boards/{boardId}",
            [("fields", "name,url"), ("lists", "open"), ("list_fields", "name"), ("labels", "all"), ("label_fields", "name,color")], null, ct);
        var b = doc.RootElement;
        return new BoardDetail(
            Id: Req(b, "id"),
            Name: Req(b, "name"),
            Url: Str(b, "url"),
            Lists: Array(b, "lists").Select(l => new ListSummary(Req(l, "id"), Req(l, "name"))).ToList(),
            Labels: Labels(b));
    }

    public async Task<IReadOnlyList<CardSummary>> ListCardsAsync(string? boardId, string? listId, int limit, CancellationToken ct)
    {
        string path;
        if (listId is not null)
        {
            EnsureAllowed(await BoardOfAsync("lists", listId, ct));
            path = $"lists/{listId}/cards";
        }
        else
        {
            EnsureAllowed(boardId!);
            path = $"boards/{boardId}/cards";
        }

        using var doc = await SendAsync(HttpMethod.Get, path, [("filter", "open"), ("fields", CardFields)], null, ct);
        return doc.RootElement.EnumerateArray().Select(ToCardSummary).Take(limit).ToList();
    }

    public async Task<CardDetail> GetCardAsync(string cardId, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"cards/{cardId}",
        [
            ("fields", CardFields + ",desc"),
            ("board", "true"), ("board_fields", "name"),
            ("list", "true"), ("list_fields", "name"),
            ("members", "true"), ("member_fields", "fullName,username"),
            ("checklists", "all"), ("checklist_fields", "all"),
            ("actions", "commentCard"), ("actions_limit", "20"),
            ("action_fields", "data,date"), ("action_memberCreator_fields", "fullName")
        ], null, ct);
        var c = doc.RootElement;
        var summary = ToCardSummary(c);
        EnsureAllowed(summary.BoardId);

        var description = Str(c, "desc") ?? "";
        var max = options.Value.MaxTextChars;
        var truncated = description.Length > max;

        return new CardDetail(
            Id: summary.Id,
            Name: summary.Name,
            Description: truncated ? description[..max] : description,
            DescriptionTruncated: truncated,
            BoardId: summary.BoardId,
            BoardName: c.TryGetProperty("board", out var board) ? Str(board, "name") : null,
            ListId: summary.ListId,
            ListName: c.TryGetProperty("list", out var list) ? Str(list, "name") : null,
            Due: summary.Due,
            DueComplete: summary.DueComplete,
            Labels: summary.Labels,
            Members: Array(c, "members").Select(m => Str(m, "fullName") ?? Str(m, "username") ?? "").ToList(),
            Checklists: Array(c, "checklists").Select(cl => new Checklist(
                Str(cl, "name") ?? "",
                Array(cl, "checkItems").Select(i => new ChecklistItem(Str(i, "name") ?? "", Str(i, "state") == "complete")).ToList())).ToList(),
            RecentComments: Array(c, "actions").Select(ToComment).ToList(),
            Url: summary.Url);
    }

    public async Task<SearchResult> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var parameters = new List<(string, string)>
        {
            ("query", query), ("modelTypes", "cards,boards"), ("partial", "true"),
            ("cards_limit", limit.ToString()), ("boards_limit", limit.ToString()),
            ("card_fields", CardFields), ("board_fields", BoardFields)
        };
        if (AllowedBoards.Count > 0)
            parameters.Add(("idBoards", string.Join(",", AllowedBoards)));

        using var doc = await SendAsync(HttpMethod.Get, "search", parameters, null, ct);
        var r = doc.RootElement;
        return new SearchResult(
            Boards: Array(r, "boards").Select(ToBoardSummary).Where(b => IsAllowed(b.Id)).ToList(),
            Cards: Array(r, "cards").Select(ToCardSummary).Where(c => IsAllowed(c.BoardId)).ToList());
    }

    public async Task<CardSummary> CreateCardAsync(
        string listId, string name, string? description, DateTimeOffset? due, IReadOnlyList<string>? labelIds, string? position, CancellationToken ct)
    {
        EnsureAllowed(await BoardOfAsync("lists", listId, ct));

        var form = new List<(string, string)> { ("idList", listId), ("name", name) };
        if (description is not null) form.Add(("desc", description));
        if (due is not null) form.Add(("due", due.Value.ToString("O")));
        if (labelIds is { Count: > 0 }) form.Add(("idLabels", string.Join(",", labelIds)));
        if (position is not null) form.Add(("pos", position));

        using var doc = await SendAsync(HttpMethod.Post, "cards", [], form, ct);
        return ToCardSummary(doc.RootElement);
    }

    public async Task<CardSummary> UpdateCardAsync(string cardId, CardChanges changes, CancellationToken ct)
    {
        EnsureAllowed(await BoardOfAsync("cards", cardId, ct));

        var form = new List<(string, string)>();
        if (changes.Name is not null) form.Add(("name", changes.Name));
        if (changes.Description is not null) form.Add(("desc", changes.Description));
        if (changes.ClearDue) form.Add(("due", "null"));
        else if (changes.Due is not null) form.Add(("due", changes.Due.Value.ToString("O")));
        if (changes.DueComplete is not null) form.Add(("dueComplete", changes.DueComplete.Value ? "true" : "false"));
        if (changes.ListId is not null)
        {
            var targetBoard = await BoardOfAsync("lists", changes.ListId, ct);
            EnsureAllowed(targetBoard);
            form.Add(("idList", changes.ListId));
            form.Add(("idBoard", targetBoard));
        }
        if (changes.Position is not null) form.Add(("pos", changes.Position));

        using var doc = await SendAsync(HttpMethod.Put, $"cards/{cardId}", [], form, ct);
        return ToCardSummary(doc.RootElement);
    }

    public async Task<Comment> AddCommentAsync(string cardId, string text, CancellationToken ct)
    {
        EnsureAllowed(await BoardOfAsync("cards", cardId, ct));
        using var doc = await SendAsync(HttpMethod.Post, $"cards/{cardId}/actions/comments", [], [("text", text)], ct);
        return ToComment(doc.RootElement);
    }

    private async Task<string> BoardOfAsync(string kind, string id, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"{kind}/{id}", [("fields", "idBoard")], null, ct);
        return Req(doc.RootElement, "idBoard");
    }

    private bool IsAllowed(string boardId) => AllowedBoards.Count == 0 || AllowedBoards.Contains(boardId);

    private void EnsureAllowed(string boardId)
    {
        if (!IsAllowed(boardId))
            throw new McpException($"board {boardId} is not one of the boards this server is allowed to use");
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, IEnumerable<(string Key, string Value)> query, IEnumerable<(string Key, string Value)>? form, CancellationToken ct)
    {
        var queryString = string.Join("&", query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        using var request = new HttpRequestMessage(method, queryString.Length == 0 ? path : $"{path}?{queryString}");
        if (form is not null)
            request.Content = new FormUrlEncodedContent(form.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));

        using var response = await http.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            return JsonDocument.Parse(content);

        throw new McpException(DescribeFailure(response.StatusCode, content));
    }

    private static string DescribeFailure(HttpStatusCode status, string content)
    {
        var detail = content.Trim();
        try
        {
            using var doc = JsonDocument.Parse(content);
            detail = Str(doc.RootElement, "message") ?? Str(doc.RootElement, "error") ?? detail;
        }
        catch (JsonException)
        {
        }
        if (detail.Length > 300)
            detail = detail[..300];

        var hint = status switch
        {
            HttpStatusCode.Unauthorized => " The Trello key or token was rejected; re-run deploy/trello/setup.sh --new-token.",
            HttpStatusCode.NotFound => " The board, list or card was not found.",
            HttpStatusCode.TooManyRequests => " Trello rate limit hit; try again shortly.",
            _ => ""
        };
        return $"Trello returned {(int)status}{(detail == "" ? "" : $" ({detail})")}.{hint}";
    }

    private static BoardSummary ToBoardSummary(JsonElement b) =>
        new(Req(b, "id"), Req(b, "name"), Str(b, "url"), Date(b, "dateLastActivity"));

    private static CardSummary ToCardSummary(JsonElement c) => new(
        Id: Req(c, "id"),
        Name: Req(c, "name"),
        BoardId: Req(c, "idBoard"),
        ListId: Req(c, "idList"),
        Due: Date(c, "due"),
        DueComplete: c.TryGetProperty("dueComplete", out var dc) && dc.ValueKind == JsonValueKind.True,
        Labels: Labels(c),
        Url: Str(c, "shortUrl"),
        LastActivity: Date(c, "dateLastActivity"));

    private static Comment ToComment(JsonElement a) => new(
        Author: a.TryGetProperty("memberCreator", out var m) ? Str(m, "fullName") : null,
        Date: Date(a, "date"),
        Text: a.TryGetProperty("data", out var d) ? Str(d, "text") ?? "" : "");

    private static IReadOnlyList<Label> Labels(JsonElement e) =>
        Array(e, "labels").Select(l => new Label(Req(l, "id"), Str(l, "name"), Str(l, "color"))).ToList();

    private static IEnumerable<JsonElement> Array(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string Req(JsonElement e, string property) => Str(e, property) ?? "";

    private static string? Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(JsonElement e, string property) =>
        Str(e, property) is { } s && DateTimeOffset.TryParse(s, out var d) ? d : null;
}
