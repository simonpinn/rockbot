using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Messaging;
using RockBot.UserProxy;
using RockBot.UserProxy.Blazor.Services;

namespace RockBot.UserProxy.Blazor.Tests;

[TestClass]
public class ReflexActivityProxyServiceTests
{
    [TestMethod]
    public async Task StartAsync_SubscribesToTheAgentScopedReflexFiredTopic()
    {
        var subscriber = new FakeMessageSubscriber();
        var service = NewService(subscriber, out _);

        await service.StartAsync(CancellationToken.None);

        Assert.AreEqual("cognition.reflex.fired.RockBot", subscriber.Subscription!.Topic);
    }

    [TestMethod]
    public async Task InboundReflexFiredEvent_AppendsToStateService()
    {
        var subscriber = new FakeMessageSubscriber();
        var service = NewService(subscriber, out var state);
        await service.StartAsync(CancellationToken.None);

        var evt = new ReflexFiredEvent
        {
            ToolName = "reflex__calendar__retry", SkillName = "calendar/scan", Filename = "retry.json",
            Succeeded = true, FiredAt = DateTimeOffset.UtcNow, DurationMs = 5, Trigger = "stimulus",
        };
        var envelope = evt.ToEnvelope<ReflexFiredEvent>(source: "RockBot");

        var result = await subscriber.Subscription!.Handler(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, result);
        Assert.AreEqual(1, state.Events.Count);
        Assert.AreEqual("reflex__calendar__retry", state.Events[0].ToolName);
    }

    [TestMethod]
    public async Task StopAsync_DisposesTheSubscription()
    {
        var subscriber = new FakeMessageSubscriber();
        var service = NewService(subscriber, out _);
        await service.StartAsync(CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(subscriber.Subscription!.Disposed);
    }

    private static ReflexActivityProxyService NewService(
        FakeMessageSubscriber subscriber, out ReflexActivityStateService state)
    {
        state = new ReflexActivityStateService();
        var options = Options.Create(new UserProxyOptions { AgentName = "RockBot", ProxyId = "blazor-1" });
        return new ReflexActivityProxyService(
            subscriber, options, state, NullLogger<ReflexActivityProxyService>.Instance);
    }
}

internal sealed class FakeMessageSubscriber : IMessageSubscriber
{
    public FakeSubscription? Subscription { get; private set; }

    public Task<ISubscription> SubscribeAsync(
        string topic,
        string subscriptionName,
        Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
        CancellationToken cancellationToken = default,
        int dispatchConcurrency = 1)
    {
        Subscription = new FakeSubscription(topic, subscriptionName, handler);
        return Task.FromResult<ISubscription>(Subscription);
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
