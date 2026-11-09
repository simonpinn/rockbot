using RockBot.UserProxy;
using RockBot.UserProxy.Blazor.Services;

namespace RockBot.UserProxy.Blazor.Tests;

[TestClass]
public class ReflexActivityStateServiceTests
{
    private static ReflexFiredEvent NewEvent(string toolName = "reflex__calendar__retry", string trigger = "llm") => new()
    {
        ToolName = toolName,
        SkillName = "calendar/scan",
        Filename = "retry.json",
        Succeeded = true,
        FiredAt = DateTimeOffset.UtcNow,
        DurationMs = 12,
        Trigger = trigger,
        SessionId = trigger == "llm" ? "s1" : null,
    };

    [TestMethod]
    public void Append_AddsEventToTheFrontOfTheFeed()
    {
        var state = new ReflexActivityStateService();

        state.Append(NewEvent("reflex__a__x"));
        state.Append(NewEvent("reflex__b__y"));

        Assert.AreEqual("reflex__b__y", state.Events[0].ToolName, "newest first");
        Assert.AreEqual("reflex__a__x", state.Events[1].ToolName);
    }

    [TestMethod]
    public void Append_RaisesOnStateChanged()
    {
        var state = new ReflexActivityStateService();
        var raised = false;
        state.OnStateChanged += () => raised = true;

        state.Append(NewEvent());

        Assert.IsTrue(raised);
    }

    [TestMethod]
    public void Append_BeyondCapacity_EvictsOldestEntries()
    {
        var state = new ReflexActivityStateService(capacity: 3);

        for (var i = 0; i < 5; i++)
            state.Append(NewEvent($"reflex__a__{i}"));

        Assert.AreEqual(3, state.Events.Count);
        Assert.AreEqual("reflex__a__4", state.Events[0].ToolName, "newest kept");
        Assert.AreEqual("reflex__a__2", state.Events[2].ToolName, "oldest surviving entry");
    }

    [TestMethod]
    public void Events_IsIndependentOfChatSessions_NoClearMethodTiedToAChatTurn()
    {
        // The whole point of this service (design/cognitive-tiers.md: "a view onto a
        // background consciousness") is that it survives chat-page reloads / per-session
        // state resets. There is deliberately no Clear() tied to a session lifecycle.
        var state = new ReflexActivityStateService();
        state.Append(NewEvent());

        Assert.AreEqual(1, state.Events.Count);
    }
}
