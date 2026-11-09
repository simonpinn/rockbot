namespace RockBot.Host.Tests;

[TestClass]
public class StimulusInvocationContextTests
{
    [TestMethod]
    public void Value_IsNullOutsideAStimulusFire_AndRestoredAfterTheScope()
    {
        Assert.IsNull(StimulusInvocationContext.Value);
        var outer = new StimulusInvocation("p1", "tool.invoke.failed", new Dictionary<string, string>());

        using (StimulusInvocationContext.Set(outer))
        {
            using (StimulusInvocationContext.Set(new StimulusInvocation("p2", "other.topic", new Dictionary<string, string>())))
                Assert.AreNotSame(outer, StimulusInvocationContext.Value);

            Assert.AreSame(outer, StimulusInvocationContext.Value);
        }

        Assert.IsNull(StimulusInvocationContext.Value);
    }

    [TestMethod]
    public void Value_CarriesPatternIdTopicAndMatchedChannels()
    {
        var channels = new Dictionary<string, string> { ["source"] = "calendar-mcp" };
        var invocation = new StimulusInvocation("calendar-transient-retry", "tool.invoke.failed", channels);

        using (StimulusInvocationContext.Set(invocation))
        {
            Assert.AreEqual("calendar-transient-retry", StimulusInvocationContext.Value!.PatternId);
            Assert.AreEqual("tool.invoke.failed", StimulusInvocationContext.Value!.Topic);
            Assert.AreEqual("calendar-mcp", StimulusInvocationContext.Value!.MatchedChannels["source"]);
        }
    }
}
