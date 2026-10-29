using System.Globalization;
using System.Runtime.CompilerServices;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Storage;

namespace AgentKnowledgeBase.Tests;

public sealed class FakeDirectoryRepository : IDirectoryRepository
{
    public Dictionary<Guid, KnowledgeDocumentDirectory> Directories { get; } = new();

    public Task<List<KnowledgeDocumentDirectory>> ListAsync(CancellationToken ct) =>
        Task.FromResult(Ordered(Directories.Values));

    public Task<List<KnowledgeDocumentDirectory>> ListByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct) =>
        Task.FromResult(Ordered(Directories.Values.Where(d => ids.Contains(d.Id))));

    public Task<KnowledgeDocumentDirectory?> FindAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Directories.TryGetValue(id, out var d) ? Clone(d) : null);

    public Task<bool> TryInsertAsync(KnowledgeDocumentDirectory directory, CancellationToken ct)
    {
        if (Directories.Values.Any(d => string.Equals(d.Name, directory.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(false);
        }

        Directories[directory.Id] = Clone(directory);
        return Task.FromResult(true);
    }

    public Task<bool> TryUpdateAsync(KnowledgeDocumentDirectory directory, CancellationToken ct)
    {
        if (!Directories.ContainsKey(directory.Id))
        {
            return Task.FromResult(false);
        }

        if (Directories.Values.Any(d =>
                d.Id != directory.Id
                && string.Equals(d.Name, directory.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(false);
        }

        Directories[directory.Id] = Clone(directory);
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Directories.Remove(id));

    /// <summary>Mirrors the name ordering both Mongo list queries apply, so tests see realistic order.</summary>
    private static List<KnowledgeDocumentDirectory> Ordered(IEnumerable<KnowledgeDocumentDirectory> directories) =>
        directories.OrderBy(d => d.Name, StringComparer.Ordinal).Select(Clone).ToList();

    private static KnowledgeDocumentDirectory Clone(KnowledgeDocumentDirectory d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Description = d.Description,
        CreatedAt = d.CreatedAt,
        CreatedBy = d.CreatedBy
    };
}

public sealed class FakeDocumentRepository : IDocumentRepository
{
    /// <summary>Mirrors the ICU 'en' secondary collation the document name index is built with.</summary>
    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("en"), CompareOptions.IgnoreCase);

    public Dictionary<Guid, KnowledgeDocument> Documents { get; } = new();
    public List<KnowledgeDocumentRevision> Revisions { get; } = [];

    public Task<List<KnowledgeDocument>> ListSummariesAsync(
        IReadOnlyCollection<Guid>? directoryIds,
        CancellationToken ct) =>
        Task.FromResult(Documents.Values
            .Where(d => directoryIds is null || directoryIds.Contains(d.DirectoryId))
            .Select(Summary)
            .ToList());

    /// <summary>Mongo projects the content out of a summary query; the fake does too, so a planner that reads
    /// content from a summary fails here instead of only in production.</summary>
    private static KnowledgeDocument Summary(KnowledgeDocument d) => new()
    {
        Id = d.Id,
        DirectoryId = d.DirectoryId,
        Name = d.Name,
        Summary = d.Summary,
        Tags = [.. d.Tags],
        Content = "",
        Version = d.Version,
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
        CreatedBy = d.CreatedBy,
        UpdatedBy = d.UpdatedBy
    };

    public Task<KnowledgeDocument?> FindAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Documents.TryGetValue(id, out var d) ? Clone(d) : null);

    public async IAsyncEnumerable<KnowledgeDocument> StreamByDirectoryIdsAsync(
        IReadOnlyCollection<Guid>? directoryIds,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();

        foreach (var document in Documents.Values
                     .Where(d => directoryIds is null || directoryIds.Contains(d.DirectoryId))
                     .Select(Clone)
                     .ToList())
        {
            ct.ThrowIfCancellationRequested();
            yield return document;
        }
    }

    public Task<KnowledgeDocument?> FindByNameAsync(Guid directoryId, string name, CancellationToken ct) =>
        Task.FromResult(Documents.Values
            .Where(d => d.DirectoryId == directoryId && NameComparer.Equals(d.Name, name))
            .Select(Clone)
            .FirstOrDefault());

    public Task<bool> TryInsertAsync(KnowledgeDocument document, CancellationToken ct)
    {
        if (Documents.ContainsKey(document.Id) || HasName(document.DirectoryId, document.Name, null))
        {
            return Task.FromResult(false);
        }

        Documents[document.Id] = Clone(document);
        return Task.FromResult(true);
    }

    public Task<ReplaceOutcome> TryReplaceAsync(KnowledgeDocument document, int expectedVersion, CancellationToken ct)
    {
        if (!Documents.TryGetValue(document.Id, out var stored) || stored.Version != expectedVersion)
        {
            return Task.FromResult(ReplaceOutcome.VersionMismatch);
        }

        if (HasName(document.DirectoryId, document.Name, document.Id))
        {
            return Task.FromResult(ReplaceOutcome.NameConflict);
        }

        Documents[document.Id] = Clone(document);
        return Task.FromResult(ReplaceOutcome.Replaced);
    }

    public Task AddRevisionAsync(KnowledgeDocumentRevision revision, CancellationToken ct)
    {
        Revisions.Add(revision);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Documents.Remove(id));

    public Task<int> CountByDirectoryAsync(Guid directoryId, CancellationToken ct) =>
        Task.FromResult(Documents.Values.Count(d => d.DirectoryId == directoryId));

    public Task<List<KnowledgeDocumentRevision>> ListRevisionsAsync(Guid documentId, CancellationToken ct) =>
        Task.FromResult(Revisions
            .Where(r => r.DocumentId == documentId)
            .OrderByDescending(r => r.Version)
            .Select(CloneRevisionSummary)
            .ToList());

    public Task<KnowledgeDocumentRevision?> FindRevisionAsync(Guid revisionId, CancellationToken ct) =>
        Task.FromResult(Revisions.FirstOrDefault(r => r.Id == revisionId));

    public Task DeleteRevisionsAsync(Guid documentId, CancellationToken ct)
    {
        Revisions.RemoveAll(r => r.DocumentId == documentId);
        return Task.CompletedTask;
    }

    private bool HasName(Guid directoryId, string name, Guid? exceptId) =>
        Documents.Values.Any(d =>
            d.DirectoryId == directoryId
            && d.Id != exceptId
            && NameComparer.Equals(d.Name, name));

    private static KnowledgeDocumentRevision CloneRevisionSummary(KnowledgeDocumentRevision r) => new()
    {
        Id = r.Id,
        DocumentId = r.DocumentId,
        DirectoryId = r.DirectoryId,
        Version = r.Version,
        Name = r.Name,
        Summary = r.Summary,
        Tags = [.. r.Tags],
        Content = "",
        ChangedAt = r.ChangedAt,
        ChangedBy = r.ChangedBy
    };

    private static KnowledgeDocument Clone(KnowledgeDocument d) => new()
    {
        Id = d.Id,
        DirectoryId = d.DirectoryId,
        Name = d.Name,
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

    public Task<bool> TryRevokeAsync(Guid id, DateTime revokedAt, CancellationToken ct)
    {
        var key = Keys.FirstOrDefault(k => k.Id == id && k.RevokedAt is null);
        if (key is null)
        {
            return Task.FromResult(false);
        }

        key.RevokedAt = revokedAt;
        return Task.FromResult(true);
    }

    public Task<ApiKeyRecord?> TryRotateAsync(Guid id, string newKeyHash, DateTime rotatedAt, CancellationToken ct)
    {
        var key = Keys.FirstOrDefault(k => k.Id == id && k.RevokedAt is null);
        if (key is null)
        {
            return Task.FromResult<ApiKeyRecord?>(null);
        }

        key.KeyHash = newKeyHash;
        key.RotatedAt = rotatedAt;
        return Task.FromResult<ApiKeyRecord?>(key);
    }

    public Task<ApiKeyRecord?> TryUpdateScopesAsync(Guid id, List<KeyScope> scopes, CancellationToken ct)
    {
        var key = Keys.FirstOrDefault(k => k.Id == id && k.RevokedAt is null);
        if (key is null)
        {
            return Task.FromResult<ApiKeyRecord?>(null);
        }

        key.Scopes = scopes;
        return Task.FromResult<ApiKeyRecord?>(key);
    }
}
