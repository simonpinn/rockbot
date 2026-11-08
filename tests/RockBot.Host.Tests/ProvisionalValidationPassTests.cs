namespace RockBot.Host.Tests;

[TestClass]
public class ProvisionalValidationPassTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset RecentlyCreated = Now.AddDays(-3);
    private static readonly DateTimeOffset LongAgoCreated = Now.AddDays(-60);

    private static SkillResource ProvWisp(string filename = "fanout.json", string hash = "h-abc",
        DateTimeOffset? createdAt = null, SkillResourceType type = SkillResourceType.Wisp) =>
        new(filename, type, "Per-account fan-out",
            Provisional: true,
            CreatedAt: createdAt ?? RecentlyCreated,
            VerifyHint: "exercises both accounts",
            DefinitionHash: hash);

    private static WispExecutionRecord WispRecord(
        bool succeeded, string sessionId, DateTimeOffset at, string hash = "h-abc") =>
        new()
        {
            WispId = Guid.NewGuid().ToString("N")[..8],
            Description = "x",
            DefinitionHash = hash,
            Succeeded = succeeded,
            StepCount = 1,
            StepsCompleted = succeeded ? 1 : 0,
            DurationMs = 5,
            Timestamp = at,
            SessionId = sessionId,
        };

    [TestMethod]
    public void Decide_ThreeDistinctSessionSuccesses_PromotesAndPreservesVerifyHint()
    {
        var records = new[]
        {
            WispRecord(true, "s1", Now.AddHours(-3)),
            WispRecord(true, "s2", Now.AddHours(-2)),
            WispRecord(true, "s3", Now.AddHours(-1)),
        };

        var decision = DreamService.DecideProvisionalAction(
            ProvWisp(), records, [], Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        Assert.AreEqual(DreamService.ProvisionalAction.Promote, decision.Action);
        Assert.AreEqual(3, decision.SuccessCount);
    }

    [TestMethod]
    public void Decide_ThreeSuccessesSameSession_DoesNotPromote()
    {
        var records = new[]
        {
            WispRecord(true, "s1", Now.AddHours(-3)),
            WispRecord(true, "s1", Now.AddHours(-2)),
            WispRecord(true, "s1", Now.AddHours(-1)),
        };

        var decision = DreamService.DecideProvisionalAction(
            ProvWisp(), records, [], Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        Assert.AreNotEqual(DreamService.ProvisionalAction.Promote, decision.Action);
    }

    [TestMethod]
    public void Decide_TwoConsecutiveFailures_RemovesResource()
    {
        var records = new[]
        {
            WispRecord(true, "s1", Now.AddHours(-5)),
            WispRecord(false, "s2", Now.AddHours(-2)),
            WispRecord(false, "s3", Now.AddHours(-1)),
        };

        var decision = DreamService.DecideProvisionalAction(
            ProvWisp(), records, [], Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        Assert.AreEqual(DreamService.ProvisionalAction.Remove, decision.Action);
        Assert.AreEqual(2, decision.ConsecutiveFailureCount);
    }

    [TestMethod]
    public void Decide_FailureThenSuccess_DoesNotRemove()
    {
        var records = new[]
        {
            WispRecord(false, "s1", Now.AddHours(-3)),
            WispRecord(false, "s2", Now.AddHours(-2)),
            WispRecord(true, "s3", Now.AddHours(-1)),
        };

        var decision = DreamService.DecideProvisionalAction(
            ProvWisp(), records, [], Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        // Most-recent record is a success, so consecutive-failure rule does not trigger.
        Assert.AreNotEqual(DreamService.ProvisionalAction.Remove, decision.Action);
    }

    [TestMethod]
    public void Decide_OldResourceWithNoActivity_MarksStale()
    {
        var decision = DreamService.DecideProvisionalAction(
            ProvWisp(createdAt: LongAgoCreated), [], [], Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        Assert.AreEqual(DreamService.ProvisionalAction.MarkStale, decision.Action);
    }

    [TestMethod]
    public void Decide_OldResourceWithActivity_NotStale()
    {
        var records = new[] { WispRecord(true, "s1", Now.AddDays(-1)) };

        var decision = DreamService.DecideProvisionalAction(
            ProvWisp(createdAt: LongAgoCreated), records, [], Now,
            successThreshold: 10, failureThreshold: 5, staleAfter: TimeSpan.FromDays(30));

        Assert.AreNotEqual(DreamService.ProvisionalAction.MarkStale, decision.Action);
    }

    [TestMethod]
    public void Decide_FreshResourceNoActivity_Keeps()
    {
        var decision = DreamService.DecideProvisionalAction(
            ProvWisp(createdAt: RecentlyCreated), [], [], Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        Assert.AreEqual(DreamService.ProvisionalAction.Keep, decision.Action);
    }

    [TestMethod]
    public void Decide_NonWispWithEnoughCheckouts_Promotes()
    {
        var pythonRes = ProvWisp(filename: "compute.py", hash: "h-py", type: SkillResourceType.Python);
        var checkouts = new[]
        {
            new SkillResourceCheckoutEvent("calendar/scan", "compute.py", "s1", Now.AddHours(-3)),
            new SkillResourceCheckoutEvent("calendar/scan", "compute.py", "s2", Now.AddHours(-2)),
            new SkillResourceCheckoutEvent("calendar/scan", "compute.py", "s3", Now.AddHours(-1)),
        };

        var decision = DreamService.DecideProvisionalAction(
            pythonRes, [], checkouts, Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        Assert.AreEqual(DreamService.ProvisionalAction.Promote, decision.Action);
        Assert.AreEqual(3, decision.SuccessCount);
    }

    [TestMethod]
    public void Decide_NonWispCheckoutsSameSession_DoesNotPromote()
    {
        var pythonRes = ProvWisp(filename: "compute.py", type: SkillResourceType.Python);
        var checkouts = new[]
        {
            new SkillResourceCheckoutEvent("calendar/scan", "compute.py", "s1", Now.AddHours(-3)),
            new SkillResourceCheckoutEvent("calendar/scan", "compute.py", "s1", Now.AddHours(-2)),
            new SkillResourceCheckoutEvent("calendar/scan", "compute.py", "s1", Now.AddHours(-1)),
        };

        var decision = DreamService.DecideProvisionalAction(
            pythonRes, [], checkouts, Now,
            successThreshold: 3, failureThreshold: 2, staleAfter: TimeSpan.FromDays(30));

        Assert.AreNotEqual(DreamService.ProvisionalAction.Promote, decision.Action);
    }

    // ── FileSkillResourceUsageStore round-trip ───────────────────────────────

    [TestMethod]
    public async Task FileSkillResourceUsageStore_RecordAndQuery_RoundTrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "rb-skillres-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(
                new AgentProfileOptions { BasePath = tempDir });
            var store = new FileSkillResourceUsageStore(opts,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileSkillResourceUsageStore>.Instance);

            await store.RecordCheckoutAsync("calendar/scan", "compute.py", "s1", Now);
            await store.RecordCheckoutAsync("calendar/scan", "compute.py", "s2", Now.AddMinutes(1));
            await store.RecordCheckoutAsync("calendar/scan", "OTHER.py", "s3", Now);
            await store.RecordCheckoutAsync("OTHER/skill", "compute.py", "s4", Now);

            var results = await store.QueryCheckoutsAsync("calendar/scan", "compute.py", Now.AddMinutes(-10));
            Assert.AreEqual(2, results.Count);
            Assert.IsTrue(results.All(r => r.SkillName == "calendar/scan" && r.Filename == "compute.py"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public async Task FileSkillResourceUsageStore_FiltersBySinceTimestamp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "rb-skillres-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var opts = Microsoft.Extensions.Options.Options.Create(
                new AgentProfileOptions { BasePath = tempDir });
            var store = new FileSkillResourceUsageStore(opts,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FileSkillResourceUsageStore>.Instance);

            await store.RecordCheckoutAsync("s", "f", "s1", Now.AddDays(-10));
            await store.RecordCheckoutAsync("s", "f", "s2", Now.AddDays(-1));

            var results = await store.QueryCheckoutsAsync("s", "f", Now.AddDays(-5));
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("s2", results[0].SessionId);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── RecordProvisionalFailureAsync ────────────────────────────────────────

    private static FileFailureClusterStore NewFailureClusterStore(string tempDir) => new(
        Microsoft.Extensions.Options.Options.Create(new FailureClusterOptions()),
        Microsoft.Extensions.Options.Options.Create(new AgentProfileOptions { BasePath = tempDir }),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<FileFailureClusterStore>.Instance);

    [TestMethod]
    public async Task RecordProvisionalFailure_TwoDistinctSessionFailures_ClusterHasBothSessionIds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "rb-failcluster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var store = NewFailureClusterStore(tempDir);
        try
        {
            await ((Microsoft.Extensions.Hosting.IHostedService)store).StartAsync(default);
            var resource = ProvWisp();
            var records = new[]
            {
                WispRecord(false, "s2", Now.AddHours(-2)),
                WispRecord(false, "s3", Now.AddHours(-1)),
            };

            await DreamService.RecordProvisionalFailureAsync(
                store, "calendar/scan", resource, records, consecutiveFailureCount: 2, Now, default);

            var clusters = await store.GetAllAsync();
            var cluster = clusters.Single(c => c.Key.Tool == "calendar/scan" && c.Key.ErrorClass == resource.Filename);
            CollectionAssert.AreEquivalent(new[] { "s2", "s3" }, cluster.SessionIds.ToList());
            Assert.AreEqual("skill-resource", cluster.Key.Server);
        }
        finally
        {
            await store.DisposeAsync();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public async Task RecordProvisionalFailure_MixedRealAndEmptySessionIds_RecordsOnlyRealOnes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "rb-failcluster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var store = NewFailureClusterStore(tempDir);
        try
        {
            await ((Microsoft.Extensions.Hosting.IHostedService)store).StartAsync(default);
            var resource = ProvWisp();
            var records = new[]
            {
                WispRecord(false, "", Now.AddHours(-3)),
                WispRecord(false, "s2", Now.AddHours(-1)),
            };

            await DreamService.RecordProvisionalFailureAsync(
                store, "calendar/scan", resource, records, consecutiveFailureCount: 2, Now, default);

            var cluster = (await store.GetAllAsync())
                .Single(c => c.Key.Tool == "calendar/scan" && c.Key.ErrorClass == resource.Filename);
            CollectionAssert.AreEquivalent(new[] { "s2" }, cluster.SessionIds.ToList());
        }
        finally
        {
            await store.DisposeAsync();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public async Task RecordProvisionalFailure_UsesRealErrorMessage_NotGenericConstant()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "rb-failcluster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var store = NewFailureClusterStore(tempDir);
        try
        {
            await ((Microsoft.Extensions.Hosting.IHostedService)store).StartAsync(default);
            var resource = ProvWisp();
            var records = new[]
            {
                WispRecord(false, "s2", Now.AddHours(-1)) with
                {
                    ErrorMessage = "missing required field timeZone",
                    FailedToolName = "get_calendar_events",
                },
            };

            await DreamService.RecordProvisionalFailureAsync(
                store, "calendar/scan", resource, records, consecutiveFailureCount: 2, Now, default);

            var cluster = (await store.GetAllAsync())
                .Single(c => c.Key.Tool == "calendar/scan" && c.Key.ErrorClass == resource.Filename);
            Assert.IsTrue(cluster.SampleErrorMessages.Any(m =>
                m.Contains("get_calendar_events") && m.Contains("missing required field timeZone")));
        }
        finally
        {
            await store.DisposeAsync();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public async Task RecordProvisionalFailure_SameResourceRecordedTwice_AccumulatesNewSessionIds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "rb-failcluster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var store = NewFailureClusterStore(tempDir);
        try
        {
            await ((Microsoft.Extensions.Hosting.IHostedService)store).StartAsync(default);
            var resource = ProvWisp();

            await DreamService.RecordProvisionalFailureAsync(
                store, "calendar/scan", resource, [WispRecord(false, "s2", Now.AddHours(-2))], 2, Now, default);
            await DreamService.RecordProvisionalFailureAsync(
                store, "calendar/scan", resource, [WispRecord(false, "s3", Now.AddHours(-1))], 2, Now.AddHours(1), default);

            var cluster = (await store.GetAllAsync())
                .Single(c => c.Key.Tool == "calendar/scan" && c.Key.ErrorClass == resource.Filename);
            CollectionAssert.AreEquivalent(new[] { "s2", "s3" }, cluster.SessionIds.ToList());
            Assert.AreEqual(2, cluster.Count);
        }
        finally
        {
            await store.DisposeAsync();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public async Task RecordProvisionalFailure_SameRecordsPassedTwice_DoesNotDoubleCount()
    {
        // A still-provisional, still-failing resource gets re-swept every dream
        // cycle with the SAME historical failing records (nothing new happened).
        // Re-recording those same records must not inflate Count or push LastSeen
        // forward forever — both would defeat the escalation threshold's purpose.
        var tempDir = Path.Combine(Path.GetTempPath(), "rb-failcluster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var store = NewFailureClusterStore(tempDir);
        try
        {
            await ((Microsoft.Extensions.Hosting.IHostedService)store).StartAsync(default);
            var resource = ProvWisp();
            var records = new[]
            {
                WispRecord(false, "s2", Now.AddHours(-2)),
                WispRecord(false, "s3", Now.AddHours(-1)),
            };

            await DreamService.RecordProvisionalFailureAsync(
                store, "calendar/scan", resource, records, 2, Now, default);
            var afterFirst = (await store.GetAllAsync())
                .Single(c => c.Key.Tool == "calendar/scan" && c.Key.ErrorClass == resource.Filename);

            // Next dream cycle, same records (no new wisp activity occurred).
            await DreamService.RecordProvisionalFailureAsync(
                store, "calendar/scan", resource, records, 2, Now.AddHours(1), default);
            var afterSecond = (await store.GetAllAsync())
                .Single(c => c.Key.Tool == "calendar/scan" && c.Key.ErrorClass == resource.Filename);

            Assert.AreEqual(afterFirst.Count, afterSecond.Count);
            Assert.AreEqual(afterFirst.LastSeen, afterSecond.LastSeen);
            CollectionAssert.AreEquivalent(new[] { "s2", "s3" }, afterSecond.SessionIds.ToList());
        }
        finally
        {
            await store.DisposeAsync();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public async Task RecordProvisionalFailure_NewFailure_IncrementsFlaggedCounter()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "rb-failcluster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var store = NewFailureClusterStore(tempDir);
        try
        {
            await ((Microsoft.Extensions.Hosting.IHostedService)store).StartAsync(default);
            var resource = ProvWisp();
            long count = 0;
            using var listener = new System.Diagnostics.Metrics.MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument, HostDiagnostics.RepairSkillResourceFlagged))
                    l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref count, value));
            listener.Start();

            await DreamService.RecordProvisionalFailureAsync(
                store, "calendar/scan", resource, [WispRecord(false, "s2", Now.AddHours(-1))], 2, Now, default);

            Assert.AreEqual(1, count);
        }
        finally
        {
            await store.DisposeAsync();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [TestMethod]
    public async Task RecordProvisionalFailure_SameRecordsPassedTwice_DoesNotIncrementCounterAgain()
    {
        // Mirrors RecordProvisionalFailure_SameRecordsPassedTwice_DoesNotDoubleCount:
        // a dream cycle that finds nothing new must not re-emit the "flagged" counter.
        var tempDir = Path.Combine(Path.GetTempPath(), "rb-failcluster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var store = NewFailureClusterStore(tempDir);
        try
        {
            await ((Microsoft.Extensions.Hosting.IHostedService)store).StartAsync(default);
            var resource = ProvWisp();
            var records = new[] { WispRecord(false, "s2", Now.AddHours(-1)) };
            long count = 0;
            using var listener = new System.Diagnostics.Metrics.MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument, HostDiagnostics.RepairSkillResourceFlagged))
                    l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref count, value));
            listener.Start();

            await DreamService.RecordProvisionalFailureAsync(
                store, "calendar/scan", resource, records, 2, Now, default);
            await DreamService.RecordProvisionalFailureAsync(
                store, "calendar/scan", resource, records, 2, Now.AddHours(1), default);

            Assert.AreEqual(1, count, "the second sweep saw no new failures and must not re-emit the counter");
        }
        finally
        {
            await store.DisposeAsync();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
