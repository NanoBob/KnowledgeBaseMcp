using AgentKnowledgeBase.Mcp;
using AgentKnowledgeBase.Mcp.Git;
using AgentKnowledgeBase.Mcp.Services;
using GenHTTP.Modules.Git;

namespace AgentKnowledgeBase.Tests;

public class KnowledgeGitRepositoryTests
{
    private static readonly DateTimeOffset ChangedAt = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private static readonly GitOptions Options = new()
    {
        Enabled = true,
        Path = "/git/knowledge.git",
        Branch = "main",
        AuthorName = "Knowledge Base",
        AuthorEmail = "knowledge-base@example.com"
    };

    private static KnowledgeSnapshot Snapshot(string content = "body") =>
        new(
            [new TransferDirectory("alpha", "alpha documents")],
            [new KnowledgeSnapshotDocument("alpha", "one", "a summary", [], "alpha/one.md", content)],
            ChangedAt);

    [Fact]
    public async Task The_branch_and_head_point_at_the_snapshot_commit()
    {
        var repository = new KnowledgeGitRepository(Snapshot(), Options);

        var references = await repository.GetReferencesAsync();
        var commit = await repository.GetCommitAsync();

        Assert.Equal("refs/heads/main", references.HeadTarget);
        Assert.Equal(commit.Id, Assert.Single(references).Target);
    }

    [Fact]
    public async Task A_configured_branch_name_is_used()
    {
        var options = new GitOptions { Branch = "knowledge" };
        var repository = new KnowledgeGitRepository(Snapshot(), options);

        var references = await repository.GetReferencesAsync();

        Assert.Equal("refs/heads/knowledge", references.HeadTarget);
    }

    [Fact]
    public async Task The_commit_has_no_parent_so_a_clone_never_traverses_history()
    {
        var commit = await new KnowledgeGitRepository(Snapshot(), Options).GetCommitAsync();

        Assert.Empty(commit.Parents);
        Assert.Equal("Knowledge Base", commit.Author.Name);
        Assert.Equal("knowledge-base@example.com", commit.Author.Email);
        Assert.Equal(ChangedAt, commit.Author.When);
        Assert.Equal("Knowledge base snapshot: 1 document in 1 directory", commit.Subject);
    }

    [Fact]
    public async Task An_unknown_commit_id_is_reported_as_missing()
    {
        var repository = new KnowledgeGitRepository(Snapshot(), Options);

        var commit = await repository.GetCommitAsync(GitObjectId.ForBlob("missing"u8));

        Assert.Null(commit);
    }

    [Fact]
    public async Task The_known_commit_is_returned_for_its_own_id()
    {
        var repository = new KnowledgeGitRepository(Snapshot(), Options);

        var commit = await repository.GetCommitAsync();
        var found = await repository.GetCommitAsync(commit.Id);

        Assert.NotNull(found);
        Assert.Equal(commit.Id, found.Id);
    }

    [Fact]
    public async Task The_tree_of_the_commit_holds_the_snapshot()
    {
        var repository = new KnowledgeGitRepository(Snapshot(), Options);

        var commit = await repository.GetCommitAsync();
        var tree = await repository.GetTreeAsync(commit);

        Assert.NotNull(tree.GetFile("alpha/one.md"));
        Assert.NotNull(tree.GetFile(KnowledgeTreeBuilder.ReadmeFileName));
        Assert.NotNull(tree.GetFile(DocumentTransferService.ManifestEntryName));
    }

    [Fact]
    public async Task The_commit_id_is_stable_while_the_snapshot_is_unchanged()
    {
        var first = await new KnowledgeGitRepository(Snapshot(), Options).GetCommitAsync();
        var second = await new KnowledgeGitRepository(Snapshot(), Options).GetCommitAsync();

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Changing_a_document_changes_the_commit()
    {
        var first = await new KnowledgeGitRepository(Snapshot("one"), Options).GetCommitAsync();
        var second = await new KnowledgeGitRepository(Snapshot("two"), Options).GetCommitAsync();

        Assert.NotEqual(first.Id, second.Id);
    }
}
