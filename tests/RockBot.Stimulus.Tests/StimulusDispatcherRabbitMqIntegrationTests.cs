using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Messaging.RabbitMQ;
using RockBot.Tools;

namespace RockBot.Stimulus.Tests;

/// <summary>
/// Live end-to-end check for Phase 1 over a real RabbitMQ broker — the one thing the rest of
/// this project's tests (all against a <see cref="FakeMessageSubscriber"/>) cannot prove: that
/// a real AMQP message, routed through a real topic exchange, actually reaches
/// <see cref="StimulusDispatcher"/> and gets matched/dispatched correctly once it's a real,
/// JSON-round-tripped <see cref="MessageEnvelope"/> rather than an in-memory object. Gated by
/// <c>ROCKBOT_RABBITMQ_HOST</c> exactly like <c>RabbitMqIntegrationTests</c> in
/// RockBot.Messaging.Tests; skipped (Inconclusive) when unset.
///
/// Deliberately does not involve <c>ReflexExecutor</c>/<c>WispExecutor</c> — those are already
/// fully unit-tested in RockBot.Tools.Reflex.Tests, and this project has no InternalsVisibleTo
/// access to their internals. The registered "reflex" tool here is a trivial stand-in that
/// records what it was called with, which is enough to prove the dispatcher's real routing and
/// matching logic end to end.
/// </summary>
[TestClass]
public class StimulusDispatcherRabbitMqIntegrationTests : IAsyncDisposable
{
    private readonly ServiceProvider? _provider;
    private readonly bool _rabbitAvailable;
    private string? _stimuliDir;

    public StimulusDispatcherRabbitMqIntegrationTests()
    {
        var host = Environment.GetEnvironmentVariable("ROCKBOT_RABBITMQ_HOST");
        _rabbitAvailable = !string.IsNullOrEmpty(host);
        if (!_rabbitAvailable) return;

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddRockBotRabbitMq(opts =>
        {
            opts.HostName = host!;
            opts.UserName = Environment.GetEnvironmentVariable("ROCKBOT_RABBITMQ_USER") ?? "guest";
            opts.Password = Environment.GetEnvironmentVariable("ROCKBOT_RABBITMQ_PASS") ?? "guest";
            opts.ExchangeName = $"rockbot-test-{Guid.NewGuid():N}";
            opts.DeadLetterExchangeName = $"rockbot-test-dlx-{Guid.NewGuid():N}";
        });
        _provider = services.BuildServiceProvider();
    }

    [TestMethod]
    public async Task RealBusMessage_MatchingPattern_FiresTheRegisteredReflexTool_NoLlmInvolved()
    {
        if (!_rabbitAvailable)
        {
            Assert.Inconclusive("RabbitMQ not available (set ROCKBOT_RABBITMQ_HOST)");
            return;
        }

        var topic = $"test.stimulus.{Guid.NewGuid():N}";
        _stimuliDir = Path.Combine(Path.GetTempPath(), "rockbot-stimulus-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stimuliDir);
        await File.WriteAllTextAsync(Path.Combine(_stimuliDir, "live-check.json"), $$"""
            {
              "id": "live-check",
              "enabled": true,
              "topic": "{{topic}}",
              "channels": { "payload:signal": "fire" },
              "reflexTool": "reflex__test__demo"
            }
            """);

        // Real file I/O, the same loader Phase 1's StimulusRegistryLoader uses at startup.
        var patterns = StimulusRegistryLoader.LoadPatternsFromDirectory(_stimuliDir, NullLogger.Instance);
        Assert.AreEqual(1, patterns.Count, "the pattern file must actually parse");

        var holder = new StimulusPatternHolder();
        holder.Update(patterns);

        var recordingExecutor = new RecordingToolExecutor();
        var registry = new FakeToolRegistry();
        registry.Register(
            new ToolRegistration { Name = "reflex__test__demo", Description = "live check target", Source = "reflex" },
            recordingExecutor);

        var subscriber = _provider!.GetRequiredService<IMessageSubscriber>();
        var publisher = _provider!.GetRequiredService<IMessagePublisher>();
        var agentIdentity = new AgentIdentity("live-check-agent");

        var dispatcher = new StimulusDispatcher(
            holder, subscriber, registry, agentIdentity, NullLogger<StimulusDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        try
        {
            var payload = new LiveCheckSignal("fire");
            var envelope = payload.ToEnvelope<LiveCheckSignal>(source: "live-check-publisher");
            await publisher.PublishAsync(topic, envelope);

            var request = await recordingExecutor.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.AreEqual("reflex__test__demo", request.ToolName);
            Assert.IsNull(request.SessionId, "a stimulus fire has no owning chat session");
            Assert.IsNotNull(recordingExecutor.ObservedInvocation, "StimulusInvocationContext must be set during the real call");
            Assert.AreEqual("live-check", recordingExecutor.ObservedInvocation!.PatternId);
            Assert.AreEqual(topic, recordingExecutor.ObservedInvocation.Topic);
            Assert.AreEqual("fire", recordingExecutor.ObservedInvocation.MatchedChannels["payload:signal"]);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
        if (_stimuliDir is not null && Directory.Exists(_stimuliDir))
            Directory.Delete(_stimuliDir, recursive: true);
    }

    private sealed record LiveCheckSignal(string Signal);

    private sealed class RecordingToolExecutor : IToolExecutor
    {
        public TaskCompletionSource<ToolInvokeRequest> Invoked { get; } = new();
        public StimulusInvocation? ObservedInvocation { get; private set; }

        public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
        {
            ObservedInvocation = StimulusInvocationContext.Value;
            Invoked.TrySetResult(request);
            return Task.FromResult(new ToolInvokeResponse { ToolCallId = request.ToolCallId, ToolName = request.ToolName });
        }
    }

    private sealed class FakeToolRegistry : IToolRegistry
    {
        private readonly Dictionary<string, (ToolRegistration Registration, IToolExecutor Executor)> _tools = new();

        public IReadOnlyList<ToolRegistration> GetTools() => _tools.Values.Select(t => t.Registration).ToList();
        public IToolExecutor? GetExecutor(string toolName) => _tools.TryGetValue(toolName, out var e) ? e.Executor : null;

        public void Register(ToolRegistration registration, IToolExecutor executor) =>
            _tools[registration.Name] = (registration, executor);

        public bool Unregister(string toolName) => _tools.Remove(toolName);
    }
}
