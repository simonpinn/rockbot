using System.Text.Json;
using RockBot.Messaging;

namespace RockBot.Stimulus;

/// <summary>
/// Resolves a small, named vocabulary of signals ("channels", in
/// <c>design/cognitive-tiers.md</c>'s terms) from a <see cref="MessageEnvelope"/> plus the
/// topic it arrived on. Deliberately not a closed list — <c>payload:&lt;dotted.path&gt;</c>
/// reaches any scalar field a pattern's author needs — but deliberately not a free-text
/// search either: every resolution is a direct, deterministic lookup, never a scan or an
/// LLM call, so dispatcher cost stays bounded regardless of how many patterns are registered.
/// </summary>
internal static class StimulusChannels
{
    private const string HeaderPrefix = "header:";
    private const string PayloadPrefix = "payload:";

    /// <summary>
    /// Resolves <paramref name="channel"/> against <paramref name="envelope"/> (and, for the
    /// <c>"topic"</c> channel, the <paramref name="topic"/> the dispatcher is currently
    /// processing on — the envelope itself carries no topic field). Returns false, with
    /// <paramref name="value"/> null, for any channel that is absent or cannot be resolved
    /// to a scalar — never throws, so one malformed message never takes down the dispatcher.
    /// </summary>
    public static bool TryResolve(string channel, string topic, MessageEnvelope envelope, out string? value)
    {
        value = null;

        switch (channel)
        {
            case "topic":
                value = topic;
                return true;

            case "messageType":
                value = envelope.MessageType;
                return true;

            case "source":
                value = envelope.Source;
                return true;
        }

        if (channel.StartsWith(HeaderPrefix, StringComparison.Ordinal))
        {
            var headerName = channel[HeaderPrefix.Length..];
            if (envelope.Headers.TryGetValue(headerName, out var headerValue))
            {
                value = headerValue;
                return true;
            }
            return false;
        }

        if (channel.StartsWith(PayloadPrefix, StringComparison.Ordinal))
        {
            var path = channel[PayloadPrefix.Length..];
            return TryResolvePayloadPath(envelope.Body, path, out value);
        }

        return false;
    }

    private static bool TryResolvePayloadPath(ReadOnlyMemory<byte> body, string dottedPath, out string? value)
    {
        value = null;

        if (body.IsEmpty)
            return false;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        var current = root;
        foreach (var segment in dottedPath.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                return false;
            current = next;
        }

        return TryStringifyScalar(current, out value);
    }

    private static bool TryStringifyScalar(JsonElement element, out string? value)
    {
        value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => null, // Object/Array leaves are "absent" for matching - never a wildcard match.
        };
        return value is not null;
    }
}
