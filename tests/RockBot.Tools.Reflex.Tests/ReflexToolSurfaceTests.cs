using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Tools.Reflex.Tests;

[TestClass]
public class ReflexToolSurfaceTests
{
    private sealed class FakeLookup : IReflexRegistrationLookup
    {
        public Dictionary<string, (string Skill, string File)> Tools { get; } = new();

        public bool TryGet(string toolName, out string skillName, out string filename)
        {
            if (Tools.TryGetValue(toolName, out var e)) { (skillName, filename) = e; return true; }
            skillName = "";
            filename = "";
            return false;
        }

        public IReadOnlyList<string> GetToolNames(string skillName) =>
            Tools.Where(kv => kv.Value.Skill == skillName).Select(kv => kv.Key).ToArray();
    }

    private static (ReflexToolSurface Surface, FakeLookup Lookup, FakeToolRegistry Registry) NewSurface()
    {
        var lookup = new FakeLookup();
        var registry = new FakeToolRegistry();
        var surface = new ReflexToolSurface(registry, NullLogger<ReflexToolSurface>.Instance);
        surface.Bind(lookup);
        return (surface, lookup, registry);
    }

    private static void RegisterTool(FakeLookup lookup, FakeToolRegistry registry, string skill, string file, string name)
    {
        lookup.Tools[name] = (skill, file);
        registry.Register(new ToolRegistration { Name = name, Description = name, Source = "reflex" }, new FakeToolExecutor());
    }

    [TestMethod]
    public void ActivateByName_UnknownTool_ReturnsNull()
    {
        var (surface, _, _) = NewSurface();
        Assert.IsNull(surface.ActivateByName("s1", "reflex__x__y"));
    }

    [TestMethod]
    public void ActivateByName_KnownTool_ActivatesAndReturnsBoundFunction()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "calendar/scan", "fanout.json", "reflex__calendar_scan__fanout_json");

        var fn = surface.ActivateByName("s1", "reflex__calendar_scan__fanout_json");

        Assert.IsNotNull(fn);
        Assert.AreEqual("reflex__calendar_scan__fanout_json", fn!.Name);
        Assert.AreEqual("s1", ((ISessionBoundTool)fn).SessionId);
        Assert.IsTrue(surface.IsActivated("s1", "reflex__calendar_scan__fanout_json"));
    }

    [TestMethod]
    public void GetActivated_EagerOrOffMode_ReturnsEmpty()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "f", "reflex__s__f");
        surface.ActivateByName("s1", "reflex__s__f");

        Assert.AreEqual(0, surface.GetActivated("s1", TypedToolMode.Eager).Count);
        Assert.AreEqual(0, surface.GetActivated("s1", TypedToolMode.Off).Count);
    }

    [TestMethod]
    public void GetActivated_Lazy_ReturnsActivatedToolsOldestFirst()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");
        RegisterTool(lookup, registry, "s", "b", "reflex__s__b");
        surface.ActivateByName("s1", "reflex__s__a");
        surface.ActivateByName("s1", "reflex__s__b");

        var names = surface.GetActivated("s1", TypedToolMode.Lazy).Select(f => f.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "reflex__s__a", "reflex__s__b" }, names);
    }

    [TestMethod]
    public void Activate_SameNameTwice_KeepsOriginalPosition()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");
        RegisterTool(lookup, registry, "s", "b", "reflex__s__b");
        surface.ActivateByName("s1", "reflex__s__a");
        surface.ActivateByName("s1", "reflex__s__b");
        surface.ActivateByName("s1", "reflex__s__a"); // re-touch

        var names = surface.GetActivated("s1", TypedToolMode.Lazy).Select(f => f.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "reflex__s__a", "reflex__s__b" }, names);
    }

    [TestMethod]
    public void Activate_OverCap_DropsOldest()
    {
        var (surface, lookup, registry) = NewSurface();
        for (var i = 0; i < 41; i++)
            RegisterTool(lookup, registry, "s", $"f{i}", $"reflex__s__f{i}");
        for (var i = 0; i < 41; i++)
            surface.ActivateByName("s1", $"reflex__s__f{i}");

        var names = surface.GetActivated("s1", TypedToolMode.Lazy).Select(f => f.Name).ToArray();
        Assert.AreEqual(40, names.Length);
        Assert.IsFalse(names.Contains("reflex__s__f0"), "oldest activation should have been dropped");
        Assert.IsTrue(names.Contains("reflex__s__f40"), "newest activation should remain");
    }

    [TestMethod]
    public void Pin_UnknownOrEmptySkill_NoOp()
    {
        var (surface, _, _) = NewSurface();
        surface.Pin("s1", "no-such-skill");
        Assert.AreEqual(0, surface.GetPinned("s1").Count);
    }

    [TestMethod]
    public void Pin_NullOrEmptySessionId_NoOp()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");
        surface.Pin(null, "s");
        surface.Pin("", "s");
        Assert.AreEqual(0, surface.GetActivated("anything", TypedToolMode.Pinned).Count);
    }

    [TestMethod]
    public void Activate_NullOrEmptySessionId_NoOp()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");
        Assert.IsNull(surface.ActivateByName(null!, "reflex__s__a"));
        Assert.IsNull(surface.ActivateByName("", "reflex__s__a"));
    }

    [TestMethod]
    public void Pin_KnownSkill_BringsAllItsReflexToolsIntoPinnedActivation()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "calendar/scan", "fanout.json", "reflex__a");
        RegisterTool(lookup, registry, "calendar/scan", "poll.json", "reflex__b");

        surface.Pin("s1", "calendar/scan");

        var names = surface.GetActivated("s1", TypedToolMode.Pinned).Select(f => f.Name).ToArray();
        CollectionAssert.AreEquivalent(new[] { "reflex__a", "reflex__b" }, names);
    }

    [TestMethod]
    public void Pin_SameSkillTwice_RefreshesRecencyWithoutDuplicating()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s1", "a", "reflex__s1__a");
        RegisterTool(lookup, registry, "s2", "a", "reflex__s2__a");
        RegisterTool(lookup, registry, "s3", "a", "reflex__s3__a");
        surface.Pin("sess", "s1");
        surface.Pin("sess", "s2");
        surface.Pin("sess", "s1"); // re-touch s1

        CollectionAssert.AreEqual(new[] { "s2", "s1" }, surface.GetPinned("sess").ToArray());
    }

    [TestMethod]
    public void Pin_OverCap_DropsOldestPinnedSkill()
    {
        var (surface, lookup, registry) = NewSurface();
        foreach (var s in new[] { "s1", "s2", "s3", "s4" })
            RegisterTool(lookup, registry, s, "a", $"reflex__{s}__a");
        foreach (var s in new[] { "s1", "s2", "s3", "s4" })
            surface.Pin("sess", s);

        CollectionAssert.AreEqual(new[] { "s2", "s3", "s4" }, surface.GetPinned("sess").ToArray());
    }

    [TestMethod]
    public void GetActivated_Pinned_SkillLosesAllReflexTools_StopsAppearingForThatSkill()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");
        surface.Pin("sess", "s");
        Assert.AreEqual(1, surface.GetActivated("sess", TypedToolMode.Pinned).Count, "precondition");

        lookup.Tools.Remove("reflex__s__a"); // reconciliation demoted/removed it

        Assert.AreEqual(0, surface.GetActivated("sess", TypedToolMode.Pinned).Count);
    }

    [TestMethod]
    public void Evict_RemovesFromEveryActivatedSession()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");
        surface.ActivateByName("s1", "reflex__s__a");
        surface.ActivateByName("s2", "reflex__s__a");

        surface.Evict(new HashSet<string> { "reflex__s__a" });

        Assert.IsFalse(surface.IsActivated("s1", "reflex__s__a"));
        Assert.IsFalse(surface.IsActivated("s2", "reflex__s__a"));
    }

    [TestMethod]
    public void IsReflexTool_DelegatesToLookup()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");
        Assert.IsTrue(surface.IsReflexTool("reflex__s__a"));
        Assert.IsFalse(surface.IsReflexTool("reflex__unknown"));
    }

    [TestMethod]
    public void OnSkillSurfaced_PinsTheSkill()
    {
        var (surface, lookup, registry) = NewSurface();
        RegisterTool(lookup, registry, "s", "a", "reflex__s__a");

        ((IReflexActivationHooks)surface).OnSkillSurfaced("sess", "s");

        Assert.AreEqual(1, surface.GetActivated("sess", TypedToolMode.Pinned).Count);
    }

    [TestMethod]
    public void TryGetToolName_ValidatedResourceWithRegisteredReflex_ReturnsItsName()
    {
        var (surface, lookup, registry) = NewSurface();
        var name = ReflexToolNames.Build("calendar/scan", "fanout.json");
        RegisterTool(lookup, registry, "calendar/scan", "fanout.json", name);

        Assert.AreEqual(name, ((IReflexActivationHooks)surface).TryGetToolName("calendar/scan", "fanout.json"));
    }

    [TestMethod]
    public void TryGetToolName_NotCurrentlyRegistered_ReturnsNull()
    {
        var (surface, _, _) = NewSurface();
        Assert.IsNull(((IReflexActivationHooks)surface).TryGetToolName("calendar/scan", "fanout.json"));
    }

    [TestMethod]
    public void UnboundSurface_EveryMemberNoOpsRatherThanThrows()
    {
        var surface = new ReflexToolSurface(new FakeToolRegistry(), NullLogger<ReflexToolSurface>.Instance);
        Assert.IsFalse(surface.IsReflexTool("anything"));
        Assert.IsNull(surface.ActivateByName("s1", "anything"));
        Assert.AreEqual(0, surface.GetActivated("s1", TypedToolMode.Pinned).Count);
        surface.Pin("s1", "anything"); // must not throw
    }
}
