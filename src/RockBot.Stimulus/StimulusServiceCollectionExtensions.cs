using Microsoft.Extensions.DependencyInjection;
using RockBot.Host;

namespace RockBot.Stimulus;

/// <summary>
/// DI registration for the stimulus dispatcher. See <c>design/cognitive-tiers.md</c>.
/// </summary>
public static class StimulusServiceCollectionExtensions
{
    /// <summary>
    /// Registers the stimulus pattern holder, the hot-reloading config loader, and the
    /// hosted dispatcher that matches bus traffic against the loaded patterns and fires
    /// reflex tools directly. No-op when <see cref="StimulusOptions.Enabled"/> is false.
    /// </summary>
    public static AgentHostBuilder AddStimulusDispatcher(
        this AgentHostBuilder builder, Action<StimulusOptions>? configure = null)
    {
        var options = new StimulusOptions();
        configure?.Invoke(options);
        builder.Services.Configure<StimulusOptions>(o =>
        {
            o.BasePath = options.BasePath;
            o.PollIntervalSeconds = options.PollIntervalSeconds;
            o.Enabled = options.Enabled;
        });

        builder.Services.AddSingleton<StimulusPatternHolder>();

        if (options.Enabled)
        {
            builder.Services.AddHostedService<StimulusRegistryLoader>();
            builder.Services.AddHostedService<StimulusDispatcher>();
        }

        return builder;
    }
}
