using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Git;

namespace AgentKnowledgeBase.Tests;

public class KnowledgeSnapshotLoaderTests
{
    private static readonly DateTime Epoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly FakeDirectoryRepository _directoryRepo = new();
    private readonly FakeDocumentRepository _documentRepo = new();
    private readonly KnowledgeSnapshotLoader _loader;

    public KnowledgeSnapshotLoaderTests()
    {
        _loader = new KnowledgeSnapshotLoader(_directoryRepo, _documentRepo);
    }

    private Guid AddDirectory(string name, string description = "", DateTime? createdAt = null)
    {
        var directory = new KnowledgeDocumentDirectory
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            CreatedAt = createdAt ?? Epoch,
            CreatedBy = "admin"
        };

        _directoryRepo.Directories[directory.Id] = directory;
        return directory.Id;
    }

    private void AddDocument(
        Guid directoryId,
        string name,
        string content = "body",
        string summary = "",
        DateTime? updatedAt = null,
        params string[] tags)
    {
        var document = new KnowledgeDocument
        {
            Id = Guid.NewGuid(),
            DirectoryId = directoryId,
            Name = name,
            Summary = summary,
            Tags = [.. tags],
            Content = content,
            Version = 1,
            CreatedAt = Epoch,
            UpdatedAt = updatedAt ?? Epoch,
            CreatedBy = "admin",
            UpdatedBy = "admin"
        };

        _documentRepo.Documents[document.Id] = document;
    }

    private static ApiCaller Scoped(params Guid[] directoryIds) =>
        new(Guid.NewGuid(), "tester", [.. directoryIds.Select(id => new KeyScope
        {
            DirectoryId = id,
            Access = AccessLevel.Read
        })]);

    [Fact]
    public async Task Admin_sees_every_directory_and_document()
    {
        var first = AddDirectory("alpha");
        var second = AddDirectory("beta");
        AddDocument(first, "One");
        AddDocument(second, "Two");

        var snapshot = await _loader.LoadAsync(ApiCaller.Admin, default);

        Assert.Equal(2, snapshot.DirectoryCount);
        Assert.Equal(2, snapshot.DocumentCount);
        Assert.Equal(["alpha", "beta"], snapshot.Directories.Select(d => d.Name));
    }

    [Fact]
    public async Task A_scoped_caller_only_sees_its_directories()
    {
        var mine = AddDirectory("mine");
        var other = AddDirectory("other");
        AddDocument(mine, "Visible");
        AddDocument(other, "Hidden");

        var snapshot = await _loader.LoadAsync(Scoped(mine), default);

        var document = Assert.Single(snapshot.Documents);
        Assert.Equal("Visible", document.Name);
        Assert.Equal("mine", Assert.Single(snapshot.Directories).Name);
    }

    [Fact]
    public async Task A_caller_without_scopes_gets_an_empty_snapshot()
    {
        var directory = AddDirectory("alpha");
        AddDocument(directory, "One");

        var snapshot = await _loader.LoadAsync(Scoped(), default);

        Assert.Equal(0, snapshot.DirectoryCount);
        Assert.Equal(0, snapshot.DocumentCount);
        Assert.Equal(DateTimeOffset.UnixEpoch, snapshot.ChangedAt);
    }

    [Fact]
    public async Task A_document_whose_directory_is_not_visible_is_left_out()
    {
        var visible = AddDirectory("visible");
        var hidden = AddDirectory("hidden");
        AddDocument(visible, "Kept");
        AddDocument(hidden, "Dropped");

        // A scope can outlive the directory it pointed at, so the document query may return more than the
        // directory query did.
        var caller = new ApiCaller(Guid.NewGuid(), "tester",
            [new KeyScope { DirectoryId = visible, Access = AccessLevel.Read }]);

        var snapshot = await _loader.LoadAsync(caller, default);

        Assert.Equal("Kept", Assert.Single(snapshot.Documents).Name);
    }

    [Fact]
    public async Task Clashing_document_names_get_distinct_paths()
    {
        var directory = AddDirectory("alpha");

        // Names differ only by a character the file system refuses, so both sanitize to the same file name.
        AddDocument(directory, "a:b");
        AddDocument(directory, "a?b");

        var snapshot = await _loader.LoadAsync(ApiCaller.Admin, default);

        Assert.Equal(2, snapshot.Documents.Select(d => d.Path).Distinct().Count());
        Assert.All(snapshot.Documents, d => Assert.StartsWith("alpha/", d.Path));
    }

    [Fact]
    public async Task The_snapshot_timestamp_is_the_newest_change()
    {
        var directory = AddDirectory("alpha", createdAt: Epoch.AddHours(1));
        AddDocument(directory, "One", updatedAt: Epoch.AddHours(2));

        var snapshot = await _loader.LoadAsync(ApiCaller.Admin, default);

        Assert.Equal(Epoch.AddHours(2), snapshot.ChangedAt.UtcDateTime);
    }

    [Fact]
    public async Task Documents_are_ordered_by_directory_then_name()
    {
        var alpha = AddDirectory("alpha");
        var beta = AddDirectory("beta");
        AddDocument(beta, "B");
        AddDocument(alpha, "Z");
        AddDocument(alpha, "A");

        var snapshot = await _loader.LoadAsync(ApiCaller.Admin, default);

        Assert.Equal(["A", "Z", "B"], snapshot.Documents.Select(d => d.Name));
    }

    [Fact]
    public async Task Content_and_metadata_are_carried_into_the_snapshot()
    {
        var directory = AddDirectory("alpha", "Alpha documents");
        AddDocument(directory, "One", "the body", "a summary", tags: ["tag"]);

        var snapshot = await _loader.LoadAsync(ApiCaller.Admin, default);

        var document = Assert.Single(snapshot.Documents);
        Assert.Equal("the body", document.Content);
        Assert.Equal("a summary", document.Summary);
        Assert.Equal(["tag"], document.Tags);
        Assert.Equal("Alpha documents", Assert.Single(snapshot.Directories).Description);
    }
}
