using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;

namespace AgentKnowledgeBase.Tests;

public class DirectoryServiceTests
{
    private readonly FakeDirectoryRepository _directoryRepo = new();
    private readonly FakeDocumentRepository _documentRepo = new();
    private readonly DirectoryService _service;
    private readonly KnowledgeService _knowledge;

    public DirectoryServiceTests()
    {
        _service = new DirectoryService(_directoryRepo, _documentRepo);
        _knowledge = new KnowledgeService(_documentRepo, _directoryRepo);
    }

    private static ApiCaller Caller(params KeyScope[] scopes) => new(Guid.NewGuid(), "tester", scopes);

    private static KeyScope Rw(Guid directoryId) => new() { DirectoryId = directoryId, Access = AccessLevel.ReadWrite };

    [Fact]
    public async Task Create_NormalizesNameAndListsIt()
    {
        var created = await _service.CreateAsync(" Dotnet ", "  .NET notes  ", "admin", default);

        Assert.Equal("dotnet", created.Name);
        Assert.Equal(".NET notes", created.Description);
        Assert.Single(await _service.ListAllAsync(default));
    }

    [Fact]
    public async Task Create_DuplicateName_ReturnsConflict()
    {
        await _service.CreateAsync("dotnet", "", "admin", default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.CreateAsync("DOTNET", "", "admin", default));

        Assert.Equal(KnowledgeErrorKind.Conflict, ex.Kind);
    }

    [Fact]
    public async Task Update_RenamesDirectory()
    {
        var created = await _service.CreateAsync("dotnet", "old", "admin", default);

        var updated = await _service.UpdateAsync(created.Id, "dotnet-notes", "new", default);

        Assert.Equal("dotnet-notes", updated.Name);
        Assert.Equal("new", updated.Description);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
    }

    [Fact]
    public async Task Update_ToExistingName_ReturnsConflict()
    {
        await _service.CreateAsync("one", "", "admin", default);
        var second = await _service.CreateAsync("two", "", "admin", default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.UpdateAsync(second.Id, "one", "", default));

        Assert.Equal(KnowledgeErrorKind.Conflict, ex.Kind);
    }

    [Fact]
    public async Task Update_UnknownDirectory_IsNotFound()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.UpdateAsync(Guid.NewGuid(), "name", "", default));

        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Delete_EmptyDirectory_RemovesIt()
    {
        var created = await _service.CreateAsync("dotnet", "", "admin", default);

        await _service.DeleteAsync(created.Id, default);

        Assert.Empty(await _service.ListAllAsync(default));
    }

    [Fact]
    public async Task Delete_DirectoryWithDocuments_ReturnsConflict()
    {
        var created = await _service.CreateAsync("dotnet", "", "admin", default);
        await _knowledge.StoreAsync(Caller(Rw(created.Id)),
            new StoreRequest(null, created.Id, "Doc", null, null, "body", null), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => _service.DeleteAsync(created.Id, default));

        Assert.Equal(KnowledgeErrorKind.Conflict, ex.Kind);
        Assert.Single(await _service.ListAllAsync(default));
    }

    [Fact]
    public async Task Delete_UnknownDirectory_IsNotFound()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => _service.DeleteAsync(Guid.NewGuid(), default));

        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task List_ReturnsOnlyReadableDirectories()
    {
        var visible = await _service.CreateAsync("visible", "", "admin", default);
        await _service.CreateAsync("hidden", "", "admin", default);

        var result = await _service.ListAsync(Caller(Rw(visible.Id)), default);

        Assert.Single(result);
        Assert.Equal("visible", result[0].Name);
    }
}
