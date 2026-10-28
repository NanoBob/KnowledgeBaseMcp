using AgentKnowledgeBase.Mcp.Domain;
using MongoDB.Driver;

namespace AgentKnowledgeBase.Mcp.Storage;

public interface IDocumentRepository
{
    Task<List<KnowledgeDocument>> ListSummariesAsync(CancellationToken ct);

    Task<KnowledgeDocument?> FindAsync(string id, CancellationToken ct);

    /// <summary>Inserts a new document. Returns false when the id already exists.</summary>
    Task<bool> TryInsertAsync(KnowledgeDocument document, CancellationToken ct);

    /// <summary>Replaces the document only if the stored version still equals <paramref name="expectedVersion"/>.</summary>
    Task<bool> TryReplaceAsync(KnowledgeDocument document, int expectedVersion, CancellationToken ct);

    Task AddRevisionAsync(DocumentRevision revision, CancellationToken ct);
}

public interface IApiKeyRepository
{
    Task InsertAsync(ApiKeyRecord record, CancellationToken ct);

    Task<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken ct);

    Task<List<ApiKeyRecord>> ListAsync(CancellationToken ct);

    /// <summary>Marks the key revoked. Returns false when no active key with that id exists.</summary>
    Task<bool> TryRevokeAsync(string id, DateTime revokedAt, CancellationToken ct);
}

public sealed class MongoDocumentRepository(IMongoDatabase database) : IDocumentRepository
{
    private readonly IMongoCollection<KnowledgeDocument> _documents = database.GetCollection<KnowledgeDocument>("documents");
    private readonly IMongoCollection<DocumentRevision> _revisions = database.GetCollection<DocumentRevision>("document_revisions");

    public Task<List<KnowledgeDocument>> ListSummariesAsync(CancellationToken ct) =>
        _documents.Find(FilterDefinition<KnowledgeDocument>.Empty)
            .Project<KnowledgeDocument>(Builders<KnowledgeDocument>.Projection.Exclude(d => d.Content))
            .SortBy(d => d.Path).ThenBy(d => d.Title)
            .ToListAsync(ct);

    public Task<KnowledgeDocument?> FindAsync(string id, CancellationToken ct) =>
        _documents.Find(d => d.Id == id).FirstOrDefaultAsync(ct)!;

    public async Task<bool> TryInsertAsync(KnowledgeDocument document, CancellationToken ct)
    {
        try
        {
            await _documents.InsertOneAsync(document, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<bool> TryReplaceAsync(KnowledgeDocument document, int expectedVersion, CancellationToken ct)
    {
        var filter = Builders<KnowledgeDocument>.Filter.Where(d => d.Id == document.Id && d.Version == expectedVersion);
        var result = await _documents.ReplaceOneAsync(filter, document, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public Task AddRevisionAsync(DocumentRevision revision, CancellationToken ct) =>
        _revisions.InsertOneAsync(revision, cancellationToken: ct);

    public static async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct)
    {
        var documents = database.GetCollection<KnowledgeDocument>("documents");
        await documents.Indexes.CreateOneAsync(
            new CreateIndexModel<KnowledgeDocument>(Builders<KnowledgeDocument>.IndexKeys.Ascending(d => d.Path)),
            cancellationToken: ct);

        var revisions = database.GetCollection<DocumentRevision>("document_revisions");
        await revisions.Indexes.CreateOneAsync(
            new CreateIndexModel<DocumentRevision>(
                Builders<DocumentRevision>.IndexKeys.Ascending(r => r.DocumentId).Ascending(r => r.Version),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);
    }
}

public sealed class MongoApiKeyRepository(IMongoDatabase database) : IApiKeyRepository
{
    private readonly IMongoCollection<ApiKeyRecord> _keys = database.GetCollection<ApiKeyRecord>("api_keys");

    public Task InsertAsync(ApiKeyRecord record, CancellationToken ct) =>
        _keys.InsertOneAsync(record, cancellationToken: ct);

    public Task<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken ct) =>
        _keys.Find(k => k.KeyHash == keyHash).FirstOrDefaultAsync(ct)!;

    public Task<List<ApiKeyRecord>> ListAsync(CancellationToken ct) =>
        _keys.Find(FilterDefinition<ApiKeyRecord>.Empty).SortBy(k => k.CreatedAt).ToListAsync(ct);

    public async Task<bool> TryRevokeAsync(string id, DateTime revokedAt, CancellationToken ct)
    {
        var filter = Builders<ApiKeyRecord>.Filter.Where(k => k.Id == id && k.RevokedAt == null);
        var update = Builders<ApiKeyRecord>.Update.Set(k => k.RevokedAt, revokedAt);
        var result = await _keys.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public static async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct)
    {
        var keys = database.GetCollection<ApiKeyRecord>("api_keys");
        await keys.Indexes.CreateOneAsync(
            new CreateIndexModel<ApiKeyRecord>(
                Builders<ApiKeyRecord>.IndexKeys.Ascending(k => k.KeyHash),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);
    }
}
