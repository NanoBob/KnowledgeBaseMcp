using AgentKnowledgeBase.Mcp.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentKnowledgeBase.Mcp.Admin.Controllers;

[ApiController]
[AdminToken]
[Route("admin/directories")]
public sealed class DirectoriesController(DirectoryService directories) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDirectoryRequest request, CancellationToken ct)
    {
        var directory = await directories.CreateAsync(request.Name, request.Description, "admin", ct);
        return Created($"/admin/directories/{directory.Id}", directory);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await directories.ListAllAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(await directories.GetAsync(id, ct));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateDirectoryRequest request, CancellationToken ct) =>
        Ok(await directories.UpdateAsync(id, request.Name, request.Description, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await directories.DeleteAsync(id, ct);
        return NoContent();
    }
}
