using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class FileXFactorMemoryStoreTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rockbot-xfactor-test-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task RecordSightingAsync_FirstSighting_CreatesRecordAtDefaultWeight_ReturnsNullAsPrior()
    {
        var store = CreateStore();
        var key = new XFactorMemoryKey("sight", "unknown-eyes");
        var t = DateTimeOffset.UtcNow;

        var prior = await store.RecordSightingAsync(key, t);

        Assert.IsNull(prior, "nothing was there before a first sighting");
        var current = await store.TryGetAsync(key);
        Assert.AreEqual(1, current!.Count);
        Assert.AreEqual(t, current.FirstSeen);
        Assert.AreEqual(t, current.LastSeen);
        Assert.AreEqual(1.0, current.SalienceWeight);
    }

    [TestMethod]
    public async Task RecordSightingAsync_SecondSighting_IncrementsCount_UpdatesLastSeen_ReturnsPriorRecord()
    {
        var store = CreateStore();
        var key = new XFactorMemoryKey("sight", "unknown-eyes");
        var t0 = DateTimeOffset.UtcNow;
        var t1 = t0.AddHours(1);
        await store.RecordSightingAsync(key, t0);

        var prior = await store.RecordSightingAsync(key, t1);

        Assert.AreEqual(1, prior!.Count, "the prior record, from before this sighting");
        Assert.AreEqual(t0, prior.LastSeen);

        var current = await store.TryGetAsync(key);
        Assert.AreEqual(2, current!.Count);
        Assert.AreEqual(t0, current.FirstSeen, "first-seen never moves");
        Assert.AreEqual(t1, current.LastSeen);
    }

    [TestMethod]
    public async Task RecordSightingAsync_PreservesSalienceWeight_AcrossRepeatedSightings()
    {
        // A sighting is evidence the pattern still happens - it must never itself change the
        // weight. Only AdjustSalienceAsync (the deliberate revision path) may do that.
        var store = CreateStore();
        var key = new XFactorMemoryKey("sight", "unknown-eyes");
        await store.RecordSightingAsync(key, DateTimeOffset.UtcNow);
        await store.AdjustSalienceAsync(key, 0.3);

        await store.RecordSightingAsync(key, DateTimeOffset.UtcNow.AddHours(1));

        var current = await store.TryGetAsync(key);
        Assert.AreEqual(0.3, current!.SalienceWeight);
    }

    [TestMethod]
    public async Task TryGetAsync_UnknownKey_ReturnsNull()
    {
        var store = CreateStore();

        var result = await store.TryGetAsync(new XFactorMemoryKey("sight", "never-seen"));

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ListAsync_ReturnsAllRecords_NewestFirst()
    {
        var store = CreateStore();
        var t0 = DateTimeOffset.UtcNow;
        await store.RecordSightingAsync(new XFactorMemoryKey("sight", "a"), t0);
        await store.RecordSightingAsync(new XFactorMemoryKey("sound", "b"), t0.AddMinutes(1));

        var all = await store.ListAsync();

        Assert.AreEqual(2, all.Count);
        Assert.AreEqual("b", all[0].Key.Value, "most recently seen first");
    }

    [TestMethod]
    public async Task AdjustSalienceAsync_SetsNewWeight_ReturnsPriorRecordForRevert()
    {
        var store = CreateStore();
        var key = new XFactorMemoryKey("sight", "unknown-eyes");
        await store.RecordSightingAsync(key, DateTimeOffset.UtcNow);

        var prior = await store.AdjustSalienceAsync(key, 0.3);

        Assert.AreEqual(1.0, prior!.SalienceWeight, "the prior weight, for a revert callback to restore");
        var current = await store.TryGetAsync(key);
        Assert.AreEqual(0.3, current!.SalienceWeight);
    }

    [TestMethod]
    public async Task AdjustSalienceAsync_UnknownKey_ReturnsNull_DoesNotThrow()
    {
        var store = CreateStore();

        var result = await store.AdjustSalienceAsync(new XFactorMemoryKey("sight", "never-seen"), 0.3);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task Persistence_SnapshotPlusJsonlReplay_SurvivesRestart()
    {
        var key = new XFactorMemoryKey("sight", "unknown-eyes");
        var t0 = DateTimeOffset.UtcNow;

        var first = CreateStore();
        await first.RecordSightingAsync(key, t0);
        await first.AdjustSalienceAsync(key, 0.4);
        await first.FlushAsync(CancellationToken.None);

        // Lands in JSONL only - after the flush.
        await first.RecordSightingAsync(key, t0.AddSeconds(1));

        var second = CreateStore();
        await second.StartAsync(CancellationToken.None);

        var current = await second.TryGetAsync(key);
        Assert.AreEqual(2, current!.Count);
        Assert.AreEqual(0.4, current.SalienceWeight, "the adjusted weight survives a restart, not just the count");

        await second.StopAsync(CancellationToken.None);
    }

    private FileXFactorMemoryStore CreateStore() => new(
        Options.Create(new XFactorMemoryOptions { BasePath = _tempDir, FlushInterval = TimeSpan.Zero }),
        Options.Create(new AgentProfileOptions()),
        NullLogger<FileXFactorMemoryStore>.Instance);
}
