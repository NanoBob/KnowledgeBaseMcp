using GenHTTP.Modules.Git;

namespace AgentKnowledgeBase.Mcp.Git;

/// <summary>A blob a stored commit references: the object id git uses for it and the content behind it.</summary>
public sealed record GitStoredBlob(string Id, string Content);

/// <summary>A file of a stored commit, pointing at the blob that holds its content.</summary>
public sealed record GitStoredFile(string Path, string BlobId);

/// <summary>
/// A commit that was served before, with everything needed to hand it out again. The raw commit data is kept
/// rather than rebuilt, so the id a client pinned keeps pointing at the same object even if the generator
/// changes later.
/// </summary>
public sealed record GitStoredCommit(
    string Sha,
    string? Parent,
    string Tree,
    byte[] Data,
    DateTimeOffset When,
    IReadOnlyList<GitStoredFile> Files);

/// <summary>
/// Remembers the commits handed out per scope. Without it every request would announce a fresh parentless
/// commit, and a clone could never fast-forward.
/// </summary>
public interface IGitHistoryStore
{
    /// <summary>The newest commit of the scope, or null when nothing has been served for it yet.</summary>
    Task<GitStoredCommit?> GetHeadAsync(string scope, CancellationToken ct);

    Task<GitStoredCommit?> FindCommitAsync(string scope, GitObjectId id, CancellationToken ct);

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
