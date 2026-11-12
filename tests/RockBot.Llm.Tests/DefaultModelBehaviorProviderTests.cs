using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Llm.Tests;

[TestClass]
public class DefaultModelBehaviorProviderTests
{
    [TestMethod]
    public void GetBehavior_NoMatchingEntry_DetectXFactorWaveringDefaultsToTrue()
    {
        var provider = new DefaultModelBehaviorProvider(
            new ModelBehaviorOptions(), NullLogger<DefaultModelBehaviorProvider>.Instance);

        var behavior = provider.GetBehavior("some/unconfigured-model");

        Assert.IsTrue(behavior.DetectXFactorWavering,
            "the whole wavering-evidence feature is dead in production if this defaults to false here");
    }

    [TestMethod]
    public void GetBehavior_MatchingEntryWithoutExplicitOverride_DetectXFactorWaveringDefaultsToTrue()
    {
        var options = new ModelBehaviorOptions
        {
            Models = { ["test-model"] = new ModelBehaviorEntry { NudgeOnHallucinatedToolCalls = true } },
        };
        var provider = new DefaultModelBehaviorProvider(options, NullLogger<DefaultModelBehaviorProvider>.Instance);

        var behavior = provider.GetBehavior("test-model");

        Assert.IsTrue(behavior.DetectXFactorWavering);
    }

    [TestMethod]
    public void GetBehavior_ExplicitOptOut_IsRespected()
    {
        var options = new ModelBehaviorOptions
        {
            Models = { ["test-model"] = new ModelBehaviorEntry { DetectXFactorWavering = false } },
        };
        var provider = new DefaultModelBehaviorProvider(options, NullLogger<DefaultModelBehaviorProvider>.Instance);

        var behavior = provider.GetBehavior("test-model");

        Assert.IsFalse(behavior.DetectXFactorWavering);
    }
}
