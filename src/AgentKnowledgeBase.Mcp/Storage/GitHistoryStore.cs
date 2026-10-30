using AgentKnowledgeBase.Mcp.Git;
using GenHTTP.Modules.Git;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace AgentKnowledgeBase.Mcp.Storage;

/// <summary>
/// Keeps the commits that were served, per scope, so a clone can fast-forward instead of re-cloning. Blobs are
/// shared between commits, so a snapshot that changes one document stores one new blob, not the whole tree.
/// </summary>
public sealed class MongoGitHistoryStore(IMongoDatabase database) : IGitHistoryStore
{
    public const string CommitCollectionName = "git_commits";
    public const string BlobCollectionName = "git_blobs";
    public const string HeadCollectionName = "git_heads";

    private readonly IMongoCollection<GitCommitDocument> _commits =
        database.GetCollection<GitCommitDocument>(CommitCollectionName);
    private readonly IMongoCollection<GitBlobDocument> _blobs =
        database.GetCollection<GitBlobDocument>(BlobCollectionName);
    private readonly IMongoCollection<GitHeadDocument> _heads =
        database.GetCollection<GitHeadDocument>(HeadCollectionName);

    public async Task<GitStoredCommit?> GetHeadAsync(string scope, CancellationToken ct)
    {
        var head = await _heads.Find(h => h.Id == scope).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return head is null ? null : await FindByShaAsync(scope, head.Sha, ct).ConfigureAwait(false);
    }

    public Task<GitStoredCommit?> FindCommitAsync(string scope, GitObjectId id, CancellationToken ct) =>
        FindByShaAsync(scope, id.ToString(), ct);

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

        await _heads.UpdateOneAsync(
            h => h.Id == scope,
            Builders<GitHeadDocument>.Update.Set(h => h.Sha, commit.Sha),
            new UpdateOptions { IsUpsert = true },
            ct).ConfigureAwait(false);

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
    private async Task PruneAsync(string scope, int depth, CancellationToken ct)
    {
        var stale = await _commits
            .Find(c => c.Scope == scope)
            .SortByDescending(c => c.When)
            .Skip(depth)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (stale.Count == 0)
        {
            return;
        }

        var staleIds = stale.Select(c => c.Id).ToList();
        await _commits.DeleteManyAsync(c => staleIds.Contains(c.Id), ct).ConfigureAwait(false);

        var candidates = stale
            .SelectMany(c => c.Files)
            .Select(f => f.Blob)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        var stillReferenced = await _commits
            .Find(Builders<GitCommitDocument>.Filter.In("Files.Blob", candidates))
            .Project(c => c.Files)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var alive = stillReferenced
            .SelectMany(files => files)
            .Select(f => f.Blob)
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

        if (document is null)
        {
            return null;
        }

        return new GitStoredCommit(
            document.Sha,
            document.Parent,
            document.Tree,
            document.Data,
            new DateTimeOffset(DateTime.SpecifyKind(document.When, DateTimeKind.Utc), TimeSpan.Zero),
            [.. document.Files.Select(f => new GitStoredFile(f.Path, f.Blob))]);
    }

    private static GitCommitDocument ToDocument(string scope, GitStoredCommit commit) => new()
    {
        Scope = scope,
        Sha = commit.Sha,
        Parent = commit.Parent,
        Tree = commit.Tree,
        Data = commit.Data,
        When = commit.When.UtcDateTime,
        Files = [.. commit.Files.Select(f => new GitFileDocument { Path = f.Path, Blob = f.BlobId })]
    };

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
                    Builders<GitCommitDocument>.IndexKeys.Ascending("Files.Blob"))
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

    public byte[] Data { get; set; } = [];

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime When { get; set; }

    public List<GitFileDocument> Files { get; set; } = [];
}

public sealed class GitFileDocument
{
    public string Path { get; set; } = "";

    /// <summary>The id of the blob holding the content, so identical content is stored once.</summary>
    public string Blob { get; set; } = "";
}

public sealed class GitBlobDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    public string Content { get; set; } = "";
}

public sealed class GitHeadDocument
{
    /// <summary>The scope, which is the set of directories a key may read.</summary>
    [BsonId]
    public string Id { get; set; } = "";

    public string Sha { get; set; } = "";
}
