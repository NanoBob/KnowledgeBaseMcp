using AgentKnowledgeBase.Mcp.Domain;

namespace AgentKnowledgeBase.Tests;

public class RulesTests
{
    [Theory]
    [InlineData("Dotnet/EFCore", "dotnet/efcore")]
    [InlineData("/dotnet/efcore/", "dotnet/efcore")]
    [InlineData("  notes  ", "notes")]
    public void NormalizeDocumentPath_LowercasesAndTrimsSlashes(string input, string expected) =>
        Assert.Equal(expected, PathRules.NormalizeDocumentPath(input));

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("bad segment")]
    [InlineData("a/b/c/d/e/f/g/h/i/j/k")]
    public void NormalizeDocumentPath_RejectsInvalidPaths(string input) =>
        Assert.Throws<KnowledgeException>(() => PathRules.NormalizeDocumentPath(input));

    [Theory]
    [InlineData("dotnet", "dotnet/efcore", true)]
    [InlineData("dotnet", "dotnet", true)]
    [InlineData("dotnet", "dotnetx/efcore", false)]
    [InlineData("", "anything/at/all", true)]
    [InlineData("dotnet/efcore", "dotnet", false)]
    public void IsWithin_MatchesWholeSegments(string prefix, string path, bool expected) =>
        Assert.Equal(expected, PathRules.IsWithin(prefix, path));

    [Fact]
    public void TagRules_NormalizesDedupesAndRejectsInvalid()
    {
        Assert.Equal(["ef-core", "perf"], TagRules.Normalize(["EF-Core", "perf", "perf", " "]));
        Assert.Throws<KnowledgeException>(() => TagRules.Normalize(["has space"]));
    }

    [Fact]
    public void TagRules_RejectsMoreThanTwentyTags() =>
        Assert.Throws<KnowledgeException>(() =>
            TagRules.Normalize(Enumerable.Range(0, 21).Select(i => $"tag{i}")));

    [Theory]
    [InlineData("EF Core Migrations Tips", "ef-core-migrations-tips")]
    [InlineData("  --Hello, World!--  ", "hello-world")]
    [InlineData("!!!", "")]
    public void Slugify_ProducesUrlSafeIds(string title, string expected) =>
        Assert.Equal(expected, IdRules.Slugify(title));

    [Fact]
    public void AccessPolicy_LongestPrefixWins_AllowingReadOnlyCarveOut()
    {
        var scopes = new[]
        {
            new KeyScope { PathPrefix = "team", Access = AccessLevel.ReadWrite },
            new KeyScope { PathPrefix = "team/secrets", Access = AccessLevel.Read }
        };

        Assert.Equal(AccessLevel.ReadWrite, AccessPolicy.GetLevel(scopes, "team/notes"));
        Assert.Equal(AccessLevel.Read, AccessPolicy.GetLevel(scopes, "team/secrets/keys"));
        Assert.Null(AccessPolicy.GetLevel(scopes, "other/x"));
    }

    [Fact]
    public void AccessPolicy_SamePrefixPrefersReadWrite()
    {
        var scopes = new[]
        {
            new KeyScope { PathPrefix = "team", Access = AccessLevel.Read },
            new KeyScope { PathPrefix = "team", Access = AccessLevel.ReadWrite }
        };

        Assert.Equal(AccessLevel.ReadWrite, AccessPolicy.GetLevel(scopes, "team/a"));
    }
}
