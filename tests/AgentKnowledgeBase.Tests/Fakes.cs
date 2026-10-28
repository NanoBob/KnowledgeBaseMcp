using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Storage;

namespace AgentKnowledgeBase.Tests;

public sealed class FakeDocumentRepository : IDocumentRepository
{
    public Dictionary<string, KnowledgeDocument> Documents { get; } = new();
    public List<DocumentRevision> Revisions { get; } = [];

    public Task<List<KnowledgeDocument>> ListSummariesAsync(CancellationToken ct) =>
        Task.FromResult(Documents.Values.Select(Clone).ToList());

    public Task<KnowledgeDocument?> FindAsync(string id, CancellationToken ct) =>
        Task.FromResult(Documents.TryGetValue(id, out var d) ? Clone(d) : null);

    public Task<bool> TryInsertAsync(KnowledgeDocument document, CancellationToken ct)
    {
        if (Documents.ContainsKey(document.Id))
        {
            return Task.FromResult(false);
        }

        Documents[document.Id] = Clone(document);
        return Task.FromResult(true);
    }

    public Task<bool> TryReplaceAsync(KnowledgeDocument document, int expectedVersion, CancellationToken ct)
    {
        if (!Documents.TryGetValue(document.Id, out var stored) || stored.Version != expectedVersion)
        {
            return Task.FromResult(false);
        }

        Documents[document.Id] = Clone(document);
        return Task.FromResult(true);
    }

    public Task AddRevisionAsync(DocumentRevision revision, CancellationToken ct)
    {
        Revisions.Add(revision);
        return Task.CompletedTask;
    }

    private static KnowledgeDocument Clone(KnowledgeDocument d) => new()
    {
        Id = d.Id,
        Path = d.Path,
        Title = d.Title,
        Summary = d.Summary,
        Tags = [.. d.Tags],
        Content = d.Content,
        Version = d.Version,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
        CreatedBy = d.CreatedBy,
        UpdatedBy = d.UpdatedBy
    };
}

public sealed class FakeApiKeyRepository : IApiKeyRepository
{
    public List<ApiKeyRecord> Keys { get; } = [];

    public Task InsertAsync(ApiKeyRecord record, CancellationToken ct)
    {
        Keys.Add(record);
        return Task.CompletedTask;
    }

    public Task<ApiKeyRecord?> FindByHashAsync(string keyHash, CancellationToken ct) =>
        Task.FromResult(Keys.FirstOrDefault(k => k.KeyHash == keyHash));

    public Task<List<ApiKeyRecord>> ListAsync(CancellationToken ct) => Task.FromResult(Keys.ToList());

    public Task<bool> TryRevokeAsync(string id, DateTime revokedAt, CancellationToken ct)
    {
        var key = Keys.FirstOrDefault(k => k.Id == id && k.RevokedAt is null);
        if (key is null)
        {
            return Task.FromResult(false);
        }

        key.RevokedAt = revokedAt;
        return Task.FromResult(true);
    }
}
