using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using Microsoft.AspNetCore.Mvc;

namespace AgentKnowledgeBase.Mcp.Admin.Controllers;

/// <summary>API keys authenticate MCP clients and carry a read or readwrite scope per directory.</summary>
[ApiController]
[AdminToken]
[Route("admin/api-keys")]
[ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status400BadRequest, AdminMediaTypes.Json)]
[ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status401Unauthorized, AdminMediaTypes.Json)]
[ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status500InternalServerError, AdminMediaTypes.Json)]
public sealed class ApiKeysController(ApiKeyService keys) : ControllerBase
{
    /// <summary>Creates a key and returns its plaintext value once.</summary>
    /// <response code="201">The key was created.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ApiKeyCreatedResponse), StatusCodes.Status201Created, AdminMediaTypes.Json)]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest request, CancellationToken ct)
    {
        var scopes = request.Scopes?.Select(s => new KeyScopeInput(s.DirectoryId, s.Access)).ToList();
        var (record, plainText) = await keys.CreateAsync(request.Name, scopes, request.AllowGitRead ?? false, ct);
        return Created($"/admin/api-keys/{record.Id}", ToCreatedResponse(record, plainText));
    }

    /// <summary>Replaces the key material of an existing key and returns the new plaintext value.</summary>
    /// <response code="404">No key has this id.</response>
    [HttpPost("{id:guid}/regenerate")]
    [ProducesResponseType(typeof(ApiKeyCreatedResponse), StatusCodes.Status200OK, AdminMediaTypes.Json)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound, AdminMediaTypes.Json)]
    public async Task<IActionResult> Regenerate(Guid id, CancellationToken ct)
    {
        var (record, plainText) = await keys.RegenerateAsync(id, ct);
        return Ok(ToCreatedResponse(record, plainText));
    }

    /// <summary>Replaces the directory scopes of a key.</summary>
    /// <response code="404">No key has this id.</response>
    [HttpPut("{id:guid}/scopes")]
    [ProducesResponseType(typeof(ApiKeySummaryResponse), StatusCodes.Status200OK, AdminMediaTypes.Json)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound, AdminMediaTypes.Json)]
    public async Task<IActionResult> UpdateScopes(
        Guid id,
        [FromBody] UpdateApiKeyScopesRequest request,
        CancellationToken ct)
    {
        var scopes = request.Scopes?.Select(s => new KeyScopeInput(s.DirectoryId, s.Access)).ToList();
        var record = await keys.UpdateScopesAsync(id, scopes, ct);
        return Ok(ToSummaryResponse(record));
    }

    /// <summary>Grants or withdraws permission to clone the read-only git repository.</summary>
    /// <response code="404">No key has this id.</response>
    [HttpPut("{id:guid}/git-access")]
    [ProducesResponseType(typeof(ApiKeySummaryResponse), StatusCodes.Status200OK, AdminMediaTypes.Json)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound, AdminMediaTypes.Json)]
    public async Task<IActionResult> UpdateGitAccess(
        Guid id,
        [FromBody] UpdateApiKeyGitAccessRequest request,
        CancellationToken ct)
    {
        if (request.AllowGitRead is null)
        {
            throw KnowledgeException.Validation("allowGitRead is required.");
        }

        var record = await keys.UpdateGitAccessAsync(id, request.AllowGitRead.Value, ct);
        return Ok(ToSummaryResponse(record));
    }

    /// <summary>Lists the keys. The plaintext value is never returned here.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ApiKeySummaryResponse>), StatusCodes.Status200OK, AdminMediaTypes.Json)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var records = await keys.ListAsync(ct);
        return Ok(records.Select(ToSummaryResponse));
    }

    /// <summary>Revokes a key. A revoked key can no longer authenticate.</summary>
    /// <response code="404">No key has this id.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(AdminErrorResponse), StatusCodes.Status404NotFound, AdminMediaTypes.Json)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        await keys.RevokeAsync(id, ct);
        return NoContent();
    }

    private static KeyScopeResponse ToDto(KeyScope scope) =>
        new(scope.DirectoryId, scope.Access.ToString().ToLowerInvariant());

    private static ApiKeyCreatedResponse ToCreatedResponse(ApiKeyRecord record, string plainText) =>
        new(record.Id, record.Name, plainText, record.Scopes.Select(ToDto).ToList(), record.AllowGitRead, record.CreatedAt);

    private static ApiKeySummaryResponse ToSummaryResponse(ApiKeyRecord record) =>
        new(
            record.Id,
            record.Name,
            record.Scopes.Select(ToDto).ToList(),
            record.AllowGitRead,
            record.CreatedAt,
            record.RotatedAt,
            record.RevokedAt);
}
