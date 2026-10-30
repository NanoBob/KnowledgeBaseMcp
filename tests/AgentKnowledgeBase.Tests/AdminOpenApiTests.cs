using System.Net;
using System.Text.Json;
using AgentKnowledgeBase.Mcp.Admin;

namespace AgentKnowledgeBase.Tests;

/// <summary>
/// The OpenAPI document and the Swagger UI that serves it. The document is generated from the admin
/// controllers, so these tests pin the parts a client generated from it depends on.
/// </summary>
public sealed class AdminOpenApiTests
{
    [Fact]
    public async Task The_document_is_served_without_the_admin_token()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateClient();

        var response = await client.GetAsync(AdminOpenApi.DocumentPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_document_is_titled_and_versioned()
    {
        using var app = new KnowledgeBaseApp();

        var document = await ReadDocument(app);

        Assert.Equal("Agent Knowledge Base Admin API", document.RootElement.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal(AdminOpenApi.DocumentName, document.RootElement.GetProperty("info").GetProperty("version").GetString());
    }

    [Fact]
    public async Task Every_admin_route_is_documented()
    {
        using var app = new KnowledgeBaseApp();

        var document = await ReadDocument(app);

        var documented = Paths(document).ToHashSet(StringComparer.Ordinal);
        string[] expected =
        [
            "/admin/directories",
            "/admin/directories/{id}",
            "/admin/documents",
            "/admin/documents/{id}",
            "/admin/documents/{id}/revisions",
            "/admin/documents/{id}/move",
            "/admin/revisions/{id}",
            "/admin/revisions/{id}/restore",
            "/admin/api-keys",
            "/admin/api-keys/{id}",
            "/admin/api-keys/{id}/regenerate",
            "/admin/api-keys/{id}/scopes",
            "/admin/transfer/export",
            "/admin/transfer/import"
        ];

        Assert.Equal(expected.OrderBy(path => path, StringComparer.Ordinal), documented.OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_health_probe_and_the_mcp_transport_are_not_part_of_the_document()
    {
        using var app = new KnowledgeBaseApp();

        var document = await ReadDocument(app);

        Assert.DoesNotContain("/health", Paths(document));
        Assert.DoesNotContain(Paths(document), path => path.StartsWith("/mcp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_document_describes_the_admin_token_header()
    {
        using var app = new KnowledgeBaseApp();

        var document = await ReadDocument(app);

        var scheme = document.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty(AdminOpenApi.AdminTokenScheme);

        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("header", scheme.GetProperty("in").GetString());
        Assert.Equal("X-Admin-Token", scheme.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Every_operation_requires_the_admin_token()
    {
        using var app = new KnowledgeBaseApp();

        var document = await ReadDocument(app);

        foreach (var (path, operation) in Operations(document))
        {
            var security = operation.GetProperty("security");
            Assert.True(
                security.EnumerateArray().Any(requirement => requirement.TryGetProperty(AdminOpenApi.AdminTokenScheme, out _)),
                $"{path} does not require the {AdminOpenApi.AdminTokenScheme} scheme.");

            Assert.True(
                operation.GetProperty("responses").TryGetProperty("401", out var unauthorized),
                $"{path} does not document a 401 response.");
            Assert.False(
                string.IsNullOrWhiteSpace(unauthorized.GetProperty("description").GetString()),
                $"{path} has an empty 401 description.");
        }
    }

    [Fact]
    public async Task Operations_are_grouped_and_summarised_from_the_controllers()
    {
        using var app = new KnowledgeBaseApp();

        var document = await ReadDocument(app);

        var list = document.RootElement.GetProperty("paths").GetProperty("/admin/directories").GetProperty("get");
        Assert.Equal("Directories", list.GetProperty("tags")[0].GetString());
        Assert.Equal("Lists every directory.", list.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Responses_reference_the_contract_types()
    {
        using var app = new KnowledgeBaseApp();

        var document = await ReadDocument(app);

        var paths = document.RootElement.GetProperty("paths");
        var listed = paths.GetProperty("/admin/directories").GetProperty("get")
            .GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal("#/components/schemas/DirectorySummary", listed.GetProperty("items").GetProperty("$ref").GetString());

        var conflict = paths.GetProperty("/admin/documents/{id}").GetProperty("put")
            .GetProperty("responses").GetProperty("409")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal("#/components/schemas/AdminErrorResponse", conflict.GetProperty("$ref").GetString());

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("DirectorySummary", out _));
        Assert.True(schemas.TryGetProperty("AdminErrorResponse", out _));
    }

    [Fact]
    public async Task The_export_action_documents_a_zip_download()
    {
        using var app = new KnowledgeBaseApp();

        var document = await ReadDocument(app);

        var content = document.RootElement.GetProperty("paths")
            .GetProperty("/admin/transfer/export").GetProperty("post")
            .GetProperty("responses").GetProperty("200")
            .GetProperty("content");

        Assert.True(content.TryGetProperty("application/zip", out _));
        Assert.False(content.TryGetProperty("application/json", out _));
    }

    [Fact]
    public async Task The_import_action_documents_the_zip_upload()
    {
        using var app = new KnowledgeBaseApp();

        var document = await ReadDocument(app);

        var body = document.RootElement.GetProperty("paths")
            .GetProperty("/admin/transfer/import").GetProperty("post")
            .GetProperty("requestBody");

        Assert.True(body.GetProperty("required").GetBoolean());
        var schema = body.GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema");
        var file = schema.GetProperty("properties").GetProperty("file");
        Assert.Equal("string", file.GetProperty("type").GetString());
        Assert.Equal("binary", file.GetProperty("format").GetString());
        Assert.Contains("file", schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Responses_declare_the_media_type_that_is_actually_sent()
    {
        using var app = new KnowledgeBaseApp();

        var document = await ReadDocument(app);

        var declared = Operations(document)
            .SelectMany(operation => operation.Operation.GetProperty("responses").EnumerateObject())
            .SelectMany(response => response.Value.TryGetProperty("content", out var content)
                ? content.EnumerateObject().Select(media => media.Name)
                : [])
            .Distinct()
            .ToList();

        // MVC assumes text/plain, application/json and text/json whenever no content type is declared.
        Assert.DoesNotContain("text/plain", declared);
        Assert.DoesNotContain("text/json", declared);
        Assert.Equal(["application/json", "application/zip"], declared.Order().ToList());
    }

    [Fact]
    public async Task The_swagger_ui_is_served()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("swagger-ui", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_swagger_ui_points_at_the_admin_document()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/swagger/index.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var script = await response.Content.ReadAsStringAsync();
        Assert.Contains($"\"url\":\"{AdminOpenApi.DocumentPath}\"", script);
        // The admin token typed into "Authorize" has to survive a page reload.
        Assert.Contains("\"persistAuthorization\":true", script);
    }

    [Fact]
    public async Task The_swagger_ui_is_reachable_without_the_admin_token()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/swagger");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonDocument> ReadDocument(KnowledgeBaseApp app)
    {
        using var client = app.CreateClient();
        var response = await client.GetAsync(AdminOpenApi.DocumentPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static IEnumerable<string> Paths(JsonDocument document) =>
        document.RootElement.GetProperty("paths").EnumerateObject().Select(path => path.Name);

    private static IEnumerable<(string Path, JsonElement Operation)> Operations(JsonDocument document) =>
        document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => operation.Name is "get" or "post" or "put" or "delete" or "patch")
                .Select(operation => ($"{operation.Name.ToUpperInvariant()} {path.Name}", operation.Value)));
}
