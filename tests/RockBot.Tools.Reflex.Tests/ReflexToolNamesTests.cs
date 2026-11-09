namespace RockBot.Tools.Reflex.Tests;

[TestClass]
public class ReflexToolNamesTests
{
    [TestMethod]
    public void Build_SimpleSkillAndFilename_UsesPrefixAndSeparator()
    {
        // The filename's '.' must NOT survive: Anthropic/OpenAI tool names are
        // ^[a-zA-Z0-9_-]{1,64}$ (see McpWrapperNaming) - a literal '.' makes every
        // provider reject the whole request once a reflex tool is registered.
        Assert.AreEqual("reflex__calendar__fanout-json", ReflexToolNames.Build("calendar", "fanout.json"));
    }

    [TestMethod]
    public void Build_SkillNameWithSlash_SanitizesSlashToHyphen()
    {
        // Matches McpWrapperNaming's own replacement character ('-'), not '_', so the
        // two naming schemes use one consistent sanitization convention.
        Assert.AreEqual("reflex__calendar-scan__fanout-json", ReflexToolNames.Build("calendar/scan", "fanout.json"));
    }

    [TestMethod]
    public void Build_NameLongerThanProviderLimit_IsNotTruncated()
    {
        // Truncation collides silently (McpWrapperNaming's own reasoning) - Build itself
        // never truncates; the caller (ReflexToolCatalog) checks FitsProviderLimit and
        // skips registering an over-limit name instead.
        var longSkillName = new string('a', 100);
        var built = ReflexToolNames.Build(longSkillName, "x.json");
        Assert.IsTrue(built.Length > 64);
        StringAssert.Contains(built, longSkillName);
    }
}
