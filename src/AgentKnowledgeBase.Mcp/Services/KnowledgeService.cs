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

/// <summary>One matching content line, with its 1-based line number.</summary>
public sealed record SearchSnippet(int Line, string Text);

public sealed record SearchMatch(
    Guid Id,
    string Name,
    string DirectoryName,
    IReadOnlyList<string> Tags,
    int Version,
    int MatchCount,
    IReadOnlyList<SearchSnippet> Snippets);

public sealed record SearchResult(IReadOnlyList<SearchMatch> Matches, bool Truncated);

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
    private const int DefaultSearchLimit = 10;
    private const int MaxSearchLimit = 50;
    private const int DefaultSnippetLimit = 3;
    private const int MaxSnippetLimit = 5;
    private const int SnippetWidth = 200;

    /// <summary>
    /// Keeps a broad search's snippets small enough that an agent can afford to iterate: with the default ten
    /// documents the whole result stays under about 4 KB, instead of one context window.
    /// </summary>
    private const int SnippetCharacterBudget = 1500;

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(
        ApiCaller caller,
        Guid? directoryId,
        int? limit,
        int? offset,
        CancellationToken ct)
    {
        var readable = ReadableScope(caller, directoryId);
        if (readable is { Count: 0 })
        {
            return [];
        }

        var take = limit ?? int.MaxValue;
        if (take <= 0)
        {
            return [];
        }

        var documentsPage = await documents.ListSummariesAsync(readable, ct);
        var page = documentsPage.Skip(Math.Max(0, offset ?? 0)).Take(take).ToList();
        var directoryNames = await LoadDirectoryNamesAsync(
            page.Select(d => d.DirectoryId).Distinct().ToList(),
            ct);

        return page
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

    /// <summary>
    /// Literal, case-insensitive substring search across each readable document's name, summary, tags and
    /// content. Snippets are content lines only: a document that matches in its metadata alone still
    /// appears, with no snippets, because its name and summary are already in the result.
    /// </summary>
    public async Task<SearchResult> SearchAsync(
        ApiCaller caller,
        string query,
        Guid? directoryId,
        int? limit,
        int? snippetLimit,
        CancellationToken ct)
    {
        var term = (query ?? "").Trim();
        if (term.Length == 0)
        {
            throw KnowledgeException.Validation("query is required: pass the text to search for.");
        }

        var readable = ReadableScope(caller, directoryId);
        if (readable is { Count: 0 })
        {
            return new SearchResult([], Truncated: false);
        }

        var ranked = (await documents.SearchAsync(readable, term, ct))
            .Select(d => (Document: d, MatchCount: CountMatches(d, term)))
            .Where(m => m.MatchCount > 0)
            .OrderByDescending(m => m.MatchCount)
            .ThenBy(m => m.Document.Name, StringComparer.Ordinal)
            .ThenBy(m => m.Document.Id)
            .ToList();

        var take = Math.Clamp(limit ?? DefaultSearchLimit, 1, MaxSearchLimit);
        var snippetsPerDocument = Math.Clamp(snippetLimit ?? DefaultSnippetLimit, 1, MaxSnippetLimit);
        var truncated = ranked.Count > take;
        var page = ranked.Take(take).ToList();

        var directoryNames = await LoadDirectoryNamesAsync(
            page.Select(m => m.Document.DirectoryId).Distinct().ToList(),
            ct);

        var remaining = SnippetCharacterBudget;
        var budgetSpent = false;

        List<SearchSnippet> SnippetsFor(string content)
        {
            var snippets = new List<SearchSnippet>();
            if (budgetSpent)
            {
                return snippets;
            }

            foreach (var (number, text) in ContentLines(content))
            {
                var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }

                if (snippets.Count == snippetsPerDocument)
                {
                    truncated = true;
                    break;
                }

                var snippet = SnippetText(text, index);
                if (snippet.Length > remaining)
                {
                    budgetSpent = true;
                    truncated = true;
                    break;
                }

                remaining -= snippet.Length;
                snippets.Add(new SearchSnippet(number, snippet));
            }

            return snippets;
        }

        var matches = page
            .Select(m => new SearchMatch(
                m.Document.Id,
                m.Document.Name,
                DirectoryName(directoryNames, m.Document.DirectoryId),
                m.Document.Tags,
                m.Document.Version,
                m.MatchCount,
                SnippetsFor(m.Document.Content)))
            .ToList();

        return new SearchResult(matches, truncated);
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

        if (request.ExpectedVersion is null)
        {
            throw KnowledgeException.Conflict(
                $"updating document '{id}' requires the version you last read; it is now at version {existing.Version}. " +
                RefetchAdvice(existing.Version),
                existing.Version);
        }

        if (request.ExpectedVersion != existing.Version)
        {
            throw KnowledgeException.Conflict(
                $"document '{id}' has been updated since you read it: expected version {request.ExpectedVersion}, " +
                $"current version {existing.Version}. {RefetchAdvice(existing.Version)}",
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
                if (latest is null)
                {
                    throw KnowledgeException.Conflict(
                        $"document '{id}' was deleted while you were saving. Nothing was written.");
                }

                throw KnowledgeException.Conflict(
                    $"document '{id}' has been updated by someone else while you were saving; it is now at version {latest.Version}. " +
                    RefetchAdvice(latest.Version),
                    latest.Version);
        }

        await RecordRevisionAsync(updated, ct);
        return new StoreResult(updated.Id, updated.DirectoryId, updated.Name, updated.Version, Created: false);
    }

    /// <summary>
    /// A retry that only swaps in the new version would write the caller's stale copy over the change it
    /// just lost to, so the message names the refetch step explicitly.
    /// </summary>
    private static string RefetchAdvice(int currentVersion) =>
        $"Fetch it again with get_knowledge_document, re-apply your changes, and retry with expectedVersion={currentVersion}.";

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

    /// <summary>
    /// The directories the caller may read, narrowed to <paramref name="directoryId"/> when one is
    /// requested. Null means every directory; an empty collection means nothing is readable. A single
    /// requested directory and the caller's readable set are intersected here, so the repository never
    /// receives two filters that could contradict each other.
    /// </summary>
    private static IReadOnlyCollection<Guid>? ReadableScope(ApiCaller caller, Guid? directoryId)
    {
        var readable = caller.ReadableDirectoryIds;
        if (readable is { Count: 0 } || directoryId is not { } only)
        {
            return readable;
        }

        if (readable is not null && !readable.Contains(only))
        {
            return [];
        }

        return [only];
    }

    private static int CountMatches(KnowledgeDocument document, string term) =>
        CountOccurrences(document.Name, term)
        + CountOccurrences(document.Summary, term)
        + document.Tags.Sum(occurrences => CountOccurrences(occurrences, term))
        + CountOccurrences(document.Content, term);

    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var index = 0;
        while (term.Length > 0 && (index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += term.Length;
        }

        return count;
    }

    /// <summary>Splits on every line ending, so CRLF and LF content report the same line numbers.</summary>
    private static IEnumerable<(int Number, string Text)> ContentLines(string content)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            yield return (index + 1, lines[index]);
        }
    }

    /// <summary>
    /// Keeps a long line to <see cref="SnippetWidth"/> characters around the match, so the snippet shows
    /// why the document matched instead of the first 200 characters of the line.
    /// </summary>
    private static string SnippetText(string line, int matchIndex)
    {
        if (line.Length <= SnippetWidth)
        {
            return line;
        }

        var window = SnippetWidth - 2;
        var start = Math.Clamp(matchIndex - (window / 3), 0, line.Length - window);
        return (start > 0 ? "…" : "")
            + line.Substring(start, window)
            + (start + window < line.Length ? "…" : "");
    }
}
