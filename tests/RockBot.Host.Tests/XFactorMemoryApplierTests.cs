using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

[TestClass]
public class XFactorMemoryApplierTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static RepairTicket NewTicket(object change) => new(
        Id: "t1",
        PatternKey: "x-factor|sight|unknown-eyes",
        Target: RepairTarget.XFactorMemory,
        Change: JsonSerializer.SerializeToElement(change, JsonOptions),
        Verify: new VerifyShape("svr", "tool", JsonDocument.Parse("{}").RootElement,
            new VerifyExpectation(VerifyExpectationKind.Success)),
        Attempts: [],
        Status: RepairStatus.Open,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow);

    [TestMethod]
    public void Target_IsXFactorMemory()
    {
        var applier = NewApplier(out _);
        Assert.AreEqual(RepairTarget.XFactorMemory, applier.Target);
    }

    [TestMethod]
    public async Task ApplyAsync_DownWeight_MultipliesCurrentWeight()
    {
        var applier = NewApplier(out var store);
        var key = new XFactorMemoryKey("sight", "unknown-eyes");
        await store.RecordSightingAsync(key, DateTimeOffset.UtcNow);

        await applier.ApplyAsync(NewTicket(new { channel = "sight", value = "unknown-eyes", op = "down-weight" }), CancellationToken.None);

        var current = await store.TryGetAsync(key);
        Assert.AreEqual(0.5, current!.SalienceWeight, 1e-9, "default down-weight factor is 0.5 against the default starting weight of 1.0");
    }

    [TestMethod]
    public async Task ApplyAsync_DownWeight_WithExplicitFactor_UsesIt()
    {
        var applier = NewApplier(out var store);
        var key = new XFactorMemoryKey("sight", "unknown-eyes");
        await store.RecordSightingAsync(key, DateTimeOffset.UtcNow);

        await applier.ApplyAsync(
            NewTicket(new { channel = "sight", value = "unknown-eyes", op = "down-weight", factor = 0.1 }), CancellationToken.None);

        var current = await store.TryGetAsync(key);
        Assert.AreEqual(0.1, current!.SalienceWeight, 1e-9);
    }

    [TestMethod]
    public async Task ApplyAsync_DownWeight_NeverGoesBelowTheFloor()
    {
        var applier = NewApplier(out var store, floor: 0.2);
        var key = new XFactorMemoryKey("sight", "unknown-eyes");
        await store.RecordSightingAsync(key, DateTimeOffset.UtcNow);

        await applier.ApplyAsync(
            NewTicket(new { channel = "sight", value = "unknown-eyes", op = "down-weight", factor = 0.01 }), CancellationToken.None);

        var current = await store.TryGetAsync(key);
        Assert.AreEqual(0.2, current!.SalienceWeight, 1e-9, "floored, never driven to near-zero");
    }

    [TestMethod]
    public async Task ApplyAsync_ReturnsRevertCallback_RestoresPriorWeightExactly()
    {
        var applier = NewApplier(out var store);
        var key = new XFactorMemoryKey("sight", "unknown-eyes");
        await store.RecordSightingAsync(key, DateTimeOffset.UtcNow);
        await store.AdjustSalienceAsync(key, 0.77);

        var outcome = await applier.ApplyAsync(
            NewTicket(new { channel = "sight", value = "unknown-eyes", op = "down-weight" }), CancellationToken.None);
        Assert.AreNotEqual(0.77, (await store.TryGetAsync(key))!.SalienceWeight, 1e-9, "precondition: the apply actually changed it");

        await outcome.Revert!(CancellationToken.None);

        var reverted = await store.TryGetAsync(key);
        Assert.AreEqual(0.77, reverted!.SalienceWeight, 1e-9);
    }

    [TestMethod]
    public async Task ApplyAsync_UnknownKey_Throws()
    {
        var applier = NewApplier(out _);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            applier.ApplyAsync(NewTicket(new { channel = "sight", value = "never-seen", op = "down-weight" }), CancellationToken.None));
    }

    [TestMethod]
    public async Task ApplyAsync_UnknownOp_Throws()
    {
        var applier = NewApplier(out var store);
        await store.RecordSightingAsync(new XFactorMemoryKey("sight", "unknown-eyes"), DateTimeOffset.UtcNow);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            applier.ApplyAsync(NewTicket(new { channel = "sight", value = "unknown-eyes", op = "forget" }), CancellationToken.None));
    }

    private static XFactorMemoryApplier NewApplier(out FileXFactorMemoryStore store, double floor = 0.05)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "rockbot-xfactor-applier-" + Guid.NewGuid().ToString("N"));
        var options = new XFactorMemoryOptions { BasePath = tempDir, FlushInterval = TimeSpan.Zero, SalienceWeightFloor = floor };
        store = new FileXFactorMemoryStore(
            Options.Create(options), Options.Create(new AgentProfileOptions()), NullLogger<FileXFactorMemoryStore>.Instance);
        return new XFactorMemoryApplier(store, Options.Create(options), NullLogger<XFactorMemoryApplier>.Instance);
    }
}
