using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;

namespace AgentKnowledgeBase.Tests;

public class KnowledgeServiceTests
{
    private readonly FakeDirectoryRepository _directoryRepo = new();
    private readonly FakeDocumentRepository _repo = new();
    private readonly DirectoryService _directories;
    private readonly KnowledgeService _service;

    public KnowledgeServiceTests()
    {
        _directories = new DirectoryService(_directoryRepo, _repo);
        _service = new KnowledgeService(_repo, _directoryRepo);
    }

    private static ApiCaller Caller(params KeyScope[] scopes) =>
        new(Guid.NewGuid(), "tester", scopes);

    private static KeyScope Rw(Guid directoryId) => new() { DirectoryId = directoryId, Access = AccessLevel.ReadWrite };

    private static KeyScope R(Guid directoryId) => new() { DirectoryId = directoryId, Access = AccessLevel.Read };

    private static StoreRequest Create(Guid directoryId, string name, string content = "body") =>
        new(null, directoryId, name, "summary", ["tag"], content, null);

    private async Task<Guid> CreateDirectoryAsync(string name = "team")
    {
        var directory = await _directories.CreateAsync(name, "", "admin", default);
        return directory.Id;
    }

    [Fact]
    public async Task Create_StartsAtVersionOneAndRecordsRevision()
    {
        var directoryId = await CreateDirectoryAsync();
        var caller = Caller(Rw(directoryId));

        var result = await _service.StoreAsync(caller, Create(directoryId, "EF Core Tips"), default);

        Assert.True(result.Created);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(directoryId, result.DirectoryId);
        Assert.Equal("EF Core Tips", result.Name);
        Assert.Equal(1, result.Version);
        Assert.Single(_repo.Revisions);
    }

    [Fact]
    public async Task Create_SameNameInDifferentDirectories_IsAllowed()
    {
        var team = await CreateDirectoryAsync("team");
        var other = await CreateDirectoryAsync("other");
        var caller = Caller(Rw(team), Rw(other));

        await _service.StoreAsync(caller, Create(team, "Notes"), default);
        var second = await _service.StoreAsync(caller, Create(other, "Notes"), default);

        Assert.True(second.Created);
        Assert.Equal(2, _repo.Documents.Count);
    }

    [Fact]
    public async Task Create_DuplicateNameInSameDirectory_ReturnsConflict()
    {
        var directoryId = await CreateDirectoryAsync();
        var caller = Caller(Rw(directoryId));
        await _service.StoreAsync(caller, Create(directoryId, "Notes"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(caller, Create(directoryId, "notes"), default));

        Assert.Equal(KnowledgeErrorKind.Conflict, ex.Kind);
    }

    [Fact]
    public async Task Create_UnknownDirectory_IsNotFound()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(Caller(Rw(Guid.NewGuid())), Create(Guid.NewGuid(), "Notes"), default));

        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Update_RequiresCurrentVersionAndIncrements()
    {
        var directoryId = await CreateDirectoryAsync();
        var caller = Caller(Rw(directoryId));
        var created = await _service.StoreAsync(caller, Create(directoryId, "Notes"), default);

        var updated = await _service.StoreAsync(caller,
            new StoreRequest(created.Id, null, null, "new", null, "changed", 1), default);

        Assert.False(updated.Created);
        Assert.Equal(2, updated.Version);
        Assert.Equal("Notes", updated.Name);
        Assert.Equal(2, _repo.Revisions.Count);
    }

    [Fact]
    public async Task Update_CanRename()
    {
        var directoryId = await CreateDirectoryAsync();
        var caller = Caller(Rw(directoryId));
        var created = await _service.StoreAsync(caller, Create(directoryId, "Old name"), default);

        var updated = await _service.StoreAsync(caller,
            new StoreRequest(created.Id, null, "New name", null, null, "body", 1), default);

        Assert.Equal("New name", updated.Name);
        Assert.Equal("New name", _repo.Documents[created.Id].Name);
    }

    [Fact]
    public async Task Update_RenameToExistingName_ReturnsConflict()
    {
        var directoryId = await CreateDirectoryAsync();
        var caller = Caller(Rw(directoryId));
        await _service.StoreAsync(caller, Create(directoryId, "Taken"), default);
        var other = await _service.StoreAsync(caller, Create(directoryId, "Free"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(caller, new StoreRequest(other.Id, null, "taken", null, null, "body", 1), default));

        Assert.Equal(KnowledgeErrorKind.Conflict, ex.Kind);
        Assert.Equal("Free", _repo.Documents[other.Id].Name);
    }

    [Fact]
    public async Task Update_WithStaleVersion_ReturnsConflictWithLatestVersion()
    {
        var directoryId = await CreateDirectoryAsync();
        var caller = Caller(Rw(directoryId));
        var created = await _service.StoreAsync(caller, Create(directoryId, "Notes"), default);
        await _service.StoreAsync(caller, new StoreRequest(created.Id, null, null, null, null, "v2", 1), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(caller, new StoreRequest(created.Id, null, null, null, null, "stale", 1), default));

        Assert.Equal(KnowledgeErrorKind.Conflict, ex.Kind);
        Assert.Equal(2, ex.LatestVersion);
    }

    [Fact]
    public async Task Update_WithoutVersion_ReturnsConflict()
    {
        var directoryId = await CreateDirectoryAsync();
        var caller = Caller(Rw(directoryId));
        var created = await _service.StoreAsync(caller, Create(directoryId, "Notes"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(caller, new StoreRequest(created.Id, null, null, null, null, "x", null), default));

        Assert.Equal(KnowledgeErrorKind.Conflict, ex.Kind);
    }

    [Fact]
    public async Task Update_UnknownDocument_IsNotFound()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(Caller(), new StoreRequest(Guid.NewGuid(), null, null, null, null, "x", 1), default));

        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Store_ToDirectoryOutsideScope_IsForbidden()
    {
        var team = await CreateDirectoryAsync("team");
        var other = await CreateDirectoryAsync("other");

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(Caller(Rw(team)), Create(other, "X"), default));

        Assert.Equal(KnowledgeErrorKind.Forbidden, ex.Kind);
        Assert.Empty(_repo.Documents);
    }

    [Fact]
    public async Task Store_WithReadOnlyAccess_IsForbidden()
    {
        var directoryId = await CreateDirectoryAsync();
        var seed = await _service.StoreAsync(Caller(Rw(directoryId)), Create(directoryId, "Doc"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(Caller(R(directoryId)),
                new StoreRequest(seed.Id, null, null, null, null, "x", 1), default));

        Assert.Equal(KnowledgeErrorKind.Forbidden, ex.Kind);
    }

    [Fact]
    public async Task Update_CannotMoveToAnotherDirectory()
    {
        var team = await CreateDirectoryAsync("team");
        var other = await CreateDirectoryAsync("other");
        var caller = Caller(Rw(team), Rw(other));
        var created = await _service.StoreAsync(caller, Create(team, "Doc"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(caller, new StoreRequest(created.Id, other, null, null, null, "x", 1), default));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Get_HiddenDocument_LooksMissing()
    {
        var privateId = await CreateDirectoryAsync("private");
        var created = await _service.StoreAsync(Caller(Rw(privateId)), Create(privateId, "Secret"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.GetAsync(Caller(Rw(Guid.NewGuid())), created.Id, default));

        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Update_OfHiddenDocument_LooksMissing()
    {
        var privateId = await CreateDirectoryAsync("private");
        var created = await _service.StoreAsync(Caller(Rw(privateId)), Create(privateId, "Secret"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(Caller(Rw(Guid.NewGuid())),
                new StoreRequest(created.Id, null, null, null, null, "x", 1), default));

        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Get_ReturnsContentAndDirectoryName()
    {
        var directoryId = await CreateDirectoryAsync("team");
        var caller = Caller(Rw(directoryId));
        var created = await _service.StoreAsync(caller, Create(directoryId, "Doc", "hello"), default);

        var detail = await _service.GetAsync(caller, created.Id, default);

        Assert.Equal("hello", detail.Content);
        Assert.Equal("team", detail.DirectoryName);
        Assert.Equal(1, detail.Version);
    }

    [Fact]
    public async Task List_ReturnsOnlyReadableDocuments()
    {
        var team = await CreateDirectoryAsync("team");
        var other = await CreateDirectoryAsync("other");
        var admin = Caller(Rw(team), Rw(other));
        await _service.StoreAsync(admin, Create(team, "A"), default);
        await _service.StoreAsync(admin, Create(other, "B"), default);

        var visible = await _service.ListAsync(Caller(R(team)), null, default);

        Assert.Single(visible);
        Assert.Equal("A", visible[0].Name);
        Assert.Equal("team", visible[0].DirectoryName);
    }

    [Fact]
    public async Task List_FiltersByDirectoryId()
    {
        var team = await CreateDirectoryAsync("team");
        var other = await CreateDirectoryAsync("other");
        var admin = Caller(Rw(team), Rw(other));
        await _service.StoreAsync(admin, Create(team, "A"), default);
        await _service.StoreAsync(admin, Create(other, "B"), default);

        var result = await _service.ListAsync(admin, other, default);

        Assert.Single(result);
        Assert.Equal("B", result[0].Name);
    }

    [Fact]
    public async Task Store_RejectsOversizedContent()
    {
        var directoryId = await CreateDirectoryAsync();
        var big = new string('x', DocumentRules.MaxContentBytes + 1);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(Caller(Rw(directoryId)), Create(directoryId, "Big", big), default));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Create_WithoutName_IsValidationError()
    {
        var directoryId = await CreateDirectoryAsync();

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(Caller(Rw(directoryId)), Create(directoryId, " "), default));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Delete_RemovesDocumentAndItsRevisions()
    {
        var directoryId = await CreateDirectoryAsync();
        var caller = Caller(Rw(directoryId));
        var created = await _service.StoreAsync(caller, Create(directoryId, "Doc"), default);

        await _service.DeleteAsync(caller, created.Id, default);

        Assert.Empty(_repo.Documents);
        Assert.Empty(_repo.Revisions);
    }

    [Fact]
    public async Task Delete_OfHiddenDocument_LooksMissing()
    {
        var privateId = await CreateDirectoryAsync("private");
        var created = await _service.StoreAsync(Caller(Rw(privateId)), Create(privateId, "Secret"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.DeleteAsync(Caller(Rw(Guid.NewGuid())), created.Id, default));

        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
        Assert.Single(_repo.Documents);
    }

    [Fact]
    public async Task Delete_WithReadOnlyAccess_IsForbidden()
    {
        var directoryId = await CreateDirectoryAsync();
        var created = await _service.StoreAsync(Caller(Rw(directoryId)), Create(directoryId, "Doc"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.DeleteAsync(Caller(R(directoryId)), created.Id, default));

        Assert.Equal(KnowledgeErrorKind.Forbidden, ex.Kind);
    }

    [Fact]
    public async Task ListRevisions_ReturnsNewestFirst()
    {
        var directoryId = await CreateDirectoryAsync();
        var caller = Caller(Rw(directoryId));
        var created = await _service.StoreAsync(caller, Create(directoryId, "Doc", "one"), default);
        await _service.StoreAsync(caller, new StoreRequest(created.Id, null, "Renamed", null, null, "two", 1), default);

        var revisions = await _service.ListRevisionsAsync(caller, created.Id, default);

        Assert.Equal([2, 1], revisions.Select(r => r.Version));
        Assert.Equal("Renamed", revisions[0].Name);
        Assert.Equal("Doc", revisions[1].Name);
    }

    [Fact]
    public async Task GetRevision_ReturnsSnapshotContent()
    {
        var directoryId = await CreateDirectoryAsync();
        var caller = Caller(Rw(directoryId));
        var created = await _service.StoreAsync(caller, Create(directoryId, "Doc", "one"), default);
        await _service.StoreAsync(caller, new StoreRequest(created.Id, null, null, null, null, "two", 1), default);
        var revisions = await _service.ListRevisionsAsync(caller, created.Id, default);
        var first = revisions.Single(r => r.Version == 1);

        var detail = await _service.GetRevisionAsync(caller, first.Id, default);

        Assert.Equal("one", detail.Content);
        Assert.Equal(1, detail.Version);
    }

    [Fact]
    public async Task GetRevision_OfHiddenDocument_LooksMissing()
    {
        var privateId = await CreateDirectoryAsync("private");
        var created = await _service.StoreAsync(Caller(Rw(privateId)), Create(privateId, "Secret"), default);
        var revision = (await _service.ListRevisionsAsync(Caller(Rw(privateId)), created.Id, default)).Single();

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.GetRevisionAsync(Caller(Rw(Guid.NewGuid())), revision.Id, default));

        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task AdminCaller_ReachesEveryDirectory()
    {
        var directoryId = await CreateDirectoryAsync();

        var created = await _service.StoreAsync(ApiCaller.Admin, Create(directoryId, "Doc"), default);
        var detail = await _service.GetAsync(ApiCaller.Admin, created.Id, default);

        Assert.Equal("Doc", detail.Name);
        Assert.Single(await _service.ListAsync(ApiCaller.Admin, null, default));
    }

    [Fact]
    public async Task Move_ChangesDirectoryBumpsVersionAndRecordsRevision()
    {
        var source = await CreateDirectoryAsync("source");
        var target = await CreateDirectoryAsync("target");
        var caller = Caller(Rw(source), Rw(target));
        var created = await _service.StoreAsync(caller, Create(source, "Doc", "body"), default);

        var moved = await _service.MoveAsync(caller, created.Id, target, null, default);

        Assert.Equal(target, moved.DirectoryId);
        Assert.Equal("Doc", moved.Name);
        Assert.Equal(2, moved.Version);
        Assert.False(moved.Created);

        var detail = await _service.GetAsync(caller, created.Id, default);
        Assert.Equal(target, detail.DirectoryId);
        Assert.Equal("body", detail.Content);

        var revision = _repo.Revisions.OrderByDescending(r => r.Version).First();
        Assert.Equal(target, revision.DirectoryId);
        Assert.Equal(2, revision.Version);
    }

    [Fact]
    public async Task Move_ToSameDirectory_IsANoOp()
    {
        var directoryId = await CreateDirectoryAsync();
        var caller = Caller(Rw(directoryId));
        var created = await _service.StoreAsync(caller, Create(directoryId, "Doc"), default);

        var result = await _service.MoveAsync(caller, created.Id, directoryId, null, default);

        Assert.Equal(1, result.Version);
        Assert.Single(_repo.Revisions);
    }

    [Fact]
    public async Task Move_WithoutWriteAccessToTarget_IsForbidden()
    {
        var source = await CreateDirectoryAsync("source");
        var target = await CreateDirectoryAsync("target");
        var created = await _service.StoreAsync(Caller(Rw(source)), Create(source, "Doc"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.MoveAsync(Caller(Rw(source), R(target)), created.Id, target, null, default));

        Assert.Equal(KnowledgeErrorKind.Forbidden, ex.Kind);
        Assert.Equal(source, _repo.Documents[created.Id].DirectoryId);
    }

    [Fact]
    public async Task Move_WithoutWriteAccessToSource_IsForbidden()
    {
        var source = await CreateDirectoryAsync("source");
        var target = await CreateDirectoryAsync("target");
        var created = await _service.StoreAsync(Caller(Rw(source)), Create(source, "Doc"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.MoveAsync(Caller(R(source), Rw(target)), created.Id, target, null, default));

        Assert.Equal(KnowledgeErrorKind.Forbidden, ex.Kind);
    }

    [Fact]
    public async Task Move_IntoDirectoryThatAlreadyHasTheName_IsConflict()
    {
        var source = await CreateDirectoryAsync("source");
        var target = await CreateDirectoryAsync("target");
        var caller = Caller(Rw(source), Rw(target));
        var created = await _service.StoreAsync(caller, Create(source, "Doc"), default);
        await _service.StoreAsync(caller, Create(target, "doc"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.MoveAsync(caller, created.Id, target, null, default));

        Assert.Equal(KnowledgeErrorKind.Conflict, ex.Kind);
        Assert.Equal(source, _repo.Documents[created.Id].DirectoryId);
    }

    [Fact]
    public async Task Move_WithStaleExpectedVersion_IsConflict()
    {
        var source = await CreateDirectoryAsync("source");
        var target = await CreateDirectoryAsync("target");
        var caller = Caller(Rw(source), Rw(target));
        var created = await _service.StoreAsync(caller, Create(source, "Doc"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.MoveAsync(caller, created.Id, target, expectedVersion: 7, default));

        Assert.Equal(KnowledgeErrorKind.Conflict, ex.Kind);
        Assert.Equal(1, ex.LatestVersion);
    }

    [Fact]
    public async Task Move_UnknownDirectory_IsNotFound()
    {
        var source = await CreateDirectoryAsync("source");
        var caller = Caller(Rw(source));
        var created = await _service.StoreAsync(caller, Create(source, "Doc"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.MoveAsync(caller, created.Id, Guid.NewGuid(), null, default));

        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Move_OfHiddenDocument_LooksMissing()
    {
        var source = await CreateDirectoryAsync("source");
        var target = await CreateDirectoryAsync("target");
        var created = await _service.StoreAsync(Caller(Rw(source)), Create(source, "Doc"), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.MoveAsync(Caller(Rw(target)), created.Id, target, null, default));

        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }
}
