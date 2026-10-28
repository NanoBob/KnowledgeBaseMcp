using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Storage;

namespace AgentKnowledgeBase.Mcp.Services;

public sealed record DirectorySummary(Guid Id, string Name, string Description, DateTime CreatedAt)
{
    public static DirectorySummary From(KnowledgeDocumentDirectory directory) =>
        new(directory.Id, directory.Name, directory.Description, directory.CreatedAt);
}

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

    /// <summary>Directories the caller has read access to.</summary>
    public async Task<IReadOnlyList<DirectorySummary>> ListAsync(ApiCaller caller, CancellationToken ct)
    {
        var all = await directories.ListAsync(ct);
        return all
            .Where(d => caller.CanRead(d.Id))
            .Select(DirectorySummary.From)
            .ToList();
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
