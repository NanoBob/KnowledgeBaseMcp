using System.Security.Cryptography;
using System.Text;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using Microsoft.Extensions.Options;

namespace AgentKnowledgeBase.Mcp.Admin;

public sealed record CreateApiKeyRequest(string? Name, List<CreateKeyScope>? Scopes);

public sealed record CreateKeyScope(Guid? DirectoryId, string? Access);

public sealed record ApiKeyCreatedResponse(Guid Id, string Name, string Key, IReadOnlyList<CreateKeyScope> Scopes, DateTime CreatedAt);

public sealed record ApiKeySummaryResponse(Guid Id, string Name, IReadOnlyList<CreateKeyScope> Scopes, DateTime CreatedAt, DateTime? RevokedAt);

public sealed record CreateDirectoryRequest(string? Name, string? Description);

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminApi(this WebApplication app)
    {
        var directories = MapAdminGroup(app, "/admin/directories");
        directories.MapPost("/", async (CreateDirectoryRequest request, DirectoryService service, CancellationToken ct) =>
        {
            var directory = await service.CreateAsync(request.Name, request.Description, "admin", ct);
            return Results.Created($"/admin/directories/{directory.Id}", directory);
        });

        directories.MapGet("/", async (DirectoryService service, CancellationToken ct) =>
            Results.Ok(await service.ListAllAsync(ct)));

        var group = MapAdminGroup(app, "/admin/api-keys");
        group.MapPost("/", async (CreateApiKeyRequest request, ApiKeyService keys, CancellationToken ct) =>
        {
            var scopes = request.Scopes?.Select(s => new KeyScopeInput(s.DirectoryId, s.Access)).ToList();
            var (record, plainText) = await keys.CreateAsync(request.Name, scopes, ct);
            var response = new ApiKeyCreatedResponse(
                record.Id,
                record.Name,
                plainText,
                record.Scopes.Select(ToDto).ToList(),
                record.CreatedAt);
            return Results.Created($"/admin/api-keys/{record.Id}", response);
        });

        group.MapGet("/", async (ApiKeyService keys, CancellationToken ct) =>
        {
            var records = await keys.ListAsync(ct);
            return Results.Ok(records.Select(r => new ApiKeySummaryResponse(
                r.Id, r.Name, r.Scopes.Select(ToDto).ToList(), r.CreatedAt, r.RevokedAt)));
        });

        group.MapDelete("/{id:guid}", async (Guid id, ApiKeyService keys, CancellationToken ct) =>
        {
            await keys.RevokeAsync(id, ct);
            return Results.NoContent();
        });

        return group;
    }

    private static RouteGroupBuilder MapAdminGroup(WebApplication app, string pattern) =>
        app.MapGroup(pattern)
            .AddEndpointFilter(async (context, next) =>
            {
                var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value;
                return IsAdmin(context.HttpContext.Request, options.Token)
                    ? await next(context)
                    : Results.Json(new { error = "admin_token_required" }, statusCode: StatusCodes.Status401Unauthorized);
            });

    private static CreateKeyScope ToDto(KeyScope scope) =>
        new(scope.DirectoryId, scope.Access.ToString());

    private static bool IsAdmin(HttpRequest request, string expectedToken)
    {
        var presented = request.Headers["X-Admin-Token"].ToString();
        if (presented.Length == 0)
        {
            var header = request.Headers.Authorization.ToString();
            const string scheme = "Bearer ";
            presented = header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : "";
        }

        return presented.Length > 0
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expectedToken));
    }
}
