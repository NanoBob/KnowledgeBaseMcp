using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Storage;

namespace AgentKnowledgeBase.Mcp.Services;

public sealed record DocumentSummary(
    Guid Id,
    Guid DirectoryId,
    string DirectoryName,
    string Name,
    string Summary,
    IReadOnlyList<string> Tags,
    int Version,
    DateTime UpdatedAt);

public sealed record DocumentDetail(
    Guid Id,
    Guid DirectoryId,
    string DirectoryName,
    string Name,
    string Summary,
    IReadOnlyList<string> Tags,
    string Content,
    int Version,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string CreatedBy,
    string UpdatedBy);

public sealed record StoreRequest(
    Guid? Id,
    Guid? DirectoryId,
    string? Name,
    string? Summary,
    IEnumerable<string>? Tags,
    string? Content,
    int? ExpectedVersion);

public sealed record StoreResult(Guid Id, Guid DirectoryId, string Name, int Version, bool Created);

public sealed record RevisionSummary(
    Guid Id,
    Guid DocumentId,
    int Version,
    string Name,
    DateTime ChangedAt,
    string ChangedBy);

public sealed record RevisionDetail(
    Guid Id,
    Guid DocumentId,
    Guid DirectoryId,
    int Version,
    string Name,
    string Summary,
    IReadOnlyList<string> Tags,
    string Content,
    DateTime ChangedAt,
    string ChangedBy);

public sealed class KnowledgeService(IDocumentRepository documents, IDirectoryRepository directories)
{
    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(
        ApiCaller caller,
        Guid? directoryId,
        CancellationToken ct)
    {
        var readable = caller.ReadableDirectoryIds;
        if (readable is { Count: 0 })
        {
            return [];
        }

        // A single requested directory and the caller's readable set are intersected here, so the
        // repository never receives two filters that could contradict each other.
        if (directoryId is { } only)
        {
            if (readable is not null && !readable.Contains(only))
            {
                return [];
            }

            readable = [only];
        }

        var documentsPage = await documents.ListSummariesAsync(readable, ct);
        var directoryNames = await LoadDirectoryNamesAsync(
            documentsPage.Select(d => d.DirectoryId).Distinct().ToList(),
            ct);

        return documentsPage
            .Select(d => new DocumentSummary(
                d.Id,
                d.DirectoryId,
                DirectoryName(directoryNames, d.DirectoryId),
                d.Name,
                d.Summary,
                d.Tags,
                d.Version,
                d.UpdatedAt))
            .ToList();
    }

    public async Task<DocumentDetail> GetAsync(ApiCaller caller, Guid id, CancellationToken ct)
    {
        var document = await documents.FindAsync(id, ct);
        // Documents in unreadable directories look identical to missing ones so callers cannot probe for them.
        if (document is null || !caller.CanRead(document.DirectoryId))
        {
            throw KnowledgeException.NotFound($"document '{id}' was not found.");
        }

        var directory = await directories.FindAsync(document.DirectoryId, ct);
        return new DocumentDetail(
            document.Id,
            document.DirectoryId,
            directory?.Name ?? "",
            document.Name,
            document.Summary,
            document.Tags,
            document.Content,
            document.Version,
            document.CreatedAt,
            document.UpdatedAt,
            document.CreatedBy,
            document.UpdatedBy);
    }

    public async Task DeleteAsync(ApiCaller caller, Guid id, CancellationToken ct)
    {
        var document = await documents.FindAsync(id, ct);
        if (document is null || !caller.CanRead(document.DirectoryId))
        {
            throw KnowledgeException.NotFound($"document '{id}' was not found.");
        }

        if (!caller.CanWrite(document.DirectoryId))
        {
            throw KnowledgeException.Forbidden("this key cannot write to the document's directory.");
        }

        await documents.DeleteRevisionsAsync(id, ct);
        if (!await documents.DeleteAsync(id, ct))
        {
            throw KnowledgeException.NotFound($"document '{id}' was not found.");
        }
    }

    public async Task<IReadOnlyList<RevisionSummary>> ListRevisionsAsync(
        ApiCaller caller,
        Guid documentId,
        CancellationToken ct)
    {
        var document = await documents.FindAsync(documentId, ct);
        if (document is null || !caller.CanRead(document.DirectoryId))
        {
            throw KnowledgeException.NotFound($"document '{documentId}' was not found.");
        }

        var revisions = await documents.ListRevisionsAsync(documentId, ct);
        return revisions
            .Select(r => new RevisionSummary(r.Id, r.DocumentId, r.Version, r.Name, r.ChangedAt, r.ChangedBy))
            .ToList();
    }

    public async Task<RevisionDetail> GetRevisionAsync(ApiCaller caller, Guid revisionId, CancellationToken ct)
    {
        var revision = await documents.FindRevisionAsync(revisionId, ct);
        if (revision is null)
        {
            throw KnowledgeException.NotFound($"revision '{revisionId}' was not found.");
        }

        // A revision keeps the directory it was written in, but access follows the document's current
        // directory so moving a document can neither expose nor hide its history.
        var document = await documents.FindAsync(revision.DocumentId, ct);
        if (document is null || !caller.CanRead(document.DirectoryId))
        {
            throw KnowledgeException.NotFound($"revision '{revisionId}' was not found.");
        }

        return new RevisionDetail(
            revision.Id,
            revision.DocumentId,
            revision.DirectoryId,
            revision.Version,
            revision.Name,
            revision.Summary,
            revision.Tags,
            revision.Content,
            revision.ChangedAt,
            revision.ChangedBy);
    }

    public async Task<StoreResult> StoreAsync(ApiCaller caller, StoreRequest request, CancellationToken ct)
    {
        var summary = DocumentRules.NormalizeSummary(request.Summary);
        var content = DocumentRules.NormalizeContent(request.Content);
        var tags = TagRules.Normalize(request.Tags);

        return request.Id is null
            ? await CreateAsync(caller, request, summary, tags, content, ct)
            : await UpdateAsync(caller, request, summary, tags, content, ct);
    }

    public async Task<StoreResult> MoveAsync(
        ApiCaller caller,
        Guid id,
        Guid directoryId,
        int? expectedVersion,
        CancellationToken ct)
    {
        var existing = await documents.FindAsync(id, ct);
        if (existing is null || !caller.CanRead(existing.DirectoryId))
        {
            throw KnowledgeException.NotFound($"document '{id}' was not found.");
        }

        var source = await directories.FindAsync(existing.DirectoryId, ct)
            ?? throw KnowledgeException.NotFound($"directory '{existing.DirectoryId}' was not found.");

        if (!caller.CanWrite(source.Id))
        {
            throw KnowledgeException.Forbidden($"this key cannot write to directory '{source.Name}'.");
        }

        if (existing.DirectoryId == directoryId)
        {
            return new StoreResult(existing.Id, existing.DirectoryId, existing.Name, existing.Version, Created: false);
        }

        var target = await directories.FindAsync(directoryId, ct)
            ?? throw KnowledgeException.NotFound($"directory '{directoryId}' was not found.");

        if (!caller.CanWrite(target.Id))
        {
            throw KnowledgeException.Forbidden($"this key cannot write to directory '{target.Name}'.");
        }

        if (expectedVersion is not null && expectedVersion != existing.Version)
        {
            throw KnowledgeException.Conflict(
                $"document '{id}' is at version {existing.Version}. Retry with expectedVersion={existing.Version} to move it.",
                existing.Version);
        }

        var moved = new KnowledgeDocument
        {
            Id = existing.Id,
            DirectoryId = target.Id,
            Name = existing.Name,
            Summary = existing.Summary,
            Tags = existing.Tags,
            Content = existing.Content,
            Version = existing.Version + 1,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            CreatedBy = existing.CreatedBy,
            UpdatedBy = caller.Name
        };

        switch (await documents.TryReplaceAsync(moved, existing.Version, ct))
        {
            case ReplaceOutcome.NameConflict:
                throw KnowledgeException.Conflict(
                    $"a document named '{moved.Name}' already exists in directory '{target.Name}'.");
            case ReplaceOutcome.VersionMismatch:
                var latest = await documents.FindAsync(id, ct);
                throw KnowledgeException.Conflict(
                    $"document '{id}' changed while moving. Retry with expectedVersion={latest?.Version}.",
                    latest?.Version);
        }

        await RecordRevisionAsync(moved, ct);
        return new StoreResult(moved.Id, moved.DirectoryId, moved.Name, moved.Version, Created: false);
    }

    private async Task<StoreResult> CreateAsync(
        ApiCaller caller,
        StoreRequest request,
        string summary,
        List<string> tags,
        string content,
        CancellationToken ct)
    {
        if (request.DirectoryId is null)
        {
            throw KnowledgeException.Validation("directoryId is required when creating a document.");
        }

        var name = DocumentRules.NormalizeName(request.Name);
        var directory = await directories.FindAsync(request.DirectoryId.Value, ct)
            ?? throw KnowledgeException.NotFound($"directory '{request.DirectoryId}' was not found.");

        if (request.ExpectedVersion is not null)
        {
            throw KnowledgeException.NotFound(
                $"document '{name}' does not exist in directory '{directory.Name}'. Omit expectedVersion to create it.");
        }

        if (!caller.CanWrite(directory.Id))
        {
            throw KnowledgeException.Forbidden($"this key cannot write to directory '{directory.Name}'.");
        }

        var now = DateTime.UtcNow;
        var document = new KnowledgeDocument
        {
            Id = Guid.NewGuid(),
            DirectoryId = directory.Id,
            Name = name,
            Summary = summary,
            Tags = tags,
            Content = content,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = caller.Name,
            UpdatedBy = caller.Name
        };

        if (!await documents.TryInsertAsync(document, ct))
        {
            throw KnowledgeException.Conflict(
                $"a document named '{name}' already exists in directory '{directory.Name}'.");
        }

        await RecordRevisionAsync(document, ct);
        return new StoreResult(document.Id, document.DirectoryId, document.Name, document.Version, Created: true);
    }

    private async Task<StoreResult> UpdateAsync(
        ApiCaller caller,
        StoreRequest request,
        string summary,
        List<string> tags,
        string content,
        CancellationToken ct)
    {
        var id = request.Id!.Value;
        var existing = await documents.FindAsync(id, ct);
        if (existing is null || !caller.CanRead(existing.DirectoryId))
        {
            throw KnowledgeException.NotFound($"document '{id}' was not found.");
        }

        var directory = await directories.FindAsync(existing.DirectoryId, ct)
            ?? throw KnowledgeException.NotFound($"directory '{existing.DirectoryId}' was not found.");

        if (!caller.CanWrite(existing.DirectoryId))
        {
            throw KnowledgeException.Forbidden($"this key cannot write to directory '{directory.Name}'.");
        }

        if (request.DirectoryId is not null && request.DirectoryId != existing.DirectoryId)
        {
            throw KnowledgeException.Validation(
                $"document '{id}' lives in directory '{directory.Name}'. It cannot be moved to another directory.");
        }

        if (request.ExpectedVersion is null || request.ExpectedVersion != existing.Version)
        {
            throw KnowledgeException.Conflict(
                $"document '{id}' is at version {existing.Version}. Retry with expectedVersion={existing.Version} to update it.",
                existing.Version);
        }

        var name = DocumentRules.NormalizeName(request.Name ?? existing.Name);
        var now = DateTime.UtcNow;
        var updated = new KnowledgeDocument
        {
            Id = id,
            DirectoryId = existing.DirectoryId,
            Name = name,
            Summary = summary,
            Tags = tags,
            Content = content,
            Version = existing.Version + 1,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = now,
            CreatedBy = existing.CreatedBy,
            UpdatedBy = caller.Name
        };

        switch (await documents.TryReplaceAsync(updated, existing.Version, ct))
        {
            case ReplaceOutcome.NameConflict:
                throw KnowledgeException.Conflict(
                    $"a document named '{name}' already exists in directory '{directory.Name}'.");
            case ReplaceOutcome.VersionMismatch:
                var latest = await documents.FindAsync(id, ct);
                throw KnowledgeException.Conflict(
                    $"document '{id}' changed while updating. Retry with expectedVersion={latest?.Version}.",
                    latest?.Version);
        }

        await RecordRevisionAsync(updated, ct);
        return new StoreResult(updated.Id, updated.DirectoryId, updated.Name, updated.Version, Created: false);
    }

    private async Task RecordRevisionAsync(KnowledgeDocument document, CancellationToken ct) =>
        await documents.AddRevisionAsync(new KnowledgeDocumentRevision
        {
            DocumentId = document.Id,
            DirectoryId = document.DirectoryId,
            Version = document.Version,
            Name = document.Name,
            Summary = document.Summary,
            Tags = document.Tags,
            Content = document.Content,
            ChangedAt = document.UpdatedAt,
            ChangedBy = document.UpdatedBy
        }, ct);

    private async Task<Dictionary<Guid, string>> LoadDirectoryNamesAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct)
    {
        var found = await directories.ListByIdsAsync(ids, ct);
        return found.ToDictionary(d => d.Id, d => d.Name);
    }

    private static string DirectoryName(Dictionary<Guid, string> names, Guid directoryId) =>
        names.TryGetValue(directoryId, out var name) ? name : "";
}
