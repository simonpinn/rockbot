using System.Text;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Stimulus.Tests;

[TestClass]
public class StimulusMatcherXFactorTests
{
    private static MessageEnvelope NewEnvelope(string? jsonBody = null) => new()
    {
        MessageId = "m1",
        MessageType = "tool.invoke.failed",
        Source = "calendar-mcp",
        Timestamp = DateTimeOffset.UtcNow,
        Body = jsonBody is null ? ReadOnlyMemory<byte>.Empty : new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(jsonBody)),
    };

    private static StimulusPattern NewPattern(StimulusXFactorSlot? xFactor) => new()
    {
        Id = "p1",
        Topic = "tool.invoke.failed",
        ReflexTool = "reflex__calendar__retry",
        XFactor = xFactor,
    };

    private static readonly XFactorMemoryOptions Options = new();

    [TestMethod]
    public async Task TryMatchAsync_NoXFactorSlot_BehavesLikeTryMatchChannels()
    {
        var pattern = NewPattern(xFactor: null);
        var store = new FakeXFactorMemoryStore();

        var result = await StimulusMatcher.TryMatchAsync(
            pattern, "tool.invoke.failed", NewEnvelope(), store, Options, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.IsTrue(result.Matched);
        Assert.IsNull(result.XFactor);
        Assert.AreEqual(0, store.Sightings.Count, "no x-factor slot means no store interaction");
    }

    [TestMethod]
    public async Task TryMatchAsync_NeverSeenValue_IsNovel_Matches()
    {
        var pattern = NewPattern(new StimulusXFactorSlot { Channel = "payload:eyes" });
        var store = new FakeXFactorMemoryStore();
        var envelope = NewEnvelope("""{ "eyes": "unknown-set-1" }""");

        var result = await StimulusMatcher.TryMatchAsync(
            pattern, "tool.invoke.failed", envelope, store, Options, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.IsTrue(result.Matched);
        Assert.AreEqual("payload:eyes", result.XFactor!.Channel);
        Assert.AreEqual("unknown-set-1", result.XFactor.Value);
        Assert.AreEqual(Options.DefaultSalienceWeight, result.XFactor.SalienceWeight);
    }

    [TestMethod]
    public async Task TryMatchAsync_SeenWithinRecencyWindow_IsNotNovel_DoesNotMatch()
    {
        var pattern = NewPattern(new StimulusXFactorSlot { Channel = "payload:eyes" });
        var store = new FakeXFactorMemoryStore();
        var now = DateTimeOffset.UtcNow;
        store.Seed(new XFactorMemoryKey("payload:eyes", "known-set"), count: 3, firstSeen: now.AddDays(-1), lastSeen: now.AddMinutes(-5), weight: 1.0);
        var envelope = NewEnvelope("""{ "eyes": "known-set" }""");

        var result = await StimulusMatcher.TryMatchAsync(
            pattern, "tool.invoke.failed", envelope, store, Options, now, CancellationToken.None);

        Assert.IsFalse(result.Matched);
        Assert.AreEqual(0, result.MatchedChannels.Count,
            "no named channels on this pattern - nothing was ever resolved to preserve");
    }

    [TestMethod]
    public async Task TryMatchAsync_NearMiss_PreservesAlreadyResolvedNamedChannels()
    {
        // A sighting was already recorded by the time novelty/weight rejects the match - the
        // named channels that DID resolve (independent of the x-factor outcome) must still be
        // visible to a caller that reads MatchedChannels on a near-miss (future diagnostics).
        var pattern = new StimulusPattern
        {
            Id = "p1",
            Topic = "tool.invoke.failed",
            ReflexTool = "reflex__calendar__retry",
            Channels = new Dictionary<string, string> { ["source"] = "calendar-mcp" },
            XFactor = new StimulusXFactorSlot { Channel = "payload:eyes" },
        };
        var store = new FakeXFactorMemoryStore();
        var now = DateTimeOffset.UtcNow;
        store.Seed(new XFactorMemoryKey("payload:eyes", "known-set"), count: 3, firstSeen: now.AddDays(-1), lastSeen: now.AddMinutes(-5), weight: 1.0);
        var envelope = NewEnvelope("""{ "eyes": "known-set" }""");

        var result = await StimulusMatcher.TryMatchAsync(
            pattern, "tool.invoke.failed", envelope, store, Options, now, CancellationToken.None);

        Assert.IsFalse(result.Matched, "precondition: this is the near-miss case");
        Assert.AreEqual("calendar-mcp", result.MatchedChannels["source"],
            "the named channel really did resolve - a near-miss must not discard that");
    }

    [TestMethod]
    public async Task TryMatchAsync_SeenButOutsideRecencyWindow_IsNovelAgain_Matches()
    {
        var pattern = NewPattern(new StimulusXFactorSlot { Channel = "payload:eyes", RecencyWindow = TimeSpan.FromHours(1) });
        var store = new FakeXFactorMemoryStore();
        var now = DateTimeOffset.UtcNow;
        store.Seed(new XFactorMemoryKey("payload:eyes", "known-set"), count: 3, firstSeen: now.AddDays(-7), lastSeen: now.AddHours(-2), weight: 1.0);
        var envelope = NewEnvelope("""{ "eyes": "known-set" }""");

        var result = await StimulusMatcher.TryMatchAsync(
            pattern, "tool.invoke.failed", envelope, store, Options, now, CancellationToken.None);

        Assert.IsTrue(result.Matched, "a value last seen outside its recency window is novel again");
    }

    [TestMethod]
    public async Task TryMatchAsync_SalienceBelowFloor_DoesNotMatch_EvenIfNovelByRecency()
    {
        var pattern = NewPattern(new StimulusXFactorSlot { Channel = "payload:eyes" });
        var store = new FakeXFactorMemoryStore();
        var now = DateTimeOffset.UtcNow;
        store.Seed(new XFactorMemoryKey("payload:eyes", "down-weighted"), count: 10, firstSeen: now.AddDays(-30), lastSeen: now.AddDays(-10), weight: 0.1);
        var envelope = NewEnvelope("""{ "eyes": "down-weighted" }""");

        var result = await StimulusMatcher.TryMatchAsync(
            pattern, "tool.invoke.failed", envelope, store, Options, now, CancellationToken.None);

        Assert.IsFalse(result.Matched, "down-weighted below the match floor never contributes a match, regardless of novelty");
    }

    [TestMethod]
    public async Task TryMatchAsync_XFactorChannelAbsentFromMessage_DoesNotMatch()
    {
        var pattern = NewPattern(new StimulusXFactorSlot { Channel = "payload:eyes" });
        var store = new FakeXFactorMemoryStore();
        var envelope = NewEnvelope("""{ "server": "calendar-mcp" }""");

        var result = await StimulusMatcher.TryMatchAsync(
            pattern, "tool.invoke.failed", envelope, store, Options, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.IsFalse(result.Matched);
    }

    [TestMethod]
    public async Task TryMatchAsync_NoStoreWired_PatternWithXFactorSlotNeverMatches()
    {
        var pattern = NewPattern(new StimulusXFactorSlot { Channel = "payload:eyes" });
        var envelope = NewEnvelope("""{ "eyes": "anything" }""");

        var result = await StimulusMatcher.TryMatchAsync(
            pattern, "tool.invoke.failed", envelope, xFactorStore: null, Options, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.IsFalse(result.Matched);
    }

    [TestMethod]
    public async Task TryMatchAsync_NamedChannelsAlsoRequired_BothMustMatch()
    {
        var pattern = new StimulusPattern
        {
            Id = "p1",
            Topic = "tool.invoke.failed",
            ReflexTool = "reflex__calendar__retry",
            Channels = new Dictionary<string, string> { ["source"] = "weather-mcp" }, // won't match NewEnvelope's "calendar-mcp"
            XFactor = new StimulusXFactorSlot { Channel = "payload:eyes" },
        };
        var store = new FakeXFactorMemoryStore();
        var envelope = NewEnvelope("""{ "eyes": "unknown" }""");

        var result = await StimulusMatcher.TryMatchAsync(
            pattern, "tool.invoke.failed", envelope, store, Options, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.IsFalse(result.Matched);
        Assert.AreEqual(0, store.Sightings.Count, "the x-factor slot shouldn't even be checked once the named channels miss");
    }

    [TestMethod]
    public async Task TryMatchAsync_RecordsASighting_ForEveryEvaluation()
    {
        var pattern = NewPattern(new StimulusXFactorSlot { Channel = "payload:eyes" });
        var store = new FakeXFactorMemoryStore();
        var envelope = NewEnvelope("""{ "eyes": "unknown-set-1" }""");

        await StimulusMatcher.TryMatchAsync(
            pattern, "tool.invoke.failed", envelope, store, Options, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.AreEqual(1, store.Sightings.Count);
        Assert.AreEqual(new XFactorMemoryKey("payload:eyes", "unknown-set-1"), store.Sightings[0]);
    }
}

internal sealed class FakeXFactorMemoryStore : IXFactorMemoryStore
{
    private readonly Dictionary<XFactorMemoryKey, XFactorMemoryRecord> _records = new();
    public List<XFactorMemoryKey> Sightings { get; } = [];

    public void Seed(XFactorMemoryKey key, int count, DateTimeOffset firstSeen, DateTimeOffset lastSeen, double weight) =>
        _records[key] = new XFactorMemoryRecord(key, count, firstSeen, lastSeen, weight);

    public Task<XFactorMemoryRecord?> RecordSightingAsync(XFactorMemoryKey key, DateTimeOffset at, CancellationToken ct = default)
    {
        Sightings.Add(key);
        var prior = _records.TryGetValue(key, out var existing) ? existing : null;
        _records[key] = prior is null
            ? new XFactorMemoryRecord(key, 1, at, at, 1.0)
            : prior with { Count = prior.Count + 1, LastSeen = at };
        return Task.FromResult(prior);
    }

    public Task<XFactorMemoryRecord?> TryGetAsync(XFactorMemoryKey key, CancellationToken ct = default) =>
        Task.FromResult(_records.TryGetValue(key, out var r) ? r : null);

    public Task<IReadOnlyList<XFactorMemoryRecord>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<XFactorMemoryRecord>>(_records.Values.ToList());

    public Task<XFactorMemoryRecord?> AdjustSalienceAsync(XFactorMemoryKey key, double newWeight, CancellationToken ct = default)
    {
        var prior = _records.TryGetValue(key, out var existing) ? existing : null;
        if (prior is not null)
            _records[key] = prior with { SalienceWeight = newWeight };
        return Task.FromResult(prior);
    }
}
