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
}
