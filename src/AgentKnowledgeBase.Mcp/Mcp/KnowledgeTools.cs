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
    [McpServerTool(Name = "list_knowledge_documents", ReadOnly = true)]
    [Description("List knowledge documents you have access to, returning id, path, title, summary, tags and version. Use the optional pathPrefix to narrow to a directory.")]
    public static Task<IReadOnlyList<DocumentSummary>> ListKnowledgeDocuments(
        KnowledgeService knowledge,
        CallerContext callerContext,
        [Description("Optional directory such as 'dotnet/efcore'. Only documents at or below it are returned.")] string? pathPrefix = null,
        CancellationToken cancellationToken = default) =>
        Run(() => knowledge.ListAsync(callerContext.Required, pathPrefix, cancellationToken));

    [McpServerTool(Name = "get_knowledge_document", ReadOnly = true)]
    [Description("Get the full content and metadata of one knowledge document by id, including its current version to use as expectedVersion when updating.")]
    public static Task<KnowledgeDocument> GetKnowledgeDocument(
        KnowledgeService knowledge,
        CallerContext callerContext,
        [Description("The document id, as returned by list_knowledge_documents.")] string id,
        CancellationToken cancellationToken = default) =>
        Run(() => knowledge.GetAsync(callerContext.Required, id, cancellationToken));

    [McpServerTool(Name = "store_knowledge_document")]
    [Description(
        "Create or update a knowledge document. Omit id and expectedVersion to create a new document (id defaults to a slug of the title). " +
        "To update, pass the id and the version you last read as expectedVersion; a stale version returns a conflict with the latest version. " +
        "Path cannot change on update. Content is Markdown, up to 100 KB.")]
    public static Task<StoreDocumentResponse> StoreKnowledgeDocument(
        KnowledgeService knowledge,
        CallerContext callerContext,
        [Description("Directory the document belongs to, e.g. 'dotnet/efcore'. Lowercase letters, digits, '.', '_' and '-' per segment.")] string path,
        [Description("Short title, up to 200 characters.")] string title,
        [Description("Markdown body of the document.")] string content,
        [Description("Optional id. Lowercase letters, digits and '-'. Defaults to a slug of the title.")] string? id = null,
        [Description("One-sentence summary, up to 500 characters, shown in listings.")] string? summary = null,
        [Description("Optional lowercase tags, up to 20.")] string[]? tags = null,
        [Description("Required when updating an existing document: the version you last read.")] int? expectedVersion = null,
        CancellationToken cancellationToken = default) =>
        Run(async () =>
        {
            var result = await knowledge.StoreAsync(
                callerContext.Required,
                new StoreRequest(id, path, title, summary, tags, content, expectedVersion),
                cancellationToken);
            return new StoreDocumentResponse(
                result.Created ? "created" : "updated",
                result.Document.Id,
                result.Document.Version,
                result.Document.Path);
        });

    private static async Task<T> Run<T>(Func<Task<T>> action)
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

public sealed record StoreDocumentResponse(string Status, string Id, int Version, string Path);
