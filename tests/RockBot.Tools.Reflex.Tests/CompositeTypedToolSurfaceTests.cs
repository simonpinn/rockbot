using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;
using RockBot.Tools;
using RockBot.Tools.Mcp;

namespace RockBot.Tools.Reflex.Tests;

[TestClass]
public class CompositeTypedToolSurfaceTests
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

    private static (CompositeTypedToolSurface Composite, ReflexToolSurface Reflex, FakeLookup Lookup, FakeToolRegistry Registry) New()
    {
        var mcp = new McpTypedToolSurface(Options.Create(new McpToolSurfaceOptions()), NullLogger<McpTypedToolSurface>.Instance);
        var registry = new FakeToolRegistry();
        var lookup = new FakeLookup();
        var reflex = new ReflexToolSurface(registry, NullLogger<ReflexToolSurface>.Instance);
        reflex.Bind(lookup);
        return (new CompositeTypedToolSurface(mcp, reflex), reflex, lookup, registry);
    }

    [TestMethod]
    public void ModeFor_DelegatesToMcp()
    {
        var (composite, _, _, _) = New();
        // McpToolSurfaceOptions.WrapperMode defaults to Pinned with no config bound, matching
        // the deployed Helm default (McpBridge__WrapperMode=Pinned).
        Assert.AreEqual(TypedToolMode.Pinned, composite.ModeFor(ModelTier.Low));
    }

    [TestMethod]
    public void IsTypedTool_TrueWhenEitherSurfaceClaimsIt()
    {
        var (composite, _, lookup, registry) = New();
        lookup.Tools["reflex__s__a"] = ("s", "a");
        registry.Register(new ToolRegistration { Name = "reflex__s__a", Description = "d", Source = "reflex" }, new FakeToolExecutor());

        Assert.IsTrue(composite.IsTypedTool("reflex__s__a"));
        Assert.IsFalse(composite.IsTypedTool("unknown_tool"));
    }

    [TestMethod]
    public void ActivateByName_UnknownToEither_ReturnsNull()
    {
        var (composite, _, _, _) = New();
        Assert.IsNull(composite.ActivateByName("s1", "unknown_tool"));
    }

    [TestMethod]
    public void ActivateByName_KnownReflexTool_ActivatesViaReflexSurface()
    {
        var (composite, reflex, lookup, registry) = New();
        lookup.Tools["reflex__s__a"] = ("s", "a");
        registry.Register(new ToolRegistration { Name = "reflex__s__a", Description = "d", Source = "reflex" }, new FakeToolExecutor());

        var fn = composite.ActivateByName("s1", "reflex__s__a");

        Assert.IsNotNull(fn);
        Assert.IsTrue(reflex.IsActivated("s1", "reflex__s__a"));
    }

    [TestMethod]
    public void IsTypedTool_AmbientModeOff_ReflexToolIsExemptSoItStaysInTheEagerList()
    {
        var mcp = new McpTypedToolSurface(
            Options.Create(new McpToolSurfaceOptions { WrapperMode = McpWrapperMode.Off }),
            NullLogger<McpTypedToolSurface>.Instance);
        var registry = new FakeToolRegistry();
        var lookup = new FakeLookup();
        var reflex = new ReflexToolSurface(registry, NullLogger<ReflexToolSurface>.Instance);
        reflex.Bind(lookup);
        var composite = new CompositeTypedToolSurface(mcp, reflex);
        lookup.Tools["reflex__s__a"] = ("s", "a");
        registry.Register(new ToolRegistration { Name = "reflex__s__a", Description = "d", Source = "reflex" }, new FakeToolExecutor());

        using var scope = TypedToolSurfaceContext.Set(composite, ModelTier.Low);
        var options = new ChatOptions { Tools = [new FakeAiFunction("reflex__s__a")] };
        var removed = TypedToolSurfaceContext.Shape(options);

        Assert.AreEqual(0, removed, "a reflex tool must not be dropped from the list on an Off tier, " +
            "since Off mode never re-adds anything and Phase 2 shipped reflex tools always-available");
    }

    private sealed class FakeAiFunction(string name) : AIFunction
    {
        public override string Name => name;
        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(null);
    }

    private sealed class FakeLoaderTool(string name, string? sessionId) : AIFunction, ISessionBoundTool
    {
        public string? SessionId => sessionId;
        public override string Name => name;
        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(null);
    }

    [TestMethod]
    public void EndToEnd_PinnedSkillSurfacedUnderTheToolSessionId_ReachesTheFinalToolList()
    {
        // Exercises the exact seam that let Critical #1 (pins keyed by the wrong session id)
        // slip past every unit test: AgentContextBuilder's hook call -> ReflexToolSurface.Pin ->
        // CompositeTypedToolSurface.GetActivated -> TypedToolSurfaceContext.Set/AddActivated.
        var (composite, reflex, lookup, registry) = New();
        lookup.Tools["reflex__s__a"] = ("s", "a");
        registry.Register(new ToolRegistration { Name = "reflex__s__a", Description = "d", Source = "reflex" }, new FakeToolExecutor());

        ((IReflexActivationHooks)reflex).OnSkillSurfaced("session/sess1", "s");

        using var scope = TypedToolSurfaceContext.Set(composite, ModelTier.Low); // Pinned by default
        var options = new ChatOptions { Tools = [new FakeLoaderTool(composite.LoaderToolName, "session/sess1")] };
        var added = TypedToolSurfaceContext.AddActivated(options);

        Assert.AreEqual(1, added);
        Assert.IsTrue(options.Tools!.Any(t => t.Name == "reflex__s__a"),
            "the reflex tool pinned under the real tool session id must reach the final tool list");
    }

    [TestMethod]
    public void GetActivated_ConcatenatesMcpThenReflex()
    {
        var (composite, reflex, lookup, registry) = New();
        lookup.Tools["reflex__s__a"] = ("s", "a");
        registry.Register(new ToolRegistration { Name = "reflex__s__a", Description = "d", Source = "reflex" }, new FakeToolExecutor());
        reflex.ActivateByName("s1", "reflex__s__a");

        var names = composite.GetActivated("s1", TypedToolMode.Lazy).Select(f => f.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "reflex__s__a" }, names); // Mcp's surface has nothing activated for s1
    }
}
