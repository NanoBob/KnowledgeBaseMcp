using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentKnowledgeBase.Mcp.Admin.Controllers;

/// <summary>Documents hold the Markdown content, and every write records a revision.</summary>
[ApiController]
[AdminToken]
[Route("admin/documents")]
[ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status400BadRequest, AdminMediaTypes.Json)]
[ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status401Unauthorized, AdminMediaTypes.Json)]
[ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status500InternalServerError, AdminMediaTypes.Json)]
public sealed class DocumentsController(KnowledgeService knowledge) : ControllerBase
{
    /// <summary>Lists documents, newest first, optionally narrowed to one directory.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<DocumentSummary>), StatusCodes.Status200OK, AdminMediaTypes.Json)]
    public async Task<IActionResult> List([FromQuery] Guid? directoryId, CancellationToken ct) =>
        Ok(await knowledge.ListAsync(ApiCaller.Admin, directoryId, ct));

    /// <summary>Gets one document, including its content.</summary>
    /// <response code="404">No document has this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DocumentDetail), StatusCodes.Status200OK, AdminMediaTypes.Json)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound, AdminMediaTypes.Json)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(await knowledge.GetAsync(ApiCaller.Admin, id, ct));

    /// <summary>Lists the revisions of one document, newest first.</summary>
    /// <response code="404">No document has this id.</response>
    [HttpGet("{id:guid}/revisions")]
    [ProducesResponseType(typeof(IReadOnlyList<RevisionSummary>), StatusCodes.Status200OK, AdminMediaTypes.Json)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound, AdminMediaTypes.Json)]
    public async Task<IActionResult> ListRevisions(Guid id, CancellationToken ct) =>
        Ok(await knowledge.ListRevisionsAsync(ApiCaller.Admin, id, ct));

    /// <summary>Creates a document.</summary>
    /// <remarks>
    /// Sending an <c>expectedVersion</c> on create means "this should already exist", which is rejected
    /// instead of silently creating a second document.
    /// </remarks>
    /// <response code="201">The document was created.</response>
    [HttpPost]
    [ProducesResponseType(typeof(StoreDocumentResponse), StatusCodes.Status201Created, AdminMediaTypes.Json)]
    public async Task<IActionResult> Create([FromBody] StoreDocumentRequest request, CancellationToken ct)
    {
        // expectedVersion must reach the service: sending one on create means "this should already
        // exist", which the service rejects instead of silently creating a second document.
        var result = await knowledge.StoreAsync(
            ApiCaller.Admin,
            ToStoreRequest(null, request, request.ExpectedVersion),
            ct);
        return Created($"/admin/documents/{result.Id}", ToResponse(result));
    }

    /// <summary>Replaces a document's content and metadata.</summary>
    /// <remarks>Omitting <c>expectedVersion</c> overwrites whatever is stored.</remarks>
    /// <response code="404">No document has this id.</response>
    /// <response code="409">The document is on another version than the one sent.</response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(StoreDocumentResponse), StatusCodes.Status200OK, AdminMediaTypes.Json)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound, AdminMediaTypes.Json)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status409Conflict, AdminMediaTypes.Json)]
    public async Task<IActionResult> Update(Guid id, [FromBody] StoreDocumentRequest request, CancellationToken ct)
    {
        // Omitting expectedVersion lets the admin client overwrite whatever is stored.
        var expectedVersion = request.ExpectedVersion
            ?? (await knowledge.GetAsync(ApiCaller.Admin, id, ct)).Version;
        var result = await knowledge.StoreAsync(ApiCaller.Admin, ToStoreRequest(id, request, expectedVersion), ct);
        return Ok(ToResponse(result));
    }

    /// <summary>Moves a document to another directory.</summary>
    /// <response code="404">No document or directory has this id.</response>
    /// <response code="409">The document is on another version than the one sent.</response>
    [HttpPost("{id:guid}/move")]
    [ProducesResponseType(typeof(StoreDocumentResponse), StatusCodes.Status200OK, AdminMediaTypes.Json)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound, AdminMediaTypes.Json)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status409Conflict, AdminMediaTypes.Json)]
    public async Task<IActionResult> Move(Guid id, [FromBody] MoveDocumentRequest request, CancellationToken ct)
    {
        if (request.DirectoryId is null || request.DirectoryId == Guid.Empty)
        {
            throw KnowledgeException.Validation("directoryId is required to move a document.");
        }

        var result = await knowledge.MoveAsync(ApiCaller.Admin, id, request.DirectoryId.Value, request.ExpectedVersion, ct);
        return Ok(ToResponse(result));
    }

    /// <summary>Deletes a document and its revisions.</summary>
    /// <response code="404">No document has this id.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound, AdminMediaTypes.Json)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await knowledge.DeleteAsync(ApiCaller.Admin, id, ct);
        return NoContent();
    }

    private static StoreRequest ToStoreRequest(Guid? id, StoreDocumentRequest request, int? expectedVersion) =>
        new(id, request.DirectoryId, request.Name, request.Summary, request.Tags, request.Content, expectedVersion);

    private static StoreDocumentResponse ToResponse(StoreResult result) =>
        new(result.Id, result.DirectoryId, result.Name, result.Version, result.Created);
}
