using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Stimulus.Tests;

[TestClass]
public class StimulusRegistryLoaderTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rockbot-stimuli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string name, string json)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private const string ValidPatternJson = """
        {
          "id": "calendar-transient-retry",
          "enabled": true,
          "topic": "tool.invoke.failed",
          "channels": { "payload:server": "calendar-mcp", "payload:errorClass": "timeout" },
          "reflexTool": "reflex__calendar__retry-transient-timeout",
          "minIntervalSeconds": 30
        }
        """;

    [TestMethod]
    public void LoadPatternsFromDirectory_ValidPatternFile_IsLoaded()
    {
        Write("calendar-transient-retry.json", ValidPatternJson);

        var patterns = StimulusRegistryLoader.LoadPatternsFromDirectory(_dir, NullLogger.Instance);

        Assert.AreEqual(1, patterns.Count);
        Assert.AreEqual("calendar-transient-retry", patterns[0].Id);
        Assert.AreEqual("reflex__calendar__retry-transient-timeout", patterns[0].ReflexTool);
        Assert.AreEqual(30, patterns[0].MinIntervalSeconds);
    }

    [TestMethod]
    public void LoadPatternsFromDirectory_MalformedJson_IsSkipped_RestOfDirectoryStillLoads()
    {
        Write("broken.json", "not valid json {{{");
        Write("good.json", ValidPatternJson);

        var patterns = StimulusRegistryLoader.LoadPatternsFromDirectory(_dir, NullLogger.Instance);

        Assert.AreEqual(1, patterns.Count);
        Assert.AreEqual("calendar-transient-retry", patterns[0].Id);
    }

    [TestMethod]
    public void LoadPatternsFromDirectory_ReflexToolMissingPrefix_IsRejectedAndExcluded()
    {
        const string json = """
            {
              "id": "bad-tool-name",
              "topic": "tool.invoke.failed",
              "reflexTool": "spawn_wisps"
            }
            """;
        Write("bad-tool-name.json", json);

        var patterns = StimulusRegistryLoader.LoadPatternsFromDirectory(_dir, NullLogger.Instance);

        Assert.AreEqual(0, patterns.Count);
    }

    [TestMethod]
    public void LoadPatternsFromDirectory_EmptyDirectory_ReturnsEmptyList()
    {
        var patterns = StimulusRegistryLoader.LoadPatternsFromDirectory(_dir, NullLogger.Instance);

        Assert.AreEqual(0, patterns.Count);
    }

    [TestMethod]
    public void LoadPatternsFromDirectory_MissingDirectory_ReturnsEmptyList_DoesNotThrow()
    {
        var patterns = StimulusRegistryLoader.LoadPatternsFromDirectory(
            Path.Combine(_dir, "does-not-exist"), NullLogger.Instance);

        Assert.AreEqual(0, patterns.Count);
    }

    [TestMethod]
    public void LoadPatternsFromDirectory_IgnoresNonJsonFiles()
    {
        Write("readme.md", "# not a pattern");
        Write("good.json", ValidPatternJson);

        var patterns = StimulusRegistryLoader.LoadPatternsFromDirectory(_dir, NullLogger.Instance);

        Assert.AreEqual(1, patterns.Count);
    }

    [TestMethod]
    public void LoadPatternsFromDirectory_DisabledPattern_IsStillLoaded()
    {
        // Loading is distinct from matching - the dispatcher is what skips disabled patterns.
        const string json = """
            {
              "id": "disabled-one",
              "enabled": false,
              "topic": "tool.invoke.failed",
              "reflexTool": "reflex__calendar__retry"
            }
            """;
        Write("disabled-one.json", json);

        var patterns = StimulusRegistryLoader.LoadPatternsFromDirectory(_dir, NullLogger.Instance);

        Assert.AreEqual(1, patterns.Count);
        Assert.IsFalse(patterns[0].Enabled);
    }
}
