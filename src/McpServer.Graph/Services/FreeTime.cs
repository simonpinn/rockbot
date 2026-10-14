namespace McpServer.Graph.Services;

public sealed record FreeSlot(string Start, string End, int Minutes);

/// <summary>Finds gaps between busy blocks. All times are wall-clock times in one zone.</summary>
public static class FreeTime
{
    public static readonly TimeSpan WorkdayStart = TimeSpan.FromHours(8);
    public static readonly TimeSpan WorkdayEnd = TimeSpan.FromHours(18);

    public static IReadOnlyList<(DateTime Start, DateTime End)> Find(
        IEnumerable<BusyBlock> busy, DateTime from, DateTime to, TimeSpan minimum, bool workingHoursOnly, int maxSlots)
    {
        var blocks = busy
            .Where(b => b.End > from && b.Start < to)
            .OrderBy(b => b.Start)
            .ToList();

        var slots = new List<(DateTime, DateTime)>();
        foreach (var (windowStart, windowEnd) in Windows(from, to, workingHoursOnly))
        {
            var cursor = windowStart;
            foreach (var block in blocks)
            {
                if (block.End <= cursor || block.Start >= windowEnd)
                    continue;
                if (block.Start - cursor >= minimum)
                    slots.Add((cursor, block.Start));
                if (block.End > cursor)
                    cursor = block.End;
                if (cursor >= windowEnd)
                    break;
            }
            if (windowEnd - cursor >= minimum)
                slots.Add((cursor, windowEnd));
            if (slots.Count >= maxSlots)
                return slots.Take(maxSlots).ToList();
        }
        return slots;
    }

    private static IEnumerable<(DateTime, DateTime)> Windows(DateTime from, DateTime to, bool workingHoursOnly)
    {
        if (!workingHoursOnly)
        {
            yield return (from, to);
            yield break;
        }

        for (var day = from.Date; day < to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;
            var start = Max(day + WorkdayStart, from);
            var end = Min(day + WorkdayEnd, to);
            if (end > start)
                yield return (start, end);
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
