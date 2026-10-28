using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using AgentKnowledgeBase.Mcp.Storage;

namespace AgentKnowledgeBase.Tests;

public class KnowledgeServiceTests
{
    private readonly FakeDocumentRepository _repo = new();
    private readonly KnowledgeService _service;

    public KnowledgeServiceTests()
    {
        _service = new KnowledgeService(_repo);
    }

    private static ApiCaller Caller(params KeyScope[] scopes) =>
        new("key-1", "tester", scopes);

    private static KeyScope Rw(string prefix) => new() { PathPrefix = prefix, Access = AccessLevel.ReadWrite };

    private static KeyScope R(string prefix) => new() { PathPrefix = prefix, Access = AccessLevel.Read };

    private static StoreRequest Create(string title, string path, string content = "body") =>
        new(null, path, title, "summary", ["tag"], content, null);

    [Fact]
    public async Task Create_DefaultsIdToSlugAndStartsAtVersionOne()
    {
        var caller = Caller(Rw(""));

        var result = await _service.StoreAsync(caller, Create("EF Core Tips", "dotnet/efcore"), default);

        Assert.True(result.Created);
        Assert.Equal("ef-core-tips", result.Document.Id);
        Assert.Equal(1, result.Document.Version);
        Assert.Single(_repo.Revisions);
    }

    [Fact]
    public async Task Update_RequiresCurrentVersionAndIncrements()
    {
        var caller = Caller(Rw(""));
        await _service.StoreAsync(caller, Create("Notes", "misc"), default);

        var updated = await _service.StoreAsync(caller,
            new StoreRequest("notes", "misc", "Notes", "new", null, "changed", 1), default);

        Assert.False(updated.Created);
        Assert.Equal(2, updated.Document.Version);
        Assert.Equal(2, _repo.Revisions.Count);
    }

    [Fact]
    public async Task Update_WithStaleVersion_ReturnsConflictWithLatestVersion()
    {
        var caller = Caller(Rw(""));
        await _service.StoreAsync(caller, Create("Notes", "misc"), default);
        await _service.StoreAsync(caller, new StoreRequest("notes", "misc", "Notes", null, null, "v2", 1), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(caller, new StoreRequest("notes", "misc", "Notes", null, null, "stale", 1), default));

        Assert.Equal(KnowledgeErrorKind.Conflict, ex.Kind);
        Assert.Equal(2, ex.LatestVersion);
    }

    [Fact]
    public async Task Update_WithoutVersion_ReturnsConflict()
    {
        var caller = Caller(Rw(""));
        await _service.StoreAsync(caller, Create("Notes", "misc"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(caller, new StoreRequest("notes", "misc", "Notes", null, null, "x", null), default));

        Assert.Equal(KnowledgeErrorKind.Conflict, ex.Kind);
    }

    [Fact]
    public async Task Create_WithExistingId_ReturnsConflict()
    {
        var caller = Caller(Rw(""));
        await _service.StoreAsync(caller, Create("Notes", "misc"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(caller, new StoreRequest("notes", "misc", "Other", null, null, "x", null), default));

        Assert.Equal(KnowledgeErrorKind.Conflict, ex.Kind);
    }

    [Fact]
    public async Task Store_ToPathOutsideScope_IsForbidden()
    {
        var caller = Caller(Rw("team"));

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(caller, Create("X", "other"), default));

        Assert.Equal(KnowledgeErrorKind.Forbidden, ex.Kind);
        Assert.Empty(_repo.Documents);
    }

    [Fact]
    public async Task Store_WithReadOnlyAccess_IsForbidden()
    {
        var seed = Caller(Rw(""));
        await _service.StoreAsync(seed, Create("Doc", "team"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(Caller(R("team")), new StoreRequest("doc", "team", "Doc", null, null, "x", 1), default));

        Assert.Equal(KnowledgeErrorKind.Forbidden, ex.Kind);
    }

    [Fact]
    public async Task Update_CannotChangePath()
    {
        var caller = Caller(Rw(""));
        await _service.StoreAsync(caller, Create("Doc", "team/a"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(caller, new StoreRequest("doc", "team/b", "Doc", null, null, "x", 1), default));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Get_HiddenDocument_LooksMissing()
    {
        await _service.StoreAsync(Caller(Rw("")), Create("Secret", "private"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.GetAsync(Caller(Rw("public")), "secret", default));

        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Update_OfHiddenDocument_LooksMissing()
    {
        await _service.StoreAsync(Caller(Rw("")), Create("Secret", "private"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(Caller(Rw("public")), new StoreRequest("secret", "private", "Secret", null, null, "x", 1), default));

        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task List_ReturnsOnlyReadableDocuments()
    {
        var admin = Caller(Rw(""));
        await _service.StoreAsync(admin, Create("A", "team/a"), default);
        await _service.StoreAsync(admin, Create("B", "other/b"), default);

        var visible = await _service.ListAsync(Caller(R("team")), null, default);

        Assert.Single(visible);
        Assert.Equal("team/a", visible[0].Path);
    }

    [Fact]
    public async Task List_FiltersByPathPrefix()
    {
        var admin = Caller(Rw(""));
        await _service.StoreAsync(admin, Create("A", "team/a"), default);
        await _service.StoreAsync(admin, Create("B", "team2/b"), default);

        var result = await _service.ListAsync(admin, "team", default);

        Assert.Single(result);
        Assert.Equal("team/a", result[0].Path);
    }

    [Fact]
    public async Task Store_RejectsOversizedContent()
    {
        var big = new string('x', KnowledgeService.MaxContentBytes + 1);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(Caller(Rw("")), Create("Big", "misc", big), default));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }
}
