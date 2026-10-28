using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Storage;

namespace AgentKnowledgeBase.Mcp.Services;

public sealed record DirectorySummary(Guid Id, string Name, string Description, DateTime CreatedAt)
{
    public static DirectorySummary From(KnowledgeDocumentDirectory directory) =>
        new(directory.Id, directory.Name, directory.Description, directory.CreatedAt);
}

/// <summary>A directory as seen by a caller, including the access level that caller holds.</summary>
public sealed record DirectoryAccessSummary(
    Guid Id,
    string Name,
    string Description,
    DateTime CreatedAt,
    string Access);

public sealed class DirectoryService(IDirectoryRepository directories, IDocumentRepository documents)
{
    public async Task<DirectorySummary> CreateAsync(
        string? name,
        string? description,
        string createdBy,
        CancellationToken ct)
    {
        var normalizedName = DirectoryRules.NormalizeName(name);
        var normalizedDescription = DirectoryRules.NormalizeDescription(description);

        var directory = new KnowledgeDocumentDirectory
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            Description = normalizedDescription,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = createdBy
        };

        if (!await directories.TryInsertAsync(directory, ct))
        {
            throw KnowledgeException.Conflict($"a directory named '{normalizedName}' already exists.");
        }

        return DirectorySummary.From(directory);
    }

    public async Task<DirectorySummary> GetAsync(Guid id, CancellationToken ct) =>
        DirectorySummary.From(await FindAsync(id, ct));

    public async Task<DirectorySummary> UpdateAsync(Guid id, string? name, string? description, CancellationToken ct)
    {
        var existing = await FindAsync(id, ct);
        var updated = new KnowledgeDocumentDirectory
        {
            Id = existing.Id,
            Name = DirectoryRules.NormalizeName(name),
            Description = DirectoryRules.NormalizeDescription(description),
            CreatedAt = existing.CreatedAt,
            CreatedBy = existing.CreatedBy
        };

        if (!await directories.TryUpdateAsync(updated, ct))
        {
            throw KnowledgeException.Conflict($"a directory named '{updated.Name}' already exists.");
        }

        return DirectorySummary.From(updated);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await FindAsync(id, ct);

        var documentCount = await documents.CountByDirectoryAsync(id, ct);
        if (documentCount > 0)
        {
            throw KnowledgeException.Conflict(
                $"directory '{id}' still holds {documentCount} document(s). Delete or move them first.");
        }

        if (!await directories.DeleteAsync(id, ct))
        {
            throw KnowledgeException.NotFound($"directory '{id}' was not found.");
        }
    }

    /// <summary>Directories the caller can read, with the access level the caller holds on each.</summary>
    public async Task<IReadOnlyList<DirectoryAccessSummary>> ListAsync(ApiCaller caller, CancellationToken ct)
    {
        var readableIds = caller.ReadableDirectoryIds;
        if (readableIds is { Count: 0 })
        {
            return [];
        }

        var readable = readableIds is { } ids
            ? await directories.ListByIdsAsync(ids, ct)
            : await directories.ListAsync(ct);
        var visible = new List<DirectoryAccessSummary>(readable.Count);

        foreach (var directory in readable)
        {
            // The query already narrowed to the readable ids; AccessFor still resolves the level per row.
            if (caller.AccessFor(directory.Id) is not { } access)
            {
                continue;
            }

            visible.Add(new DirectoryAccessSummary(
                directory.Id,
                directory.Name,
                directory.Description,
                directory.CreatedAt,
                access.ToString().ToLowerInvariant()));
        }

        return visible;
    }

    public async Task<IReadOnlyList<DirectorySummary>> ListAllAsync(CancellationToken ct)
    {
        var all = await directories.ListAsync(ct);
        return all.Select(DirectorySummary.From).ToList();
    }

    private async Task<KnowledgeDocumentDirectory> FindAsync(Guid id, CancellationToken ct) =>
        await directories.FindAsync(id, ct)
            ?? throw KnowledgeException.NotFound($"directory '{id}' was not found.");
}
