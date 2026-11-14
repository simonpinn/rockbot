namespace RockBot.Tools.Reflex.Tests;

[TestClass]
public class ReflexToolNamesTests
{
    [TestMethod]
    public void Build_DefinitionHash_PrependsPrefixAndSeparator()
    {
        Assert.AreEqual("reflex__da8b3366e7837c2c", ReflexToolNames.Build("da8b3366e7837c2c"));
    }

    [TestMethod]
    public void Build_HashWithDisallowedCharacters_SanitizesToHyphen()
    {
        // Defensive: DefinitionHash is always hex in practice, but Build must never emit a
        // character outside ^[a-zA-Z0-9_-]{1,64}$ regardless of its input.
        Assert.AreEqual("reflex__ab-cd", ReflexToolNames.Build("ab.cd"));
    }
}
