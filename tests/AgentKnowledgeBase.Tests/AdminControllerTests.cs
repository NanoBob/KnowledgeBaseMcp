using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentKnowledgeBase.Mcp.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AgentKnowledgeBase.Tests;

public sealed class AdminTokenValidatorTests
{
    [Fact]
    public void Accepts_the_configured_token_from_the_admin_header()
    {
        var request = RequestWith("X-Admin-Token", "secret");

        Assert.True(AdminTokenValidator.IsValid(request, "secret"));
    }

    [Fact]
    public void Accepts_the_configured_token_from_a_bearer_header()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = "Bearer secret";

        Assert.True(AdminTokenValidator.IsValid(request, "secret"));
    }

    [Fact]
    public void The_admin_header_wins_over_the_bearer_header()
    {
        var request = RequestWith("X-Admin-Token", "stale");
        request.Headers.Authorization = "Bearer secret";

        Assert.False(AdminTokenValidator.IsValid(request, "secret"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Secret")]
    [InlineData("secre")]
    [InlineData("secrets")]
    public void Rejects_a_token_that_does_not_match_exactly(string presented)
    {
        var request = RequestWith("X-Admin-Token", presented);

        Assert.False(AdminTokenValidator.IsValid(request, "secret"));
    }

    [Fact]
    public void Rejects_a_request_with_no_token()
    {
        var request = new DefaultHttpContext().Request;

        Assert.False(AdminTokenValidator.IsValid(request, "secret"));
    }

    [Fact]
    public void Rejects_a_bearer_header_that_uses_another_scheme()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = "Basic secret";

        Assert.False(AdminTokenValidator.IsValid(request, "secret"));
    }

    [Fact]
    public void Rejects_every_request_when_no_token_is_configured()
    {
        var request = RequestWith("X-Admin-Token", "");

        Assert.False(AdminTokenValidator.IsValid(request, ""));
        Assert.False(AdminTokenValidator.IsValid(RequestWith("X-Admin-Token", "secret"), ""));
    }

    private static HttpRequest RequestWith(string header, string value)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers[header] = value;
        return request;
    }
}

/// <summary>
/// The admin API moved from minimal API endpoints to controllers. These tests pin the parts of the HTTP
/// contract that clients depend on across that move.
/// </summary>
public sealed class AdminControllerContractTests
{
    [Fact]
    public async Task Rejecting_a_request_keeps_the_legacy_401_body()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateClient();

        var missing = await client.GetAsync("/admin/directories");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("application/json", missing.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"error":"admin_token_required"}""", await missing.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Add("X-Admin-Token", "not-the-admin-token");
        var wrong = await client.GetAsync("/admin/documents");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("""{"error":"admin_token_required"}""", await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unbindable_body_returns_problem_details()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateAdminClient();

        var body = new StringContent(
            """{"directoryId":"not-a-guid","name":"async-await","content":"# Async await"}""",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/admin/documents", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task A_missing_body_returns_problem_details()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateAdminClient();

        var response = await client.PostAsync(
            "/admin/directories",
            new StringContent("", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Domain_errors_keep_the_error_kind_envelope()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateAdminClient();

        var response = await client.GetAsync($"/admin/documents/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.NotNull(error);
        Assert.Equal("notfound", error.Kind);
        Assert.False(string.IsNullOrWhiteSpace(error.Error));
        Assert.Null(error.LatestVersion);
    }

    [Fact]
    public async Task A_stale_expected_version_reports_the_latest_version()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateAdminClient();
        var directory = await CreateDirectory(client, "dotnet");
        var document = await CreateDocument(client, directory, "async-await");

        var response = await client.PutAsJsonAsync($"/admin/documents/{document}", new
        {
            directoryId = directory,
            name = "async-await",
            content = "# Async await, revised",
            expectedVersion = 99
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.NotNull(error);
        Assert.Equal("conflict", error.Kind);
        Assert.Equal(1, error.LatestVersion);
    }

    [Fact]
    public async Task Restoring_a_revision_writes_its_content_as_the_newest_version()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateAdminClient();
        var directory = await CreateDirectory(client, "dotnet");
        var document = await CreateDocument(client, directory, "async-await");
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/admin/documents/{document}", new
        {
            name = "async-await",
            content = "# Async await, revised"
        })).StatusCode);

        var revisions = await client.GetFromJsonAsync<List<RevisionRow>>($"/admin/documents/{document}/revisions");
        Assert.NotNull(revisions);
        var first = revisions.Single(r => r.Version == 1);

        var restored = await client.PostAsJsonAsync($"/admin/revisions/{first.Id}/restore", new { });

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(3, (await restored.Content.ReadFromJsonAsync<DocumentRow>())!.Version);
        var detail = await client.GetFromJsonAsync<DocumentDetailRow>($"/admin/documents/{document}");
        Assert.NotNull(detail);
        Assert.Equal("# Async await", detail.Content);
        Assert.Equal(3, detail.Version);
        Assert.Equal(3, (await client.GetFromJsonAsync<List<RevisionRow>>($"/admin/documents/{document}/revisions"))!.Count);
    }

    [Fact]
    public async Task Restoring_with_a_stale_expected_version_reports_the_latest_version()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateAdminClient();
        var directory = await CreateDirectory(client, "dotnet");
        var document = await CreateDocument(client, directory, "async-await");
        var revisions = await client.GetFromJsonAsync<List<RevisionRow>>($"/admin/documents/{document}/revisions");
        Assert.NotNull(revisions);

        var response = await client.PostAsJsonAsync(
            $"/admin/revisions/{revisions[0].Id}/restore",
            new { expectedVersion = 99 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorEnvelope>();
        Assert.NotNull(error);
        Assert.Equal("conflict", error.Kind);
        Assert.Equal(1, error.LatestVersion);
    }

    [Fact]
    public async Task Restoring_an_unknown_revision_is_not_found()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateAdminClient();

        var response = await client.PostAsJsonAsync($"/admin/revisions/{Guid.NewGuid()}/restore", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("notfound", (await response.Content.ReadFromJsonAsync<ErrorEnvelope>())!.Kind);
    }

    private sealed record RevisionRow(Guid Id, int Version);

    private sealed record DocumentDetailRow(Guid Id, string Content, int Version);

    private static async Task<Guid> CreateDirectory(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/admin/directories", new { name, description = "" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var directory = await response.Content.ReadFromJsonAsync<DirectoryRow>();
        Assert.NotNull(directory);
        return directory.Id;
    }

    private static async Task<Guid> CreateDocument(HttpClient client, Guid directoryId, string name)
    {
        var response = await client.PostAsJsonAsync("/admin/documents", new
        {
            directoryId,
            name,
            summary = "",
            tags = Array.Empty<string>(),
            content = "# Async await"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<DocumentRow>();
        Assert.NotNull(document);
        return document.Id;
    }

    private sealed record DirectoryRow(Guid Id, string Name);

    private sealed record DocumentRow(Guid Id, int Version);

    private sealed record ErrorEnvelope(string? Error, string? Kind, int? LatestVersion);
}

public sealed class AdminRouteSmokeTests
{
    [Fact]
    public async Task Every_admin_route_is_reachable()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateAdminClient();

        // Directories
        var created = await client.PostAsJsonAsync("/admin/directories", new { name = "dotnet", description = "C# guidance" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var directoryRow = (await created.Content.ReadFromJsonAsync<DirectoryRow>())!;
        Assert.Equal($"/admin/directories/{directoryRow.Id}", created.Headers.Location?.ToString());
        var directory = directoryRow.Id;

        var other = await client.PostAsJsonAsync("/admin/directories", new { name = "python", description = "" });
        var otherDirectory = (await other.Content.ReadFromJsonAsync<DirectoryRow>())!.Id;

        var listedDirectories = await client.GetFromJsonAsync<List<DirectoryRow>>("/admin/directories");
        Assert.NotNull(listedDirectories);
        Assert.Equal(2, listedDirectories.Count);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/admin/directories/{directory}")).StatusCode);

        var renamed = await client.PutAsJsonAsync($"/admin/directories/{directory}", new { name = "dotnet", description = "C# and .NET" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("C# and .NET", (await renamed.Content.ReadFromJsonAsync<DirectoryRow>())!.Description);

        // Documents
        var stored = await client.PostAsJsonAsync("/admin/documents", new
        {
            directoryId = directory,
            name = "async-await",
            summary = "Awaiting",
            tags = new[] { "csharp" },
            content = "# Async await"
        });
        Assert.Equal(HttpStatusCode.Created, stored.StatusCode);
        var document = (await stored.Content.ReadFromJsonAsync<DocumentRow>())!.Id;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/admin/documents?directoryId={directory}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/admin/documents/{document}")).StatusCode);

        var updated = await client.PutAsJsonAsync($"/admin/documents/{document}", new
        {
            name = "async-await",
            summary = "Awaiting",
            tags = new[] { "csharp" },
            content = "# Async await, revised"
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(2, (await updated.Content.ReadFromJsonAsync<DocumentRow>())!.Version);

        var moved = await client.PostAsJsonAsync($"/admin/documents/{document}/move", new { directoryId = otherDirectory });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(otherDirectory, (await moved.Content.ReadFromJsonAsync<DocumentRow>())!.DirectoryId);

        // Creating, updating and moving each record a revision, newest first.
        var revisions = await client.GetFromJsonAsync<List<RevisionRow>>($"/admin/documents/{document}/revisions");
        Assert.NotNull(revisions);
        Assert.Equal(3, revisions.Count);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/admin/revisions/{revisions[0].Id}")).StatusCode);

        // Restoring the oldest revision records a fourth one and puts the original content back.
        var restored = await client.PostAsJsonAsync($"/admin/revisions/{revisions[^1].Id}/restore", new { });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var restoredRow = (await restored.Content.ReadFromJsonAsync<DocumentRow>())!;
        Assert.Equal(4, restoredRow.Version);
        Assert.False(restoredRow.Created);
        Assert.Equal(4, (await client.GetFromJsonAsync<List<RevisionRow>>($"/admin/documents/{document}/revisions"))!.Count);

        // API keys
        var key = await client.PostAsJsonAsync("/admin/api-keys", new
        {
            name = "ci",
            scopes = new[] { new { directoryId = directory, access = "read" } }
        });
        Assert.Equal(HttpStatusCode.Created, key.StatusCode);
        var createdKey = (await key.Content.ReadFromJsonAsync<KeyRow>())!;
        Assert.False(string.IsNullOrWhiteSpace(createdKey.Key));

        var keys = await client.GetFromJsonAsync<List<KeyRow>>("/admin/api-keys");
        Assert.NotNull(keys);
        Assert.Single(keys);

        var scoped = await client.PutAsJsonAsync($"/admin/api-keys/{createdKey.Id}/scopes", new
        {
            scopes = new[] { new { directoryId = otherDirectory, access = "readwrite" } }
        });
        Assert.Equal(HttpStatusCode.OK, scoped.StatusCode);
        var rescoped = (await scoped.Content.ReadFromJsonAsync<KeyRow>())!;
        Assert.Single(rescoped.Scopes);
        Assert.Null(rescoped.Key);

        var regenerated = await client.PostAsync($"/admin/api-keys/{createdKey.Id}/regenerate", null);
        Assert.Equal(HttpStatusCode.OK, regenerated.StatusCode);
        Assert.NotEqual(createdKey.Key, (await regenerated.Content.ReadFromJsonAsync<KeyRow>())!.Key);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/admin/api-keys/{createdKey.Id}")).StatusCode);

        // Transfer
        var export = await client.PostAsJsonAsync("/admin/transfer/export", new { directoryIds = new[] { otherDirectory } });
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", export.Content.Headers.ContentDisposition?.DispositionType);
        Assert.True((await export.Content.ReadAsByteArrayAsync()).Length > 0);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/admin/documents/{document}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/admin/directories/{directory}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/admin/directories/{otherDirectory}")).StatusCode);
    }

    private sealed record DirectoryRow(Guid Id, string Name, string Description, DateTime CreatedAt);

    private sealed record DocumentRow(Guid Id, Guid DirectoryId, string Name, int Version, bool Created);

    private sealed record RevisionRow(Guid Id, Guid DocumentId, int Version, string Name, DateTime ChangedAt, string ChangedBy);

    private sealed record KeyRow(Guid Id, string Name, string? Key, List<JsonElement> Scopes, DateTime CreatedAt);
}
