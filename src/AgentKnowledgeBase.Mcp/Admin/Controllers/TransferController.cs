using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AgentKnowledgeBase.Mcp.Admin.Controllers;

[ApiController]
[AdminToken]
[Route("admin/transfer")]
public sealed class TransferController(
    DocumentTransferService service,
    IOptions<TransferOptions> options) : ControllerBase
{
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] ExportDocumentsRequest request, CancellationToken ct)
    {
        var plan = await service.PlanExportAsync(request.DirectoryIds, ct);

        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"knowledge-export-{plan.ExportedAt:yyyyMMddTHHmmssZ}.zip\"";

        // ZipArchive writes entry headers and the central directory synchronously, so the response stream
        // has to allow it while the archive is streamed out.
        if (HttpContext.Features.Get<IHttpBodyControlFeature>() is { } bodyControl)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        await service.WriteArchiveAsync(plan, Response.Body, ct);
        return new EmptyResult();
    }

    // The archive is read through the request body so the configured limit can replace the framework
    // default; DocumentTransferService still enforces the exact MaxArchiveBytes bound on the zip itself.
    // The form is read manually because model binding would read it before the size limit is applied and
    // would turn an oversized or malformed body into an unhandled exception instead of a validation error.
    [HttpPost("import")]
    public async Task<IActionResult> Import([FromQuery] string? conflict, CancellationToken ct)
    {
        var maxArchiveBytes = options.Value.MaxArchiveBytes;

        var sizeFeature = Request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = maxArchiveBytes + (1024 * 1024);
        }

        if (!Request.HasFormContentType)
        {
            throw KnowledgeException.Validation(
                "send the archive as multipart/form-data with the zip in the 'file' field.");
        }

        IFormCollection form;
        try
        {
            form = await Request.ReadFormAsync(ct);
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
        return Ok(new ImportDocumentsResponse(
            result.DirectoriesCreated,
            result.DocumentsCreated,
            result.DocumentsUpdated,
            result.DocumentsSkipped));
    }

    private static ImportConflictMode ParseConflictMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "overwrite" => ImportConflictMode.Overwrite,
            "skip" => ImportConflictMode.Skip,
            "fail" => ImportConflictMode.Fail,
            _ => throw KnowledgeException.Validation(
                "conflict must be one of 'overwrite', 'skip' or 'fail'.")
        };
}
