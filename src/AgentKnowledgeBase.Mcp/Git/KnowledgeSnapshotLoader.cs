using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using AgentKnowledgeBase.Mcp.Storage;

namespace AgentKnowledgeBase.Mcp.Git;

/// <summary>
/// Reads the directories and documents one caller may see and lays them out as the files of a snapshot.
/// The access check is pushed into the queries, so a key scoped to one directory never reads another.
/// </summary>
public sealed class KnowledgeSnapshotLoader(IDirectoryRepository directories, IDocumentRepository documents)
{
    public async Task<KnowledgeSnapshot> LoadAsync(ApiCaller caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var readable = caller.ReadableDirectoryIds;
        if (readable is { Count: 0 })
        {
            return KnowledgeSnapshot.Empty;
        }

        var visible = readable is null
            ? await directories.ListAsync(ct)
            : await directories.ListByIdsAsync(readable, ct);

        if (visible.Count == 0)
        {
            return KnowledgeSnapshot.Empty;
        }

        var directoriesById = visible.ToDictionary(d => d.Id);
        var changedAt = visible.Aggregate(DateTimeOffset.UnixEpoch, (latest, d) => Later(latest, d.CreatedAt));

        var found = new List<KnowledgeDocument>();
        await foreach (var document in documents
                           .StreamByDirectoryIdsAsync(readable, ct)
                           .WithCancellation(ct))
        {
            // A document may outlive its directory; without a directory it has no place in the tree.
            if (directoriesById.ContainsKey(document.DirectoryId))
            {
                found.Add(document);
            }
        }

        // The order decides which document keeps a clashing file name, so it must not depend on the database.
        found.Sort((left, right) =>
        {
            var result = string.CompareOrdinal(
                directoriesById[left.DirectoryId].Name, directoriesById[right.DirectoryId].Name);

            return result != 0 ? result : CompareNames(left, right);
        });

        var used = new Dictionary<Guid, HashSet<string>>();
        var entries = new List<KnowledgeSnapshotDocument>(found.Count);

        foreach (var document in found)
        {
            if (!used.TryGetValue(document.DirectoryId, out var taken))
            {
                taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                used[document.DirectoryId] = taken;
            }

            var directoryName = directoriesById[document.DirectoryId].Name;
            entries.Add(new KnowledgeSnapshotDocument(
                directoryName,
                document.Name,
                document.Summary,
                [.. document.Tags],
                DocumentFileNames.RelativePath(directoryName, document.Name, document.Id, taken),
                document.Content));

            changedAt = Later(changedAt, document.UpdatedAt);
        }

        return new KnowledgeSnapshot(
            [.. visible
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .Select(d => new TransferDirectory(d.Name, d.Description))],
            entries,
            changedAt);
    }

    /// <summary>Orders documents the way the archive export does: by name, ignoring case, then by id.</summary>
    private static int CompareNames(KnowledgeDocument left, KnowledgeDocument right)
    {
        var result = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        return result != 0 ? result : left.Id.CompareTo(right.Id);
    }

    /// <summary>
    /// Normalizes a stored timestamp to UTC. Mongo returns UTC, but the kind is not always set, and an
    /// unspecified value would be read in the local time zone and change the commit id per machine.
    /// </summary>
    private static DateTimeOffset Later(DateTimeOffset latest, DateTime value)
    {
        var candidate = new DateTimeOffset(value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime());

        return candidate > latest ? candidate : latest;
    }
}
