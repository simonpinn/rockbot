using Microsoft.Extensions.Options;

namespace McpServer.Graph.Services;

/// <summary>The configured calendar time zone, in the two forms the calendar tools need.</summary>
public sealed class GraphTimeZone(IOptions<GraphMailOptions> options, TimeProvider time)
{
    /// <summary>For local arithmetic: today's date, working hours, free-time gaps.</summary>
    public TimeZoneInfo Info { get; } = Resolve(options.Value.TimeZone)
        ?? throw new InvalidOperationException($"Graph:TimeZone '{options.Value.TimeZone}' is not a known time zone");

    /// <summary>The name to show the agent: the configured value, as written.</summary>
    public string Name => options.Value.TimeZone;

    /// <summary>The name Graph is sent, in Prefer: outlook.timezone and in dateTimeTimeZone values.</summary>
    public string GraphName => GraphNameOf(Info);

    public DateTime Now => TimeZoneInfo.ConvertTime(time.GetUtcNow(), Info).DateTime;

    public static TimeZoneInfo? Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    // Graph documents Windows names for dateTimeTimeZone; IANA names have no Windows mapping
    // only for obscure zones, and Graph accepts most of those too.
    public static string GraphNameOf(TimeZoneInfo zone) =>
        zone.HasIanaId && TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var windows) ? windows : zone.Id;
}
