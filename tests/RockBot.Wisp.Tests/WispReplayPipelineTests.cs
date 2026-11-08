using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Tools;

namespace RockBot.Wisp.Tests;

[TestClass]
public class WispReplayPipelineTests
{
    [TestMethod]
    public async Task RunAsync_CircuitBreakerTrips_ReturnsTrippedResult_DoesNotExecute()
    {
        var registry = new FakeToolRegistry();
        registry.Register(
            new ToolRegistration { Name = "web_search", Description = "Search", Source = "web" },
            new FakeToolExecutor("search results"));
        var memory = new FakeWorkingMemory();
        var wispOptions = new WispOptions
        {
            DispatchCircuitBreakerEnabled = true,
            DispatchCircuitBreakerMaxPerWindow = 1,
            DispatchCircuitBreakerWindow = TimeSpan.FromMinutes(5),
        };
        var wispExecutor = new WispExecutor(
            registry, memory, agentLoopRunner: null!, wispOptions, NullLogger<WispExecutor>.Instance);
        var circuitBreaker = new WispDispatchCircuitBreaker(wispOptions);

        var definition = new WispDefinition
        {
            Description = "Simple search",
            Steps =
            [
                new WispStep
                {
                    Id = "search",
                    Mode = StepMode.Direct,
                    Gateway = GatewayType.Web,
                    Tool = "web_search",
                    Params = JsonSerializer.SerializeToElement(new { query = "test" }),
                },
            ],
        };

        var prepared = WispReplayPipeline.Prepare(definition, mcpToolDirectory: null);
        circuitBreaker.Admit(prepared.DefinitionHash); // consumes the one allowed slot

        var (result, tripped) = await WispReplayPipeline.RunAsync(
            wispExecutor, prepared, "wisp-test1", parentSessionId: null, circuitBreaker,
            NullLogger.Instance, CancellationToken.None);

        Assert.IsTrue(tripped, "the second Admit (inside RunAsync) must trip with MaxPerWindow=1");
        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("circuit-breaker", result.FailedStep?.StepId,
            "a real execution would never produce this StepId — proves ExecuteAsync was never reached");
    }
}
