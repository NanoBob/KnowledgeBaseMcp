using System.ComponentModel;
using AgentKnowledgeBase.Mcp.Auth;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgentKnowledgeBase.Mcp.Mcp;

[McpServerToolType]
public sealed class KnowledgeTools
{
    [McpServerTool(Name = "list_knowledge_directories", ReadOnly = true)]
    [Description("List the document directories you can read, with your access level ('read' or 'readwrite') for each. Documents live directly in a directory; directories never nest.")]
    public static Task<IReadOnlyList<DirectoryAccessSummary>> ListKnowledgeDirectories(
        DirectoryService directories,
        CallerContext callerContext,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => directories.ListAsync(callerContext.Required, cancellationToken));

    [McpServerTool(Name = "list_knowledge_documents", ReadOnly = true)]
    [Description(
        "List knowledge documents you can read, returning id, directoryId, directoryName, name, summary, tags and version. " +
        "Use directoryId to narrow to one directory, and limit/offset to page through a large catalogue: repeat with " +
        "offset increased by limit until fewer than limit rows come back. Omit both to get every document.")]
    public static Task<IReadOnlyList<DocumentSummary>> ListKnowledgeDocuments(
        KnowledgeService knowledge,
        CallerContext callerContext,
        [Description("Optional directory id, as returned by list_knowledge_directories.")] Guid? directoryId = null,
        [Description("Optional page size. Omit for every document.")] int? limit = null,
        [Description("Optional number of documents to skip. Omit to start at the first document.")] int? offset = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => knowledge.ListAsync(callerContext.Required, directoryId, limit, offset, cancellationToken));

    [McpServerTool(Name = "search_knowledge", ReadOnly = true)]
    [Description(
        "Search the knowledge documents you can read for a literal, case-insensitive substring of a document's name, " +
        "summary, tags or content. Identifiers such as 'ConfigureAwait' work. Returns each matching document with its " +
        "matchCount and up to snippetLimit matching content lines, so a broad query stays small. Results are capped: " +
        "when truncated is true, more documents or snippets exist than were returned, so narrow the query or raise the " +
        "limits rather than assuming you saw everything.")]
    public static Task<SearchResult> SearchKnowledge(
        KnowledgeService knowledge,
        CallerContext callerContext,
        [Description("Text to find, matched case-insensitively as a literal substring.")] string query,
        [Description("Optional directory id, as returned by list_knowledge_directories.")] Guid? directoryId = null,
        [Description("Maximum documents to return. Defaults to 10, at most 50.")] int? limit = null,
        [Description("Maximum snippets per document. Defaults to 3, at most 5.")] int? snippetLimit = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => knowledge.SearchAsync(
            callerContext.Required, query, directoryId, limit, snippetLimit, cancellationToken));

    [McpServerTool(Name = "get_knowledge_document", ReadOnly = true)]
    [Description("Get the full content and metadata of one knowledge document by id, including its current version to use as expectedVersion when updating.")]
    public static Task<DocumentDetail> GetKnowledgeDocument(
        KnowledgeService knowledge,
        CallerContext callerContext,
        [Description("The document id, as returned by list_knowledge_documents.")] Guid id,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => knowledge.GetAsync(callerContext.Required, id, cancellationToken));

    [McpServerTool(Name = "store_knowledge_document")]
    [Description(
        "Create or update a knowledge document. To create, pass directoryId, name and content; the id is generated. " +
        "To update, pass the id and the version you last read as expectedVersion. If the document changed since you read it, " +
        "the call fails with a conflict; fetch it again with get_knowledge_document, re-apply your changes, and retry. " +
        "A document cannot be moved between directories. Content is Markdown, up to 100 KB.")]
    public static Task<StoreDocumentResponse> StoreKnowledgeDocument(
        KnowledgeService knowledge,
        CallerContext callerContext,
        [Description("Markdown body of the document.")] string content,
        [Description("Directory id the document belongs to, as returned by list_knowledge_directories. Required when creating.")] Guid? directoryId = null,
        [Description("Document name, unique within its directory (case-insensitive), up to 200 characters. Required when creating.")] string? name = null,
        [Description("One-sentence summary, up to 500 characters, shown in listings.")] string? summary = null,
        [Description("Optional lowercase tags, up to 20.")] string[]? tags = null,
        [Description("Document id when updating an existing document.")] Guid? id = null,
        [Description("Required when updating an existing document: the version you last read.")] int? expectedVersion = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var result = await knowledge.StoreAsync(
                callerContext.Required,
                new StoreRequest(id, directoryId, name, summary, tags, content, expectedVersion),
                cancellationToken);
            return new StoreDocumentResponse(
                result.Created ? "created" : "updated",
                result.Id,
                result.DirectoryId,
                result.Name,
                result.Version);
        });

    private static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (KnowledgeException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}

public sealed record StoreDocumentResponse(string Status, Guid Id, Guid DirectoryId, string Name, int Version);
