using AgentKnowledgeBase.Mcp.Services;

namespace AgentKnowledgeBase.Mcp.Git;

/// <summary>A document as it appears in a snapshot, with the path it has in the git tree.</summary>
public sealed record KnowledgeSnapshotDocument(
    string DirectoryName,
    string Name,
    string Summary,
    IReadOnlyList<string> Tags,
    string Path,
    string Content);

/// <summary>Everything a git snapshot is built from, already filtered to one caller's readable directories.</summary>
public sealed record KnowledgeSnapshot(
    IReadOnlyList<TransferDirectory> Directories,
    IReadOnlyList<KnowledgeSnapshotDocument> Documents,
    DateTimeOffset ChangedAt)
{
    public static KnowledgeSnapshot Empty { get; } = new([], [], DateTimeOffset.UnixEpoch);

    public int DirectoryCount => Directories.Count;

    public int DocumentCount => Documents.Count;
}
