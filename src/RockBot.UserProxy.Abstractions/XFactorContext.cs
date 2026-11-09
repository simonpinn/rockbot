namespace RockBot.UserProxy;

/// <summary>
/// The x-factor slot a stimulus match resolved, carried onto <see cref="ReflexFiredEvent"/> and
/// (in <c>RockBot.Host</c>/<c>RockBot.Stimulus</c>) a stimulus invocation's ambient context.
/// Deliberately plain strings/double here rather than referencing <c>RockBot.Host</c>'s
/// <c>XFactorMemoryKey</c>/<c>XFactorMemoryRecord</c> types directly — <c>RockBot.Host.Abstractions</c>
/// already depends on this project, so the dependency can't run the other way.
/// See <c>design/cognitive-tiers.md</c>, "X-factor memory".
/// </summary>
public sealed record XFactorContext(string Channel, string Value, double SalienceWeight);
