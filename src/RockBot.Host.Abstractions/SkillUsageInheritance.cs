namespace RockBot.Host;

/// <summary>
/// Carries skill-invocation attribution from an orchestrating session into a
/// subagent/worker session it spawns. Without this, a subagent that never itself
/// calls <c>get_skill</c> (because the orchestrator already looked the skill up and
/// just delegates execution) leaves its own session's usage history empty — and
/// <c>DreamService</c>'s wisp-success-analysis pass, which attributes a repeated wisp
/// to a promotion target by checking only the wisp's own session, can never resolve
/// one. A pattern can then recur arbitrarily often and still never become eligible
/// for promotion. See design/known-issues.md.
/// </summary>
public static class SkillUsageInheritance
{
    /// <summary>
    /// Copies the most recently invoked skill in <paramref name="primarySessionId"/>
    /// (as of now) into <paramref name="newSessionId"/>'s own usage history, so later
    /// attribution that looks only at the new session still finds it. A no-op when
    /// <paramref name="usageStore"/> is null, <paramref name="primarySessionId"/> is
    /// null/empty, or the primary session has no recorded invocation.
    /// </summary>
    public static async Task InheritFromPrimarySessionAsync(
        ISkillUsageStore? usageStore,
        string? primarySessionId,
        string newSessionId,
        CancellationToken ct = default)
    {
        if (usageStore is null || string.IsNullOrEmpty(primarySessionId))
            return;

        var events = await usageStore.GetBySessionAsync(ToSkillUsageSessionId(primarySessionId), ct);
        var mostRecent = events.OrderByDescending(e => e.Timestamp).FirstOrDefault();
        if (mostRecent is null)
            return;

        await usageStore.AppendAsync(new SkillInvocationEvent(
            Id: Guid.NewGuid().ToString("N")[..12],
            SkillName: mostRecent.SkillName,
            SessionId: newSessionId,
            Timestamp: DateTimeOffset.UtcNow), ct);
    }

    /// <summary>
    /// Converts a session identifier that may be in one of this codebase's
    /// working-memory-namespace forms into the raw conversation-session-id form that
    /// <see cref="ISkillUsageStore"/> actually keys events under — the same form
    /// <c>get_skill</c> records under (<c>SkillTools._sessionId</c>).
    ///
    /// Two distinct identifiers exist per spawned session: a WM namespace (e.g.
    /// <c>"subagent/{taskId}"</c>, <c>"session/{rawId}"</c>) used for working-memory
    /// scoping and lineage tracking, and a conversation session id (e.g.
    /// <c>"subagent-{taskId}"</c>, the raw id) used by <c>AgentLoopRunner</c>,
    /// <c>SkillTools</c>, and the skill-usage store. Generic registry tools (including
    /// <c>spawn_wisps</c>) report their <c>ToolInvokeRequest.SessionId</c> in the
    /// WM-namespace form, so <c>WispExecutionRecord.SessionId</c> is in that form too —
    /// without this conversion, every lookup against the skill-usage store misses.
    /// </summary>
    public static string ToSkillUsageSessionId(string sessionId)
    {
        const string SessionPrefix = "session/";
        if (sessionId.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase))
            return sessionId[SessionPrefix.Length..];

        const string SubagentPrefix = "subagent/";
        if (sessionId.StartsWith(SubagentPrefix, StringComparison.OrdinalIgnoreCase))
            return "subagent-" + sessionId[SubagentPrefix.Length..];

        const string WorkerPrefix = "worker/";
        if (sessionId.StartsWith(WorkerPrefix, StringComparison.OrdinalIgnoreCase))
            return "worker-" + sessionId[WorkerPrefix.Length..];

        return sessionId;
    }
}
