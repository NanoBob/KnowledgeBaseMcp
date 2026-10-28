using AgentKnowledgeBase.Mcp.Domain;
using MongoDB.Driver;

namespace AgentKnowledgeBase.Mcp.Storage;

public interface IDirectoryRepository
{
    Task<List<KnowledgeDocumentDirectory>> ListAsync(CancellationToken ct);

    Task<KnowledgeDocumentDirectory?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>Inserts a new directory. Returns false when the name is already taken.</summary>
    Task<bool> TryInsertAsync(KnowledgeDocumentDirectory directory, CancellationToken ct);

    /// <summary>Replaces the directory. Returns false when another directory already uses the name.</summary>
    Task<bool> TryUpdateAsync(KnowledgeDocumentDirectory directory, CancellationToken ct);

    /// <summary>Deletes the directory. Returns false when it does not exist.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}

public enum ReplaceOutcome
{
    Replaced,

    /// <summary>The stored version no longer equals the expected version.</summary>
    VersionMismatch,

    /// <summary>Another document in the same directory already uses the new name.</summary>
    NameConflict
}

public interface IDocumentRepository
{
    Task<List<KnowledgeDocument>> ListSummariesAsync(CancellationToken ct);

    Task<KnowledgeDocument?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>Inserts a new document. Returns false when the id or the directory/name pair already exists.</summary>
    Task<bool> TryInsertAsync(KnowledgeDocument document, CancellationToken ct);

    /// <summary>Replaces the document only if the stored version still equals <paramref name="expectedVersion"/>.</summary>
    Task<ReplaceOutcome> TryReplaceAsync(KnowledgeDocument document, int expectedVersion, CancellationToken ct);

    Task<bool> DeleteAsync(Guid id, CancellationToken ct);

    Task<int> CountByDirectoryAsync(Guid directoryId, CancellationToken ct);

    /// <summary>Revision metadata, newest first, without the document content.</summary>
    Task<List<KnowledgeDocumentRevision>> ListRevisionsAsync(Guid documentId, CancellationToken ct);

    Task<KnowledgeDocumentRevision?> FindRevisionAsync(Guid revisionId, CancellationToken ct);

    Task AddRevisionAsync(KnowledgeDocumentRevision revision, CancellationToken ct);

    Task DeleteRevisionsAsync(Guid documentId, CancellationToken ct);
}

public interface IApiKeyRepository
{
    Task InsertAsync(ApiKeyRecord record, CancellationToken ct);

    Task<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken ct);

    Task<List<ApiKeyRecord>> ListAsync(CancellationToken ct);

    /// <summary>Marks the key revoked. Returns false when no active key with that id exists.</summary>
    Task<bool> TryRevokeAsync(Guid id, DateTime revokedAt, CancellationToken ct);

    /// <summary>
    /// Replaces the key's secret in place, keeping its id, name and scopes.
    /// Returns null when no active key with that id exists.
    /// </summary>
    Task<ApiKeyRecord?> TryRotateAsync(Guid id, string newKeyHash, DateTime rotatedAt, CancellationToken ct);

    /// <summary>
    /// Replaces the key's directory scopes in place, keeping its id, name and secret.
    /// Returns null when no active key with that id exists.
    /// </summary>
    Task<ApiKeyRecord?> TryUpdateScopesAsync(Guid id, List<KeyScope> scopes, CancellationToken ct);
}

public sealed class MongoDirectoryRepository(IMongoDatabase database) : IDirectoryRepository
{
    private readonly IMongoCollection<KnowledgeDocumentDirectory> _directories =
        database.GetCollection<KnowledgeDocumentDirectory>("knowledge_document_directories");

    public Task<List<KnowledgeDocumentDirectory>> ListAsync(CancellationToken ct) =>
        _directories.Find(FilterDefinition<KnowledgeDocumentDirectory>.Empty)
            .SortBy(d => d.Name)
            .ToListAsync(ct);

    public Task<KnowledgeDocumentDirectory?> FindAsync(Guid id, CancellationToken ct) =>
        _directories.Find(d => d.Id == id).FirstOrDefaultAsync(ct)!;

    public async Task<bool> TryInsertAsync(KnowledgeDocumentDirectory directory, CancellationToken ct)
    {
        try
        {
            await _directories.InsertOneAsync(directory, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<bool> TryUpdateAsync(KnowledgeDocumentDirectory directory, CancellationToken ct)
    {
        try
        {
            var result = await _directories.ReplaceOneAsync(
                d => d.Id == directory.Id, directory, cancellationToken: ct);
            return result.MatchedCount == 1;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var result = await _directories.DeleteOneAsync(d => d.Id == id, ct);
        return result.DeletedCount == 1;
    }

    public static async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct)
    {
        var directories = database.GetCollection<KnowledgeDocumentDirectory>("knowledge_document_directories");
        await directories.Indexes.CreateOneAsync(
            new CreateIndexModel<KnowledgeDocumentDirectory>(
                Builders<KnowledgeDocumentDirectory>.IndexKeys.Ascending(d => d.Name),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);
    }
}

public sealed class MongoDocumentRepository(IMongoDatabase database) : IDocumentRepository
{
    private readonly IMongoCollection<KnowledgeDocument> _documents = database.GetCollection<KnowledgeDocument>("knowledge_documents");
    private readonly IMongoCollection<KnowledgeDocumentRevision> _revisions =
        database.GetCollection<KnowledgeDocumentRevision>("knowledge_document_revisions");

    public Task<List<KnowledgeDocument>> ListSummariesAsync(CancellationToken ct) =>
        _documents.Find(FilterDefinition<KnowledgeDocument>.Empty)
            .Project<KnowledgeDocument>(Builders<KnowledgeDocument>.Projection.Exclude(d => d.Content))
            .SortBy(d => d.DirectoryId).ThenBy(d => d.Name)
            .ToListAsync(ct);

    public Task<KnowledgeDocument?> FindAsync(Guid id, CancellationToken ct) =>
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

    public async Task<ReplaceOutcome> TryReplaceAsync(KnowledgeDocument document, int expectedVersion, CancellationToken ct)
    {
        var filter = Builders<KnowledgeDocument>.Filter.Where(d => d.Id == document.Id && d.Version == expectedVersion);
        try
        {
            var result = await _documents.ReplaceOneAsync(filter, document, cancellationToken: ct);
            return result.ModifiedCount == 1 ? ReplaceOutcome.Replaced : ReplaceOutcome.VersionMismatch;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return ReplaceOutcome.NameConflict;
        }
    }

    public Task AddRevisionAsync(KnowledgeDocumentRevision revision, CancellationToken ct) =>
        _revisions.InsertOneAsync(revision, cancellationToken: ct);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var result = await _documents.DeleteOneAsync(d => d.Id == id, ct);
        return result.DeletedCount == 1;
    }

    public async Task<int> CountByDirectoryAsync(Guid directoryId, CancellationToken ct) =>
        (int)await _documents.CountDocumentsAsync(d => d.DirectoryId == directoryId, cancellationToken: ct);

    public Task<List<KnowledgeDocumentRevision>> ListRevisionsAsync(Guid documentId, CancellationToken ct) =>
        _revisions.Find(r => r.DocumentId == documentId)
            .Project<KnowledgeDocumentRevision>(Builders<KnowledgeDocumentRevision>.Projection.Exclude(r => r.Content))
            .SortByDescending(r => r.Version)
            .ToListAsync(ct);

    public Task<KnowledgeDocumentRevision?> FindRevisionAsync(Guid revisionId, CancellationToken ct) =>
        _revisions.Find(r => r.Id == revisionId).FirstOrDefaultAsync(ct)!;

    public Task DeleteRevisionsAsync(Guid documentId, CancellationToken ct) =>
        _revisions.DeleteManyAsync(r => r.DocumentId == documentId, ct);

    public static async Task EnsureIndexesAsync(IMongoDatabase database, CancellationToken ct)
    {
        var documents = database.GetCollection<KnowledgeDocument>("knowledge_documents");
        await documents.Indexes.CreateOneAsync(
            new CreateIndexModel<KnowledgeDocument>(
                Builders<KnowledgeDocument>.IndexKeys.Ascending(d => d.DirectoryId).Ascending(d => d.Name),
                new CreateIndexOptions
                {
                    Unique = true,
                    Collation = new Collation("en", strength: CollationStrength.Secondary)
                }),
            cancellationToken: ct);

        var revisions = database.GetCollection<KnowledgeDocumentRevision>("knowledge_document_revisions");
        await revisions.Indexes.CreateOneAsync(
            new CreateIndexModel<KnowledgeDocumentRevision>(
                Builders<KnowledgeDocumentRevision>.IndexKeys.Ascending(r => r.DocumentId).Ascending(r => r.Version),
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

    public async Task<bool> TryRevokeAsync(Guid id, DateTime revokedAt, CancellationToken ct)
    {
        var filter = Builders<ApiKeyRecord>.Filter.Where(k => k.Id == id && k.RevokedAt == null);
        var update = Builders<ApiKeyRecord>.Update.Set(k => k.RevokedAt, revokedAt);
        var result = await _keys.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public Task<ApiKeyRecord?> TryRotateAsync(Guid id, string newKeyHash, DateTime rotatedAt, CancellationToken ct)
    {
        var filter = Builders<ApiKeyRecord>.Filter.Where(k => k.Id == id && k.RevokedAt == null);
        var update = Builders<ApiKeyRecord>.Update
            .Set(k => k.KeyHash, newKeyHash)
            .Set(k => k.RotatedAt, rotatedAt);
        var options = new FindOneAndUpdateOptions<ApiKeyRecord> { ReturnDocument = ReturnDocument.After };
        return _keys.FindOneAndUpdateAsync(filter, update, options, ct)!;
    }

    public Task<ApiKeyRecord?> TryUpdateScopesAsync(Guid id, List<KeyScope> scopes, CancellationToken ct)
    {
        var filter = Builders<ApiKeyRecord>.Filter.Where(k => k.Id == id && k.RevokedAt == null);
        var update = Builders<ApiKeyRecord>.Update.Set(k => k.Scopes, scopes);
        var options = new FindOneAndUpdateOptions<ApiKeyRecord> { ReturnDocument = ReturnDocument.After };
        return _keys.FindOneAndUpdateAsync(filter, update, options, ct)!;
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
