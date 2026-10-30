using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentKnowledgeBase.Mcp.Admin.Controllers;

/// <summary>Revisions are the immutable history of a document.</summary>
[ApiController]
[AdminToken]
[Route("admin/revisions")]
[ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status400BadRequest, AdminMediaTypes.Json)]
[ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status401Unauthorized, AdminMediaTypes.Json)]
[ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status500InternalServerError, AdminMediaTypes.Json)]
public sealed class RevisionsController(KnowledgeService knowledge) : ControllerBase
{
    /// <summary>Gets one revision, including the content it held.</summary>
    /// <response code="404">No revision has this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RevisionDetail), StatusCodes.Status200OK, AdminMediaTypes.Json)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound, AdminMediaTypes.Json)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(await knowledge.GetRevisionAsync(ApiCaller.Admin, id, ct));

    /// <summary>Restores a revision's content as a new revision.</summary>
    /// <remarks>
    /// The new revision keeps the document's current name, summary, tags and directory, and only replaces the
    /// content. Omitting <c>expectedVersion</c> restores over whatever is stored.
    /// </remarks>
    /// <response code="404">No revision has this id, or its document was deleted.</response>
    /// <response code="409">The document is on another version than the one sent.</response>
    [HttpPost("{id:guid}/restore")]
    [ProducesResponseType(typeof(StoreDocumentResponse), StatusCodes.Status200OK, AdminMediaTypes.Json)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound, AdminMediaTypes.Json)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status409Conflict, AdminMediaTypes.Json)]
    public async Task<IActionResult> Restore(Guid id, [FromBody] RestoreRevisionRequest request, CancellationToken ct)
    {
        var result = await knowledge.RestoreAsync(ApiCaller.Admin, id, request.ExpectedVersion, ct);
        return Ok(new StoreDocumentResponse(result.Id, result.DirectoryId, result.Name, result.Version, result.Created));
    }
}
