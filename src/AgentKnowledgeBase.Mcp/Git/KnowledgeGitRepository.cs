using GenHTTP.Modules.Git;

namespace AgentKnowledgeBase.Mcp.Git;

/// <summary>
/// Serves one snapshot as a repository with a single, parentless commit. There is no history to traverse, so a
/// client that already has the snapshot only ever asks for the commit it knows about.
/// </summary>
/// <remarks>
/// The commit is created once per instance and the instance lives for one request, which keeps the branch, the
/// commit and the tree in agreement even when the knowledge base changes between requests.
/// </remarks>
public sealed class KnowledgeGitRepository(KnowledgeSnapshot snapshot, GitOptions options) : IGitRepository
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private (GitCommit Commit, GitTree Tree)? content;

    /// <summary>The commit every reference points to. Deterministic, so an unchanged knowledge base keeps its id.</summary>
    public async ValueTask<GitCommit> GetCommitAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return (await MaterializeAsync().ConfigureAwait(false)).Commit;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<GitReferences> GetReferencesAsync()
    {
        var commit = await GetCommitAsync().ConfigureAwait(false);

        return new GitReferences()
            .Head(options.Branch)
            .Branch(options.Branch, commit);
    }

    public async ValueTask<GitCommit?> GetCommitAsync(GitObjectId id)
    {
        var commit = await GetCommitAsync().ConfigureAwait(false);

        return commit.Id == id ? commit : null;
    }

    public async ValueTask<GitTree> GetTreeAsync(GitCommit commit)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Only one commit exists, and the server only asks for commits it was given, so the tree is always
            // the one this instance already built.
            return (await MaterializeAsync().ConfigureAwait(false)).Tree;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(GitCommit Commit, GitTree Tree)> MaterializeAsync()
    {
        if (content is { } existing)
        {
            return existing;
        }

        var tree = KnowledgeTreeBuilder.Build(snapshot);
        var treeId = await tree.ComputeIdAsync().ConfigureAwait(false);

        var message = $"Knowledge base snapshot: {snapshot.DocumentCount} " +
                      $"{(snapshot.DocumentCount == 1 ? "document" : "documents")} in {snapshot.DirectoryCount} " +
                      $"{(snapshot.DirectoryCount == 1 ? "directory" : "directories")}";

        var commit = GitCommit.Create()
            .Tree(treeId)
            .Author(options.AuthorName, options.AuthorEmail, snapshot.ChangedAt)
            .Committer(options.AuthorName, options.AuthorEmail, snapshot.ChangedAt)
            .Message(message)
            .Build();

        content = (commit, tree);
        return content.Value;
    }
}
