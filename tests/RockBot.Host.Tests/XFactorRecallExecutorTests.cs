using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Tools;

namespace RockBot.Host.Tests;

[TestClass]
public class XFactorRecallExecutorTests
{
    private static ToolInvokeRequest NewRequest() => new() { ToolCallId = "tc-1", ToolName = "recall_x_factor_memory" };

    [TestMethod]
    public async Task ExecuteAsync_NoRecords_ReturnsEmptyMessage_NotAnError()
    {
        var executor = new XFactorRecallExecutor(new FakeXFactorMemoryStore(), NullLogger<XFactorRecallExecutor>.Instance);

        var response = await executor.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content, "No x-factor memories");
    }

    [TestMethod]
    public async Task ExecuteAsync_RecordsExist_ListsEachInTheResponse()
    {
        var store = new FakeXFactorMemoryStore();
        var now = DateTimeOffset.UtcNow;
        store.Seed(new XFactorMemoryKey("sight", "unknown-eyes"), count: 5, firstSeen: now.AddDays(-10), lastSeen: now.AddHours(-1), weight: 1.0);
        var executor = new XFactorRecallExecutor(store, NullLogger<XFactorRecallExecutor>.Instance);

        var response = await executor.ExecuteAsync(NewRequest(), CancellationToken.None);

        StringAssert.Contains(response.Content, "sight");
        StringAssert.Contains(response.Content, "unknown-eyes");
        StringAssert.Contains(response.Content, "5");
    }

    [TestMethod]
    public async Task ExecuteAsync_AmbientLedgerSet_RecordsEveryListedKey()
    {
        var store = new FakeXFactorMemoryStore();
        var now = DateTimeOffset.UtcNow;
        store.Seed(new XFactorMemoryKey("sight", "a"), 1, now, now, 1.0);
        store.Seed(new XFactorMemoryKey("sound", "b"), 1, now, now, 1.0);
        var executor = new XFactorRecallExecutor(store, NullLogger<XFactorRecallExecutor>.Instance);
        var ledger = new XFactorRecallLedger();

        using (XFactorRecallContext.Set(ledger))
            await executor.ExecuteAsync(NewRequest(), CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { ("sight", "a"), ("sound", "b") }, ledger.Recalled.ToArray());
    }

    [TestMethod]
    public async Task ExecuteAsync_NoAmbientLedger_DoesNotThrow()
    {
        var store = new FakeXFactorMemoryStore();
        store.Seed(new XFactorMemoryKey("sight", "a"), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1.0);
        var executor = new XFactorRecallExecutor(store, NullLogger<XFactorRecallExecutor>.Instance);

        var response = await executor.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.IsFalse(response.IsError);
    }

    [TestMethod]
    public async Task ExecuteAsync_StoreThrows_ReturnsErrorResponse_DoesNotThrow()
    {
        var executor = new XFactorRecallExecutor(new ThrowingXFactorMemoryStore(), NullLogger<XFactorRecallExecutor>.Instance);

        var response = await executor.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.IsTrue(response.IsError);
    }
}

internal sealed class ThrowingXFactorMemoryStore : IXFactorMemoryStore
{
    public Task<XFactorMemoryRecord?> RecordSightingAsync(XFactorMemoryKey key, DateTimeOffset at, CancellationToken ct = default) =>
        throw new IOException("simulated store failure");
    public Task<XFactorMemoryRecord?> TryGetAsync(XFactorMemoryKey key, CancellationToken ct = default) =>
        throw new IOException("simulated store failure");
    public Task<IReadOnlyList<XFactorMemoryRecord>> ListAsync(CancellationToken ct = default) =>
        throw new IOException("simulated store failure");
    public Task<XFactorMemoryRecord?> AdjustSalienceAsync(XFactorMemoryKey key, double newWeight, CancellationToken ct = default) =>
        throw new IOException("simulated store failure");
}

internal sealed class FakeXFactorMemoryStore : IXFactorMemoryStore
{
    private readonly Dictionary<XFactorMemoryKey, XFactorMemoryRecord> _records = new();

    public void Seed(XFactorMemoryKey key, int count, DateTimeOffset firstSeen, DateTimeOffset lastSeen, double weight) =>
        _records[key] = new XFactorMemoryRecord(key, count, firstSeen, lastSeen, weight);

    public Task<XFactorMemoryRecord?> RecordSightingAsync(XFactorMemoryKey key, DateTimeOffset at, CancellationToken ct = default)
    {
        var prior = _records.TryGetValue(key, out var existing) ? existing : null;
        _records[key] = prior is null
            ? new XFactorMemoryRecord(key, 1, at, at, 1.0)
            : prior with { Count = prior.Count + 1, LastSeen = at };
        return Task.FromResult(prior);
    }

    public Task<XFactorMemoryRecord?> TryGetAsync(XFactorMemoryKey key, CancellationToken ct = default) =>
        Task.FromResult(_records.TryGetValue(key, out var r) ? r : null);

    public Task<IReadOnlyList<XFactorMemoryRecord>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<XFactorMemoryRecord>>(_records.Values.OrderByDescending(r => r.LastSeen).ToList());

    public Task<XFactorMemoryRecord?> AdjustSalienceAsync(XFactorMemoryKey key, double newWeight, CancellationToken ct = default)
    {
        var prior = _records.TryGetValue(key, out var existing) ? existing : null;
        if (prior is not null)
            _records[key] = prior with { SalienceWeight = newWeight };
        return Task.FromResult(prior);
    }
}
