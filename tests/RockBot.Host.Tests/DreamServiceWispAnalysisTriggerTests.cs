namespace RockBot.Host.Tests;

/// <summary>
/// Candidate-group selection for the wisp-success-analysis pass, extracted so the
/// explicit-target bypass (used by the on-demand "fast iteration" trigger) is
/// testable without standing up the full DreamService + LLM stack. Mirrors
/// DreamServiceWispSuccessTests' "outer shape is unit-tested, LLM-driven half is
/// covered by the directive prompt" approach.
/// </summary>
[TestClass]
public class DreamServiceWispAnalysisTriggerTests
{
    private static WispExecutionRecord Record(
        string wispId, string shapeHash, bool succeeded, DateTimeOffset timestamp, string description = "x") =>
        new()
        {
            WispId = wispId,
            Description = description,
            DefinitionHash = "def-" + shapeHash,
            ShapeHash = shapeHash,
            Succeeded = succeeded,
            StepCount = 1,
            StepsCompleted = succeeded ? 1 : 0,
            DurationMs = 10,
            Timestamp = timestamp,
        };

    [TestMethod]
    public void SelectCandidateGroups_NoRequest_OnlyKeepsGroupsMeetingThreshold()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = new List<WispExecutionRecord>
        {
            Record("w1", "hot-hash", true, now),
            Record("w2", "hot-hash", true, now),
            Record("w3", "hot-hash", true, now),
            Record("w4", "cold-hash", true, now),
        };

        var groups = DreamService.SelectWispSuccessCandidateGroups(
            recentRecords: recent, allRecords: recent, threshold: 3, request: null);

        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual("hot-hash", groups[0].Key);
    }

    [TestMethod]
    public void SelectCandidateGroups_NoRequest_ExcludesGroupWithAnyFailure()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = new List<WispExecutionRecord>
        {
            Record("w1", "flaky-hash", true, now),
            Record("w2", "flaky-hash", true, now),
            Record("w3", "flaky-hash", false, now),
        };

        var groups = DreamService.SelectWispSuccessCandidateGroups(
            recentRecords: recent, allRecords: recent, threshold: 3, request: null);

        Assert.AreEqual(0, groups.Count);
    }

    [TestMethod]
    public void SelectCandidateGroups_ExplicitShapeHashTarget_BypassesThresholdAndWindow()
    {
        var now = DateTimeOffset.UtcNow;
        // Only 1 occurrence, and it's outside "recent" (simulating aged out of the
        // 7-day lookback) but present in the full history.
        var old = new List<WispExecutionRecord> { Record("w1", "rare-hash", true, now.AddDays(-30)) };
        var recent = new List<WispExecutionRecord>();

        var request = new WispAnalysisTriggerRequest(TargetShapeHashes: ["rare-hash"]);

        var groups = DreamService.SelectWispSuccessCandidateGroups(
            recentRecords: recent, allRecords: old, threshold: 3, request: request);

        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual("rare-hash", groups[0].Key);
    }

    [TestMethod]
    public void SelectCandidateGroups_ExplicitDescriptionTarget_BypassesThresholdAndWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var old = new List<WispExecutionRecord>
        {
            Record("w1", "board-scan-hash", true, now.AddDays(-30), description: "ALX board per-list dump"),
        };

        var request = new WispAnalysisTriggerRequest(DescriptionContains: "ALX board");

        var groups = DreamService.SelectWispSuccessCandidateGroups(
            recentRecords: [], allRecords: old, threshold: 3, request: request);

        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual("board-scan-hash", groups[0].Key);
    }

    [TestMethod]
    public void SelectCandidateGroups_ExplicitTarget_StillExcludesAnyFailure()
    {
        var now = DateTimeOffset.UtcNow;
        var old = new List<WispExecutionRecord>
        {
            Record("w1", "rare-hash", true, now.AddDays(-30)),
            Record("w2", "rare-hash", false, now.AddDays(-29)),
        };

        var request = new WispAnalysisTriggerRequest(TargetShapeHashes: ["rare-hash"]);

        var groups = DreamService.SelectWispSuccessCandidateGroups(
            recentRecords: [], allRecords: old, threshold: 3, request: request);

        Assert.AreEqual(0, groups.Count, "a targeted candidate with any failed run must never be force-included");
    }

    [TestMethod]
    public void SelectCandidateGroups_TargetAlsoNormallyQualifying_AppearsOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = new List<WispExecutionRecord>
        {
            Record("w1", "hot-hash", true, now),
            Record("w2", "hot-hash", true, now),
            Record("w3", "hot-hash", true, now),
        };

        var request = new WispAnalysisTriggerRequest(TargetShapeHashes: ["hot-hash"]);

        var groups = DreamService.SelectWispSuccessCandidateGroups(
            recentRecords: recent, allRecords: recent, threshold: 3, request: request);

        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual(3, groups[0].Count());
    }

    [TestMethod]
    public void SelectCandidateGroups_RequestWithNoMatchingTarget_OnlyNormalGroupsReturned()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = new List<WispExecutionRecord>
        {
            Record("w1", "hot-hash", true, now),
            Record("w2", "hot-hash", true, now),
            Record("w3", "hot-hash", true, now),
        };

        var request = new WispAnalysisTriggerRequest(TargetShapeHashes: ["no-such-hash"]);

        var groups = DreamService.SelectWispSuccessCandidateGroups(
            recentRecords: recent, allRecords: recent, threshold: 3, request: request);

        Assert.AreEqual(1, groups.Count);
        Assert.AreEqual("hot-hash", groups[0].Key);
    }
}
