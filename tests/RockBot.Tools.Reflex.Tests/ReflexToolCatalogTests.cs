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
                    new SkillResource("fanout.json", SkillResourceType.Wisp, "d", Provisional: false),
                ]),
            ],
        };
        var registry = new FakeToolRegistry();
        var catalog = NewCatalog(skillStore, registry);

        await catalog.ReconcileOnceAsync(CancellationToken.None);

        var expectedName = ReflexToolNames.Build("calendar/scan", "fanout.json");
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
        var resource = new SkillResource("fanout.json", SkillResourceType.Wisp, "d", Provisional: false);
        var skillStore = new FakeSkillStore
        {
            Skills = [new Skill("calendar/scan", "s", "c", DateTimeOffset.UtcNow, Manifest: [resource])],
        };
        var registry = new FakeToolRegistry();
        var catalog = NewCatalog(skillStore, registry);
        await catalog.ReconcileOnceAsync(CancellationToken.None);
        var name = ReflexToolNames.Build("calendar/scan", "fanout.json");
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
    public async Task ReconcileOnceAsync_TwoSkillsSanitizeToSameToolName_SkipsSecondWithoutThrowing()
    {
        var skillStore = new FakeSkillStore
        {
            Skills =
            [
                new Skill("calendar_scan", "s", "c", DateTimeOffset.UtcNow, Manifest:
                    [new SkillResource("x.json", SkillResourceType.Wisp, "d", Provisional: false)]),
                new Skill("calendar/scan", "s", "c", DateTimeOffset.UtcNow, Manifest:
                    [new SkillResource("x.json", SkillResourceType.Wisp, "d", Provisional: false)]),
            ],
        };
        var registry = new FakeToolRegistry();
        var catalog = NewCatalog(skillStore, registry);

        await catalog.ReconcileOnceAsync(CancellationToken.None);

        var name = ReflexToolNames.Build("calendar_scan", "x.json");
        Assert.AreEqual(1, registry.GetTools().Count(t => t.Name == name));
    }

    [TestMethod]
    public async Task ReconcileOnceAsync_NameLongerThanProviderLimit_SkipsRegistration()
    {
        var skillStore = new FakeSkillStore
        {
            Skills =
            [
                new Skill(new string('a', 100), "s", "c", DateTimeOffset.UtcNow, Manifest:
                    [new SkillResource("x.json", SkillResourceType.Wisp, "d", Provisional: false)]),
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
                    [new SkillResource("fanout.json", SkillResourceType.Wisp, "d", Provisional: false)]),
            ],
        };
        var registry = new FakeToolRegistry();
        var name = ReflexToolNames.Build("calendar/scan", "fanout.json");
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

    private static ReflexToolCatalog NewCatalog(ISkillStore skillStore, IToolRegistry registry) =>
        new(skillStore, registry, () => StubExecutor, NullLogger<ReflexToolCatalog>.Instance);
}
