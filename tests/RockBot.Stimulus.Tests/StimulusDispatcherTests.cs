using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
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
