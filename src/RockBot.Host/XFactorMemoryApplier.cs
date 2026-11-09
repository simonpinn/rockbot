using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Applies a <see cref="RepairTarget.XFactorMemory"/> change: down-weight a specific
/// <c>(channel, value)</c>'s current salience. Modeled directly on
/// <see cref="SkillResourceApplier"/>'s <c>demote-provisional</c> op — load the current state,
/// compute and persist the new value, ship a revert that restores the exact prior value on a
/// failed verify. One op today: <c>down-weight</c>. See <c>design/cognitive-tiers.md</c>,
/// "X-factor memory".
/// </summary>
internal sealed class XFactorMemoryApplier(
    IXFactorMemoryStore store, IOptions<XFactorMemoryOptions> options, ILogger<XFactorMemoryApplier> logger) : IRepairTargetApplier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public RepairTarget Target => RepairTarget.XFactorMemory;

    public async Task<RepairApplyOutcome> ApplyAsync(RepairTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var change = ticket.Change.Deserialize<XFactorMemoryChange>(JsonOptions)
            ?? throw new ArgumentException("XFactorMemory change is empty.", nameof(ticket));

        if (string.IsNullOrWhiteSpace(change.Channel))
            throw new ArgumentException("XFactorMemory change missing 'channel'.", nameof(ticket));
        if (string.IsNullOrWhiteSpace(change.Value))
            throw new ArgumentException("XFactorMemory change missing 'value'.", nameof(ticket));

        var op = (change.Op ?? string.Empty).ToLowerInvariant();
        return op switch
        {
            "down-weight" => await ApplyDownWeightAsync(change, cancellationToken),
            _ => throw new ArgumentException($"Unknown XFactorMemory op: '{change.Op}'. Expected down-weight.", nameof(ticket)),
        };
    }

    private async Task<RepairApplyOutcome> ApplyDownWeightAsync(XFactorMemoryChange change, CancellationToken ct)
    {
        var key = new XFactorMemoryKey(change.Channel!, change.Value!);
        var current = await store.TryGetAsync(key, ct)
            ?? throw new InvalidOperationException($"X-factor memory '{change.Channel}'/'{change.Value}' has never been sighted.");

        var factor = change.Factor ?? options.Value.DefaultDownWeightFactor;
        var newWeight = Math.Max(options.Value.SalienceWeightFloor, current.SalienceWeight * factor);

        var prior = await store.AdjustSalienceAsync(key, newWeight, ct)
            ?? throw new InvalidOperationException($"X-factor memory '{change.Channel}'/'{change.Value}' vanished mid-apply.");

        var diff = JsonSerializer.SerializeToElement(new
        {
            op = "down-weight",
            channel = change.Channel,
            value = change.Value,
            priorWeight = prior.SalienceWeight,
            newWeight,
        }, JsonOptions);

        logger.LogInformation(
            "XFactorMemoryApplier: down-weighted '{Channel}'/'{Value}' from {Prior} to {New}",
            change.Channel, change.Value, prior.SalienceWeight, newWeight);

        Func<CancellationToken, Task> revert = async revertCt =>
        {
            await store.AdjustSalienceAsync(key, prior.SalienceWeight, revertCt);
            logger.LogInformation(
                "XFactorMemoryApplier reverted down-weight: restored salience {Weight} for '{Channel}'/'{Value}'",
                prior.SalienceWeight, change.Channel, change.Value);
        };

        return new RepairApplyOutcome(diff, revert);
    }

    internal sealed class XFactorMemoryChange
    {
        public string? Channel { get; set; }
        public string? Value { get; set; }
        public string? Op { get; set; }
        public double? Factor { get; set; }
    }
}
