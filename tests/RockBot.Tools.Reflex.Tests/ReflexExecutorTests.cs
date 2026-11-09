using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Wisp;

namespace RockBot.Tools.Reflex.Tests;

[TestClass]
public class ReflexExecutorTests
{
    private const string ToolName = "reflex__calendar_scan__fanout.json";
    private const string SkillName = "calendar/scan";
    private const string Filename = "fanout.json";

    private const string ValidWispBody = """
        {
          "description": "Simple search",
          "steps": [
            {
              "id": "search",
              "mode": "direct",
              "gateway": "web",
              "tool": "web_search",
              "params": { "query": "test" }
            }
          ]
        }
        """;

    [TestMethod]
    public async Task ExecuteAsync_ValidatedResource_ReplaysWispAndAppendsExecutionRecord()
    {
        var lookup = new FakeReflexRegistrationLookup((ToolName, SkillName, Filename));
        var skillStore = new FakeSkillStore { Bodies = { [(SkillName, Filename)] = ValidWispBody } };
        var executionLog = new FakeWispExecutionLog();
        var wispExecutor = NewWispExecutor();

        var executor = new ReflexExecutor(
            lookup, skillStore, wispExecutor, executionLog, circuitBreaker: null,
            mcpToolDirectory: null, NullLogger<ReflexExecutor>.Instance);

        var response = await executor.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual(1, executionLog.Records.Count);
        Assert.IsTrue(executionLog.Records[0].Succeeded);
        Assert.IsFalse(string.IsNullOrEmpty(executionLog.Records[0].DefinitionHash));
        StringAssert.Contains(response.Content, "search results",
            "the wisp's actual output must reach the caller, or a reflex call gives the model nothing " +
            "and defeats the point of replaying it directly instead of through spawn_wisps");
    }

    [TestMethod]
    public async Task ExecuteAsync_NullSteps_ReturnsErrorResponse_NoThrow()
    {
        const string nullStepsBody = """{ "description": "d", "steps": null }""";
        var lookup = new FakeReflexRegistrationLookup((ToolName, SkillName, Filename));
        var skillStore = new FakeSkillStore { Bodies = { [(SkillName, Filename)] = nullStepsBody } };
        var executor = new ReflexExecutor(
            lookup, skillStore, NewWispExecutor(), executionLog: null, circuitBreaker: null,
            mcpToolDirectory: null, NullLogger<ReflexExecutor>.Instance);

        var response = await executor.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.IsTrue(response.IsError);
    }

    [TestMethod]
    public async Task ExecuteAsync_ExecutionLogAppendThrows_StillReturnsSuccessResponse()
    {
        // The wisp's work already ran and succeeded by the time AppendAsync is called - a
        // logging failure (I/O error, cancellation) must not be reported back as a tool
        // failure, or the agent may re-run side effects that already happened.
        var lookup = new FakeReflexRegistrationLookup((ToolName, SkillName, Filename));
        var skillStore = new FakeSkillStore { Bodies = { [(SkillName, Filename)] = ValidWispBody } };
        var executionLog = new FakeWispExecutionLog { ThrowOnAppend = true };
        var executor = new ReflexExecutor(
            lookup, skillStore, NewWispExecutor(), executionLog, circuitBreaker: null,
            mcpToolDirectory: null, NullLogger<ReflexExecutor>.Instance);

        var response = await executor.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.IsFalse(response.IsError, response.Content);
    }

    [TestMethod]
    public async Task ExecuteAsync_SuccessfulReplay_ReportsSucceededOutcomeToAmbientContext()
    {
        // Matches spawn_wisps' own #686 contract: the running tool call's ledger/outcome
        // must see the reflex's result, not just the HTTP-shaped ToolInvokeResponse.
        var lookup = new FakeReflexRegistrationLookup((ToolName, SkillName, Filename));
        var skillStore = new FakeSkillStore { Bodies = { [(SkillName, Filename)] = ValidWispBody } };
        var executor = new ReflexExecutor(
            lookup, skillStore, NewWispExecutor(), executionLog: null, circuitBreaker: null,
            mcpToolDirectory: null, NullLogger<ReflexExecutor>.Instance);
        var outcome = new ToolCallOutcome();

        using (ToolCallOutcomeContext.Set(outcome))
            await executor.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.AreEqual(true, outcome.Succeeded);
    }

    [TestMethod]
    public async Task ExecuteAsync_LookupMisses_ReturnsErrorResponse_NoThrow()
    {
        var lookup = new FakeReflexRegistrationLookup(); // nothing registered
        var skillStore = new FakeSkillStore();
        var executor = new ReflexExecutor(
            lookup, skillStore, NewWispExecutor(), executionLog: null, circuitBreaker: null,
            mcpToolDirectory: null, NullLogger<ReflexExecutor>.Instance);

        var response = await executor.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.IsTrue(response.IsError);
    }

    [TestMethod]
    public async Task ExecuteAsync_ResourceBodyMissing_ReturnsErrorResponse_NoThrow()
    {
        var lookup = new FakeReflexRegistrationLookup((ToolName, SkillName, Filename));
        var skillStore = new FakeSkillStore(); // no body stored - deleted between cycles
        var executor = new ReflexExecutor(
            lookup, skillStore, NewWispExecutor(), executionLog: null, circuitBreaker: null,
            mcpToolDirectory: null, NullLogger<ReflexExecutor>.Instance);

        var response = await executor.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.IsTrue(response.IsError);
    }

    [TestMethod]
    public async Task ExecuteAsync_StoredBodyNotValidWispJson_ReturnsErrorResponse_NoThrow()
    {
        var lookup = new FakeReflexRegistrationLookup((ToolName, SkillName, Filename));
        var skillStore = new FakeSkillStore { Bodies = { [(SkillName, Filename)] = "not valid json" } };
        var executor = new ReflexExecutor(
            lookup, skillStore, NewWispExecutor(), executionLog: null, circuitBreaker: null,
            mcpToolDirectory: null, NullLogger<ReflexExecutor>.Instance);

        var response = await executor.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.IsTrue(response.IsError);
    }

    [TestMethod]
    public async Task ExecuteAsync_CircuitBreakerTripped_ReturnsErrorResponse_DoesNotAppendExecutionRecord()
    {
        var lookup = new FakeReflexRegistrationLookup((ToolName, SkillName, Filename));
        var skillStore = new FakeSkillStore { Bodies = { [(SkillName, Filename)] = ValidWispBody } };
        var executionLog = new FakeWispExecutionLog();
        var wispOptions = new WispOptions
        {
            DispatchCircuitBreakerEnabled = true,
            DispatchCircuitBreakerMaxPerWindow = 1,
            DispatchCircuitBreakerWindow = TimeSpan.FromMinutes(5),
        };
        var circuitBreaker = new WispDispatchCircuitBreaker(wispOptions);
        var executor = new ReflexExecutor(
            lookup, skillStore, NewWispExecutor(wispOptions), executionLog, circuitBreaker,
            mcpToolDirectory: null, NullLogger<ReflexExecutor>.Instance);

        // First call consumes the one allowed slot (same body -> same definition hash each time).
        var first = await executor.ExecuteAsync(NewRequest(), CancellationToken.None);
        Assert.IsFalse(first.IsError, first.Content);
        Assert.AreEqual(1, executionLog.Records.Count);

        // Second call, same tool/body, trips the breaker.
        var second = await executor.ExecuteAsync(NewRequest(), CancellationToken.None);

        Assert.IsTrue(second.IsError);
        Assert.AreEqual(1, executionLog.Records.Count, "a tripped dispatch must not append a second record");
    }

    private static ToolInvokeRequest NewRequest() =>
        new() { ToolCallId = "tc-1", ToolName = ToolName, SessionId = "s1" };

    private static WispExecutor NewWispExecutor(WispOptions? options = null)
    {
        var registry = new FakeToolRegistry();
        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("search results"));
        return new WispExecutor(
            registry, new FakeWorkingMemory(), agentLoopRunner: null!,
            options ?? new WispOptions(), NullLogger<WispExecutor>.Instance);
    }
}

internal sealed class FakeReflexRegistrationLookup : IReflexRegistrationLookup
{
    private readonly Dictionary<string, (string SkillName, string Filename)> _byName = new();

    public FakeReflexRegistrationLookup(params (string ToolName, string SkillName, string Filename)[] entries)
    {
        foreach (var (toolName, skillName, filename) in entries)
            _byName[toolName] = (skillName, filename);
    }

    public bool TryGet(string toolName, out string skillName, out string filename)
    {
        if (_byName.TryGetValue(toolName, out var entry))
        {
            (skillName, filename) = entry;
            return true;
        }
        skillName = "";
        filename = "";
        return false;
    }

    public IReadOnlyList<string> GetToolNames(string skillName) =>
        _byName.Where(kv => kv.Value.SkillName == skillName).Select(kv => kv.Key).ToArray();
}

internal sealed class FakeSkillStore : ISkillStore
{
    public Dictionary<(string SkillName, string Filename), string> Bodies { get; } = new();
    public List<Skill> Skills { get; set; } = [];

    public Task<string?> GetResourceAsync(string skillName, string filename) =>
        Task.FromResult(Bodies.TryGetValue((skillName, filename), out var body) ? body : null);

    public Task SaveAsync(Skill skill) => Task.CompletedTask;
    public Task<Skill?> GetAsync(string name) => Task.FromResult<Skill?>(null);
    public Task<IReadOnlyList<Skill>> ListAsync() => Task.FromResult<IReadOnlyList<Skill>>(Skills);
    public Task DeleteAsync(string name) => Task.CompletedTask;
    public Task<IReadOnlyList<Skill>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null) =>
        Task.FromResult<IReadOnlyList<Skill>>([]);
}

internal sealed class FakeWispExecutionLog : IWispExecutionLog
{
    public List<WispExecutionRecord> Records { get; } = [];
    public bool ThrowOnAppend { get; set; }

    public Task AppendAsync(WispExecutionRecord record, CancellationToken ct = default)
    {
        if (ThrowOnAppend)
            throw new IOException("simulated log write failure");
        lock (Records) { Records.Add(record); }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WispExecutionRecord>> QueryRecentAsync(
        DateTimeOffset since, int maxResults, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WispExecutionRecord>>([]);

    public Task<WispExecutionRecord?> FindRecentFailureAsync(
        string definitionHash, string? sessionId, CancellationToken ct = default) =>
        Task.FromResult<WispExecutionRecord?>(null);
}

internal sealed class FakeToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, (ToolRegistration Registration, IToolExecutor Executor)> _tools = new();

    public IReadOnlyList<ToolRegistration> GetTools() => _tools.Values.Select(t => t.Registration).ToList();
    public IToolExecutor? GetExecutor(string toolName) => _tools.TryGetValue(toolName, out var e) ? e.Executor : null;

    public void Register(ToolRegistration registration, IToolExecutor executor)
    {
        // Matches the real RockBot.Tools.ToolRegistry: TryAdd, throw on an existing name.
        if (!_tools.TryAdd(registration.Name, (registration, executor)))
            throw new InvalidOperationException($"Tool '{registration.Name}' is already registered.");
    }

    public bool Unregister(string toolName) => _tools.Remove(toolName);
}

internal sealed class FakeToolExecutor(string? content = null) : IToolExecutor
{
    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct) =>
        Task.FromResult(new ToolInvokeResponse { ToolCallId = request.ToolCallId, ToolName = request.ToolName, Content = content });
}

internal sealed class FakeWorkingMemory : IWorkingMemory
{
    public Task SetAsync(string key, string value, TimeSpan? ttl = null, string? category = null, IReadOnlyList<string>? tags = null) =>
        Task.CompletedTask;
    public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);
    public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null) =>
        Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
    public Task DeleteAsync(string key) => Task.CompletedTask;
    public Task ClearAsync(string? prefix = null) => Task.CompletedTask;
    public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null) =>
        Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);
}
