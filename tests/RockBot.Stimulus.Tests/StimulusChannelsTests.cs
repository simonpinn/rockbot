using System.Text;
using RockBot.Messaging;

namespace RockBot.Stimulus.Tests;

[TestClass]
public class StimulusChannelsTests
{
    private static MessageEnvelope NewEnvelope(
        string messageType = "tool.invoke.failed",
        string source = "calendar-mcp",
        IReadOnlyDictionary<string, string>? headers = null,
        string? jsonBody = null)
    {
        var body = jsonBody is null
            ? ReadOnlyMemory<byte>.Empty
            : new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(jsonBody));

        return new MessageEnvelope
        {
            MessageId = "m1",
            MessageType = messageType,
            Source = source,
            Timestamp = DateTimeOffset.UtcNow,
            Body = body,
            Headers = headers ?? new Dictionary<string, string>(),
        };
    }

    [TestMethod]
    public void TryResolve_Topic_ReturnsTheSubscribedTopicPassedIn()
    {
        var ok = StimulusChannels.TryResolve("topic", "tool.invoke.failed", NewEnvelope(), out var value);

        Assert.IsTrue(ok);
        Assert.AreEqual("tool.invoke.failed", value);
    }

    [TestMethod]
    public void TryResolve_MessageType_ReturnsEnvelopeMessageType()
    {
        var ok = StimulusChannels.TryResolve(
            "messageType", "any.topic", NewEnvelope(messageType: "calendar.error"), out var value);

        Assert.IsTrue(ok);
        Assert.AreEqual("calendar.error", value);
    }

    [TestMethod]
    public void TryResolve_Source_ReturnsEnvelopeSource()
    {
        var ok = StimulusChannels.TryResolve("source", "any.topic", NewEnvelope(source: "weather-mcp"), out var value);

        Assert.IsTrue(ok);
        Assert.AreEqual("weather-mcp", value);
    }

    [TestMethod]
    public void TryResolve_HeaderPresent_ReturnsHeaderValue()
    {
        var envelope = NewEnvelope(headers: new Dictionary<string, string> { ["Retryable"] = "true" });

        var ok = StimulusChannels.TryResolve("header:Retryable", "any.topic", envelope, out var value);

        Assert.IsTrue(ok);
        Assert.AreEqual("true", value);
    }

    [TestMethod]
    public void TryResolve_HeaderMissing_ReturnsFalse()
    {
        var ok = StimulusChannels.TryResolve("header:Missing", "any.topic", NewEnvelope(), out var value);

        Assert.IsFalse(ok);
        Assert.IsNull(value);
    }

    [TestMethod]
    public void TryResolve_PayloadDottedPath_ReturnsNestedScalarValue()
    {
        const string json = """{ "server": "calendar-mcp", "error": { "class": "timeout" } }""";
        var envelope = NewEnvelope(jsonBody: json);

        var ok = StimulusChannels.TryResolve("payload:error.class", "any.topic", envelope, out var value);

        Assert.IsTrue(ok);
        Assert.AreEqual("timeout", value);
    }

    [TestMethod]
    public void TryResolve_PayloadTopLevelField_ReturnsValue()
    {
        const string json = """{ "server": "calendar-mcp" }""";
        var envelope = NewEnvelope(jsonBody: json);

        var ok = StimulusChannels.TryResolve("payload:server", "any.topic", envelope, out var value);

        Assert.IsTrue(ok);
        Assert.AreEqual("calendar-mcp", value);
    }

    [TestMethod]
    public void TryResolve_PayloadPathMissing_ReturnsFalse()
    {
        const string json = """{ "server": "calendar-mcp" }""";
        var envelope = NewEnvelope(jsonBody: json);

        var ok = StimulusChannels.TryResolve("payload:nonexistent.path", "any.topic", envelope, out var value);

        Assert.IsFalse(ok);
        Assert.IsNull(value);
    }

    [TestMethod]
    public void TryResolve_PayloadPathResolvesToObject_ReturnsFalse()
    {
        // An object/array leaf is "absent" for matching purposes, never a wildcard match.
        const string json = """{ "error": { "class": "timeout" } }""";
        var envelope = NewEnvelope(jsonBody: json);

        var ok = StimulusChannels.TryResolve("payload:error", "any.topic", envelope, out var value);

        Assert.IsFalse(ok);
        Assert.IsNull(value);
    }

    [TestMethod]
    public void TryResolve_PayloadBodyNotJson_ReturnsFalse_DoesNotThrow()
    {
        var envelope = NewEnvelope(jsonBody: "not valid json");

        var ok = StimulusChannels.TryResolve("payload:server", "any.topic", envelope, out var value);

        Assert.IsFalse(ok);
        Assert.IsNull(value);
    }

    [TestMethod]
    public void TryResolve_PayloadEmptyBody_ReturnsFalse_DoesNotThrow()
    {
        var ok = StimulusChannels.TryResolve("payload:server", "any.topic", NewEnvelope(), out var value);

        Assert.IsFalse(ok);
        Assert.IsNull(value);
    }

    [TestMethod]
    public void TryResolve_UnknownChannelName_ReturnsFalse()
    {
        var ok = StimulusChannels.TryResolve("nonsense", "any.topic", NewEnvelope(), out var value);

        Assert.IsFalse(ok);
        Assert.IsNull(value);
    }

    [TestMethod]
    public void TryResolve_PayloadNumericLeaf_StringifiesIt()
    {
        const string json = """{ "retryCount": 3 }""";
        var envelope = NewEnvelope(jsonBody: json);

        var ok = StimulusChannels.TryResolve("payload:retryCount", "any.topic", envelope, out var value);

        Assert.IsTrue(ok);
        Assert.AreEqual("3", value);
    }
}
