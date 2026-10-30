using GenHTTP.Modules.Git;

namespace AgentKnowledgeBase.Mcp.Git;

/// <summary>
/// Serves one scope of the knowledge base as a repository. The commit comes from
/// <see cref="KnowledgeGitHistory"/>, which keeps the history stable across requests so a clone can pull.
/// </summary>
/// <remarks>
/// The instance lives for one request, so it caches only within that request; the stored history is what makes
/// the branch, the commit and the tree agree even when the knowledge base changes between requests.
/// </remarks>
public sealed class KnowledgeGitRepository(
    string scope,
    KnowledgeSnapshot snapshot,
    KnowledgeGitHistory history,
    GitOptions options) : IGitRepository
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private GitResolvedCommit? resolved;

    /// <summary>The commit every reference points to. Reused while the knowledge base still produces its tree.</summary>
    public async ValueTask<GitCommit> GetCommitAsync() => (await ResolveAsync().ConfigureAwait(false)).Commit;

    public async ValueTask<GitReferences> GetReferencesAsync()
    {
        var commit = await GetCommitAsync().ConfigureAwait(false);

        return new GitReferences()
            .Head(options.Branch)
            .Branch(options.Branch, commit);
    }

    public async ValueTask<GitCommit?> GetCommitAsync(GitObjectId id)
    {
        var current = await ResolveAsync().ConfigureAwait(false);
        if (current.Commit.Id == id)
        {
            return current.Commit;
        }

        // An older commit is still served while it is retained, which is what lets a client that pinned one
        // fetch it again instead of re-cloning.
        var stored = await history.FindAsync(scope, id, CancellationToken.None).ConfigureAwait(false);

        return stored is null ? null : history.Derive(stored);
    }

    public async ValueTask<GitTree> GetTreeAsync(GitCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        var current = await ResolveAsync().ConfigureAwait(false);
        if (current.Commit.Id == commit.Id)
        {
            return current.Tree;
        }

        var stored = await history.FindAsync(scope, commit.Id, CancellationToken.None).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Commit {commit.Id} is not part of this repository.");

        return await history.GetTreeAsync(scope, stored, CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask<GitResolvedCommit> ResolveAsync()
    {
        if (resolved is { } cached)
        {
            return cached;
        }

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            resolved ??= await history.ResolveAsync(scope, snapshot, CancellationToken.None).ConfigureAwait(false);
            return resolved;
        }
        finally
        {
            gate.Release();
        }
    }
}
