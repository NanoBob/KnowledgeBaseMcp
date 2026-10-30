using System.Collections.Concurrent;
using System.Text;
using AgentKnowledgeBase.Mcp.Domain;
using GenHTTP.Modules.Git;

namespace AgentKnowledgeBase.Mcp.Git;

/// <summary>A commit together with the tree the current snapshot produces.</summary>
public sealed record GitResolvedCommit(GitCommit Commit, GitTree Tree);

/// <summary>
/// Turns a snapshot into a commit and remembers what it takes to hand that commit out again, which is what lets
/// a clone pull.
/// </summary>
/// <remarks>
/// Git only fast-forwards a clone when the new commit descends from the one the client already has, so the
/// history is persisted per scope: an unchanged snapshot resolves to the stored head, a changed one appends a
/// child of it. The scope is the set of directories a key may read, so two keys see separate histories at the
/// same URL and neither can read the other's.
///
/// Only the chain of object ids and the files that changed are stored; the commit itself is rebuilt from them
/// every time it is served. That keeps the stored history to a handful of ids per change instead of a copy of
/// the objects, at the price of the derivation being a permanent contract: every commit id a client holds was
/// produced by it, so <see cref="Derive"/> refuses to serve a commit whose rebuilt id no longer matches.
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

        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var blob = BlobId(file.Content);
            current[file.Path] = blob;
            contents[blob] = file.Content;
        }

        var gate = gates.GetOrAdd(scope, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var history = await store.GetHistoryAsync(scope, options.HistoryDepth, ct).ConfigureAwait(false);
            var head = history.Count == 0 ? null : history[0];

            if (head is not null && string.Equals(head.Tree, treeId.ToString(), StringComparison.Ordinal))
            {
                return new GitResolvedCommit(Derive(head), tree);
            }

            var when = Timestamp(snapshot.ChangedAt);
            if (head is not null && head.When >= when)
            {
                // Two snapshots can carry the same timestamp. A child that is not newer than its parent makes
                // clients that order history by date drop one of them, so the history is kept monotonic.
                when = head.When.AddSeconds(1);
            }

            var changes = Diff(
                head is null ? new Dictionary<string, string>(StringComparer.Ordinal) : GitHistoryFold.Files(history),
                current,
                head is null);

            var builder = GitCommit.Create()
                .Tree(treeId)
                .Author(options.AuthorName, options.AuthorEmail, when)
                .Committer(options.AuthorName, options.AuthorEmail, when)
                .Message(Message(snapshot.DocumentCount, snapshot.DirectoryCount));

            if (head is not null)
            {
                builder.Parent(GitObjectId.Parse(head.Sha));
            }

            var commit = builder.Build();
            var stored = new GitStoredCommit(
                commit.Id.ToString(),
                head?.Sha,
                treeId.ToString(),
                when,
                snapshot.DocumentCount,
                snapshot.DirectoryCount,
                changes,
                IsBoundary: head is null);

            await store.AppendAsync(
                scope,
                stored,
                [.. changes
                    .Select(change => change.BlobId)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Select(id => new GitStoredBlob(id, contents[id]))],
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

    /// <summary>
    /// Rebuilds the commit object from the facts stored with it. The id is checked, because a commit that no
    /// longer hashes to the id a client holds would make the client drop it and ask for the whole history again.
    /// </summary>
    public GitCommit Derive(GitStoredCommit stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var builder = GitCommit.Create()
            .Tree(GitObjectId.Parse(stored.Tree))
            .Author(options.AuthorName, options.AuthorEmail, stored.When)
            .Committer(options.AuthorName, options.AuthorEmail, stored.When)
            .Message(Message(stored.Documents, stored.Directories));

        if (stored.Parent is not null)
        {
            builder.Parent(GitObjectId.Parse(stored.Parent));
        }

        var commit = builder.Build();
        if (!string.Equals(commit.Id.ToString(), stored.Sha, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Commit {stored.Sha} no longer rebuilds to its own id ({commit.Id}); the stored history cannot " +
                "be served without breaking the clones that pinned it.");
        }

        return commit;
    }

    /// <summary>Rebuilds the tree of a commit served earlier from the stored chain and content.</summary>
    public async Task<GitTree> GetTreeAsync(string scope, GitStoredCommit commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commit);

        var history = await store.GetHistoryAsync(scope, options.HistoryDepth, ct).ConfigureAwait(false);
        var index = IndexOf(history, commit.Sha);

        if (index < 0)
        {
            throw new InvalidOperationException($"Commit {commit.Sha} is no longer part of this history.");
        }

        var files = GitHistoryFold.Files([.. history.Skip(index)]);
        var contents = await store
            .ReadBlobsAsync([.. files.Values.Distinct(StringComparer.Ordinal)], ct)
            .ConfigureAwait(false);

        var builder = GitTree.Create();
        foreach (var file in files.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            if (!contents.TryGetValue(file.Value, out var content))
            {
                throw new InvalidOperationException(
                    $"The content of '{file.Key}' in commit {commit.Sha} is no longer stored.");
            }

            builder.Add(file.Key, content);
        }

        return builder.Build();
    }

    public static string Message(KnowledgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return Message(snapshot.DocumentCount, snapshot.DirectoryCount);
    }

    /// <summary>
    /// The message is rebuilt from the two counts rather than the snapshot, because a directory without
    /// documents leaves no file in the tree to count.
    /// </summary>
    public static string Message(int documents, int directories) =>
        $"Knowledge base snapshot: {documents} {(documents == 1 ? "document" : "documents")} in " +
        $"{directories} {(directories == 1 ? "directory" : "directories")}";

    private static int IndexOf(IReadOnlyList<GitStoredCommit> history, string sha)
    {
        for (var index = 0; index < history.Count; index++)
        {
            if (string.Equals(history[index].Sha, sha, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// What a commit changes about the previous one. A root commit carries every file; later ones carry only the
    /// files that appeared, changed or disappeared, which is what keeps the stored history small.
    /// </summary>
    private static IReadOnlyList<GitStoredChange> Diff(
        IReadOnlyDictionary<string, string> previous,
        IReadOnlyDictionary<string, string> current,
        bool all)
    {
        var changes = new List<GitStoredChange>();

        foreach (var file in current.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            if (all
                || !previous.TryGetValue(file.Key, out var blob)
                || !string.Equals(blob, file.Value, StringComparison.Ordinal))
            {
                changes.Add(new GitStoredChange(file.Key, file.Value));
            }
        }

        if (!all)
        {
            changes.AddRange(previous.Keys
                .Where(path => !current.ContainsKey(path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => new GitStoredChange(path, null)));
        }

        return changes;
    }

    /// <summary>
    /// Git timestamps have a resolution of one second and the offset is part of the commit bytes, so the value is
    /// pinned to UTC at that resolution. Storing and rebuilding it then always produces the same object.
    /// </summary>
    private static DateTimeOffset Timestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    private static string BlobId(string content) => GitObjectId.ForBlob(Encoding.UTF8.GetBytes(content)).ToString();
}
