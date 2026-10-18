using System.Text.Json;

namespace McpServer.Obsidian.Tests;

[TestClass]
public sealed class VaultToolsTests : VaultServerTestBase
{
    private static string[] Paths(JsonElement array, string property = "path") =>
        array.EnumerateArray().Select(e => e.GetProperty(property).GetString()!).ToArray();

    [TestMethod]
    public async Task ListTools_ExposesTheVaultTools()
    {
        var tools = await (await ClientAsync()).ListToolsAsync();

        CollectionAssert.AreEquivalent(
            new[] { "search_notes", "read_note", "list_notes", "list_folders", "create_note", "append_to_note" },
            tools.Select(t => t.Name).ToArray());
    }

    // ---- search ------------------------------------------------------------------------------

    [TestMethod]
    public async Task SearchNotes_MatchesAllWordsInPathOrContent_TitleMatchesFirst()
    {
        Write("Daily/2026-10-01.md", "Talked about the Kubernetes upgrade with the team.");
        Write("Projects/Kubernetes upgrade.md", "Plan for the cluster.");
        Write("Projects/Other.md", "Kubernetes only, no second word.");
        Write(".obsidian/workspace.md", "kubernetes upgrade");
        Write(".trash/Old.md", "kubernetes upgrade");

        var hits = await CallOkAsync("search_notes", new() { ["query"] = "kubernetes UPGRADE" });

        CollectionAssert.AreEqual(new[] { "Projects/Kubernetes upgrade.md", "Daily/2026-10-01.md" }, Paths(hits));
        StringAssert.Contains(hits[1].GetProperty("snippet").GetString(), "Kubernetes upgrade");
        Assert.AreEqual("Kubernetes upgrade", hits[0].GetProperty("title").GetString());
        StringAssert.EndsWith(hits[0].GetProperty("modified").GetString(), "+11:00");
    }

    [TestMethod]
    public async Task SearchNotes_FolderAndTopLimitResults()
    {
        for (var i = 0; i < 5; i++)
            Write($"Notes/n{i}.md", "apple");
        Write("Elsewhere/x.md", "apple");

        var hits = await CallOkAsync("search_notes", new() { ["query"] = "apple", ["folder"] = "Notes", ["top"] = 3 });

        Assert.AreEqual(3, hits.GetArrayLength());
        Assert.IsTrue(Paths(hits).All(p => p.StartsWith("Notes/")));
    }

    // ---- read and list -----------------------------------------------------------------------

    [TestMethod]
    public async Task ReadNote_SplitsFrontmatter_AndAcceptsPathWithoutExtension()
    {
        Write("Projects/Plan.md", "---\ntags:\n  - work\n---\n# Plan\n\nBody text\n");

        var note = await CallOkAsync("read_note", new() { ["path"] = "Projects/Plan" });

        Assert.AreEqual("Projects/Plan.md", note.GetProperty("path").GetString());
        Assert.AreEqual("tags:\n  - work", note.GetProperty("frontmatter").GetString());
        Assert.AreEqual("# Plan\n\nBody text\n", note.GetProperty("content").GetString());
        Assert.IsFalse(note.GetProperty("truncated").GetBoolean());
    }

    [TestMethod]
    public async Task ReadNote_LongNote_IsTruncated()
    {
        Settings["Obsidian:MaxReadChars"] = "1000";
        Write("Long.md", new string('x', 5000));

        var note = await CallOkAsync("read_note", new() { ["path"] = "Long.md" });

        Assert.IsTrue(note.GetProperty("truncated").GetBoolean());
        StringAssert.Contains(note.GetProperty("content").GetString(), "[truncated: the note is 5,000 characters]");
    }

    [TestMethod]
    public async Task ReadNote_Missing_IsError()
    {
        StringAssert.Contains(await CallErrorAsync("read_note", new() { ["path"] = "Nope.md" }), "note not found");
    }

    [TestMethod]
    [DataRow("../outside.md")]
    [DataRow("Projects/../../outside.md")]
    [DataRow("/etc/passwd")]
    [DataRow(".obsidian/app.json")]
    [DataRow("Projects/.hidden/x.md")]
    [DataRow("C:/x.md")]
    public async Task ReadNote_UnsafePath_IsError(string path)
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(VaultDir)!, "outside.md"), "secret");
        Write(".obsidian/app.json.md", "{}");

        StringAssert.Contains(await CallErrorAsync("read_note", new() { ["path"] = path }), "vault-relative path");
    }

    [TestMethod]
    public async Task ReadNote_NonMarkdownFile_IsNotFound()
    {
        Write("data.txt", "plain");

        StringAssert.Contains(await CallErrorAsync("read_note", new() { ["path"] = "data.txt" }), "note not found: data.txt.md");
    }

    [TestMethod]
    public async Task ReadNote_ThroughSymlink_IsError()
    {
        var outside = Directory.CreateTempSubdirectory("obsidian-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.md"), "secret");
            Directory.CreateSymbolicLink(Path.Combine(VaultDir, "Linked"), outside);
            File.CreateSymbolicLink(Path.Combine(VaultDir, "Alias.md"), Path.Combine(outside, "secret.md"));

            StringAssert.Contains(await CallErrorAsync("read_note", new() { ["path"] = "Linked/secret.md" }), "symbolic link");
            StringAssert.Contains(await CallErrorAsync("read_note", new() { ["path"] = "Alias.md" }), "symbolic link");
            var search = await CallOkAsync("search_notes", new() { ["query"] = "secret" });
            Assert.AreEqual(0, search.GetArrayLength());
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [TestMethod]
    public async Task ListNotes_SortsByModifiedOrName_SkippingHidden()
    {
        File.SetLastWriteTimeUtc(Write("B.md", "b"), DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(Write("Sub/A.md", "a"), DateTime.UtcNow);
        Write(".obsidian/x.md", "hidden");
        Write("image.png", "not a note");

        var byModified = await CallOkAsync("list_notes");
        var byName = await CallOkAsync("list_notes", new() { ["sort"] = "name" });

        CollectionAssert.AreEqual(new[] { "Sub/A.md", "B.md" }, Paths(byModified));
        CollectionAssert.AreEqual(new[] { "B.md", "Sub/A.md" }, Paths(byName));
    }

    [TestMethod]
    public async Task ListFolders_ListsOneLevelWithoutHiddenFolders()
    {
        Write("Projects/Deep/x.md", "x");
        Write("Daily/y.md", "y");
        Write(".obsidian/z.md", "z");

        var top = await CallOkAsync("list_folders");
        var projects = await CallOkAsync("list_folders", new() { ["folder"] = "Projects" });

        CollectionAssert.AreEqual(new[] { "Daily", "Projects" }, top.EnumerateArray().Select(e => e.GetString()).ToArray());
        CollectionAssert.AreEqual(new[] { "Projects/Deep" }, projects.EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    // ---- create ------------------------------------------------------------------------------

    [TestMethod]
    public async Task CreateNote_WritesUnderWriteFolderWithFrontmatter()
    {
        var result = await CallOkAsync("create_note", new()
        {
            ["title"] = "Should we: move to k8s?",
            ["content"] = "## Synthesis\r\n\r\nYes.",
            ["folder"] = "Council",
            ["tags"] = new[] { "#decision", "home lab", "!!" },
            ["source"] = "AdvisorCouncil \"advise\""
        });

        Assert.AreEqual("RockBot/Council/Should we move to k8s.md", result.GetProperty("path").GetString());
        Assert.AreEqual("obsidian://open?vault=Main%20Vault&file=RockBot%2FCouncil%2FShould%20we%20move%20to%20k8s",
            result.GetProperty("link").GetString());
        var text = ReadFile("RockBot/Council/Should we move to k8s.md");
        StringAssert.StartsWith(text, "---\ncreated: 20");
        StringAssert.Contains(text, "source: \"AdvisorCouncil \\u0022advise\\u0022\"\n");
        StringAssert.Contains(text, "tags:\n  - rockbot\n  - decision\n  - home-lab\n---\n\n## Synthesis\n\nYes.\n");
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(VaultDir, "RockBot/Council"), ".*").Length, "temp file left behind");
    }

    [TestMethod]
    public async Task CreateNote_TitleTaken_AddsNumberUnlessOverwrite()
    {
        Write("RockBot/Idea.md", "original");

        var second = await CallOkAsync("create_note", new() { ["title"] = "Idea", ["content"] = "two" });
        var third = await CallOkAsync("create_note", new() { ["title"] = "Idea", ["content"] = "three" });
        var replaced = await CallOkAsync("create_note", new() { ["title"] = "Idea", ["content"] = "new", ["overwrite"] = true });

        Assert.AreEqual("RockBot/Idea (2).md", second.GetProperty("path").GetString());
        Assert.AreEqual("RockBot/Idea (3).md", third.GetProperty("path").GetString());
        Assert.AreEqual("RockBot/Idea.md", replaced.GetProperty("path").GetString());
        StringAssert.EndsWith(ReadFile("RockBot/Idea.md"), "\nnew\n");
    }

    [TestMethod]
    [DataRow("../Projects")]
    [DataRow("/Projects")]
    [DataRow(".obsidian")]
    public async Task CreateNote_FolderEscapingWriteFolder_IsError(string folder)
    {
        Write("Projects/Idea.md", "original");

        await CallErrorAsync("create_note", new() { ["title"] = "Idea", ["content"] = "x", ["folder"] = folder, ["overwrite"] = true });

        Assert.AreEqual("original", ReadFile("Projects/Idea.md"));
        Assert.IsFalse(Directory.Exists(Path.Combine(VaultDir, ".obsidian")));
    }

    [TestMethod]
    public async Task CreateNote_WriteFolderIsSymlink_IsError()
    {
        var outside = Directory.CreateTempSubdirectory("obsidian-outside-").FullName;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(VaultDir, WriteFolder), outside);

            StringAssert.Contains(await CallErrorAsync("create_note", new() { ["title"] = "x", ["content"] = "x" }), "symbolic link");
            Assert.AreEqual(0, Directory.GetFiles(outside).Length);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("###", "x", "title must contain")]
    [DataRow("ok", "", "content must not be empty")]
    public async Task CreateNote_BadInput_IsError(string title, string content, string message)
    {
        StringAssert.Contains(await CallErrorAsync("create_note", new() { ["title"] = title, ["content"] = content }), message);
        Assert.IsFalse(Directory.Exists(Path.Combine(VaultDir, WriteFolder)));
    }

    [TestMethod]
    public async Task CreateNote_ContentOverLimit_IsError()
    {
        Settings["Obsidian:MaxWriteChars"] = "1000";

        StringAssert.Contains(
            await CallErrorAsync("create_note", new() { ["title"] = "Big", ["content"] = new string('x', 1001) }),
            "at most 1,000 characters");
    }

    // ---- append ------------------------------------------------------------------------------

    [TestMethod]
    public async Task AppendToNote_UnderHeading_InsertsAtEndOfThatSection()
    {
        Write("RockBot/Log.md", "# Log\n\n## Actions\n\n- one\n\n## Notes\n\ntext\n");

        await CallOkAsync("append_to_note", new() { ["path"] = "RockBot/Log", ["content"] = "- two", ["heading"] = "actions" });

        Assert.AreEqual("# Log\n\n## Actions\n\n- one\n\n- two\n\n## Notes\n\ntext\n", ReadFile("RockBot/Log.md"));
    }

    [TestMethod]
    public async Task AppendToNote_MissingHeading_AddsItAtTheEnd_NoHeading_AppendsAtEnd()
    {
        Write("RockBot/Log.md", "# Log\n");

        await CallOkAsync("append_to_note", new() { ["path"] = "RockBot/Log.md", ["content"] = "first", ["heading"] = "Follow-ups" });
        await CallOkAsync("append_to_note", new() { ["path"] = "RockBot/Log.md", ["content"] = "last" });

        Assert.AreEqual("# Log\n\n## Follow-ups\n\nfirst\n\nlast\n", ReadFile("RockBot/Log.md"));
    }

    [TestMethod]
    public async Task AppendToNote_OutsideWriteFolder_IsReadOnly()
    {
        Write("Projects/Plan.md", "mine");

        StringAssert.Contains(await CallErrorAsync("append_to_note", new() { ["path"] = "Projects/Plan.md", ["content"] = "x" }), "read-only");
        StringAssert.Contains(await CallErrorAsync("append_to_note", new() { ["path"] = "RockBot/../Projects/Plan.md", ["content"] = "x" }), "vault-relative");
        Assert.AreEqual("mine", ReadFile("Projects/Plan.md"));
    }

    [TestMethod]
    public async Task AppendToNote_MissingNote_IsError()
    {
        StringAssert.Contains(await CallErrorAsync("append_to_note", new() { ["path"] = "RockBot/None.md", ["content"] = "x" }), "note not found");
        Assert.IsFalse(Exists("RockBot/None.md"));
    }
}
