using System.Text;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Storage;

namespace AgentKnowledgeBase.Mcp.Services;

public sealed record DocumentSummary(
    string Id,
    string Path,
    string Title,
    string Summary,
    IReadOnlyList<string> Tags,
    int Version,
    DateTime UpdatedAt)
{
    public static DocumentSummary From(KnowledgeDocument d) =>
        new(d.Id, d.Path, d.Title, d.Summary, d.Tags, d.Version, d.UpdatedAt);
}

public sealed record StoreRequest(
    string? Id,
    string? Path,
    string? Title,
    string? Summary,
    IEnumerable<string>? Tags,
    string? Content,
    int? ExpectedVersion);

public sealed record StoreResult(KnowledgeDocument Document, bool Created);

public sealed class KnowledgeService(IDocumentRepository documents)
{
    public const int MaxTitleLength = 200;
    public const int MaxSummaryLength = 500;
    public const int MaxContentBytes = 100 * 1024;

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(ApiCaller caller, string? pathPrefix, CancellationToken ct)
    {
        var prefix = PathRules.NormalizePrefix(pathPrefix);
        var all = await documents.ListSummariesAsync(ct);
        return all
            .Where(d => PathRules.IsWithin(prefix, d.Path) && caller.CanRead(d.Path))
            .Select(DocumentSummary.From)
            .ToList();
    }

    public async Task<KnowledgeDocument> GetAsync(ApiCaller caller, string id, CancellationToken ct)
    {
        var document = await documents.FindAsync(id, ct);
        // Hidden documents look identical to missing ones so callers cannot probe for them.
        if (document is null || !caller.CanRead(document.Path))
        {
            throw KnowledgeException.NotFound($"document '{id}' was not found.");
        }

        return document;
    }

    public async Task<StoreResult> StoreAsync(ApiCaller caller, StoreRequest request, CancellationToken ct)
    {
        var title = ValidateTitle(request.Title);
        var summary = ValidateSummary(request.Summary);
        var content = ValidateContent(request.Content);
        var path = PathRules.NormalizeDocumentPath(request.Path);
        var tags = TagRules.Normalize(request.Tags);
        var id = ResolveId(request.Id, title);

        if (request.ExpectedVersion is < 1)
        {
            throw KnowledgeException.Validation("expectedVersion must be a positive version number.");
        }

        var existing = await documents.FindAsync(id, ct);
        var now = DateTime.UtcNow;

        if (existing is null)
        {
            if (request.ExpectedVersion is not null)
            {
                throw KnowledgeException.NotFound(
                    $"document '{id}' does not exist. Omit expectedVersion to create it.");
            }

            if (!caller.CanWrite(path))
            {
                throw KnowledgeException.Forbidden($"this key cannot write to '{path}'.");
            }

            var created = new KnowledgeDocument
            {
                Id = id,
                Path = path,
                Title = title,
                Summary = summary,
                Tags = tags,
                Content = content,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = caller.Name,
                UpdatedBy = caller.Name
            };

            if (!await documents.TryInsertAsync(created, ct))
            {
                var latest = await documents.FindAsync(id, ct);
                throw KnowledgeException.Conflict(
                    $"document '{id}' was created concurrently. Retry with expectedVersion={latest?.Version}.",
                    latest?.Version);
            }

            await RecordRevisionAsync(created, ct);
            return new StoreResult(created, Created: true);
        }

        if (!caller.CanRead(existing.Path))
        {
            throw KnowledgeException.NotFound($"document '{id}' was not found.");
        }

        if (!caller.CanWrite(existing.Path))
        {
            throw KnowledgeException.Forbidden($"this key cannot write to '{existing.Path}'.");
        }

        if (path != existing.Path)
        {
            throw KnowledgeException.Validation(
                $"document '{id}' lives at '{existing.Path}'. Its path cannot be changed; store a new id instead.");
        }

        if (request.ExpectedVersion is null || request.ExpectedVersion != existing.Version)
        {
            throw KnowledgeException.Conflict(
                $"document '{id}' is at version {existing.Version}. Retry with expectedVersion={existing.Version} to update it.",
                existing.Version);
        }

        var updated = new KnowledgeDocument
        {
            Id = id,
            Path = path,
            Title = title,
            Summary = summary,
            Tags = tags,
            Content = content,
            Version = existing.Version + 1,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = now,
            CreatedBy = existing.CreatedBy,
            UpdatedBy = caller.Name
        };

        if (!await documents.TryReplaceAsync(updated, existing.Version, ct))
        {
            var latest = await documents.FindAsync(id, ct);
            throw KnowledgeException.Conflict(
                $"document '{id}' changed while updating. Retry with expectedVersion={latest?.Version}.",
                latest?.Version);
        }

        await RecordRevisionAsync(updated, ct);
        return new StoreResult(updated, Created: false);
    }

    private async Task RecordRevisionAsync(KnowledgeDocument document, CancellationToken ct) =>
        await documents.AddRevisionAsync(new DocumentRevision
        {
            DocumentId = document.Id,
            Version = document.Version,
            Path = document.Path,
            Title = document.Title,
            Summary = document.Summary,
            Tags = document.Tags,
            Content = document.Content,
            ChangedAt = document.UpdatedAt,
            ChangedBy = document.UpdatedBy
        }, ct);

    private static string ResolveId(string? requested, string title)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return IdRules.Validate(requested.Trim());
        }

        var slug = IdRules.Slugify(title);
        if (slug.Length == 0)
        {
            throw KnowledgeException.Validation("id is required when the title contains no letters or digits.");
        }

        return slug;
    }

    private static string ValidateTitle(string? title)
    {
        var trimmed = title?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > MaxTitleLength)
        {
            throw KnowledgeException.Validation($"title is required and must be at most {MaxTitleLength} characters.");
        }

        return trimmed;
    }

    private static string ValidateSummary(string? summary)
    {
        var trimmed = summary?.Trim() ?? "";
        if (trimmed.Length > MaxSummaryLength)
        {
            throw KnowledgeException.Validation($"summary must be at most {MaxSummaryLength} characters.");
        }

        return trimmed;
    }

    private static string ValidateContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw KnowledgeException.Validation("content is required.");
        }

        if (Encoding.UTF8.GetByteCount(content) > MaxContentBytes)
        {
            throw KnowledgeException.Validation($"content must be at most {MaxContentBytes / 1024} KB.");
        }

        return content;
    }
}
