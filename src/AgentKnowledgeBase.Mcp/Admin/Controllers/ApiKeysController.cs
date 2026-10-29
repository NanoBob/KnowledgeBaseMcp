using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentKnowledgeBase.Mcp.Admin.Controllers;

[ApiController]
[AdminToken]
[Route("admin/api-keys")]
public sealed class ApiKeysController(ApiKeyService keys) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest request, CancellationToken ct)
    {
        var scopes = request.Scopes?.Select(s => new KeyScopeInput(s.DirectoryId, s.Access)).ToList();
        var (record, plainText) = await keys.CreateAsync(request.Name, scopes, ct);
        return Created($"/admin/api-keys/{record.Id}", ToCreatedResponse(record, plainText));
    }

    [HttpPost("{id:guid}/regenerate")]
    public async Task<IActionResult> Regenerate(Guid id, CancellationToken ct)
    {
        var (record, plainText) = await keys.RegenerateAsync(id, ct);
        return Ok(ToCreatedResponse(record, plainText));
    }

    [HttpPut("{id:guid}/scopes")]
    public async Task<IActionResult> UpdateScopes(
        Guid id,
        [FromBody] UpdateApiKeyScopesRequest request,
        CancellationToken ct)
    {
        var scopes = request.Scopes?.Select(s => new KeyScopeInput(s.DirectoryId, s.Access)).ToList();
        var record = await keys.UpdateScopesAsync(id, scopes, ct);
        return Ok(ToSummaryResponse(record));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var records = await keys.ListAsync(ct);
        return Ok(records.Select(ToSummaryResponse));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        await keys.RevokeAsync(id, ct);
        return NoContent();
    }

    private static KeyScopeResponse ToDto(KeyScope scope) =>
        new(scope.DirectoryId, scope.Access.ToString().ToLowerInvariant());

    private static ApiKeyCreatedResponse ToCreatedResponse(ApiKeyRecord record, string plainText) =>
        new(record.Id, record.Name, plainText, record.Scopes.Select(ToDto).ToList(), record.CreatedAt);

    private static ApiKeySummaryResponse ToSummaryResponse(ApiKeyRecord record) =>
        new(record.Id, record.Name, record.Scopes.Select(ToDto).ToList(), record.CreatedAt, record.RotatedAt, record.RevokedAt);
}
