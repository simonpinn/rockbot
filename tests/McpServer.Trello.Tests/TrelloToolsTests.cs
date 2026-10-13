using System.Net;

namespace McpServer.Trello.Tests;

[TestClass]
public sealed class TrelloToolsTests : TrelloServerTestBase
{
    [TestMethod]
    public async Task ListBoards_SendsCredentialsInHeaderNotUrl_AndFiltersToAllowlist()
    {
        AllowedBoardIds = AllowedBoard;
        Trello.Respond = _ => FakeTrelloApi.Json($$"""
            [{"id":"{{AllowedBoard}}","name":"Home","url":"https://trello.com/b/1"},
             {"id":"{{OtherBoard}}","name":"Work","url":"https://trello.com/b/2"}]
            """);

        var boards = await CallOkAsync("list_boards");

        var request = Trello.Requests.Single();
        Assert.AreEqual("/1/members/me/boards", request.Path);
        Assert.AreEqual("OAuth oauth_consumer_key=\"test-key\", oauth_token=\"test-token\"", request.Authorization);
        Assert.IsFalse(request.Uri.ToString().Contains("test-token"));
        Assert.AreEqual(1, boards.GetArrayLength());
        Assert.AreEqual("Home", boards[0].GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task GetBoard_NotOnAllowlist_IsErrorWithoutCallingTrello()
    {
        AllowedBoardIds = AllowedBoard;

        var result = await CallAsync("get_board", new() { ["board_id"] = OtherBoard });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "not one of the boards");
        Assert.AreEqual(0, Trello.Requests.Count);
    }

    [TestMethod]
    public async Task GetBoard_ReturnsListsAndLabels()
    {
        Trello.Respond = _ => FakeTrelloApi.Json($$"""
            {"id":"{{AllowedBoard}}","name":"Home","url":"u","lists":[{"id":"{{ListId}}","name":"To do"}],
             "labels":[{"id":"333333333333333333333333","name":"Urgent","color":"red"}]}
            """);

        var board = await CallOkAsync("get_board", new() { ["board_id"] = AllowedBoard });

        Assert.AreEqual("To do", board.GetProperty("lists")[0].GetProperty("name").GetString());
        Assert.AreEqual("red", board.GetProperty("labels")[0].GetProperty("color").GetString());
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow(AllowedBoard, ListId)]
    public async Task ListCards_RequiresExactlyOneOfBoardOrList(string? boardId, string? listId)
    {
        var args = new Dictionary<string, object?>();
        if (boardId is not null) args["board_id"] = boardId;
        if (listId is not null) args["list_id"] = listId;

        var result = await CallAsync("list_cards", args);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "exactly one");
        Assert.AreEqual(0, Trello.Requests.Count);
    }

    [TestMethod]
    public async Task ListCards_ByList_ChecksTheListsBoardAgainstAllowlist()
    {
        AllowedBoardIds = AllowedBoard;
        Trello.Respond = r => r.Path == $"/1/lists/{ListId}"
            ? FakeTrelloApi.Json($$"""{"id":"{{ListId}}","idBoard":"{{OtherBoard}}"}""")
            : FakeTrelloApi.Json("[]");

        var result = await CallAsync("list_cards", new() { ["list_id"] = ListId });

        Assert.IsTrue(result.IsError);
        Assert.AreEqual(1, Trello.Requests.Count, "cards must not be fetched for a disallowed board");
    }

    [TestMethod]
    public async Task ListCards_ByBoard_ProjectsCardsAndAppliesLimit()
    {
        Trello.Respond = _ => FakeTrelloApi.Json($"[{Card()},{Card(id: "444444444444444444444444", name: "Second")}]");

        var cards = await CallOkAsync("list_cards", new() { ["board_id"] = AllowedBoard, ["limit"] = 1 });

        StringAssert.Contains(Trello.Requests.Single().Query, "filter=open");
        Assert.AreEqual(1, cards.GetArrayLength());
        var card = cards[0];
        Assert.AreEqual("Pay invoice", card.GetProperty("name").GetString());
        Assert.AreEqual("Urgent", card.GetProperty("labels")[0].GetProperty("name").GetString());
        Assert.AreEqual(ListId, card.GetProperty("listId").GetString());
    }

    [TestMethod]
    public async Task GetCard_ReturnsChecklistsAndComments_AndTruncatesDescription()
    {
        var longDescription = new string('d', 1500);
        Trello.Respond = _ => FakeTrelloApi.Json(Card().TrimEnd('}') + $$$"""
            ,"desc":"{{{longDescription}}}","board":{"name":"Home"},"list":{"name":"To do"},
             "members":[{"fullName":"Simon"}],
             "checklists":[{"name":"Steps","checkItems":[{"name":"Find bill","state":"complete"},{"name":"Pay","state":"incomplete"}]}],
             "actions":[{"date":"2026-10-12T01:00:00.000Z","data":{"text":"Done soon"},"memberCreator":{"fullName":"Alex"}}]}
            """);

        var card = await CallOkAsync("get_card", new() { ["card_id"] = CardId });

        Assert.AreEqual(1000, card.GetProperty("description").GetString()!.Length);
        Assert.IsTrue(card.GetProperty("descriptionTruncated").GetBoolean());
        Assert.AreEqual("Home", card.GetProperty("boardName").GetString());
        Assert.AreEqual("To do", card.GetProperty("listName").GetString());
        var items = card.GetProperty("checklists")[0].GetProperty("items");
        Assert.IsTrue(items[0].GetProperty("complete").GetBoolean());
        Assert.IsFalse(items[1].GetProperty("complete").GetBoolean());
        Assert.AreEqual("Alex", card.GetProperty("recentComments")[0].GetProperty("author").GetString());
        Assert.AreEqual("Done soon", card.GetProperty("recentComments")[0].GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task GetCard_OnDisallowedBoard_IsError()
    {
        AllowedBoardIds = AllowedBoard;
        Trello.Respond = _ => FakeTrelloApi.Json(Card(boardId: OtherBoard));

        var result = await CallAsync("get_card", new() { ["card_id"] = CardId });

        Assert.IsTrue(result.IsError);
        Assert.IsFalse(TextOf(result).Contains("Pay invoice"));
    }

    [TestMethod]
    public async Task Search_WithAllowlist_ScopesQueryAndFiltersResults()
    {
        AllowedBoardIds = AllowedBoard;
        Trello.Respond = _ => FakeTrelloApi.Json(
            $$"""{"boards":[{"id":"{{OtherBoard}}","name":"Work"}],"cards":[""" +
            Card() + "," + Card(boardId: OtherBoard, name: "Secret") + "]}");

        var result = await CallOkAsync("search", new() { ["query"] = "invoice" });

        StringAssert.Contains(Trello.Requests.Single().Query, $"idBoards={AllowedBoard}");
        Assert.AreEqual(0, result.GetProperty("boards").GetArrayLength());
        Assert.AreEqual(1, result.GetProperty("cards").GetArrayLength());
        Assert.AreEqual("Pay invoice", result.GetProperty("cards")[0].GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task CreateCard_PostsFormWithAllFields()
    {
        Trello.Respond = r => r.Method == HttpMethod.Post
            ? FakeTrelloApi.Json(Card())
            : FakeTrelloApi.Json($$"""{"idBoard":"{{AllowedBoard}}"}""");

        await CallOkAsync("create_card", new()
        {
            ["list_id"] = ListId,
            ["name"] = "  Pay invoice ",
            ["description"] = "From the email",
            ["due"] = "2026-10-31T17:00:00+11:00",
            ["label_ids"] = new[] { "333333333333333333333333" },
            ["position"] = "Top"
        });

        var post = Trello.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.AreEqual("/1/cards", post.Path);
        var form = post.Form;
        Assert.AreEqual(ListId, form["idList"]);
        Assert.AreEqual("Pay invoice", form["name"]);
        Assert.AreEqual("From the email", form["desc"]);
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-31T06:00:00Z"), DateTimeOffset.Parse(form["due"]));
        Assert.AreEqual("333333333333333333333333", form["idLabels"]);
        Assert.AreEqual("top", form["pos"]);
    }

    [TestMethod]
    public async Task UpdateCard_MoveAndClearDue_SendsListBoardAndNullDue()
    {
        Trello.Respond = r => r.Method == HttpMethod.Put
            ? FakeTrelloApi.Json(Card())
            : FakeTrelloApi.Json($$"""{"idBoard":"{{AllowedBoard}}"}""");

        await CallOkAsync("update_card", new() { ["card_id"] = CardId, ["list_id"] = ListId, ["due"] = "none", ["due_complete"] = true });

        var put = Trello.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.AreEqual($"/1/cards/{CardId}", put.Path);
        var form = put.Form;
        Assert.AreEqual(ListId, form["idList"]);
        Assert.AreEqual(AllowedBoard, form["idBoard"]);
        Assert.AreEqual("null", form["due"]);
        Assert.AreEqual("true", form["dueComplete"]);
        Assert.IsFalse(form.ContainsKey("closed"));
    }

    [TestMethod]
    public async Task UpdateCard_MoveToDisallowedBoard_IsErrorAndNothingWritten()
    {
        AllowedBoardIds = AllowedBoard;
        Trello.Respond = r => r.Path.StartsWith("/1/lists/")
            ? FakeTrelloApi.Json($$"""{"idBoard":"{{OtherBoard}}"}""")
            : FakeTrelloApi.Json($$"""{"idBoard":"{{AllowedBoard}}"}""");

        var result = await CallAsync("update_card", new() { ["card_id"] = CardId, ["list_id"] = ListId });

        Assert.IsTrue(result.IsError);
        Assert.IsFalse(Trello.Requests.Any(r => r.Method != HttpMethod.Get));
    }

    [TestMethod]
    public async Task UpdateCard_NothingToChange_IsError()
    {
        var result = await CallAsync("update_card", new() { ["card_id"] = CardId });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "nothing to update");
        Assert.AreEqual(0, Trello.Requests.Count);
    }

    [TestMethod]
    public async Task AddComment_PostsText()
    {
        Trello.Respond = r => r.Method == HttpMethod.Post
            ? FakeTrelloApi.Json("""{"date":"2026-10-13T00:00:00.000Z","data":{"text":"On it"},"memberCreator":{"fullName":"RockBot"}}""")
            : FakeTrelloApi.Json($$"""{"idBoard":"{{AllowedBoard}}"}""");

        var comment = await CallOkAsync("add_comment", new() { ["card_id"] = CardId, ["text"] = "On it" });

        var post = Trello.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.AreEqual($"/1/cards/{CardId}/actions/comments", post.Path);
        Assert.AreEqual("On it", post.Form["text"]);
        Assert.AreEqual("On it", comment.GetProperty("text").GetString());
    }

    [TestMethod]
    [DataRow("get_card", "card_id", "../members/me")]
    [DataRow("get_board", "board_id", "123")]
    [DataRow("add_comment", "card_id", "zzzzzzzzzzzzzzzzzzzzzzzz")]
    public async Task InvalidId_IsErrorWithoutCallingTrello(string tool, string argument, string value)
    {
        var args = new Dictionary<string, object?> { [argument] = value };
        if (tool == "add_comment") args["text"] = "hi";

        var result = await CallAsync(tool, args);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), $"invalid {argument}");
        Assert.AreEqual(0, Trello.Requests.Count);
    }

    [TestMethod]
    public async Task Unauthorized_IsErrorWithHintAndNoCredentials()
    {
        Trello.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("invalid token") };

        var result = await CallAsync("list_boards");

        Assert.IsTrue(result.IsError);
        var text = TextOf(result);
        StringAssert.Contains(text, "401");
        StringAssert.Contains(text, "deploy/trello/setup.sh");
        Assert.IsFalse(text.Contains("test-token") || text.Contains("test-key"));
    }

    [TestMethod]
    public async Task UnknownArgument_IsRejected()
    {
        var result = await CallAsync("update_card", new() { ["card_id"] = CardId, ["closed"] = true });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "'closed'");
        Assert.AreEqual(0, Trello.Requests.Count);
    }

    [TestMethod]
    public async Task ListTools_ReadToolsAreReadOnly_NoArchiveOrDelete_SchemasStrict()
    {
        var tools = await (await ClientAsync()).ListToolsAsync();

        CollectionAssert.AreEquivalent(
            new[] { "add_comment", "create_card", "get_board", "get_card", "list_boards", "list_cards", "search", "update_card" },
            tools.Select(t => t.Name).ToArray());
        foreach (var name in new[] { "list_boards", "get_board", "list_cards", "get_card", "search" })
            Assert.IsTrue(tools.Single(t => t.Name == name).ProtocolTool.Annotations?.ReadOnlyHint, name);
        foreach (var tool in tools)
        {
            Assert.IsFalse(tool.JsonSchema.GetProperty("additionalProperties").GetBoolean(), tool.Name);
            Assert.IsFalse(tool.JsonSchema.GetProperty("properties").TryGetProperty("closed", out _), tool.Name);
        }
    }
}
