using System.Text;
using RockBot.Messaging;

namespace RockBot.Stimulus.Tests;

[TestClass]
public class StimulusMatcherTests
{
    private static MessageEnvelope NewEnvelope(string source = "calendar-mcp", string? jsonBody = null) => new()
    {
        MessageId = "m1",
        MessageType = "tool.invoke.failed",
        Source = source,
        Timestamp = DateTimeOffset.UtcNow,
        Body = jsonBody is null ? ReadOnlyMemory<byte>.Empty : new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(jsonBody)),
    };

    private static StimulusPattern NewPattern(IReadOnlyDictionary<string, string>? channels = null) => new()
    {
        Id = "p1",
        Topic = "tool.invoke.failed",
        Channels = channels ?? new Dictionary<string, string>(),
        ReflexTool = "reflex__calendar__retry",
    };

    [TestMethod]
    public void TryMatchChannels_AllNamedChannelsEqual_ReturnsTrue()
    {
        var pattern = NewPattern(new Dictionary<string, string>
        {
            ["source"] = "calendar-mcp",
            ["payload:errorClass"] = "timeout",
        });
        var envelope = NewEnvelope(jsonBody: """{ "errorClass": "timeout" }""");

        var ok = StimulusMatcher.TryMatchChannels(pattern, "tool.invoke.failed", envelope, out var matched);

        Assert.IsTrue(ok);
        Assert.AreEqual("calendar-mcp", matched["source"]);
        Assert.AreEqual("timeout", matched["payload:errorClass"]);
    }

    [TestMethod]
    public void TryMatchChannels_OneChannelMismatched_ReturnsFalse()
    {
        var pattern = NewPattern(new Dictionary<string, string>
        {
            ["source"] = "calendar-mcp",
            ["payload:errorClass"] = "timeout",
        });
        var envelope = NewEnvelope(jsonBody: """{ "errorClass": "not-found" }""");

        var ok = StimulusMatcher.TryMatchChannels(pattern, "tool.invoke.failed", envelope, out var matched);

        Assert.IsFalse(ok);
        Assert.AreEqual(0, matched.Count);
    }

    [TestMethod]
    public void TryMatchChannels_NamedChannelAbsentFromMessage_ReturnsFalse()
    {
        var pattern = NewPattern(new Dictionary<string, string> { ["payload:errorClass"] = "timeout" });
        var envelope = NewEnvelope(jsonBody: """{ "server": "calendar-mcp" }""");

        var ok = StimulusMatcher.TryMatchChannels(pattern, "tool.invoke.failed", envelope, out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void TryMatchChannels_EmptyChannelsDictionary_MatchesEveryMessageOnTopic()
    {
        // A topic-only pattern (no channels) fires on any message on that topic.
        var pattern = NewPattern();
        var envelope = NewEnvelope();

        var ok = StimulusMatcher.TryMatchChannels(pattern, "tool.invoke.failed", envelope, out var matched);

        Assert.IsTrue(ok);
        Assert.AreEqual(0, matched.Count);
    }

    [TestMethod]
    public void TryMatchChannels_UnnamedChannels_AreNeverInspected()
    {
        // Only "source" is named - a wildly different payload must not block the match.
        var pattern = NewPattern(new Dictionary<string, string> { ["source"] = "calendar-mcp" });
        var envelope = NewEnvelope(jsonBody: """{ "anything": "goes-here" }""");

        var ok = StimulusMatcher.TryMatchChannels(pattern, "tool.invoke.failed", envelope, out _);

        Assert.IsTrue(ok);
    }
}
