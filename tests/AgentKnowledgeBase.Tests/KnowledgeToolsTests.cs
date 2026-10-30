using AgentKnowledgeBase.Mcp.Auth;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Mcp;
using AgentKnowledgeBase.Mcp.Services;
using ModelContextProtocol;

namespace AgentKnowledgeBase.Tests;

public class KnowledgeToolsTests
{
    private readonly FakeDocumentRepository _repo = new();
    private readonly FakeDirectoryRepository _directoryRepo = new();
    private readonly DirectoryService _directories;
    private readonly KnowledgeService _knowledge;

    public KnowledgeToolsTests()
    {
        _directories = new DirectoryService(_directoryRepo, _repo);
        _knowledge = new KnowledgeService(_repo, _directoryRepo);
    }

    private static CallerContext Context(params KeyScope[] scopes) =>
        new() { Caller = new ApiCaller(Guid.NewGuid(), "tester", scopes) };

    private static KeyScope Read(Guid directoryId) => new() { DirectoryId = directoryId, Access = AccessLevel.Read };

    private async Task<Guid> CreateDocumentAsync(Guid directoryId, string name, string content) =>
        (await _knowledge.StoreAsync(
            ApiCaller.Admin,
            new StoreRequest(null, directoryId, name, "summary", ["tag"], content, null),
            default)).Id;

    [Fact]
    public async Task SearchKnowledge_ReturnsMatchesForTheCaller()
    {
        var directory = await _directories.CreateAsync("team", "", "admin", default);
        await CreateDocumentAsync(directory.Id, "Doc", "always ConfigureAwait(false)");

        var result = await KnowledgeTools.SearchKnowledge(
            _knowledge,
            Context(Read(directory.Id)),
            "configureawait");

        var match = Assert.Single(result.Matches);
        Assert.Equal("Doc", match.Name);
        Assert.Equal("team", match.DirectoryName);
    }

    [Fact]
    public async Task SearchKnowledge_PassesDirectoryAndSnippetLimitThrough()
    {
        var team = await _directories.CreateAsync("team", "", "admin", default);
        var other = await _directories.CreateAsync("other", "", "admin", default);
        const string content = "needle one\nneedle two\nneedle three";
        await CreateDocumentAsync(team.Id, "Team doc", content);
        await CreateDocumentAsync(other.Id, "Other doc", content);

        var result = await KnowledgeTools.SearchKnowledge(
            _knowledge,
            Context(Read(team.Id), Read(other.Id)),
            "needle",
            directoryId: other.Id,
            snippetLimit: 1);

        var match = Assert.Single(result.Matches);
        Assert.Equal("Other doc", match.Name);
        Assert.Single(match.Snippets);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task SearchKnowledge_WithoutAuthentication_Throws()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            KnowledgeTools.SearchKnowledge(_knowledge, new CallerContext(), "needle"));

        Assert.Contains("Authentication required", ex.Message);
    }

    [Fact]
    public async Task SearchKnowledge_WithAnEmptyQuery_SurfacesValidationAsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            KnowledgeTools.SearchKnowledge(_knowledge, Context(), "   "));

        Assert.Contains("query is required", ex.Message);
    }

    [Fact]
    public async Task ListKnowledgeDocuments_ReturnsTheRequestedPage()
    {
        var directory = await _directories.CreateAsync("team", "", "admin", default);
        for (var index = 0; index < 3; index++)
        {
            await CreateDocumentAsync(directory.Id, $"Doc {index}", "body");
        }

        var page = await KnowledgeTools.ListKnowledgeDocuments(
            _knowledge,
            Context(Read(directory.Id)),
            limit: 2,
            offset: 2);

        var single = Assert.Single(page);
        Assert.Equal("Doc 2", single.Name);
    }

    [Fact]
    public async Task ListKnowledgeDocuments_WithoutPaging_ReturnsEveryDocument()
    {
        var directory = await _directories.CreateAsync("team", "", "admin", default);
        for (var index = 0; index < 3; index++)
        {
            await CreateDocumentAsync(directory.Id, $"Doc {index}", "body");
        }

        var all = await KnowledgeTools.ListKnowledgeDocuments(_knowledge, Context(Read(directory.Id)));

        Assert.Equal(3, all.Count);
    }
}
