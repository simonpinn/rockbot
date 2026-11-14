using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Tools.Reflex.Tests;

[TestClass]
public class ReflexToolCatalogTests
{
    private static readonly IToolExecutor StubExecutor = new FakeToolExecutor();

    [TestMethod]
    public async Task ReconcileOnceAsync_ValidatedWispResource_RegistersReflexTool()
    {
        var skillStore = new FakeSkillStore
        {
            Skills =
            [
                new Skill("calendar/scan", "s", "c", DateTimeOffset.UtcNow, Manifest:
                [
                    new SkillResource("fanout.json", SkillResourceType.Wisp, "d", Provisional: false, DefinitionHash: "hash1"),
                ]),
            ],
        };
        var registry = new FakeToolRegistry();
        var catalog = NewCatalog(skillStore, registry);

        await catalog.ReconcileOnceAsync(CancellationToken.None);

        var expectedName = ReflexToolNames.Build("hash1");
        Assert.IsTrue(registry.GetTools().Any(t => t.Name == expectedName));
        Assert.IsTrue(((IReflexRegistrationLookup)catalog).TryGet(expectedName, out var skillName, out var filename));
        Assert.AreEqual("calendar/scan", skillName);
        Assert.AreEqual("fanout.json", filename);
    }

    [TestMethod]
    public async Task ReconcileOnceAsync_ProvisionalOrNonWispResource_NeverRegistered()
    {
        var skillStore = new FakeSkillStore
        {
            Skills =
            [
                new Skill("calendar/scan", "s", "c", DateTimeOffset.UtcNow, Manifest:
                [
                    new SkillResource("still-provisional.json", SkillResourceType.Wisp, "d", Provisional: true),
                    new SkillResource("compute.py", SkillResourceType.Python, "d", Provisional: false),
                ]),
            ],
        };
        var registry = new FakeToolRegistry();
        var catalog = NewCatalog(skillStore, registry);

        await catalog.ReconcileOnceAsync(CancellationToken.None);

        Assert.AreEqual(0, registry.GetTools().Count);
    }

    [TestMethod]
    public async Task ReconcileOnceAsync_ResourceDemotedSinceLastCycle_UnregistersTool()
    {
        var resource = new SkillResource("fanout.json", SkillResourceType.Wisp, "d", Provisional: false, DefinitionHash: "hash1");
        var skillStore = new FakeSkillStore
        {
            Skills = [new Skill("calendar/scan", "s", "c", DateTimeOffset.UtcNow, Manifest: [resource])],
        };
        var registry = new FakeToolRegistry();
        var catalog = NewCatalog(skillStore, registry);
        await catalog.ReconcileOnceAsync(CancellationToken.None);
        var name = ReflexToolNames.Build("hash1");
        Assert.IsTrue(registry.GetTools().Any(t => t.Name == name), "precondition: registered after cycle 1");

        // Phase 1's repair path demoted it back to provisional between cycles.
        skillStore.Skills[0] = skillStore.Skills[0] with
        {
            Manifest = [resource with { Provisional = true }],
        };
        await catalog.ReconcileOnceAsync(CancellationToken.None);

        Assert.IsFalse(registry.GetTools().Any(t => t.Name == name));
        Assert.IsFalse(((IReflexRegistrationLookup)catalog).TryGet(name, out _, out _));
    }

    [TestMethod]
    public async Task ReconcileOnceAsync_TwoResourcesShareDefinitionHash_SkipsSecondWithoutThrowing()
    {
        var skillStore = new FakeSkillStore
        {
            Skills =
            [
                new Skill("skill-a", "s", "c", DateTimeOffset.UtcNow, Manifest:
                    [new SkillResource("x.json", SkillResourceType.Wisp, "d", Provisional: false, DefinitionHash: "samehash")]),
                new Skill("skill-b", "s", "c", DateTimeOffset.UtcNow, Manifest:
                    [new SkillResource("y.json", SkillResourceType.Wisp, "d", Provisional: false, DefinitionHash: "samehash")]),
            ],
        };
        var registry = new FakeToolRegistry();
        var catalog = NewCatalog(skillStore, registry);

        await catalog.ReconcileOnceAsync(CancellationToken.None);

        var name = ReflexToolNames.Build("samehash");
        Assert.AreEqual(1, registry.GetTools().Count(t => t.Name == name));
    }

    [TestMethod]
    public async Task ReconcileOnceAsync_ResourceWithNoDefinitionHash_SkipsRegistrationWithoutThrowing()
    {
        var skillStore = new FakeSkillStore
        {
            Skills =
            [
                new Skill("calendar/scan", "s", "c", DateTimeOffset.UtcNow, Manifest:
                    [new SkillResource("x.json", SkillResourceType.Wisp, "d", Provisional: false, DefinitionHash: null)]),
            ],
        };
        var registry = new FakeToolRegistry();
        var catalog = NewCatalog(skillStore, registry);

        await catalog.ReconcileOnceAsync(CancellationToken.None);

        Assert.AreEqual(0, registry.GetTools().Count);
    }

    [TestMethod]
    public async Task ReconcileOnceAsync_RegistryThrowsOnRegister_NameNotTrackedAsOwned_RetriedNextCycle()
    {
        var skillStore = new FakeSkillStore
        {
            Skills =
            [
                new Skill("calendar/scan", "s", "c", DateTimeOffset.UtcNow, Manifest:
                    [new SkillResource("fanout.json", SkillResourceType.Wisp, "d", Provisional: false, DefinitionHash: "hash1")]),
            ],
        };
        var registry = new FakeToolRegistry();
        var name = ReflexToolNames.Build("hash1");
        // Some other component already owns this exact name - Register will throw.
        registry.Register(new ToolRegistration { Name = name, Description = "other", Source = "other" }, StubExecutor);
        var catalog = NewCatalog(skillStore, registry);

        await catalog.ReconcileOnceAsync(CancellationToken.None);

        Assert.IsFalse(((IReflexRegistrationLookup)catalog).TryGet(name, out _, out _),
            "a failed registration must not be recorded as owned by this catalog");
        Assert.AreEqual("other", registry.GetTools().Single(t => t.Name == name).Source,
            "the name must still belong to whoever actually registered it");

        // Next cycle must retry rather than skip forever, since current/_byName never claimed it.
        registry.Unregister(name);
        await catalog.ReconcileOnceAsync(CancellationToken.None);
        Assert.IsTrue(((IReflexRegistrationLookup)catalog).TryGet(name, out _, out _),
            "once the name frees up, the next cycle must successfully claim it");
    }

    [TestMethod]
    public async Task ReconcileOnceAsync_ResourceDemotedSinceLastCycle_EvictsFromBoundSurface()
    {
        var resource = new SkillResource("fanout.json", SkillResourceType.Wisp, "d", Provisional: false, DefinitionHash: "hash1");
        var skillStore = new FakeSkillStore
        {
            Skills = [new Skill("calendar/scan", "s", "c", DateTimeOffset.UtcNow, Manifest: [resource])],
        };
        var registry = new FakeToolRegistry();
        var catalog = NewCatalog(skillStore, registry);
        var surface = new ReflexToolSurface(registry, NullLogger<ReflexToolSurface>.Instance);
        surface.Bind((IReflexRegistrationLookup)catalog);
        catalog.Bind(surface);

        await catalog.ReconcileOnceAsync(CancellationToken.None);
        var name = ReflexToolNames.Build("hash1");
        surface.ActivateByName("sess", name);
        Assert.IsTrue(surface.IsActivated("sess", name), "precondition");

        skillStore.Skills[0] = skillStore.Skills[0] with
        {
            Manifest = [resource with { Provisional = true }],
        };
        await catalog.ReconcileOnceAsync(CancellationToken.None);

        Assert.IsFalse(surface.IsActivated("sess", name));
    }

    [TestMethod]
    public async Task ReconcileOnceAsync_SkillRenamedBetweenCycles_ToolNameRemainsRegistered()
    {
        var resource = new SkillResource("fanout.json", SkillResourceType.Wisp, "d", Provisional: false, DefinitionHash: "stablehash1");
        var skillStore = new FakeSkillStore
        {
            Skills = [new Skill("calendar/scan", "s", "c", DateTimeOffset.UtcNow, Manifest: [resource])],
        };
        var registry = new FakeToolRegistry();
        var catalog = NewCatalog(skillStore, registry);
        await catalog.ReconcileOnceAsync(CancellationToken.None);
        var name = ReflexToolNames.Build("stablehash1");
        Assert.IsTrue(registry.GetTools().Any(t => t.Name == name), "precondition: registered after cycle 1");

        // A dream rename changed the skill's display name; the resource (and its hash) are unchanged.
        skillStore.Skills[0] = skillStore.Skills[0] with { Name = "calendar/scan-renamed" };
        await catalog.ReconcileOnceAsync(CancellationToken.None);

        Assert.IsTrue(registry.GetTools().Any(t => t.Name == name), "same hash must keep the same tool name across a rename");
        Assert.IsTrue(((IReflexRegistrationLookup)catalog).TryGet(name, out var skillName, out _));
        Assert.AreEqual("calendar/scan-renamed", skillName, "lookup must reflect the resource's current skill name");
    }

    [TestMethod]
    public async Task GetToolNames_ReturnsOnlyToolsRegisteredForThatSkill()
    {
        var skillStore = new FakeSkillStore
        {
            Skills =
            [
                new Skill("calendar/scan", "s", "c", DateTimeOffset.UtcNow, Manifest:
                    [new SkillResource("fanout.json", SkillResourceType.Wisp, "d", Provisional: false, DefinitionHash: "hashA")]),
                new Skill("other/skill", "s", "c", DateTimeOffset.UtcNow, Manifest:
                    [new SkillResource("x.json", SkillResourceType.Wisp, "d", Provisional: false, DefinitionHash: "hashB")]),
            ],
        };
        var registry = new FakeToolRegistry();
        var catalog = NewCatalog(skillStore, registry);
        await catalog.ReconcileOnceAsync(CancellationToken.None);

        var lookup = (IReflexRegistrationLookup)catalog;
        var expected = ReflexToolNames.Build("hashA");
        CollectionAssert.AreEqual(new[] { expected }, lookup.GetToolNames("calendar/scan").ToArray());
        Assert.AreEqual(0, lookup.GetToolNames("no-such-skill").Count);
    }

    private static ReflexToolCatalog NewCatalog(ISkillStore skillStore, IToolRegistry registry) =>
        new(skillStore, registry, () => StubExecutor, NullLogger<ReflexToolCatalog>.Instance);
}
