using System.Text.Json;

namespace RockBot.Host.Tests;

[TestClass]
public class RepairTicketCreationPromptTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void BuildPrompt_ClusterWithEscalatedPriorTicket_IncludesPriorAttemptNote()
    {
        var cluster = new FailureCluster(
            Key: new ClusterKey("skill-resource", "calendar/scan", "fanout.json"),
            Count: 4,
            SessionIds: new HashSet<string> { "s2", "s3" },
            FirstSeen: Now.AddHours(-5),
            LastSeen: Now,
            SampleErrorMessages: new List<string> { "tool fingerprint changed" });

        var priorTicket = new RepairTicket(
            Id: "ticket-abc123",
            PatternKey: "skill-resource|calendar/scan|fanout.json",
            Target: RepairTarget.SkillResource,
            Change: JsonDocument.Parse(
                """{"skill":"calendar/scan","filename":"fanout.json","op":"demote-provisional"}""").RootElement,
            Verify: new VerifyShape("calendar-mcp", "get_events", JsonDocument.Parse("{}").RootElement,
                new VerifyExpectation(VerifyExpectationKind.Success, null)),
            Attempts: [],
            Status: RepairStatus.Escalated,
            CreatedAt: Now.AddDays(-1),
            UpdatedAt: Now.AddHours(-1));

        var priorByPattern = new Dictionary<string, List<RepairTicket>>
        {
            ["skill-resource|calendar/scan|fanout.json"] = [priorTicket],
        };

        var prompt = DreamService.BuildRepairTicketCreationPrompt([cluster], priorByPattern);

        StringAssert.Contains(prompt, "demote-provisional");
        StringAssert.Contains(prompt, "Escalated");
    }

    [TestMethod]
    public void BuildPrompt_ClusterWithNoPriorTickets_OmitsPriorAttemptNote()
    {
        var cluster = new FailureCluster(
            Key: new ClusterKey("skill-resource", "calendar/scan", "fanout.json"),
            Count: 3,
            SessionIds: new HashSet<string> { "s1", "s2" },
            FirstSeen: Now.AddHours(-2),
            LastSeen: Now,
            SampleErrorMessages: new List<string> { "boom" });

        var prompt = DreamService.BuildRepairTicketCreationPrompt([cluster], new Dictionary<string, List<RepairTicket>>());

        // "prior attempt:" is the real line prefix this method emits (see the
        // other test) — asserting its absence is what actually guards against a
        // fabricated "no prior attempts" line; the earlier wording here checked
        // substrings the code never prints either way, so a regression that
        // printed "prior attempt: (none)" would have passed unnoticed.
        Assert.IsFalse(prompt.Contains("prior attempt", StringComparison.OrdinalIgnoreCase));
    }
}
