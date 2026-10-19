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
}
