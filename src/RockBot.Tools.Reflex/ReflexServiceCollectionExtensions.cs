using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Wisp;

namespace RockBot.Tools.Reflex;

/// <summary>
/// DI registration for reflex tools. See <c>design/reflex-tools.md</c> Phase 2.
/// </summary>
public static class ReflexServiceCollectionExtensions
{
    /// <summary>
    /// Registers the reflex tool catalog, its shared executor, and the hosted reconciliation
    /// loop. <see cref="ReflexToolCatalog"/> and the reflex <see cref="IToolExecutor"/> each
    /// depend on the other (the catalog hands the executor to <c>IToolRegistry.Register</c>; the
    /// executor resolves tool names back through the catalog's <c>IReflexRegistrationLookup</c>)
    /// — the catalog takes a <c>Func&lt;IToolExecutor&gt;</c> rather than the executor itself so
    /// DI can construct both singletons without a circular constructor dependency.
    /// </summary>
    public static AgentHostBuilder AddReflexTools(
        this AgentHostBuilder builder, Action<ReflexOptions>? configure = null)
    {
        var options = new ReflexOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        builder.Services.AddSingleton<ReflexToolSurface>();
        builder.Services.AddSingleton<ReflexToolCatalog>(sp =>
        {
            var catalog = new ReflexToolCatalog(
                sp.GetRequiredService<ISkillStore>(),
                sp.GetRequiredService<IToolRegistry>(),
                () => sp.GetRequiredService<ReflexExecutor>(),
                sp.GetRequiredService<ILogger<ReflexToolCatalog>>());
            var surface = sp.GetRequiredService<ReflexToolSurface>();
            catalog.Bind(surface);
            surface.Bind(catalog);
            return catalog;
        });
        builder.Services.TryAddSingleton<IReflexRegistrationLookup>(
            sp => sp.GetRequiredService<ReflexToolCatalog>());
        builder.Services.TryAddSingleton<IReflexActivationHooks>(
            sp => sp.GetRequiredService<ReflexToolSurface>());

        builder.Services.AddSingleton<ReflexExecutor>(sp => new ReflexExecutor(
            sp.GetRequiredService<IReflexRegistrationLookup>(),
            sp.GetRequiredService<ISkillStore>(),
            sp.GetRequiredService<WispExecutor>(),
            sp.GetService<IWispExecutionLog>(),
            sp.GetService<WispDispatchCircuitBreaker>(),
            sp.GetService<IMcpToolDirectory>(),
            sp.GetRequiredService<ILogger<ReflexExecutor>>()));

        if (options.Enabled)
            builder.Services.AddHostedService<ReflexToolRegistrar>();

        return builder;
    }
}

/// <summary>
/// Runs <see cref="ReflexToolCatalog.ReconcileOnceAsync"/> once at startup and then on a
/// <see cref="PeriodicTimer"/> at <see cref="ReflexOptions.ReconciliationInterval"/>. Each tick's
/// exception is caught and logged so one bad cycle doesn't stop the loop.
/// </summary>
internal sealed class ReflexToolRegistrar(
    ReflexToolCatalog catalog, ReflexOptions options, ILogger<ReflexToolRegistrar> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ReconciliationInterval);
        do
        {
            try
            {
                await catalog.ReconcileOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reflex tool reconciliation cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
