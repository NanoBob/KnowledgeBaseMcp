using AgentKnowledgeBase.Mcp.Domain;

namespace AgentKnowledgeBase.Tests;

public class RulesTests
{
    [Theory]
    [InlineData("Dotnet", "dotnet")]
    [InlineData("  notes  ", "notes")]
    [InlineData("team.a_b-c", "team.a_b-c")]
    public void DirectoryRules_NormalizeName(string input, string expected) =>
        Assert.Equal(expected, DirectoryRules.NormalizeName(input));

    [Theory]
    [InlineData("")]
    [InlineData("bad name")]
    [InlineData("team/child")]
    [InlineData("..")]
    public void DirectoryRules_RejectInvalidNames(string input) =>
        Assert.Throws<KnowledgeException>(() => DirectoryRules.NormalizeName(input));

    [Fact]
    public void DirectoryRules_RejectLongDescription() =>
        Assert.Throws<KnowledgeException>(() =>
            DirectoryRules.NormalizeDescription(new string('x', DirectoryRules.MaxDescriptionLength + 1)));

    [Fact]
    public void DocumentRules_NormalizeNameAndSummary()
    {
        Assert.Equal("EF Core Tips", DocumentRules.NormalizeName("  EF Core Tips  "));
        Assert.Equal("", DocumentRules.NormalizeSummary(null));
        Assert.Equal("desc", DocumentRules.NormalizeSummary(" desc "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DocumentRules_RejectEmptyName(string input) =>
        Assert.Throws<KnowledgeException>(() => DocumentRules.NormalizeName(input));

    [Fact]
    public void DocumentRules_RejectControlCharactersInName() =>
        Assert.Throws<KnowledgeException>(() => DocumentRules.NormalizeName("bad\nname"));

    [Fact]
    public void DocumentRules_RejectOversizedContent() =>
        Assert.Throws<KnowledgeException>(() =>
            DocumentRules.NormalizeContent(new string('x', DocumentRules.MaxContentBytes + 1)));

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

    [Fact]
    public void AccessPolicy_GrantsAccessPerDirectory()
    {
        var team = Guid.NewGuid();
        var other = Guid.NewGuid();
        var scopes = new[]
        {
            new KeyScope { DirectoryId = team, Access = AccessLevel.Read },
            new KeyScope { DirectoryId = other, Access = AccessLevel.ReadWrite }
        };

        Assert.Equal(AccessLevel.Read, AccessPolicy.GetLevel(scopes, team));
        Assert.Equal(AccessLevel.ReadWrite, AccessPolicy.GetLevel(scopes, other));
        Assert.Null(AccessPolicy.GetLevel(scopes, Guid.NewGuid()));
    }

    [Fact]
    public void AccessPolicy_HighestLevelWinsForDuplicateDirectory()
    {
        var directoryId = Guid.NewGuid();
        var scopes = new[]
        {
            new KeyScope { DirectoryId = directoryId, Access = AccessLevel.Read },
            new KeyScope { DirectoryId = directoryId, Access = AccessLevel.ReadWrite }
        };

        Assert.Equal(AccessLevel.ReadWrite, AccessPolicy.GetLevel(scopes, directoryId));
    }
}
