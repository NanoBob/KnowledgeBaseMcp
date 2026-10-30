using System.Collections.Concurrent;
using System.Text;
using AgentKnowledgeBase.Mcp.Domain;
using GenHTTP.Modules.Git;

namespace AgentKnowledgeBase.Mcp.Git;

/// <summary>A commit together with the tree the current snapshot produces.</summary>
public sealed record GitResolvedCommit(GitCommit Commit, GitTree Tree);

/// <summary>
/// Turns a snapshot into a commit and remembers the commits it handed out, which is what lets a clone pull.
/// </summary>
/// <remarks>
/// Git only fast-forwards a clone when the new commit descends from the one the client already has, so the
/// history is persisted per scope: an unchanged snapshot resolves to the stored head, a changed one appends a
/// child of it. The scope is the set of directories a key may read, so two keys see separate histories at the
/// same URL and neither can read the other's.
/// </remarks>
public sealed class KnowledgeGitHistory(IGitHistoryStore store, GitOptions options)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Identifies the history a caller sees. Directory ids are sorted so the same set of scopes always maps to
    /// the same history, no matter in which order the key lists them.
    /// </summary>
    public static string ScopeKey(ApiCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var readable = caller.ReadableDirectoryIds;
        if (readable is null)
        {
            return "all";
        }

        return readable.Count == 0
            ? "none"
            : string.Join(',', readable.Select(id => id.ToString("N")).OrderBy(id => id, StringComparer.Ordinal));
    }

    /// <summary>
    /// The commit the scope is served right now. Reuses the stored head while the snapshot still produces the
    /// same tree, so an unchanged knowledge base keeps the commit a clone already has.
    /// </summary>
    public async Task<GitResolvedCommit> ResolveAsync(
        string scope,
        KnowledgeSnapshot snapshot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var files = KnowledgeTreeBuilder.Files(snapshot);
        var tree = KnowledgeTreeBuilder.Build(files);
        var treeId = await tree.ComputeIdAsync().ConfigureAwait(false);

        var gate = gates.GetOrAdd(scope, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var head = await store.GetHeadAsync(scope, ct).ConfigureAwait(false);
            if (head is not null && string.Equals(head.Tree, treeId.ToString(), StringComparison.Ordinal))
            {
                return new GitResolvedCommit(GitCommit.Parse(head.Data), tree);
            }

            var when = snapshot.ChangedAt;
            if (head is not null && head.When >= when)
            {
                // Two snapshots can carry the same timestamp. A child that is not newer than its parent makes
                // clients that order history by date drop one of them, so the history is kept monotonic.
                when = head.When.AddSeconds(1);
            }

            var builder = GitCommit.Create()
                .Tree(treeId)
                .Author(options.AuthorName, options.AuthorEmail, when)
                .Committer(options.AuthorName, options.AuthorEmail, when)
                .Message(Message(snapshot));

            if (head is not null)
            {
                builder.Parent(GitObjectId.Parse(head.Sha));
            }

            var commit = builder.Build();
            var stored = new GitStoredCommit(
                commit.Id.ToString(),
                head?.Sha,
                treeId.ToString(),
                commit.Data.ToArray(),
                when,
                [.. files.Select(file => new GitStoredFile(file.Path, BlobId(file.Content)))]);

            await store.AppendAsync(
                scope,
                stored,
                [.. files.Select(file => new GitStoredBlob(BlobId(file.Content), file.Content))],
                options.HistoryDepth,
                ct).ConfigureAwait(false);

            return new GitResolvedCommit(commit, tree);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A commit this scope served before, so a client that pinned it can still fetch it.</summary>
    public async Task<GitStoredCommit?> FindAsync(string scope, GitObjectId id, CancellationToken ct) =>
        await store.FindCommitAsync(scope, id, ct).ConfigureAwait(false);

    /// <summary>Rebuilds the tree of a commit served earlier from the content stored with it.</summary>
    public async Task<GitTree> GetTreeAsync(string scope, GitStoredCommit commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commit);

        var contents = await store
            .ReadBlobsAsync([.. commit.Files.Select(file => file.BlobId).Distinct(StringComparer.Ordinal)], ct)
            .ConfigureAwait(false);

        var builder = GitTree.Create();
        foreach (var file in commit.Files)
        {
            if (!contents.TryGetValue(file.BlobId, out var content))
            {
                throw new InvalidOperationException(
                    $"The content of '{file.Path}' in commit {commit.Sha} is no longer stored.");
            }

            builder.Add(file.Path, content);
        }

        return builder.Build();
    }

    public static string Message(KnowledgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return $"Knowledge base snapshot: {snapshot.DocumentCount} " +
               $"{(snapshot.DocumentCount == 1 ? "document" : "documents")} in {snapshot.DirectoryCount} " +
               $"{(snapshot.DirectoryCount == 1 ? "directory" : "directories")}";
    }

    private static string BlobId(string content) => GitObjectId.ForBlob(Encoding.UTF8.GetBytes(content)).ToString();
}
