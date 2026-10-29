using AgentKnowledgeBase.Mcp.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentKnowledgeBase.Mcp.Admin.Controllers;

/// <summary>Directories group documents; every document belongs to exactly one.</summary>
[ApiController]
[AdminToken]
[Route("admin/directories")]
[ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status400BadRequest, AdminMediaTypes.Json)]
[ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status401Unauthorized, AdminMediaTypes.Json)]
[ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status500InternalServerError, AdminMediaTypes.Json)]
public sealed class DirectoriesController(DirectoryService directories) : ControllerBase
{
    /// <summary>Creates a directory.</summary>
    /// <response code="201">The directory was created.</response>
    [HttpPost]
    [ProducesResponseType(typeof(DirectorySummary), StatusCodes.Status201Created, AdminMediaTypes.Json)]
    public async Task<IActionResult> Create([FromBody] CreateDirectoryRequest request, CancellationToken ct)
    {
        var directory = await directories.CreateAsync(request.Name, request.Description, "admin", ct);
        return Created($"/admin/directories/{directory.Id}", directory);
    }

    /// <summary>Lists every directory.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<DirectorySummary>), StatusCodes.Status200OK, AdminMediaTypes.Json)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await directories.ListAllAsync(ct));

    /// <summary>Gets one directory.</summary>
    /// <response code="404">No directory has this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DirectorySummary), StatusCodes.Status200OK, AdminMediaTypes.Json)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound, AdminMediaTypes.Json)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(await directories.GetAsync(id, ct));

    /// <summary>Renames a directory or changes its description.</summary>
    /// <response code="404">No directory has this id.</response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(DirectorySummary), StatusCodes.Status200OK, AdminMediaTypes.Json)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound, AdminMediaTypes.Json)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateDirectoryRequest request, CancellationToken ct) =>
        Ok(await directories.UpdateAsync(id, request.Name, request.Description, ct));

    /// <summary>Deletes a directory together with the documents it holds.</summary>
    /// <response code="404">No directory has this id.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound, AdminMediaTypes.Json)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await directories.DeleteAsync(id, ct);
        return NoContent();
    }
}
