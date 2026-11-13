using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RockBot.Host.Tests;

/// <summary>
/// IDreamCycleTrigger's orchestration behavior (Disabled/AlreadyRunning/Busy/Started
/// outcomes). The inner pass logic these triggers call into (candidate selection,
/// apply, provisional-validation decisions) is covered separately by
/// DreamServiceWispAnalysisTriggerTests, DreamServiceWispSuccessTests and
/// SkillResourceApplierTests — these tests only exercise the new entry points.
/// </summary>
[TestClass]
public class DreamServiceTriggerTests
{
    private static readonly LlmCostEstimator SharedCostEstimator = new(
        Options.Create(new LlmPricingOptions { ConfigPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json") }),
        NullLogger<LlmCostEstimator>.Instance);

    private static DreamService CreateService(
        IAgentWorkSerializer workSerializer,
        bool enabled = true,
        IWispExecutionLog? wispLog = null,
        ISkillStore? skillStore = null)
    {
        var service = new DreamService(
            new EmptyLongTermMemory(),
            skillStore is null ? [] : [skillStore],
            new UnusedLlmClient(),
            SharedCostEstimator,
            workSerializer,
            new StubActivityMonitor(),
            new AgentClock(
                new ConfigurationBuilder().Build(),
                Options.Create(new AgentProfileOptions()),
                NullLogger<AgentClock>.Instance),
            Options.Create(new DreamOptions { Enabled = enabled }),
            Options.Create(new AgentProfileOptions()),
            NullLogger<DreamService>.Instance,
            wispExecutionLog: wispLog);
        return service;
    }

    [TestMethod]
    public async Task TriggerFullCycleAsync_DreamingDisabled_ReturnsDisabled()
    {
        var service = CreateService(new AlwaysFreeWorkSerializer(), enabled: false);

        var outcome = await ((IDreamCycleTrigger)service).TriggerFullCycleAsync();

        Assert.AreEqual(DreamTriggerOutcome.Disabled, outcome);
    }

    [TestMethod]
    public async Task TriggerFullCycleAsync_NoCycleInFlight_ReturnsStarted()
    {
        var service = CreateService(new AlwaysFreeWorkSerializer());

        var outcome = await ((IDreamCycleTrigger)service).TriggerFullCycleAsync();

        Assert.AreEqual(DreamTriggerOutcome.Started, outcome);
    }

    [TestMethod]
    public async Task TriggerFullCycleAsync_CycleAlreadyInFlight_ReturnsAlreadyRunning()
    {
        var blocking = new BlockingWorkSerializer();
        var service = (IDreamCycleTrigger)CreateService(blocking);

        var first = await service.TriggerFullCycleAsync();
        Assert.AreEqual(DreamTriggerOutcome.Started, first);

        var second = await service.TriggerFullCycleAsync();
        Assert.AreEqual(DreamTriggerOutcome.AlreadyRunning, second);

        blocking.Release();
        await blocking.Completed;
    }

    [TestMethod]
    public async Task TriggerWispAnalysisAsync_DreamingDisabled_DoesNotRun()
    {
        var service = (IDreamCycleTrigger)CreateService(new AlwaysFreeWorkSerializer(), enabled: false);

        var outcome = await service.TriggerWispAnalysisAsync();

        Assert.IsFalse(outcome.Ran);
        Assert.IsNotNull(outcome.SkippedReason);
    }

    [TestMethod]
    public async Task TriggerWispAnalysisAsync_AgentBusy_DoesNotRun()
    {
        var service = (IDreamCycleTrigger)CreateService(new AlwaysBusyWorkSerializer());

        var outcome = await service.TriggerWispAnalysisAsync();

        Assert.IsFalse(outcome.Ran);
        Assert.IsNotNull(outcome.SkippedReason);
    }

    [TestMethod]
    public async Task TriggerWispAnalysisAsync_AgentFree_RunsAndReturnsZeroedResultWhenNothingToDo()
    {
        var service = (IDreamCycleTrigger)CreateService(
            new AlwaysFreeWorkSerializer(),
            wispLog: new StubWispExecutionLog([]),
            skillStore: new EmptySkillStore());

        var outcome = await service.TriggerWispAnalysisAsync();

        Assert.IsTrue(outcome.Ran);
        Assert.IsNull(outcome.SkippedReason);
        Assert.AreEqual(0, outcome.CandidatesAttached);
        Assert.AreEqual(0, outcome.ProvisionalConsidered);
    }

    private sealed class UnusedLlmClient : ILlmClient
    {
        public bool IsIdle => true;
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No LLM call expected in these tests");
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No LLM call expected in these tests");
    }

    private sealed class StubActivityMonitor : IUserActivityMonitor
    {
        public void RecordActivity() { }
        public bool IsUserActive(TimeSpan idleThreshold) => false;
    }

    private sealed class EmptyLongTermMemory : ILongTermMemory
    {
        public Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        public Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<MemoryEntry?>(null);
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubWispExecutionLog(IReadOnlyList<WispExecutionRecord> records) : IWispExecutionLog
    {
        public Task AppendAsync(WispExecutionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<WispExecutionRecord>> QueryRecentAsync(
            DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
            Task.FromResult(records);
        public Task<WispExecutionRecord?> FindRecentFailureAsync(
            string definitionHash, string? sessionId, CancellationToken ct = default) =>
            Task.FromResult<WispExecutionRecord?>(null);
    }

    private sealed class EmptySkillStore : ISkillStore
    {
        public Task SaveAsync(Skill skill) => Task.CompletedTask;
        public Task SaveAsync(Skill skill, IReadOnlyList<SkillResourceInput>? resources) => Task.CompletedTask;
        public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
        public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task DeleteAsync(string name) => Task.CompletedTask;
        public Task<IReadOnlyList<Skill>> SearchAsync(
            string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
            Task.FromResult<IReadOnlyList<Skill>>([]);
        public Task<string?> GetResourceAsync(string skillName, string filename) => Task.FromResult<string?>(null);
        public Task<bool> AttachResourceAsync(
            string skillName, SkillResourceInput resource, SkillResource? manifestEntry = null) =>
            Task.FromResult(false);
    }

    /// <summary>Always grants the slot immediately — mirrors SchedulerServiceTests' own double.</summary>
    private sealed class AlwaysFreeWorkSerializer : IAgentWorkSerializer
    {
        public Task<IAsyncDisposable> AcquireForUserAsync(CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable>(new NullSlot());
        public Task<IScheduledTaskSlot?> TryAcquireForScheduledAsync(CancellationToken ct) =>
            Task.FromResult<IScheduledTaskSlot?>(new Slot(ct));

        private sealed class NullSlot : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Slot(CancellationToken token) : IScheduledTaskSlot
        {
            public CancellationToken Token { get; } = token;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Always reports the slot busy — mirrors SchedulerServiceTests' own double.</summary>
    private sealed class AlwaysBusyWorkSerializer : IAgentWorkSerializer
    {
        public Task<IAsyncDisposable> AcquireForUserAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Not used in this test");
        public Task<IScheduledTaskSlot?> TryAcquireForScheduledAsync(CancellationToken ct) =>
            Task.FromResult<IScheduledTaskSlot?>(null);
    }

    /// <summary>
    /// Blocks TryAcquireForScheduledAsync until Release() is called, so a test can
    /// deterministically observe "a cycle is in flight" before letting it finish.
    /// </summary>
    private sealed class BlockingWorkSerializer : IAgentWorkSerializer
    {
        private readonly TaskCompletionSource _gate = new();
        private readonly TaskCompletionSource _completed = new();
        public Task Completed => _completed.Task;

        public Task<IAsyncDisposable> AcquireForUserAsync(CancellationToken ct) =>
            throw new InvalidOperationException("Not used in this test");

        public async Task<IScheduledTaskSlot?> TryAcquireForScheduledAsync(CancellationToken ct)
        {
            await _gate.Task;
            return new Slot(ct, _completed);
        }

        public void Release() => _gate.TrySetResult();

        private sealed class Slot(CancellationToken token, TaskCompletionSource completed) : IScheduledTaskSlot
        {
            public CancellationToken Token { get; } = token;
            public ValueTask DisposeAsync()
            {
                completed.TrySetResult();
                return ValueTask.CompletedTask;
            }
        }
    }
}
