using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Storage;

namespace AgentKnowledgeBase.Mcp.Services;

public sealed record DirectorySummary(Guid Id, string Name, string Description, DateTime CreatedAt)
{
    public static DirectorySummary From(KnowledgeDocumentDirectory directory) =>
        new(directory.Id, directory.Name, directory.Description, directory.CreatedAt);
}

public sealed class DirectoryService(IDirectoryRepository directories)
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
}
