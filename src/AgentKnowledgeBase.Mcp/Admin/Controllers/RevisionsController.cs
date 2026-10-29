using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentKnowledgeBase.Mcp.Admin.Controllers;

[ApiController]
[AdminToken]
[Route("admin/revisions")]
public sealed class RevisionsController(KnowledgeService knowledge) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(await knowledge.GetRevisionAsync(ApiCaller.Admin, id, ct));
}
