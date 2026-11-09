namespace RockBot.Host.Tests;

[TestClass]
public class XFactorEvidenceRecorderTests
{
    [TestMethod]
    public async Task WaveringDetected_WithRecalls_RecordsOneClusterEventPerRecall()
    {
        var store = new RecordingFailureClusterStore();
        var diagnostics = new LoopDiagnostics { WaveringXFactorDetected = true, XFactorRecalls = new XFactorRecallLedger() };
        diagnostics.XFactorRecalls.Add("sight", "unknown-eyes");
        diagnostics.XFactorRecalls.Add("sound", "silence");

        await XFactorEvidenceRecorder.RecordIfWaveringAsync(diagnostics, store, "s1", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.AreEqual(2, store.Recorded.Count);
        Assert.IsTrue(store.Recorded.Any(r => r.Key == new ClusterKey("x-factor", "sight", "unknown-eyes")));
        Assert.IsTrue(store.Recorded.Any(r => r.Key == new ClusterKey("x-factor", "sound", "silence")));
        Assert.IsTrue(store.Recorded.All(r => r.SessionId == "s1"));
    }

    [TestMethod]
    public async Task WaveringDetected_WithNoRecalls_RecordsNothing()
    {
        var store = new RecordingFailureClusterStore();
        var diagnostics = new LoopDiagnostics { WaveringXFactorDetected = true, XFactorRecalls = new XFactorRecallLedger() };

        await XFactorEvidenceRecorder.RecordIfWaveringAsync(diagnostics, store, "s1", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.AreEqual(0, store.Recorded.Count);
    }

    [TestMethod]
    public async Task NoWaveringDetected_RecordsNothing_EvenWithRecalls()
    {
        var store = new RecordingFailureClusterStore();
        var diagnostics = new LoopDiagnostics { WaveringXFactorDetected = false, XFactorRecalls = new XFactorRecallLedger() };
        diagnostics.XFactorRecalls.Add("sight", "unknown-eyes");

        await XFactorEvidenceRecorder.RecordIfWaveringAsync(diagnostics, store, "s1", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.AreEqual(0, store.Recorded.Count);
    }

    [TestMethod]
    public async Task NullDiagnostics_DoesNotThrow()
    {
        var store = new RecordingFailureClusterStore();

        await XFactorEvidenceRecorder.RecordIfWaveringAsync(null, store, "s1", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.AreEqual(0, store.Recorded.Count);
    }

    [TestMethod]
    public async Task NullFailureClusterStore_DoesNotThrow()
    {
        var diagnostics = new LoopDiagnostics { WaveringXFactorDetected = true, XFactorRecalls = new XFactorRecallLedger() };
        diagnostics.XFactorRecalls.Add("sight", "unknown-eyes");

        await XFactorEvidenceRecorder.RecordIfWaveringAsync(diagnostics, null, "s1", DateTimeOffset.UtcNow, CancellationToken.None);
        // no assert needed beyond "did not throw"
    }

    [TestMethod]
    public async Task StoreThrows_IsSwallowed_DoesNotPropagate()
    {
        // Best-effort telemetry: a failure here must never fail the turn it's about.
        var diagnostics = new LoopDiagnostics { WaveringXFactorDetected = true, XFactorRecalls = new XFactorRecallLedger() };
        diagnostics.XFactorRecalls.Add("sight", "unknown-eyes");

        await XFactorEvidenceRecorder.RecordIfWaveringAsync(
            diagnostics, new ThrowingFailureClusterStore(), "s1", DateTimeOffset.UtcNow, CancellationToken.None);
        // no assert needed beyond "did not throw"
    }

    [TestMethod]
    public async Task NullSessionId_StillRecords()
    {
        // A stimulus-fired reflex's own evidence has no session - still legitimate evidence.
        var store = new RecordingFailureClusterStore();
        var diagnostics = new LoopDiagnostics { WaveringXFactorDetected = true, XFactorRecalls = new XFactorRecallLedger() };
        diagnostics.XFactorRecalls.Add("sight", "unknown-eyes");

        await XFactorEvidenceRecorder.RecordIfWaveringAsync(diagnostics, store, null, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.AreEqual(1, store.Recorded.Count);
        Assert.IsNull(store.Recorded[0].SessionId);
    }
}

internal sealed class RecordingFailureClusterStore : IFailureClusterStore
{
    public List<(ClusterKey Key, string? SessionId, string ErrorMessage, DateTimeOffset At)> Recorded { get; } = [];

    public Task RecordAsync(ClusterKey key, string? sessionId, string errorMessage, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        Recorded.Add((key, sessionId, errorMessage, at));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FailureCluster>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FailureCluster>>([]);

    public Task<IReadOnlyList<FailureCluster>> GetEscalatableAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FailureCluster>>([]);
}

internal sealed class ThrowingFailureClusterStore : IFailureClusterStore
{
    public Task RecordAsync(ClusterKey key, string? sessionId, string errorMessage, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        throw new IOException("simulated store failure");

    public Task<IReadOnlyList<FailureCluster>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FailureCluster>>([]);

    public Task<IReadOnlyList<FailureCluster>> GetEscalatableAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<FailureCluster>>([]);
}
