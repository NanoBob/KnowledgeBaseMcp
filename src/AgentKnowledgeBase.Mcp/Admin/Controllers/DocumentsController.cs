using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentKnowledgeBase.Mcp.Admin.Controllers;

[ApiController]
[AdminToken]
[Route("admin/documents")]
public sealed class DocumentsController(KnowledgeService knowledge) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? directoryId, CancellationToken ct) =>
        Ok(await knowledge.ListAsync(ApiCaller.Admin, directoryId, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(await knowledge.GetAsync(ApiCaller.Admin, id, ct));

    [HttpGet("{id:guid}/revisions")]
    public async Task<IActionResult> ListRevisions(Guid id, CancellationToken ct) =>
        Ok(await knowledge.ListRevisionsAsync(ApiCaller.Admin, id, ct));

    [HttpPost]
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

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] StoreDocumentRequest request, CancellationToken ct)
    {
        // Omitting expectedVersion lets the admin client overwrite whatever is stored.
        var expectedVersion = request.ExpectedVersion
            ?? (await knowledge.GetAsync(ApiCaller.Admin, id, ct)).Version;
        var result = await knowledge.StoreAsync(ApiCaller.Admin, ToStoreRequest(id, request, expectedVersion), ct);
        return Ok(ToResponse(result));
    }

    [HttpPost("{id:guid}/move")]
    public async Task<IActionResult> Move(Guid id, [FromBody] MoveDocumentRequest request, CancellationToken ct)
    {
        if (request.DirectoryId is null || request.DirectoryId == Guid.Empty)
        {
            throw KnowledgeException.Validation("directoryId is required to move a document.");
        }

        var result = await knowledge.MoveAsync(ApiCaller.Admin, id, request.DirectoryId.Value, request.ExpectedVersion, ct);
        return Ok(ToResponse(result));
    }

    [HttpDelete("{id:guid}")]
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
