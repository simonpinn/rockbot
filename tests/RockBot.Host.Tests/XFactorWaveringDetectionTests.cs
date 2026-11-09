using Microsoft.Extensions.AI;
using RockBot.Llm;

namespace RockBot.Host.Tests;

/// <summary>
/// The evidence-gathering half of x-factor memory (design/cognitive-tiers.md): a textual
/// detector that records, but never acts on, language that questions or hesitates about an
/// instinct/reflex recommendation.
/// </summary>
[TestClass]
public class XFactorWaveringDetectionTests
{
    [TestMethod]
    [DataRow(false, DisplayName = "native (FICC)")]
    [DataRow(true, DisplayName = "text-based loop")]
    public async Task WaveringText_SetsWaveringXFactorDetected_TextAndControlFlowUnchanged(bool textBased)
    {
        const string waveringText = "I'm not entirely sure this reflex still applies here, but here is the answer.";
        var (diagnostics, text) = await RunAsync(textBased, waveringText, detectWavering: true);

        Assert.IsTrue(diagnostics.WaveringXFactorDetected);
        Assert.AreEqual(waveringText, text, "the detector must not alter the returned text");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "native (FICC)")]
    [DataRow(true, DisplayName = "text-based loop")]
    public async Task OrdinaryText_DoesNotSetWaveringXFactorDetected(bool textBased)
    {
        var (diagnostics, _) = await RunAsync(textBased, "Here is a confident, ordinary answer.", detectWavering: true);

        Assert.IsFalse(diagnostics.WaveringXFactorDetected);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "native (FICC)")]
    [DataRow(true, DisplayName = "text-based loop")]
    public async Task DetectXFactorWaveringDisabled_SkipsTheRegexEntirely(bool textBased)
    {
        const string waveringText = "I'm not entirely sure this reflex still applies here.";
        var (diagnostics, _) = await RunAsync(textBased, waveringText, detectWavering: false);

        Assert.IsFalse(diagnostics.WaveringXFactorDetected);
    }

    [TestMethod]
    public async Task XFactorRecalls_IsSetOnEveryRun_EmptyWhenNothingRecalled()
    {
        var (diagnostics, _) = await RunAsync(textBased: false, "An ordinary answer.", detectWavering: true);

        Assert.IsNotNull(diagnostics.XFactorRecalls);
        Assert.AreEqual(0, diagnostics.XFactorRecalls!.Recalled.Count);
    }

    [TestMethod]
    public async Task ToolCallThatRecallsAnXFactor_IsVisibleOnDiagnosticsAfterTheRun()
    {
        var model = new ToolThenWaveringTextChatClient(
            new FunctionCallContent("call-1", "recall_x_factor_memory", new Dictionary<string, object?>()),
            "I'm not entirely sure this reflex still applies here.");
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() =>
            {
                XFactorRecallContext.Value?.Add("sight", "unknown-eyes");
                return "1 x-factor memory: [sight] \"unknown-eyes\"";
            }, "recall_x_factor_memory")],
        };
        var diagnostics = new LoopDiagnostics();
        var behavior = new ModelBehavior { UseTextBasedToolCalling = false, DetectXFactorWavering = true };
        var runner = ConsequentialActionGateTests.CreateRunner(model, textBased: false, new AgentHostOptions(), behaviorOverride: behavior);

        await runner.RunAsync(
            [new(ChatRole.System, "You are a test agent."), new(ChatRole.User, "Check your x-factor memories.")],
            options, "s1", enableFollowUp: false, enableCompletionEval: false, diagnostics: diagnostics,
            cancellationToken: CancellationToken.None);

        Assert.IsTrue(diagnostics.WaveringXFactorDetected);
        CollectionAssert.AreEquivalent(
            new[] { ("sight", "unknown-eyes") }, diagnostics.XFactorRecalls!.Recalled.ToArray());
    }

    private static async Task<(LoopDiagnostics Diagnostics, string Text)> RunAsync(
        bool textBased, string responseText, bool detectWavering)
    {
        var model = new FixedTextChatClient(responseText);
        var options = new ChatOptions();
        var diagnostics = new LoopDiagnostics();
        var behavior = new ModelBehavior { UseTextBasedToolCalling = textBased, DetectXFactorWavering = detectWavering };
        var runner = ConsequentialActionGateTests.CreateRunner(model, textBased, new AgentHostOptions(), behaviorOverride: behavior);

        var text = await runner.RunAsync(
            [new(ChatRole.System, "You are a test agent."), new(ChatRole.User, "Hello.")],
            options, "s1", enableFollowUp: false, enableCompletionEval: false, diagnostics: diagnostics,
            cancellationToken: CancellationToken.None);

        return (diagnostics, text);
    }

    private sealed class FixedTextChatClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>First call returns a tool call; later calls return wavering text.</summary>
    private sealed class ToolThenWaveringTextChatClient(FunctionCallContent call, string finalText) : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, finalText)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
