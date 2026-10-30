using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentKnowledgeBase.Mcp.Services;
using GenHTTP.Modules.Git;

namespace AgentKnowledgeBase.Mcp.Git;

/// <summary>
/// Turns a snapshot into a git tree. Everything here is deterministic: the same knowledge base produces the
/// same object ids on any machine, which is what lets a submodule pin a commit and stay quiet.
/// </summary>
public static class KnowledgeTreeBuilder
{
    public const string ReadmeFileName = "README.md";

    /// <summary>
    /// The archive manifest is reused so a checkout can be imported as an archive without conversion.
    /// The newline is pinned because the writer otherwise follows the platform and changes every id on Windows.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n"
    };

    public static GitTree Build(KnowledgeSnapshot snapshot) => Build(Files(snapshot));

    public static GitTree Build(IReadOnlyList<KnowledgeFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var builder = GitTree.Create();
        foreach (var file in files)
        {
            builder.Add(file.Path, file.Content);
        }

        return builder.Build();
    }

    /// <summary>
    /// The files of the snapshot with the exact content git will store. The list is also what a stored commit
    /// keeps, so a tree can be rebuilt for a client that asks for an older commit.
    /// </summary>
    public static IReadOnlyList<KnowledgeFile> Files(KnowledgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var files = new List<KnowledgeFile>
        {
            new(ReadmeFileName, BuildReadme(snapshot)),
            new(DocumentTransferService.ManifestEntryName, BuildManifest(snapshot))
        };

        foreach (var document in snapshot.Documents)
        {
            files.Add(new KnowledgeFile(document.Path, Normalize(document.Content)));
        }

        return files;
    }

    public static string BuildManifest(KnowledgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var manifest = new TransferManifest(
            DocumentTransferService.FormatVersion,
            snapshot.ChangedAt.UtcDateTime,
            [.. snapshot.Directories],
            [.. snapshot.Documents.Select(d => new TransferDocument(
                d.DirectoryName,
                d.Name,
                d.Summary,
                [.. d.Tags],
                d.Path))]);

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    /// <summary>
    /// A short index so a checkout is readable without the MCP server. It is generated from the same snapshot as
    /// the documents, so a stale entry cannot survive a commit.
    /// </summary>
    public static string BuildReadme(KnowledgeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var text = new StringBuilder();
        text.Append("# Knowledge base\n\n");
        text.Append("A read-only snapshot of the knowledge base, served as a git repository.\n");
        text.Append("It is generated, so changes made here are not accepted and would be lost.\n\n");
        text.Append(CultureInfo.InvariantCulture, $"Snapshot: {snapshot.DocumentCount} ");
        text.Append(snapshot.DocumentCount == 1 ? "document" : "documents");
        text.Append(CultureInfo.InvariantCulture, $" in {snapshot.DirectoryCount} ");
        text.Append(snapshot.DirectoryCount == 1 ? "directory" : "directories");
        text.Append(" as of ");
        text.Append(snapshot.ChangedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        text.Append(".\n");

        foreach (var directory in snapshot.Directories)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n## {directory.Name}\n\n");
            if (!string.IsNullOrWhiteSpace(directory.Description))
            {
                text.Append(directory.Description.Trim()).Append("\n\n");
            }

            var entries = snapshot.Documents
                .Where(d => string.Equals(d.DirectoryName, directory.Name, StringComparison.Ordinal))
                .ToList();

            if (entries.Count == 0)
            {
                text.Append("_No documents._\n");
                continue;
            }

            foreach (var document in entries)
            {
                text.Append(CultureInfo.InvariantCulture, $"- [{document.Name}]({document.Path})");
                if (!string.IsNullOrWhiteSpace(document.Summary))
                {
                    text.Append(CultureInfo.InvariantCulture, $": {document.Summary.Trim()}");
                }

                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Git stores bytes, but a document authored on Windows would otherwise carry CRLF and change its id when the
    /// same content is read on another machine.
    /// </summary>
    private static string Normalize(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal);
}

/// <summary>One file of the repository: the path it has in the tree and the content git stores for it.</summary>
public sealed record KnowledgeFile(string Path, string Content);
