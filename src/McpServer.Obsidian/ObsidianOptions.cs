using System.ComponentModel.DataAnnotations;

namespace McpServer.Obsidian;

public sealed class ObsidianOptions
{
    public const string SectionName = "Obsidian";

    /// <summary>The vault's root directory. Every note the tools touch is inside it.</summary>
    [Required] public string VaultPath { get; set; } = "/vault";

    /// <summary>Vault-relative folder the tools may write to. Everything else is read-only.</summary>
    [Required] public string WriteFolder { get; set; } = "RockBot";

    /// <summary>The vault's name in Obsidian, used only to build obsidian:// links. Optional.</summary>
    public string VaultName { get; set; } = string.Empty;

    /// <summary>IANA or Windows time zone for the timestamps the tools return and write.</summary>
    [Required] public string TimeZone { get; set; } = "UTC";

    [Range(1_000, 1_000_000)] public int MaxReadChars { get; set; } = 200_000;
    [Range(1_000, 1_000_000)] public int MaxWriteChars { get; set; } = 100_000;
}
