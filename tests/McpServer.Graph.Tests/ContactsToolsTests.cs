using System.Net;
using System.Text.Json;

namespace McpServer.Graph.Tests;

[TestClass]
public sealed class ContactsToolsTests : GraphServerTestBase
{
    private static string Contact(string id, string name, params string[] emails) => JsonSerializer.Serialize(new
    {
        id,
        displayName = name,
        givenName = name.Split(' ')[0],
        surname = name.Split(' ')[^1],
        emailAddresses = emails.Select(e => new { name, address = e }).ToArray(),
        mobilePhone = "0400 000 000",
        businessPhones = new[] { "02 9000 0000" },
        homePhones = Array.Empty<string>(),
        companyName = "Contoso",
        jobTitle = "CTO",
        businessAddress = new { street = "1 George St", city = "Sydney", state = "NSW", postalCode = "2000", countryOrRegion = "Australia" },
        homeAddress = new { },
        birthday = "1980-05-17T11:59:00Z",
        personalNotes = "Met at conf"
    });

    private static string Page(string? nextLink, params string[] contacts)
    {
        var value = "[" + string.Join(",", contacts) + "]";
        return nextLink is null
            ? $$"""{"value":{{value}}}"""
            : $$"""{"value":{{value}},"@odata.nextLink":{{JsonSerializer.Serialize(nextLink)}}}""";
    }

    [TestMethod]
    public async Task ListContacts_PagesSortedByName()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(Page(null, Contact("c1", "Alice Smith", "alice@example.com")));

        var result = await CallOkAsync("list_contacts", new() { ["top"] = 10, ["skip"] = 20 });

        var request = Graph.Single;
        Assert.AreEqual("/v1.0/users/me%40contoso.com/contacts", request.RequestUri!.AbsolutePath);
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        StringAssert.Contains(query, "$top=10");
        StringAssert.Contains(query, "$skip=20");
        StringAssert.Contains(query, "$orderby=displayName");
        var c = result[0];
        Assert.AreEqual("Alice Smith", c.GetProperty("displayName").GetString());
        Assert.AreEqual("alice@example.com", c.GetProperty("emails")[0].GetString());
        Assert.AreEqual("Contoso", c.GetProperty("company").GetString());
    }

    [TestMethod]
    public async Task SearchContacts_ByName_FiltersOnNamePrefixesWithQuotesEscaped()
    {
        await CallOkAsync("search_contacts", new() { ["query"] = "O'Brien" });

        var query = Uri.UnescapeDataString(Graph.Single.RequestUri!.Query);
        StringAssert.Contains(query, "startswith(displayName,'O''Brien')");
        StringAssert.Contains(query, "startswith(surname,'O''Brien')");
        StringAssert.Contains(query, "startswith(companyName,'O''Brien')");
    }

    [TestMethod]
    public async Task SearchContacts_ByEmail_ScansPagesAndMatchesPartOfAddress()
    {
        var next = "https://graph.microsoft.com/v1.0/users/me%40contoso.com/contacts?$skip=100";
        Graph.Respond = r => FakeGraphHandler.Json(r.RequestUri!.Query.Contains("skip=100")
            ? Page(null, Contact("c3", "Carol Jones", "carol@contoso.com"))
            : Page(next, Contact("c1", "Alice Smith", "alice@example.com"), Contact("c2", "Bob Lee", "bob@CONTOSO.com")));

        var result = await CallOkAsync("search_contacts", new() { ["query"] = "@contoso.com" });

        Assert.AreEqual(2, Graph.Requests.Count);
        Assert.IsFalse(Uri.UnescapeDataString(Graph.Requests[0].RequestUri!.Query).Contains("$filter"));
        CollectionAssert.AreEqual(new[] { "c2", "c3" }, result.EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToArray());
    }

    [TestMethod]
    public async Task GetContact_ReturnsAllDetails()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(Contact("c1", "Alice Smith", "alice@example.com"));

        var c = await CallOkAsync("get_contact", new() { ["id"] = "c1" });

        Assert.AreEqual("/v1.0/users/me%40contoso.com/contacts/c1", Graph.Single.RequestUri!.AbsolutePath);
        Assert.AreEqual("Alice", c.GetProperty("givenName").GetString());
        Assert.AreEqual("1 George St, Sydney, NSW, 2000, Australia", c.GetProperty("businessAddress").GetString());
        Assert.AreEqual(JsonValueKind.Null, c.GetProperty("homeAddress").ValueKind);
        Assert.AreEqual("1980-05-17", c.GetProperty("birthday").GetString());
        Assert.AreEqual("Met at conf", c.GetProperty("notes").GetString());
    }

    [TestMethod]
    public async Task CreateContact_PostsFieldsAndDerivesDisplayName()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(Contact("new", "Test Person", "test@example.com"), HttpStatusCode.Created);

        await CallOkAsync("create_contact", new()
        {
            ["given_name"] = "Test", ["surname"] = "Person", ["emails"] = new[] { "test@example.com" },
            ["mobile_phone"] = "0400 111 222", ["company"] = "Example"
        });

        var request = Graph.Single;
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("/v1.0/users/me%40contoso.com/contacts", request.RequestUri!.AbsolutePath);
        var body = Graph.BodyOf(0);
        Assert.AreEqual("Test Person", body.GetProperty("displayName").GetString());
        Assert.AreEqual("test@example.com", body.GetProperty("emailAddresses")[0].GetProperty("address").GetString());
        Assert.AreEqual("0400 111 222", body.GetProperty("mobilePhone").GetString());
        Assert.AreEqual("Example", body.GetProperty("companyName").GetString());
    }

    [TestMethod]
    public async Task CreateContact_EmailOnly_UsesAddressAsDisplayName()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(Contact("new", "x", "x@example.com"));

        await CallOkAsync("create_contact", new() { ["emails"] = new[] { "x@example.com" } });

        Assert.AreEqual("x@example.com", Graph.BodyOf(0).GetProperty("displayName").GetString());
    }

    [TestMethod]
    [DataRow(null, "give at least a name")]
    [DataRow("a@example.com,b@example.com,c@example.com,d@example.com", "at most 3")]
    [DataRow("nope", "not a valid email address")]
    public async Task CreateContact_BadInput_IsErrorWithoutCallingGraph(string? emails, string message)
    {
        var arguments = new Dictionary<string, object?>();
        if (emails is not null)
            arguments["emails"] = emails.Split(',');
        else
            arguments["notes"] = "no name";

        var result = await CallAsync("create_contact", arguments);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), message);
        Assert.AreEqual(0, Graph.Requests.Count);
    }

    [TestMethod]
    public async Task UpdateContact_PatchesOnlyTheGivenFields()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(Contact("c1", "Alice Smith", "alice@example.com"));

        await CallOkAsync("update_contact", new() { ["id"] = "c1", ["job_title"] = "CEO", ["mobile_phone"] = "" });

        var request = Graph.Single;
        Assert.AreEqual(HttpMethod.Patch, request.Method);
        Assert.AreEqual("/v1.0/users/me%40contoso.com/contacts/c1", request.RequestUri!.AbsolutePath);
        var body = Graph.BodyOf(0);
        CollectionAssert.AreEquivalent(new[] { "jobTitle", "mobilePhone" }, body.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual("", body.GetProperty("mobilePhone").GetString());
    }

    [TestMethod]
    public async Task UpdateContact_NothingGiven_IsError()
    {
        var result = await CallAsync("update_contact", new() { ["id"] = "c1" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "nothing to update");
    }

    [TestMethod]
    public async Task GraphForbidden_NamesContactsPermission()
    {
        Graph.Respond = _ => FakeGraphHandler.Json(
            """{"error":{"code":"ErrorAccessDenied","message":"Access is denied"}}""", HttpStatusCode.Forbidden);

        var result = await CallAsync("list_contacts");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(TextOf(result), "not authorised for contacts");
    }
}
