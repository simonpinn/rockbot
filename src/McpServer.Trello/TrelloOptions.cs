using System.ComponentModel.DataAnnotations;

namespace McpServer.Trello;

public sealed class TrelloOptions
{
    public const string SectionName = "Trello";

    [Required] public string ApiKey { get; set; } = string.Empty;
    [Required] public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated board ids the tools may touch. Empty means every board the token can see.
    /// </summary>
    public string AllowedBoardIds { get; set; } = string.Empty;

    [Range(1_000, 200_000)] public int MaxTextChars { get; set; } = 20_000;

    public IReadOnlySet<string> AllowedBoards =>
        AllowedBoardIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
