using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;

namespace RockBot.Stimulus.Tests;

[TestClass]
public class StimulusDispatcherTests
{
    private static readonly AgentIdentity Agent = new("test-agent");

    private static StimulusPattern NewPattern(
        string id = "p1",
        string topic = "tool.invoke.failed",
        string reflexTool = "reflex__calendar__retry",
        bool enabled = true,
        IReadOnlyDictionary<string, string>? channels = null) => new()
    {
        Id = id,
        Topic = topic,
        Enabled = enabled,
        Channels = channels ?? new Dictionary<string, string>(),
        ReflexTool = reflexTool,
    };

    private static MessageEnvelope NewEnvelope(string source = "calendar-mcp") => new()
    {
        MessageId = "m1",
        MessageType = "tool.invoke.failed",
        Source = source,
        Timestamp = DateTimeOffset.UtcNow,
        Body = ReadOnlyMemory<byte>.Empty,
    };

    [TestMethod]
    public async Task StartAsync_SubscribesOneTopicPerDistinctEnabledPatternTopic()
    {
        var holder = new StimulusPatternHolder();
        holder.Update([NewPattern(id: "a", topic: "t1"), NewPattern(id: "b", topic: "t1"), NewPattern(id: "c", topic: "t2")]);
        var subscriber = new FakeMessageSubscriber();
        var dispatcher = NewDispatcher(holder, subscriber, new FakeToolRegistry());

        await dispatcher.StartAsync(CancellationToken.None);

        CollectionAssert.AreEquivalent(new[] { "t1", "t2" }, subscriber.SubscribedTopics.ToArray());
    }

    [TestMethod]
    public async Task StartAsync_DisabledPattern_IsNotSubscribed()
    {
        var holder = new StimulusPatternHolder();
        holder.Update([NewPattern(id: "a", topic: "t1", enabled: false)]);
        var subscriber = new FakeMessageSubscriber();
        var dispatcher = NewDispatcher(holder, subscriber, new FakeToolRegistry());

        await dispatcher.StartAsync(CancellationToken.None);

        Assert.AreEqual(0, subscriber.SubscribedTopics.Count);
    }

    [TestMethod]
    public async Task OnPatternsUpdated_NewTopicAdded_SubscribesIt()
    {
        var holder = new StimulusPatternHolder();
        holder.Update([NewPattern(id: "a", topic: "t1")]);
        var subscriber = new FakeMessageSubscriber();
        var dispatcher = NewDispatcher(holder, subscriber, new FakeToolRegistry());
        await dispatcher.StartAsync(CancellationToken.None);

        holder.Update([NewPattern(id: "a", topic: "t1"), NewPattern(id: "b", topic: "t2")]);
        await WaitForAsync(() => subscriber.SubscribedTopics.Contains("t2"));

        CollectionAssert.AreEquivalent(new[] { "t1", "t2" }, subscriber.SubscribedTopics.ToArray());
    }

    [TestMethod]
    public async Task OnPatternsUpdated_TopicNoLongerNeeded_Unsubscribes()
    {
        var holder = new StimulusPatternHolder();
        holder.Update([NewPattern(id: "a", topic: "t1"), NewPattern(id: "b", topic: "t2")]);
        var subscriber = new FakeMessageSubscriber();
        var dispatcher = NewDispatcher(holder, subscriber, new FakeToolRegistry());
        await dispatcher.StartAsync(CancellationToken.None);

        holder.Update([NewPattern(id: "a", topic: "t1")]); // "t2" no longer referenced
        await WaitForAsync(() => !subscriber.SubscribedTopics.Contains("t2"));

        CollectionAssert.AreEquivalent(new[] { "t1" }, subscriber.SubscribedTopics.ToArray());
        Assert.IsTrue(subscriber.Subscriptions["t2"].Disposed, "the old subscription must actually be disposed, not just forgotten");
    }

    [TestMethod]
    public async Task InboundMessage_MatchesPattern_InvokesReflexExecutorDirectly_NoSessionId()
    {
        var holder = new StimulusPatternHolder();
        holder.Update([NewPattern(reflexTool: "reflex__calendar__retry")]);
        var subscriber = new FakeMessageSubscriber();
        var registry = new FakeToolRegistry();
        var executor = new RecordingToolExecutor();
        registry.Register(
            new ToolRegistration { Name = "reflex__calendar__retry", Description = "d", Source = "reflex" }, executor);
        var dispatcher = NewDispatcher(holder, subscriber, registry);
        await dispatcher.StartAsync(CancellationToken.None);

        var result = await subscriber.Subscriptions["tool.invoke.failed"].Handler(NewEnvelope(), CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, result);
        Assert.AreEqual(1, executor.Requests.Count);
        Assert.AreEqual("reflex__calendar__retry", executor.Requests[0].ToolName);
        Assert.IsNull(executor.Requests[0].SessionId, "a stimulus fire has no owning chat session");
    }

    [TestMethod]
    public async Task InboundMessage_MatchesPattern_SetsStimulusInvocationContextDuringTheCall()
    {
        var holder = new StimulusPatternHolder();
        holder.Update([NewPattern(id: "calendar-retry", reflexTool: "reflex__calendar__retry")]);
        var subscriber = new FakeMessageSubscriber();
        var registry = new FakeToolRegistry();
        var executor = new RecordingToolExecutor();
        registry.Register(
            new ToolRegistration { Name = "reflex__calendar__retry", Description = "d", Source = "reflex" }, executor);
        var dispatcher = NewDispatcher(holder, subscriber, registry);
        await dispatcher.StartAsync(CancellationToken.None);

        await subscriber.Subscriptions["tool.invoke.failed"].Handler(NewEnvelope(), CancellationToken.None);

        Assert.AreEqual("calendar-retry", executor.ObservedInvocation!.PatternId);
        Assert.IsNull(StimulusInvocationContext.Value, "the ambient scope must not leak past the call");
    }

    [TestMethod]
    public async Task InboundMessage_NoPatternMatches_AcksWithoutInvokingAnything()
    {
        var holder = new StimulusPatternHolder();
        holder.Update([NewPattern(channels: new Dictionary<string, string> { ["source"] = "weather-mcp" })]);
        var subscriber = new FakeMessageSubscriber();
        var registry = new FakeToolRegistry();
        var executor = new RecordingToolExecutor();
        registry.Register(new ToolRegistration { Name = "reflex__calendar__retry", Description = "d", Source = "reflex" }, executor);
        var dispatcher = NewDispatcher(holder, subscriber, registry);
        await dispatcher.StartAsync(CancellationToken.None);

        var result = await subscriber.Subscriptions["tool.invoke.failed"].Handler(NewEnvelope(source: "calendar-mcp"), CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, result);
        Assert.AreEqual(0, executor.Requests.Count);
    }

    [TestMethod]
    public async Task InboundMessage_ReflexToolNotRegistered_LogsAndAcks_DoesNotThrow()
    {
        var holder = new StimulusPatternHolder();
        holder.Update([NewPattern(reflexTool: "reflex__calendar__retry")]);
        var subscriber = new FakeMessageSubscriber();
        var dispatcher = NewDispatcher(holder, subscriber, new FakeToolRegistry()); // nothing registered
        await dispatcher.StartAsync(CancellationToken.None);

        var result = await subscriber.Subscriptions["tool.invoke.failed"].Handler(NewEnvelope(), CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, result);
    }

    [TestMethod]
    public async Task InboundMessage_ExecutorThrows_StillAcksTheBusMessage()
    {
        var holder = new StimulusPatternHolder();
        holder.Update([NewPattern(reflexTool: "reflex__calendar__retry")]);
        var subscriber = new FakeMessageSubscriber();
        var registry = new FakeToolRegistry();
        registry.Register(
            new ToolRegistration { Name = "reflex__calendar__retry", Description = "d", Source = "reflex" },
            new ThrowingToolExecutor());
        var dispatcher = NewDispatcher(holder, subscriber, registry);
        await dispatcher.StartAsync(CancellationToken.None);

        var result = await subscriber.Subscriptions["tool.invoke.failed"].Handler(NewEnvelope(), CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, result, "the bus message itself is legitimate even when the reflex call fails");
    }

    [TestMethod]
    public async Task InboundMessage_XFactorStoreThrowsDuringMatching_StillAcks_DoesNotDeadLetter()
    {
        var pattern = NewPattern(reflexTool: "reflex__calendar__retry") with
        {
            XFactor = new StimulusXFactorSlot { Channel = "payload:eyes" },
        };
        var holder = new StimulusPatternHolder();
        holder.Update([pattern]);
        var subscriber = new FakeMessageSubscriber();
        var dispatcher = new StimulusDispatcher(
            holder, subscriber, new FakeToolRegistry(), Agent, NullLogger<StimulusDispatcher>.Instance,
            xFactorStore: new ThrowingXFactorMemoryStore());
        await dispatcher.StartAsync(CancellationToken.None);

        var envelope = new MessageEnvelope
        {
            MessageId = "m1",
            MessageType = "tool.invoke.failed",
            Source = "calendar-mcp",
            Timestamp = DateTimeOffset.UtcNow,
            Body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("""{ "eyes": "unknown" }""")),
        };

        var result = await subscriber.Subscriptions["tool.invoke.failed"].Handler(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, result,
            "a transient store I/O failure must not dead-letter an otherwise-legitimate bus message");
    }

    [TestMethod]
    public async Task InboundMessage_WithinMinIntervalOfLastFire_IsThrottled_StillAcks()
    {
        var pattern = NewPattern(reflexTool: "reflex__calendar__retry") with { MinIntervalSeconds = 60 };
        var holder = new StimulusPatternHolder();
        holder.Update([pattern]);
        var subscriber = new FakeMessageSubscriber();
        var registry = new FakeToolRegistry();
        var executor = new RecordingToolExecutor();
        registry.Register(new ToolRegistration { Name = "reflex__calendar__retry", Description = "d", Source = "reflex" }, executor);
        var dispatcher = NewDispatcher(holder, subscriber, registry);
        await dispatcher.StartAsync(CancellationToken.None);

        var first = await subscriber.Subscriptions["tool.invoke.failed"].Handler(NewEnvelope(), CancellationToken.None);
        var second = await subscriber.Subscriptions["tool.invoke.failed"].Handler(NewEnvelope(), CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, first);
        Assert.AreEqual(MessageResult.Ack, second, "a throttled message is still legitimate - it's acked, not dead-lettered");
        Assert.AreEqual(1, executor.Requests.Count, "the second fire within MinIntervalSeconds must be throttled");
    }

    [TestMethod]
    public async Task InboundMessage_AfterMinIntervalElapses_FiresAgain()
    {
        var pattern = NewPattern(reflexTool: "reflex__calendar__retry") with { MinIntervalSeconds = 0.05 };
        var holder = new StimulusPatternHolder();
        holder.Update([pattern]);
        var subscriber = new FakeMessageSubscriber();
        var registry = new FakeToolRegistry();
        var executor = new RecordingToolExecutor();
        registry.Register(new ToolRegistration { Name = "reflex__calendar__retry", Description = "d", Source = "reflex" }, executor);
        var dispatcher = NewDispatcher(holder, subscriber, registry);
        await dispatcher.StartAsync(CancellationToken.None);

        await subscriber.Subscriptions["tool.invoke.failed"].Handler(NewEnvelope(), CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(0.1));
        await subscriber.Subscriptions["tool.invoke.failed"].Handler(NewEnvelope(), CancellationToken.None);

        Assert.AreEqual(2, executor.Requests.Count, "once the interval elapses, the pattern must be able to fire again");
    }

    [TestMethod]
    public async Task InboundMessage_NoMinIntervalConfigured_FiresEveryTime()
    {
        var pattern = NewPattern(reflexTool: "reflex__calendar__retry"); // MinIntervalSeconds null
        var holder = new StimulusPatternHolder();
        holder.Update([pattern]);
        var subscriber = new FakeMessageSubscriber();
        var registry = new FakeToolRegistry();
        var executor = new RecordingToolExecutor();
        registry.Register(new ToolRegistration { Name = "reflex__calendar__retry", Description = "d", Source = "reflex" }, executor);
        var dispatcher = NewDispatcher(holder, subscriber, registry);
        await dispatcher.StartAsync(CancellationToken.None);

        await subscriber.Subscriptions["tool.invoke.failed"].Handler(NewEnvelope(), CancellationToken.None);
        await subscriber.Subscriptions["tool.invoke.failed"].Handler(NewEnvelope(), CancellationToken.None);

        Assert.AreEqual(2, executor.Requests.Count);
    }

    [TestMethod]
    public async Task ReconcileAsync_CalledAfterStopAsync_DoesNotResubscribe()
    {
        // Simulates the exact race the finding describes: a reload's ReconcileSafelyAsync
        // queues behind StopAsync's lock, StopAsync wins and clears the subscription map, then
        // the queued reconcile finally runs. It must see the dispatcher as stopped and do
        // nothing - not treat every enabled pattern's topic as newly-needed and resubscribe,
        // leaking a bus subscription for the rest of the process's life.
        var holder = new StimulusPatternHolder();
        holder.Update([NewPattern(id: "a", topic: "t1")]);
        var subscriber = new FakeMessageSubscriber();
        var dispatcher = NewDispatcher(holder, subscriber, new FakeToolRegistry());
        await dispatcher.StartAsync(CancellationToken.None);
        Assert.IsTrue(subscriber.SubscribedTopics.Contains("t1"), "precondition");

        await dispatcher.StopAsync(CancellationToken.None);
        Assert.IsTrue(subscriber.Subscriptions["t1"].Disposed, "precondition: stop tore down the subscription");

        // The reconcile that "arrives late" after Stop has already completed.
        await dispatcher.ReconcileAsync(CancellationToken.None);

        Assert.AreEqual(0, subscriber.SubscribedTopics.Count,
            "a reconcile arriving after Stop must not resubscribe anything");
    }

    [TestMethod]
    public async Task StopAsync_DisposesAllSubscriptions()
    {
        var holder = new StimulusPatternHolder();
        holder.Update([NewPattern(id: "a", topic: "t1")]);
        var subscriber = new FakeMessageSubscriber();
        var dispatcher = NewDispatcher(holder, subscriber, new FakeToolRegistry());
        await dispatcher.StartAsync(CancellationToken.None);

        await dispatcher.StopAsync(CancellationToken.None);

        Assert.IsTrue(subscriber.Subscriptions["t1"].Disposed);
    }

    [TestMethod]
    public async Task DIResolved_HonorsConfiguredXFactorMemoryOptions_NotBuiltInDefaults()
    {
        // StimulusDispatcher's own ctor param is a bare XFactorMemoryOptions; only
        // IOptions<XFactorMemoryOptions> is ever registered by WithXFactorMemory. If DI can't
        // bridge that, this falls back to the 24h built-in default recency window regardless
        // of what's configured - this test configures 1h instead, so a 2h-old sighting must
        // be read as novel (not blocked) only if the configured value actually made it through.
        var services = new ServiceCollection();
        services.Configure<XFactorMemoryOptions>(o => o.DefaultRecencyWindow = TimeSpan.FromHours(1));

        var pattern = NewPattern(reflexTool: "reflex__test__demo") with
        {
            XFactor = new StimulusXFactorSlot { Channel = "payload:eyes" },
        };
        var holder = new StimulusPatternHolder();
        holder.Update([pattern]);
        services.AddSingleton(holder);

        var subscriber = new FakeMessageSubscriber();
        services.AddSingleton<IMessageSubscriber>(subscriber);

        var executor = new RecordingToolExecutor();
        var registry = new FakeToolRegistry();
        registry.Register(new ToolRegistration { Name = "reflex__test__demo", Description = "d", Source = "reflex" }, executor);
        services.AddSingleton<IToolRegistry>(registry);

        services.AddSingleton(Agent);
        services.AddSingleton<ILogger<StimulusDispatcher>>(NullLogger<StimulusDispatcher>.Instance);

        var xFactorStore = new FakeXFactorMemoryStore();
        var key = new XFactorMemoryKey("payload:eyes", "known-set");
        xFactorStore.Seed(key, count: 3, firstSeen: DateTimeOffset.UtcNow.AddDays(-1),
            lastSeen: DateTimeOffset.UtcNow.AddHours(-2), weight: 1.0);
        services.AddSingleton<IXFactorMemoryStore>(xFactorStore);

        using var provider = services.BuildServiceProvider();
        // The exact resolution mechanism AddHostedService<StimulusDispatcher>() uses internally.
        var dispatcher = ActivatorUtilities.CreateInstance<StimulusDispatcher>(provider);
        await dispatcher.StartAsync(CancellationToken.None);

        var envelope = new MessageEnvelope
        {
            MessageId = "m1",
            MessageType = "tool.invoke.failed",
            Source = "calendar-mcp",
            Timestamp = DateTimeOffset.UtcNow,
            Body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("""{ "eyes": "known-set" }""")),
        };
        await subscriber.Subscriptions["tool.invoke.failed"].Handler(envelope, CancellationToken.None);

        Assert.AreEqual(1, executor.Requests.Count,
            "a 2h-old sighting is novel under the configured 1h window - the configured IOptions value must have reached the dispatcher");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.IsTrue(condition(), "condition did not become true within timeout");
    }

    private static StimulusDispatcher NewDispatcher(
        StimulusPatternHolder holder, FakeMessageSubscriber subscriber, IToolRegistry registry) =>
        new(holder, subscriber, registry, Agent, NullLogger<StimulusDispatcher>.Instance);
}

internal sealed class RecordingToolExecutor : IToolExecutor
{
    public List<ToolInvokeRequest> Requests { get; } = [];
    public StimulusInvocation? ObservedInvocation { get; private set; }

    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        ObservedInvocation = StimulusInvocationContext.Value;
        return Task.FromResult(new ToolInvokeResponse { ToolCallId = request.ToolCallId, ToolName = request.ToolName });
    }
}

internal sealed class ThrowingToolExecutor : IToolExecutor
{
    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("simulated executor failure");
}

internal sealed class FakeToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, (ToolRegistration Registration, IToolExecutor Executor)> _tools = new();

    public IReadOnlyList<ToolRegistration> GetTools() => _tools.Values.Select(t => t.Registration).ToList();
    public IToolExecutor? GetExecutor(string toolName) => _tools.TryGetValue(toolName, out var e) ? e.Executor : null;

    public void Register(ToolRegistration registration, IToolExecutor executor) =>
        _tools[registration.Name] = (registration, executor);

    public bool Unregister(string toolName) => _tools.Remove(toolName);
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

internal sealed class FakeMessageSubscriber : IMessageSubscriber
{
    public Dictionary<string, FakeSubscription> Subscriptions { get; } = new();
    public IReadOnlyCollection<string> SubscribedTopics => Subscriptions.Where(kv => !kv.Value.Disposed).Select(kv => kv.Key).ToList();

    public Task<ISubscription> SubscribeAsync(
        string topic,
        string subscriptionName,
        Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
        CancellationToken cancellationToken = default,
        int dispatchConcurrency = 1)
    {
        var sub = new FakeSubscription(topic, subscriptionName, handler);
        Subscriptions[topic] = sub;
        return Task.FromResult<ISubscription>(sub);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeSubscription(
    string topic, string subscriptionName, Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler) : ISubscription
{
    public string Topic => topic;
    public string SubscriptionName => subscriptionName;
    public bool IsActive => !Disposed;
    public bool Disposed { get; private set; }
    public Func<MessageEnvelope, CancellationToken, Task<MessageResult>> Handler => handler;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
