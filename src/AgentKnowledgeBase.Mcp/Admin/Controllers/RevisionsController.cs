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
}
