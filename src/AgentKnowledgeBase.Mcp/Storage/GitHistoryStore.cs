using AgentKnowledgeBase.Mcp.Git;
using GenHTTP.Modules.Git;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace AgentKnowledgeBase.Mcp.Storage;

/// <summary>
/// Keeps the commits that were served, per scope, so a clone can fast-forward instead of re-cloning. A commit
/// stores only the files it changed, so a snapshot that edits one document adds one change, not a tree; blobs are
/// shared between commits, so the content behind an unchanged file is stored once.
/// </summary>
public sealed class MongoGitHistoryStore(IMongoDatabase database) : IGitHistoryStore
{
    public const string CommitCollectionName = "git_commits";
    public const string BlobCollectionName = "git_blobs";

    private readonly IMongoCollection<GitCommitDocument> _commits =
        database.GetCollection<GitCommitDocument>(CommitCollectionName);
    private readonly IMongoCollection<GitBlobDocument> _blobs =
        database.GetCollection<GitBlobDocument>(BlobCollectionName);

    public Task<GitStoredCommit?> FindCommitAsync(string scope, GitObjectId id, CancellationToken ct) =>
        FindByShaAsync(scope, id.ToString(), ct);

    public async Task<IReadOnlyList<GitStoredCommit>> GetHistoryAsync(
        string scope,
        int limit,
        CancellationToken ct)
    {
        var documents = await _commits
            .Find(c => c.Scope == scope)
            .SortByDescending(c => c.When)
            .ThenByDescending(c => c.Id)
            .Limit(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. documents.Select(ToCommit)];
    }

    public async Task AppendAsync(
        string scope,
        GitStoredCommit commit,
        IReadOnlyCollection<GitStoredBlob> blobs,
        int depth,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commit);

        try
        {
            await _commits.InsertOneAsync(ToDocument(scope, commit), cancellationToken: ct).ConfigureAwait(false);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // The same commit is already stored, which means the tree and the parent were identical.
        }

        if (blobs.Count > 0)
        {
            await _blobs.BulkWriteAsync(
                blobs.Select(blob => new ReplaceOneModel<GitBlobDocument>(
                    Builders<GitBlobDocument>.Filter.Eq(b => b.Id, blob.Id),
                    new GitBlobDocument { Id = blob.Id, Content = blob.Content })
                {
                    IsUpsert = true
                }),
                new BulkWriteOptions { IsOrdered = false },
                ct).ConfigureAwait(false);
        }

        await PruneAsync(scope, depth, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadBlobsAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var found = await _blobs
            .Find(Builders<GitBlobDocument>.Filter.In(b => b.Id, ids))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return found.ToDictionary(blob => blob.Id, blob => blob.Content, StringComparer.Ordinal);
    }

    /// <summary>
    /// Drops the commits beyond <paramref name="depth"/> and then the blobs nothing references any more, so the
    /// stored history stays bounded even though every change adds a commit.
    /// </summary>
    /// <remarks>
    /// The oldest commit that stays is rewritten into a boundary carrying every file, because the commit it used
    /// to be a difference against is about to be deleted and its tree still has to be rebuildable. One commit
    /// past the window is loaded, which is all the fold needs: that commit is the previous boundary once the
    /// window is full.
    /// </remarks>
    private async Task PruneAsync(string scope, int depth, CancellationToken ct)
    {
        var window = await _commits
            .Find(c => c.Scope == scope)
            .SortByDescending(c => c.When)
            .ThenByDescending(c => c.Id)
            .Limit(depth + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (window.Count <= depth)
        {
            return;
        }

        var history = window.Select(ToCommit).ToList();
        var kept = GitHistoryFold.Keep(history, depth);

        var stale = window.Skip(depth).ToList();
        await _commits
            .DeleteManyAsync(c => stale.Select(d => d.Id).Contains(c.Id), ct)
            .ConfigureAwait(false);

        var boundary = kept[^1];
        if (!window[depth - 1].IsBoundary)
        {
            await _commits.UpdateOneAsync(
                c => c.Scope == scope && c.Sha == boundary.Sha,
                Builders<GitCommitDocument>.Update
                    .Set(c => c.IsBoundary, true)
                    .Set(c => c.Changes, Changes(boundary.Changes)),
                cancellationToken: ct).ConfigureAwait(false);
        }

        var candidates = stale
            .SelectMany(c => c.Changes)
            .Select(change => change.Blob)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (candidates.Count > 0)
        {
            await CollectBlobsAsync(candidates, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes the blobs nothing references any more. Only the content the pruned commits held is considered, and
    /// every scope is checked, because a blob is shared by every scope that serves the same document.
    /// </summary>
    private async Task CollectBlobsAsync(IReadOnlyCollection<string> candidates, CancellationToken ct)
    {
        var stillReferenced = await _commits
            .Find(Builders<GitCommitDocument>.Filter.In("Changes.Blob", candidates))
            .Project(c => c.Changes)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var alive = stillReferenced
            .SelectMany(changes => changes)
            .Select(change => change.Blob)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var unused = candidates.Where(id => !alive.Contains(id)).ToList();
        if (unused.Count > 0)
        {
            await _blobs.DeleteManyAsync(b => unused.Contains(b.Id), ct).ConfigureAwait(false);
        }
    }

    private async Task<GitStoredCommit?> FindByShaAsync(string scope, string sha, CancellationToken ct)
    {
        var document = await _commits
            .Find(c => c.Scope == scope && c.Sha == sha)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return document is null ? null : ToCommit(document);
    }

    private static GitStoredCommit ToCommit(GitCommitDocument document) => new(
        document.Sha,
        document.Parent,
        document.Tree,
        new DateTimeOffset(DateTime.SpecifyKind(document.When, DateTimeKind.Utc), TimeSpan.Zero),
        document.Documents,
        document.Directories,
        [.. document.Changes.Select(change => new GitStoredChange(change.Path, change.Blob))],
        document.IsBoundary);

    private static GitCommitDocument ToDocument(string scope, GitStoredCommit commit) => new()
    {
        Scope = scope,
        Sha = commit.Sha,
        Parent = commit.Parent,
        Tree = commit.Tree,
        When = commit.When.UtcDateTime,
        Documents = commit.Documents,
        Directories = commit.Directories,
        Changes = Changes(commit.Changes),
        IsBoundary = commit.IsBoundary
    };

    private static List<GitChangeDocument> Changes(IReadOnlyList<GitStoredChange> changes) =>
        [.. changes.Select(change => new GitChangeDocument { Path = change.Path, Blob = change.BlobId })];

    public static async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct)
    {
        var commits = database.GetCollection<GitCommitDocument>(CommitCollectionName);
        await commits.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<GitCommitDocument>(
                    Builders<GitCommitDocument>.IndexKeys.Ascending(c => c.Scope).Ascending(c => c.Sha),
                    new CreateIndexOptions { Unique = true }),
                new CreateIndexModel<GitCommitDocument>(
                    Builders<GitCommitDocument>.IndexKeys.Ascending(c => c.Scope).Descending(c => c.When)),
                new CreateIndexModel<GitCommitDocument>(
                    Builders<GitCommitDocument>.IndexKeys.Ascending("Changes.Blob"))
            ],
            cancellationToken: ct).ConfigureAwait(false);
    }
}

public sealed class GitCommitDocument
{
    public ObjectId Id { get; set; }

    public string Scope { get; set; } = "";

    public string Sha { get; set; } = "";

    public string? Parent { get; set; }

    public string Tree { get; set; } = "";

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime When { get; set; }

    /// <summary>How many documents the snapshot held, which the commit message is rebuilt from.</summary>
    public int Documents { get; set; }

    public int Directories { get; set; }

    /// <summary>
    /// The files this commit added, changed or removed relative to its parent. A removal carries no blob, which
    /// is how a deleted document stays visible in the history after the document itself is gone.
    /// </summary>
    public List<GitChangeDocument> Changes { get; set; } = [];

    /// <summary>
    /// Set when <see cref="Changes"/> carries every file rather than a difference, which is true of the first
    /// commit of a scope and of the oldest commit that survives pruning.
    /// </summary>
    public bool IsBoundary { get; set; }
}

public sealed class GitChangeDocument
{
    public string Path { get; set; } = "";

    /// <summary>The id of the blob holding the content, so identical content is stored once.</summary>
    public string? Blob { get; set; }
}

public sealed class GitBlobDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    public string Content { get; set; } = "";
}
