using AgentKnowledgeBase.Mcp;
using AgentKnowledgeBase.Mcp.Git;
using AgentKnowledgeBase.Mcp.Services;
using GenHTTP.Modules.Git;

namespace AgentKnowledgeBase.Tests;

public class KnowledgeGitRepositoryTests
{
    private const string Scope = "scope";

    private static readonly DateTimeOffset ChangedAt = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private static readonly GitOptions Options = new()
    {
        Enabled = true,
        Path = "/git/knowledge.git",
        Branch = "main",
        AuthorName = "Knowledge Base",
        AuthorEmail = "knowledge-base@example.com"
    };

    private static KnowledgeSnapshot Snapshot(string content = "body", DateTimeOffset? changedAt = null) =>
        new(
            [new TransferDirectory("alpha", "alpha documents")],
            [new KnowledgeSnapshotDocument("alpha", "one", "a summary", [], "alpha/one.md", content)],
            changedAt ?? ChangedAt);

    private static KnowledgeGitRepository Repository(
        KnowledgeSnapshot snapshot,
        FakeGitHistoryStore? store = null,
        GitOptions? options = null,
        string scope = Scope)
    {
        var settings = options ?? Options;

        return new KnowledgeGitRepository(
            scope,
            snapshot,
            new KnowledgeGitHistory(store ?? new FakeGitHistoryStore(), settings),
            settings);
    }

    [Fact]
    public async Task The_branch_and_head_point_at_the_snapshot_commit()
    {
        var repository = Repository(Snapshot());

        var references = await repository.GetReferencesAsync();
        var commit = await repository.GetCommitAsync();

        Assert.Equal("refs/heads/main", references.HeadTarget);
        Assert.Equal(commit.Id, Assert.Single(references).Target);
    }

    [Fact]
    public async Task A_configured_branch_name_is_used()
    {
        var options = new GitOptions { Branch = "knowledge" };
        var repository = Repository(Snapshot(), options: options);

        var references = await repository.GetReferencesAsync();

        Assert.Equal("refs/heads/knowledge", references.HeadTarget);
    }

    [Fact]
    public async Task The_first_commit_has_no_parent()
    {
        var commit = await Repository(Snapshot()).GetCommitAsync();

        Assert.Empty(commit.Parents);
        Assert.Equal("Knowledge Base", commit.Author.Name);
        Assert.Equal("knowledge-base@example.com", commit.Author.Email);
        Assert.Equal(ChangedAt, commit.Author.When);
        Assert.Equal("Knowledge base snapshot: 1 document in 1 directory", commit.Subject);
    }

    [Fact]
    public async Task An_unknown_commit_id_is_reported_as_missing()
    {
        var repository = Repository(Snapshot());

        var commit = await repository.GetCommitAsync(GitObjectId.ForBlob("missing"u8));

        Assert.Null(commit);
    }

    [Fact]
    public async Task The_known_commit_is_returned_for_its_own_id()
    {
        var repository = Repository(Snapshot());

        var commit = await repository.GetCommitAsync();
        var found = await repository.GetCommitAsync(commit.Id);

        Assert.NotNull(found);
        Assert.Equal(commit.Id, found.Id);
    }

    [Fact]
    public async Task The_tree_of_the_commit_holds_the_snapshot()
    {
        var repository = Repository(Snapshot());

        var commit = await repository.GetCommitAsync();
        var tree = await repository.GetTreeAsync(commit);

        Assert.NotNull(tree.GetFile("alpha/one.md"));
        Assert.NotNull(tree.GetFile(KnowledgeTreeBuilder.ReadmeFileName));
        Assert.NotNull(tree.GetFile(DocumentTransferService.ManifestEntryName));
    }

    [Fact]
    public async Task The_commit_id_is_stable_while_the_snapshot_is_unchanged()
    {
        var store = new FakeGitHistoryStore();

        var first = await Repository(Snapshot(), store).GetCommitAsync();
        var second = await Repository(Snapshot(), store).GetCommitAsync();

        Assert.Equal(first.Id, second.Id);
        Assert.Single(store.Commits(Scope));
    }

    [Fact]
    public async Task Changing_a_document_changes_the_commit()
    {
        var store = new FakeGitHistoryStore();

        var first = await Repository(Snapshot("one"), store).GetCommitAsync();
        var second = await Repository(Snapshot("two"), store).GetCommitAsync();

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task A_changed_snapshot_appends_a_commit_that_descends_from_the_previous_one()
    {
        var store = new FakeGitHistoryStore();

        var first = await Repository(Snapshot("one"), store).GetCommitAsync();
        var second = await Repository(Snapshot("two"), store).GetCommitAsync();

        Assert.Equal(first.Id, Assert.Single(second.Parents));
        Assert.Equal(2, store.Commits(Scope).Count);
    }

    [Fact]
    public async Task An_older_commit_is_still_served_with_its_own_tree()
    {
        var store = new FakeGitHistoryStore();

        var first = await Repository(Snapshot("one"), store).GetCommitAsync();
        await Repository(Snapshot("two"), store).GetCommitAsync();

        var repository = Repository(Snapshot("two"), store);
        var found = await repository.GetCommitAsync(first.Id);

        Assert.NotNull(found);

        var tree = await repository.GetTreeAsync(found);
        var document = tree.GetFile("alpha/one.md");

        Assert.NotNull(document);
        Assert.Equal("one", (await document.ReadStringAsync()).Trim());
    }

    [Fact]
    public async Task A_repository_only_serves_the_commits_of_its_own_scope()
    {
        var store = new FakeGitHistoryStore();

        var mine = await Repository(Snapshot("one"), store, scope: "mine").GetCommitAsync();
        await Repository(Snapshot("two"), store, scope: "other").GetCommitAsync();

        var repository = Repository(Snapshot("two"), store, scope: "other");

        Assert.Null(await repository.GetCommitAsync(mine.Id));
    }
}
