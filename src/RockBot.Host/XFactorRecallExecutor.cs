using System.Text;
using Microsoft.Extensions.Logging;
using RockBot.Tools;

namespace RockBot.Host;

/// <summary>
/// Lets thought inspect its own x-factor memories mid-deliberation — the reverse of thought
/// raising a stimulus. A standing aversion that's become a blocker needs to be something the
/// agent (and a human reading its transcript) can actually see, not just infer from hesitant
/// text. See <c>design/cognitive-tiers.md</c>, "X-factor memory". Registered globally, not
/// per-skill, so it needs no activation/pinning — it's cheap, generic introspection.
/// </summary>
internal sealed class XFactorRecallExecutor(IXFactorMemoryStore store, ILogger<XFactorRecallExecutor> logger) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        IReadOnlyList<XFactorMemoryRecord> records;
        try
        {
            records = await store.ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to list x-factor memories");
            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = $"Failed to list x-factor memories: {ex.Message}",
                IsError = true,
            };
        }

        if (records.Count == 0)
        {
            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = "No x-factor memories recorded yet.",
            };
        }

        var ledger = XFactorRecallContext.Value;
        var sb = new StringBuilder();
        sb.AppendLine($"{records.Count} x-factor memor{(records.Count == 1 ? "y" : "ies")}:");
        foreach (var r in records)
        {
            sb.AppendLine(
                $"- [{r.Key.Channel}] \"{r.Key.Value}\" — seen {r.Count}x, last {r.LastSeen:u}, salience {r.SalienceWeight:F2}");
            ledger?.Add(r.Key.Channel, r.Key.Value);
        }

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = sb.ToString().TrimEnd(),
        };
    }
}
