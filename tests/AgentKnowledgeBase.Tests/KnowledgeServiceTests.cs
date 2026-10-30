using System.Text.Json;
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
        Assert.Contains("has been updated since you read it", ex.Message);
        Assert.Contains("expected version 1, current version 2", ex.Message);
        Assert.Equal("v2", _repo.Documents[created.Id].Content);
    }

    [Fact]
    public async Task Update_OnConflict_TellsTheCallerToRefetchAndReapply()
    {
        var directoryId = await CreateDirectoryAsync();
        var caller = Caller(Rw(directoryId));
        var created = await _service.StoreAsync(caller, Create(directoryId, "Notes"), default);
        await _service.StoreAsync(caller, new StoreRequest(created.Id, null, null, null, null, "v2", 1), default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(caller, new StoreRequest(created.Id, null, null, null, null, "stale", 1), default));

        // Retrying with only the new version would overwrite the edit this caller never saw.
        Assert.Contains("get_knowledge_document", ex.Message);
        Assert.Contains("re-apply your changes", ex.Message);
        Assert.Contains("expectedVersion=2", ex.Message);
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
        Assert.Equal(1, ex.LatestVersion);
        Assert.Contains("requires the version you last read", ex.Message);
        Assert.Contains("get_knowledge_document", ex.Message);
    }

    [Fact]
    public async Task Update_WhenAnotherWriterCommitsFirst_ReturnsConflictAndKeepsTheirEdit()
    {
        var directoryId = await CreateDirectoryAsync();
        var caller = Caller(Rw(directoryId));
        var created = await _service.StoreAsync(caller, Create(directoryId, "Notes"), default);

        // Both callers read version 1; the other one commits between this caller's read and its replace.
        _repo.BeforeReplace = () =>
        {
            _repo.Documents[created.Id].Version = 2;
            _repo.Documents[created.Id].Content = "other agent";
        };

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.StoreAsync(caller, new StoreRequest(created.Id, null, null, null, null, "mine", 1), default));

        Assert.Equal(KnowledgeErrorKind.Conflict, ex.Kind);
        Assert.Equal(2, ex.LatestVersion);
        Assert.Contains("updated by someone else while you were saving", ex.Message);
        Assert.Contains("re-apply your changes", ex.Message);
        Assert.Equal("other agent", _repo.Documents[created.Id].Content);
        Assert.Single(_repo.Revisions);
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

        var visible = await _service.ListAsync(Caller(R(team)), null, null, null, default);

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

        var result = await _service.ListAsync(admin, other, null, null, default);

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
    public async Task GetRevision_AfterMove_FollowsTheDocumentNotTheSnapshotDirectory()
    {
        var source = await CreateDirectoryAsync("source");
        var target = await CreateDirectoryAsync("target");
        var mover = Caller(Rw(source), Rw(target));
        var created = await _service.StoreAsync(mover, Create(source, "Doc", "body"), default);
        var preMoveRevision = (await _service.ListRevisionsAsync(mover, created.Id, default)).Single();
        Assert.Equal(source, _repo.Revisions.Single(r => r.Id == preMoveRevision.Id).DirectoryId);

        await _service.MoveAsync(mover, created.Id, target, null, default);

        // Access follows the document's current directory: the old snapshot carries the source id but
        // is no longer reachable from it.
        var sourceOnly = Caller(R(source));
        var hidden = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.GetRevisionAsync(sourceOnly, preMoveRevision.Id, default));
        Assert.Equal(KnowledgeErrorKind.NotFound, hidden.Kind);

        var detail = await _service.GetRevisionAsync(Caller(R(target)), preMoveRevision.Id, default);
        Assert.Equal("body", detail.Content);
        Assert.Equal(1, detail.Version);
    }

    [Fact]
    public async Task AdminCaller_ReachesEveryDirectory()
    {
        var directoryId = await CreateDirectoryAsync();

        var created = await _service.StoreAsync(ApiCaller.Admin, Create(directoryId, "Doc"), default);
        var detail = await _service.GetAsync(ApiCaller.Admin, created.Id, default);

        Assert.Equal("Doc", detail.Name);
        Assert.Single(await _service.ListAsync(ApiCaller.Admin, null, null, null, default));
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

    private static StoreRequest Doc(
        Guid directoryId,
        string name,
        string content,
        string summary = "no summary",
        params string[] tags) =>
        new(null, directoryId, name, summary, tags.Length == 0 ? ["note"] : tags, content, null);

    private Task<SearchResult> Search(
        ApiCaller caller,
        string query,
        Guid? directoryId = null,
        int? limit = null,
        int? snippetLimit = null) =>
        _service.SearchAsync(caller, query, directoryId, limit, snippetLimit, default);

    private async Task<Guid> CreateDocAsync(Guid directoryId, StoreRequest request) =>
        (await _service.StoreAsync(Caller(Rw(directoryId)), request, default)).Id;

    [Fact]
    public async Task Search_FindsMatchInContent()
    {
        var directoryId = await CreateDirectoryAsync("team");
        await CreateDocAsync(directoryId, Doc(directoryId, "Threading", "always ConfigureAwait(false) in libraries"));

        var result = await Search(Caller(R(directoryId)), "configureawait");

        var match = Assert.Single(result.Matches);
        Assert.Equal("Threading", match.Name);
        Assert.Equal("team", match.DirectoryName);
        Assert.Equal(1, match.Version);
        Assert.Equal(1, match.MatchCount);
        var snippet = Assert.Single(match.Snippets);
        Assert.Equal(1, snippet.Line);
        Assert.Equal("always ConfigureAwait(false) in libraries", snippet.Text);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Search_FindsMatchInNameWithoutSnippets()
    {
        var directoryId = await CreateDirectoryAsync("team");
        await CreateDocAsync(directoryId, Doc(directoryId, "ConfigureAwait notes", "body"));

        var match = Assert.Single((await Search(Caller(R(directoryId)), "configureawait")).Matches);

        Assert.Equal(1, match.MatchCount);
        Assert.Empty(match.Snippets);
    }

    [Fact]
    public async Task Search_FindsMatchInSummary()
    {
        var directoryId = await CreateDirectoryAsync("team");
        await CreateDocAsync(directoryId,
            Doc(directoryId, "Threading", "body", summary: "prefer ConfigureAwait in libraries"));

        var match = Assert.Single((await Search(Caller(R(directoryId)), "configureawait")).Matches);

        Assert.Equal(1, match.MatchCount);
        Assert.Empty(match.Snippets);
    }

    [Fact]
    public async Task Search_FindsMatchInTag()
    {
        var directoryId = await CreateDirectoryAsync("team");
        await CreateDocAsync(directoryId, Doc(directoryId, "Threading", "body", tags: ["configureawait"]));

        var match = Assert.Single((await Search(Caller(R(directoryId)), "ConfigureAwait")).Matches);

        Assert.Equal(1, match.MatchCount);
        Assert.Equal(new[] { "configureawait" }, match.Tags);
    }

    [Fact]
    public async Task Search_CountsEveryOccurrenceAcrossFields()
    {
        var directoryId = await CreateDirectoryAsync("team");
        await CreateDocAsync(directoryId,
            Doc(
                directoryId,
                "needle needle",
                "needle here\nnothing\nanother needle",
                summary: "a needle",
                tags: ["needle"]));

        var match = Assert.Single((await Search(Caller(R(directoryId)), "needle")).Matches);

        Assert.Equal(6, match.MatchCount);
        Assert.Equal(new[] { 1, 3 }, match.Snippets.Select(s => s.Line));
    }

    [Fact]
    public async Task Search_NarrowsToDirectoryId()
    {
        var team = await CreateDirectoryAsync("team");
        var other = await CreateDirectoryAsync("other");
        await CreateDocAsync(team, Doc(team, "Team doc", "needle"));
        await CreateDocAsync(other, Doc(other, "Other doc", "needle"));

        var result = await Search(Caller(Rw(team), Rw(other)), "needle", other);

        var match = Assert.Single(result.Matches);
        Assert.Equal("Other doc", match.Name);
    }

    [Fact]
    public async Task Search_NeverRevealsMatchesOrCountsFromUnreadableDirectories()
    {
        var team = await CreateDirectoryAsync("team");
        var other = await CreateDirectoryAsync("other");
        await CreateDocAsync(team, Doc(team, "Team doc", "needle needle needle"));
        await CreateDocAsync(other, Doc(other, "Other doc", "needle\nneedle\nneedle\nneedle\nneedle"));

        var result = await Search(Caller(R(team)), "needle");

        var match = Assert.Single(result.Matches);
        Assert.Equal("Team doc", match.Name);
        Assert.Equal(3, match.MatchCount);
        Assert.Single(match.Snippets);
    }

    [Fact]
    public async Task Search_WhenOnlyTheUnreadableDirectoryMatches_ReturnsNothing()
    {
        var team = await CreateDirectoryAsync("team");
        var other = await CreateDirectoryAsync("other");
        await CreateDocAsync(other, Doc(other, "Other doc", "needle"));

        var result = await Search(Caller(R(team)), "needle");

        Assert.Empty(result.Matches);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Search_OfAnUnreadableDirectory_ReturnsNothing()
    {
        var team = await CreateDirectoryAsync("team");
        var other = await CreateDirectoryAsync("other");
        await CreateDocAsync(other, Doc(other, "Other doc", "needle"));

        var result = await Search(Caller(R(team)), "needle", other);

        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task Search_WithoutMatches_ReturnsAnEmptyResult()
    {
        var directoryId = await CreateDirectoryAsync("team");
        await CreateDocAsync(directoryId, Doc(directoryId, "Doc", "body"));

        var result = await Search(Caller(R(directoryId)), "absent-term");

        Assert.Empty(result.Matches);
        Assert.False(result.Truncated);
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("(")]
    [InlineData("*")]
    public async Task Search_TreatsRegexMetacharactersLiterally(string query)
    {
        var directoryId = await CreateDirectoryAsync("team");
        await CreateDocAsync(directoryId, Doc(directoryId, "Literal", $"literal {query} here"));
        await CreateDocAsync(directoryId, Doc(directoryId, "Decoy", "literal axb here"));

        var match = Assert.Single((await Search(Caller(R(directoryId)), query)).Matches);

        Assert.Equal("Literal", match.Name);
    }

    [Theory]
    [InlineData("first\nneedle\nlast")]
    [InlineData("first\r\nneedle\r\nlast")]
    public async Task Search_ReportsOneBasedLineNumbersForEitherLineEnding(string content)
    {
        var directoryId = await CreateDirectoryAsync("team");
        await CreateDocAsync(directoryId, Doc(directoryId, "Doc", content));

        var snippet = Assert.Single(Assert.Single((await Search(Caller(R(directoryId)), "needle")).Matches).Snippets);

        Assert.Equal(2, snippet.Line);
        Assert.Equal("needle", snippet.Text);
    }

    [Fact]
    public async Task Search_TrimsLongLinesAroundTheMatch()
    {
        var directoryId = await CreateDirectoryAsync("team");
        await CreateDocAsync(directoryId, Doc(directoryId, "Doc", new string('x', 250) + "needle" + new string('y', 144)));

        var snippet = Assert.Single(Assert.Single((await Search(Caller(R(directoryId)), "needle")).Matches).Snippets);

        Assert.Equal(200, snippet.Text.Length);
        Assert.StartsWith("…", snippet.Text);
        Assert.EndsWith("…", snippet.Text);
        Assert.Contains("needle", snippet.Text);
    }

    [Fact]
    public async Task Search_ClampsLimitToItsMaximum()
    {
        var directoryId = await CreateDirectoryAsync("team");
        for (var index = 0; index < 60; index++)
        {
            await CreateDocAsync(directoryId, Doc(directoryId, $"Doc {index:00}", "needle"));
        }

        var result = await Search(Caller(R(directoryId)), "needle", limit: 1000);

        Assert.Equal(50, result.Matches.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Search_DefaultsToTenDocuments()
    {
        var directoryId = await CreateDirectoryAsync("team");
        for (var index = 0; index < 15; index++)
        {
            await CreateDocAsync(directoryId, Doc(directoryId, $"Doc {index:00}", "needle"));
        }

        var result = await Search(Caller(R(directoryId)), "needle");

        Assert.Equal(10, result.Matches.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Search_ClampsSnippetLimitToItsMaximum()
    {
        var directoryId = await CreateDirectoryAsync("team");
        await CreateDocAsync(directoryId, Doc(directoryId, "Doc", string.Join("\n", Enumerable.Repeat("needle", 10))));

        var result = await Search(Caller(R(directoryId)), "needle", snippetLimit: 100);

        var match = Assert.Single(result.Matches);
        Assert.Equal(5, match.Snippets.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Search_OrdersByMatchCountThenName()
    {
        var directoryId = await CreateDirectoryAsync("team");
        await CreateDocAsync(directoryId, Doc(directoryId, "Bravo", "needle"));
        await CreateDocAsync(directoryId, Doc(directoryId, "Alpha", "needle"));
        await CreateDocAsync(directoryId, Doc(directoryId, "Zulu", "needle\nneedle"));

        var result = await Search(Caller(R(directoryId)), "needle");

        Assert.Equal(new[] { "Zulu", "Alpha", "Bravo" }, result.Matches.Select(m => m.Name));
    }

    [Fact]
    public async Task Search_OrderingIsStableAcrossRuns()
    {
        var directoryId = await CreateDirectoryAsync("team");
        foreach (var name in new[] { "Delta", "Alpha", "Charlie", "Bravo" })
        {
            await CreateDocAsync(directoryId, Doc(directoryId, name, "needle"));
        }

        var runs = new List<List<Guid>>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            runs.Add((await Search(Caller(R(directoryId)), "needle")).Matches.Select(m => m.Id).ToList());
        }

        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie", "Delta" },
            (await Search(Caller(R(directoryId)), "needle")).Matches.Select(m => m.Name));
        Assert.All(runs, run => Assert.Equal(runs[0], run));
    }

    [Fact]
    public async Task Search_BroadQueryOverALargeCorpus_ReturnsASmallResponse()
    {
        var directoryId = await CreateDirectoryAsync("bulk");
        var content = string.Join("\n", Enumerable.Repeat("needle " + new string('x', 112), 10));
        for (var index = 0; index < 200; index++)
        {
            await CreateDocAsync(directoryId, Doc(directoryId, $"Doc {index:000}", content, tags: ["bulk"]));
        }

        var result = await Search(Caller(R(directoryId)), "needle");
        var serialized = JsonSerializer.Serialize(result);

        Assert.Equal(10, result.Matches.Count);
        Assert.True(result.Truncated);
        Assert.True(serialized.Length < 4096, $"search response was {serialized.Length} characters");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Search_WithoutAQuery_IsValidationError(string query)
    {
        var directoryId = await CreateDirectoryAsync("team");

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => Search(Caller(R(directoryId)), query));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task List_PagesThroughEveryDocumentWithoutGapsOrRepeats()
    {
        var directoryId = await CreateDirectoryAsync("team");
        for (var index = 0; index < 7; index++)
        {
            await CreateDocAsync(directoryId, Doc(directoryId, $"Doc {index}", "body"));
        }

        const int pageSize = 3;
        var paged = new List<Guid>();
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await _service.ListAsync(Caller(R(directoryId)), null, pageSize, offset, default);
            paged.AddRange(page.Select(d => d.Id));
            if (page.Count < pageSize)
            {
                break;
            }
        }

        var unpaged = await _service.ListAsync(Caller(R(directoryId)), null, null, null, default);

        Assert.Equal(7, paged.Count);
        Assert.Equal(paged.Distinct().Count(), paged.Count);
        Assert.Equal(unpaged.Select(d => d.Id), paged);
    }

    [Fact]
    public async Task List_WithoutPaging_ReturnsEveryDocument()
    {
        var directoryId = await CreateDirectoryAsync("team");
        for (var index = 0; index < 7; index++)
        {
            await CreateDocAsync(directoryId, Doc(directoryId, $"Doc {index}", "body"));
        }

        var result = await _service.ListAsync(Caller(R(directoryId)), null, null, null, default);

        Assert.Equal(7, result.Count);
    }

    [Fact]
    public async Task List_WithOffsetPastTheEnd_ReturnsNothing()
    {
        var directoryId = await CreateDirectoryAsync("team");
        for (var index = 0; index < 3; index++)
        {
            await CreateDocAsync(directoryId, Doc(directoryId, $"Doc {index}", "body"));
        }

        var result = await _service.ListAsync(Caller(R(directoryId)), null, 5, 10, default);

        Assert.Empty(result);
    }
}
