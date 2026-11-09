namespace RockBot.Host;

/// <summary>
/// Identity of an x-factor memory record: a slot (channel) plus the specific value seen in it.
/// <see cref="Channel"/> is normalised to lowercase, mirroring <see cref="ClusterKey"/>, since
/// it's drawn from a small, known vocabulary (<c>topic</c>, <c>payload:server</c>, …) where case
/// differences would only fragment records by accident. <see cref="Value"/> is left as-is — it's
/// arbitrary data, and lowercasing it could collide two genuinely different values.
/// See <c>design/cognitive-tiers.md</c>, "X-factor memory".
/// </summary>
/// <param name="Channel">The channel this value was seen in (lowercased on construction).</param>
/// <param name="Value">The specific value seen.</param>
public sealed record XFactorMemoryKey(string Channel, string Value)
{
    public string Channel { get; } = NormaliseLowerOrThrow(Channel, nameof(Channel));
    public string Value { get; } = ValidateOrThrow(Value, nameof(Value));

    private static string NormaliseLowerOrThrow(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.ToLowerInvariant();
    }

    private static string ValidateOrThrow(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value;
    }
}
