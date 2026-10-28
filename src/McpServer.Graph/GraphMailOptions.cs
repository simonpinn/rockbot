using System.ComponentModel.DataAnnotations;

namespace McpServer.Graph;

public sealed class GraphMailOptions
{
    public const string SectionName = "Graph";

    [Required] public string TenantId { get; set; } = string.Empty;
    [Required] public string ClientId { get; set; } = string.Empty;
    [Required] public string ClientSecret { get; set; } = string.Empty;

    /// <summary>The only mailbox this server reads. Never taken from tool arguments.</summary>
    [Required, EmailAddress] public string Mailbox { get; set; } = string.Empty;

    [Range(1_000, 200_000)] public int MaxBodyChars { get; set; } = 20_000;

    /// <summary>Largest attachment get_attachment downloads. Graph allows up to 150 MB.</summary>
    [Range(1_024, 157_286_400)] public long MaxAttachmentBytes { get; set; } = 25 * 1_048_576;

    /// <summary>
    /// IANA or Windows time zone the calendar tools read and write in, e.g. Australia/Sydney.
    /// Compose sets it from AGENT_TIMEZONE so the agent and the calendar agree on "today".
    /// </summary>
    [Required] public string TimeZone { get; set; } = "UTC";

    /// <summary>
    /// OneDrive/SharePoint drives the Drive tools can reach. Empty by default — Files access is
    /// opt-in. Which aliases are read-only, read-write or unreachable is decided outside this
    /// server entirely, by the MCP bridge's path-prefix argument guards (see deploy/graph/README.md);
    /// this list only says which aliases exist and which Graph drive each one resolves to.
    /// </summary>
    public List<GraphDriveEntry> Drives { get; set; } = [];

    /// <summary>Largest file download_drive_file and upload_drive_file will move in one call.</summary>
    [Range(1_024, 157_286_400)] public long MaxDriveFileBytes { get; set; } = 25 * 1_048_576;
}
