using System.Security.Cryptography;
using System.Text;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace AgentKnowledgeBase.Mcp.Admin;

public sealed record CreateApiKeyRequest(string? Name, List<CreateKeyScope>? Scopes);

public sealed record CreateKeyScope(Guid? DirectoryId, string? Access);

/// <summary>A stored scope. Unlike the request shape, both fields are always present.</summary>
public sealed record KeyScopeResponse(Guid DirectoryId, string Access);

public sealed record UpdateApiKeyScopesRequest(List<CreateKeyScope>? Scopes);

public sealed record ApiKeyCreatedResponse(Guid Id, string Name, string Key, IReadOnlyList<KeyScopeResponse> Scopes, DateTime CreatedAt);

public sealed record ApiKeySummaryResponse(Guid Id, string Name, IReadOnlyList<KeyScopeResponse> Scopes, DateTime CreatedAt, DateTime? RotatedAt, DateTime? RevokedAt);

public sealed record CreateDirectoryRequest(string? Name, string? Description);

public sealed record UpdateDirectoryRequest(string? Name, string? Description);

public sealed record StoreDocumentRequest(
    Guid? DirectoryId,
    string? Name,
    string? Summary,
    string[]? Tags,
    string? Content,
    int? ExpectedVersion);

public sealed record StoreDocumentResponse(Guid Id, Guid DirectoryId, string Name, int Version, bool Created);

public sealed record MoveDocumentRequest(Guid? DirectoryId, int? ExpectedVersion);

public sealed record ExportDocumentsRequest(List<Guid>? DirectoryIds);

public sealed record ImportDocumentsResponse(
    int DirectoriesCreated,
    int DocumentsCreated,
    int DocumentsUpdated,
    int DocumentsSkipped);


public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminApi(this WebApplication app)
    {
        MapDirectories(app);
        MapDocuments(app);
        MapTransfer(app);
        MapRevisions(app);
        return MapApiKeys(app);
    }

    private static void MapDirectories(WebApplication app)
    {
        var directories = MapAdminGroup(app, "/admin/directories");

        directories.MapPost("/", async (CreateDirectoryRequest request, DirectoryService service, CancellationToken ct) =>
        {
            var directory = await service.CreateAsync(request.Name, request.Description, "admin", ct);
            return Results.Created($"/admin/directories/{directory.Id}", directory);
        });

        directories.MapGet("/", async (DirectoryService service, CancellationToken ct) =>
            Results.Ok(await service.ListAllAsync(ct)));

        directories.MapGet("/{id:guid}", async (Guid id, DirectoryService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(id, ct)));

        directories.MapPut("/{id:guid}", async (
            Guid id,
            UpdateDirectoryRequest request,
            DirectoryService service,
            CancellationToken ct) =>
            Results.Ok(await service.UpdateAsync(id, request.Name, request.Description, ct)));

        directories.MapDelete("/{id:guid}", async (Guid id, DirectoryService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });
    }

    private static void MapDocuments(WebApplication app)
    {
        var documents = MapAdminGroup(app, "/admin/documents");

        documents.MapGet("/", async (Guid? directoryId, KnowledgeService knowledge, CancellationToken ct) =>
            Results.Ok(await knowledge.ListAsync(ApiCaller.Admin, directoryId, ct)));

        documents.MapGet("/{id:guid}", async (Guid id, KnowledgeService knowledge, CancellationToken ct) =>
            Results.Ok(await knowledge.GetAsync(ApiCaller.Admin, id, ct)));

        documents.MapGet("/{id:guid}/revisions", async (Guid id, KnowledgeService knowledge, CancellationToken ct) =>
            Results.Ok(await knowledge.ListRevisionsAsync(ApiCaller.Admin, id, ct)));

        documents.MapPost("/", async (StoreDocumentRequest request, KnowledgeService knowledge, CancellationToken ct) =>
        {
            // expectedVersion must reach the service: sending one on create means "this should already
            // exist", which the service rejects instead of silently creating a second document.
            var result = await knowledge.StoreAsync(
                ApiCaller.Admin,
                ToStoreRequest(null, request, request.ExpectedVersion),
                ct);
            return Results.Created($"/admin/documents/{result.Id}", ToResponse(result));
        });

        documents.MapPut("/{id:guid}", async (
            Guid id,
            StoreDocumentRequest request,
            KnowledgeService knowledge,
            CancellationToken ct) =>
        {
            // Omitting expectedVersion lets the admin client overwrite whatever is stored.
            var expectedVersion = request.ExpectedVersion
                ?? (await knowledge.GetAsync(ApiCaller.Admin, id, ct)).Version;
            var result = await knowledge.StoreAsync(ApiCaller.Admin, ToStoreRequest(id, request, expectedVersion), ct);
            return Results.Ok(ToResponse(result));
        });

        documents.MapPost("/{id:guid}/move", async (
            Guid id,
            MoveDocumentRequest request,
            KnowledgeService knowledge,
            CancellationToken ct) =>
        {
            if (request.DirectoryId is null || request.DirectoryId == Guid.Empty)
            {
                throw KnowledgeException.Validation("directoryId is required to move a document.");
            }

            var result = await knowledge.MoveAsync(ApiCaller.Admin, id, request.DirectoryId.Value, request.ExpectedVersion, ct);
            return Results.Ok(ToResponse(result));
        });

        documents.MapDelete("/{id:guid}", async (Guid id, KnowledgeService knowledge, CancellationToken ct) =>
        {
            await knowledge.DeleteAsync(ApiCaller.Admin, id, ct);
            return Results.NoContent();
        });
    }

    private static void MapTransfer(WebApplication app)
    {
        var transfer = MapAdminGroup(app, "/admin/transfer");

        transfer.MapPost("/export", async (
            ExportDocumentsRequest request,
            DocumentTransferService service,
            HttpResponse response,
            CancellationToken ct) =>
        {
            var plan = await service.PlanExportAsync(request.DirectoryIds, ct);

            response.ContentType = "application/zip";
            response.Headers.ContentDisposition =
                $"attachment; filename=\"knowledge-export-{plan.ExportedAt:yyyyMMddTHHmmssZ}.zip\"";

            // ZipArchive writes entry headers and the central directory synchronously, so the response stream
            // has to allow it while the archive is streamed out.
            if (response.HttpContext.Features.Get<IHttpBodyControlFeature>() is { } bodyControl)
            {
                bodyControl.AllowSynchronousIO = true;
            }

            await service.WriteArchiveAsync(plan, response.Body, ct);
            return Results.Empty;
        });

        // The archive is read through the request body so the configured limit can replace the framework
        // default; DocumentTransferService still enforces the exact MaxArchiveBytes bound on the zip itself.
        transfer.MapPost("/import", async (
            HttpRequest request,
            string? conflict,
            DocumentTransferService service,
            IOptions<TransferOptions> options,
            CancellationToken ct) =>
        {
            var maxArchiveBytes = options.Value.MaxArchiveBytes;

            var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
            {
                sizeFeature.MaxRequestBodySize = maxArchiveBytes + (1024 * 1024);
            }

            if (!request.HasFormContentType)
            {
                throw KnowledgeException.Validation(
                    "send the archive as multipart/form-data with the zip in the 'file' field.");
            }

            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(ct);
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                throw KnowledgeException.Validation(
                    $"the archive exceeds the maximum allowed size of {maxArchiveBytes} bytes.");
            }
            catch (InvalidDataException)
            {
                throw KnowledgeException.Validation("the request body is not valid multipart/form-data.");
            }
            catch (BadHttpRequestException)
            {
                throw KnowledgeException.Validation("the request body is not valid multipart/form-data.");
            }

            var file = form.Files.GetFile("file") ?? (form.Files.Count == 1 ? form.Files[0] : null);
            if (file is null || file.Length == 0)
            {
                throw KnowledgeException.Validation("a .zip file is required in the 'file' form field.");
            }

            await using var stream = file.OpenReadStream();
            var result = await service.ImportAsync(stream, ParseConflictMode(conflict), ct);
            return Results.Ok(new ImportDocumentsResponse(
                result.DirectoriesCreated,
                result.DocumentsCreated,
                result.DocumentsUpdated,
                result.DocumentsSkipped));
        });
    }

    private static void MapRevisions(WebApplication app)
    {
        var revisions = MapAdminGroup(app, "/admin/revisions");

        revisions.MapGet("/{id:guid}", async (Guid id, KnowledgeService knowledge, CancellationToken ct) =>
            Results.Ok(await knowledge.GetRevisionAsync(ApiCaller.Admin, id, ct)));
    }

    private static RouteGroupBuilder MapApiKeys(WebApplication app)
    {
        var group = MapAdminGroup(app, "/admin/api-keys");
        group.MapPost("/", async (CreateApiKeyRequest request, ApiKeyService keys, CancellationToken ct) =>
        {
            var scopes = request.Scopes?.Select(s => new KeyScopeInput(s.DirectoryId, s.Access)).ToList();
            var (record, plainText) = await keys.CreateAsync(request.Name, scopes, ct);
            return Results.Created($"/admin/api-keys/{record.Id}", ToCreatedResponse(record, plainText));
        });

        group.MapPost("/{id:guid}/regenerate", async (Guid id, ApiKeyService keys, CancellationToken ct) =>
        {
            var (record, plainText) = await keys.RegenerateAsync(id, ct);
            return Results.Ok(ToCreatedResponse(record, plainText));
        });

        group.MapPut("/{id:guid}/scopes", async (
            Guid id,
            UpdateApiKeyScopesRequest request,
            ApiKeyService keys,
            CancellationToken ct) =>
        {
            var scopes = request.Scopes?.Select(s => new KeyScopeInput(s.DirectoryId, s.Access)).ToList();
            var record = await keys.UpdateScopesAsync(id, scopes, ct);
            return Results.Ok(ToSummaryResponse(record));
        });

        group.MapGet("/", async (ApiKeyService keys, CancellationToken ct) =>
        {
            var records = await keys.ListAsync(ct);
            return Results.Ok(records.Select(ToSummaryResponse));
        });

        group.MapDelete("/{id:guid}", async (Guid id, ApiKeyService keys, CancellationToken ct) =>
        {
            await keys.RevokeAsync(id, ct);
            return Results.NoContent();
        });

        return group;
    }

    private static StoreRequest ToStoreRequest(Guid? id, StoreDocumentRequest request, int? expectedVersion) =>
        new(id, request.DirectoryId, request.Name, request.Summary, request.Tags, request.Content, expectedVersion);

    private static StoreDocumentResponse ToResponse(StoreResult result) =>
        new(result.Id, result.DirectoryId, result.Name, result.Version, result.Created);

    private static RouteGroupBuilder MapAdminGroup(WebApplication app, string pattern) =>
        app.MapGroup(pattern)
            .AddEndpointFilter(async (context, next) =>
            {
                var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value;
                return IsAdmin(context.HttpContext.Request, options.Token)
                    ? await next(context)
                    : Results.Json(new { error = "admin_token_required" }, statusCode: StatusCodes.Status401Unauthorized);
            });

    private static ImportConflictMode ParseConflictMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "overwrite" => ImportConflictMode.Overwrite,
            "skip" => ImportConflictMode.Skip,
            "fail" => ImportConflictMode.Fail,
            _ => throw KnowledgeException.Validation(
                "conflict must be one of 'overwrite', 'skip' or 'fail'.")
        };

    private static KeyScopeResponse ToDto(KeyScope scope) =>
        new(scope.DirectoryId, scope.Access.ToString().ToLowerInvariant());

    private static ApiKeyCreatedResponse ToCreatedResponse(ApiKeyRecord record, string plainText) =>
        new(record.Id, record.Name, plainText, record.Scopes.Select(ToDto).ToList(), record.CreatedAt);

    private static ApiKeySummaryResponse ToSummaryResponse(ApiKeyRecord record) =>
        new(record.Id, record.Name, record.Scopes.Select(ToDto).ToList(), record.CreatedAt, record.RotatedAt, record.RevokedAt);

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
