using Microsoft.Extensions.Hosting;
using RockBot.Tools;

namespace RockBot.Host;

/// <summary>
/// Registers <c>recall_x_factor_memory</c> once at startup. Registered globally through
/// <see cref="IToolRegistry"/> rather than per-skill, it shows up in every handler's assembled
/// tool list with no per-handler wiring — the same registry-based exposure any other generic
/// tool already gets. See <c>design/cognitive-tiers.md</c>, "X-factor memory".
/// </summary>
internal sealed class XFactorToolRegistrar(IToolRegistry toolRegistry, XFactorRecallExecutor executor) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        toolRegistry.Register(
            new ToolRegistration
            {
                Name = "recall_x_factor_memory",
                Description = "List every recorded x-factor memory - a standing sense that something is " +
                              "novel or notable, and how strongly it currently applies. Use this to check " +
                              "whether a reflex or instinct's hesitation traces back to a specific, " +
                              "nameable prior experience rather than fresh judgment.",
                Source = "x-factor-memory",
            },
            executor);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        toolRegistry.Unregister("recall_x_factor_memory");
        return Task.CompletedTask;
    }
}
