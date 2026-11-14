namespace RockBot.Host.Tests;

[TestClass]
public class SkillUsageInheritanceTests
{
    private sealed class RecordingSkillUsageStore : ISkillUsageStore
    {
        private readonly List<SkillInvocationEvent> _events;
        public List<SkillInvocationEvent> Appended { get; } = [];

        public RecordingSkillUsageStore(params SkillInvocationEvent[] events) => _events = [.. events];

        public Task AppendAsync(SkillInvocationEvent evt, CancellationToken ct = default)
        {
            Appended.Add(evt);
            _events.Add(evt);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SkillInvocationEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SkillInvocationEvent>>(
                _events.Where(e => e.SessionId == sessionId).ToList());

        public Task<IReadOnlyList<SkillInvocationEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SkillInvocationEvent>>(_events);
    }

    [TestMethod]
    public async Task InheritFromPrimarySessionAsync_PrimaryHasEvent_CopiesItUnderNewSessionId()
    {
        var store = new RecordingSkillUsageStore(
            new SkillInvocationEvent("id-1", "trello-per-list-wisp-dump", "primary-session", DateTimeOffset.UtcNow.AddMinutes(-5)));

        await SkillUsageInheritance.InheritFromPrimarySessionAsync(store, "primary-session", "subagent/abc123");

        Assert.AreEqual(1, store.Appended.Count);
        Assert.AreEqual("trello-per-list-wisp-dump", store.Appended[0].SkillName);
        Assert.AreEqual("subagent/abc123", store.Appended[0].SessionId);
    }

    [TestMethod]
    public async Task InheritFromPrimarySessionAsync_PrimaryHasMultipleEvents_CopiesTheNewestOne()
    {
        var store = new RecordingSkillUsageStore(
            new SkillInvocationEvent("id-1", "older-skill", "primary-session", DateTimeOffset.UtcNow.AddMinutes(-30)),
            new SkillInvocationEvent("id-2", "newest-skill", "primary-session", DateTimeOffset.UtcNow.AddMinutes(-1)));

        await SkillUsageInheritance.InheritFromPrimarySessionAsync(store, "primary-session", "worker-xyz");

        Assert.AreEqual(1, store.Appended.Count);
        Assert.AreEqual("newest-skill", store.Appended[0].SkillName);
    }

    [TestMethod]
    public async Task InheritFromPrimarySessionAsync_PrimaryHasNoEvents_AppendsNothing()
    {
        var store = new RecordingSkillUsageStore();

        await SkillUsageInheritance.InheritFromPrimarySessionAsync(store, "primary-session", "subagent/abc123");

        Assert.AreEqual(0, store.Appended.Count);
    }

    [TestMethod]
    public async Task InheritFromPrimarySessionAsync_NullStore_DoesNotThrow()
    {
        await SkillUsageInheritance.InheritFromPrimarySessionAsync(null, "primary-session", "subagent/abc123");
    }

    [TestMethod]
    public async Task InheritFromPrimarySessionAsync_NullOrEmptyPrimarySessionId_AppendsNothing()
    {
        var store = new RecordingSkillUsageStore(
            new SkillInvocationEvent("id-1", "some-skill", "primary-session", DateTimeOffset.UtcNow));

        await SkillUsageInheritance.InheritFromPrimarySessionAsync(store, null, "subagent/abc123");
        await SkillUsageInheritance.InheritFromPrimarySessionAsync(store, "", "subagent/abc123");

        Assert.AreEqual(0, store.Appended.Count);
    }

    [TestMethod]
    public async Task InheritFromPrimarySessionAsync_PrimaryIdHasSessionNamespacePrefix_StripsItBeforeLookup()
    {
        // get_skill records events under the raw session id ("cli-session"), but
        // spawn_subagent passes the working-memory namespace form ("session/cli-session")
        // as primarySessionId — the lookup must strip the prefix to find them.
        var store = new RecordingSkillUsageStore(
            new SkillInvocationEvent("id-1", "trello-per-list-digests", "cli-session", DateTimeOffset.UtcNow.AddMinutes(-1)));

        await SkillUsageInheritance.InheritFromPrimarySessionAsync(store, "session/cli-session", "subagent/abc123");

        Assert.AreEqual(1, store.Appended.Count);
        Assert.AreEqual("trello-per-list-digests", store.Appended[0].SkillName);
        Assert.AreEqual("subagent/abc123", store.Appended[0].SessionId);
    }

    [TestMethod]
    [DataRow("session/cli-session", "cli-session")]
    [DataRow("subagent/abc123", "subagent-abc123")]
    [DataRow("worker/abc123", "worker-abc123")]
    [DataRow("SUBAGENT/abc123", "subagent-abc123")]
    [DataRow("blazor-session", "blazor-session")]
    [DataRow("patrol/daily-briefing-0800", "patrol/daily-briefing-0800")]
    public void ToSkillUsageSessionId_ConvertsKnownNamespaceForms(string input, string expected) =>
        Assert.AreEqual(expected, SkillUsageInheritance.ToSkillUsageSessionId(input));

    [TestMethod]
    public async Task InheritFromPrimarySessionAsync_PrimaryIdIsSubagentNamespaceForm_ConvertsBeforeLookup()
    {
        // A subagent that itself spawns further work (e.g. via spawn_wisps) reports its
        // ToolInvokeRequest.SessionId in the WM-namespace form ("subagent/abc123"), but its
        // own get_skill calls — and any inherited event — are recorded under the dash form
        // ("subagent-abc123"). The lookup must convert, not just strip a prefix.
        var store = new RecordingSkillUsageStore(
            new SkillInvocationEvent("id-1", "trello-per-list-digests", "subagent-abc123", DateTimeOffset.UtcNow.AddMinutes(-1)));

        await SkillUsageInheritance.InheritFromPrimarySessionAsync(store, "subagent/abc123", "subagent/def456");

        Assert.AreEqual(1, store.Appended.Count);
        Assert.AreEqual("trello-per-list-digests", store.Appended[0].SkillName);
    }
}
