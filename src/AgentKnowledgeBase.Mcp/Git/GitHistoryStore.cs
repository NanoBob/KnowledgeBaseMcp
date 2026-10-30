using GenHTTP.Modules.Git;

namespace AgentKnowledgeBase.Mcp.Git;

/// <summary>A blob a stored commit references: the object id git uses for it and the content behind it.</summary>
public sealed record GitStoredBlob(string Id, string Content);

/// <summary>
/// One file a commit added or changed. <see cref="BlobId"/> is null when the commit removed the file, which is
/// the only way a removal can be remembered once the document itself is gone.
/// </summary>
public sealed record GitStoredChange(string Path, string? BlobId);

/// <summary>
/// A commit that was served before, reduced to what cannot be recomputed: the object ids that identify it, when
/// it was made, and how its files differ from its parent. The commit object, the tree and every earlier version
/// of the content are derived from these facts, so a clone can still fast-forward without a copy of the objects
/// being kept.
/// </summary>
/// <remarks>
/// <see cref="IsBoundary"/> marks a commit that carries every file instead of a difference. The oldest commit of
/// a scope is turned into one when older commits are pruned, which is what keeps the retained history foldable
/// and matches the shallow boundary git expects when a client asks for a parent that is gone.
/// </remarks>
public sealed record GitStoredCommit(
    string Sha,
    string? Parent,
    string Tree,
    DateTimeOffset When,
    int Documents,
    int Directories,
    IReadOnlyList<GitStoredChange> Changes,
    bool IsBoundary);

/// <summary>
/// Remembers the commits handed out per scope. Without it every request would announce a fresh parentless
/// commit, and a clone could never fast-forward.
/// </summary>
public interface IGitHistoryStore
{
    Task<GitStoredCommit?> FindCommitAsync(string scope, GitObjectId id, CancellationToken ct);

    /// <summary>
    /// The newest commits of the scope, newest first, at most <paramref name="limit"/>. The oldest one is a
    /// boundary, so the result can be folded into the files of the newest commit.
    /// </summary>
    Task<IReadOnlyList<GitStoredCommit>> GetHistoryAsync(string scope, int limit, CancellationToken ct);

    /// <summary>
    /// Stores the commit and the blobs it references, makes it the head of the scope, then drops everything
    /// beyond <paramref name="depth"/> commits together with the blobs only that history used.
    /// </summary>
    Task AppendAsync(
        string scope,
        GitStoredCommit commit,
        IReadOnlyCollection<GitStoredBlob> blobs,
        int depth,
        CancellationToken ct);

    /// <summary>Content of the requested blobs. An id that is no longer stored is absent from the result.</summary>
    Task<IReadOnlyDictionary<string, string>> ReadBlobsAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken ct);
}
