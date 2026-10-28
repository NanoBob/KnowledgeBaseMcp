using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using AgentKnowledgeBase.Mcp.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

namespace AgentKnowledgeBase.Tests;

/// <summary>
/// Boots the real HTTP pipeline with the in-memory repositories standing in for MongoDB, so the
/// admin filter and the MCP key middleware are exercised the way clients hit them.
/// </summary>
public sealed class KnowledgeBaseApp : WebApplicationFactory<Program>
{
    public const string AdminToken = "integration-test-admin-token";

    public FakeDirectoryRepository Directories { get; } = new();
    public FakeDocumentRepository Documents { get; } = new();
    public FakeApiKeyRepository Keys { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Mongo:ConnectionString", "mongodb://localhost:27017");
        builder.UseSetting("Mongo:Database", "integration_test");
        builder.UseSetting("Admin:Token", AdminToken);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMongoClient>();
            services.RemoveAll<IMongoDatabase>();
            services.RemoveAll<IDirectoryRepository>();
            services.RemoveAll<IDocumentRepository>();
            services.RemoveAll<IApiKeyRepository>();

            // The index initializer would dial MongoDB on startup.
            foreach (var hosted in services
                         .Where(d => d.ServiceType == typeof(IHostedService)
                                     && d.ImplementationType == typeof(MongoIndexInitializer))
                         .ToList())
            {
                services.Remove(hosted);
            }

            services.AddSingleton<IDirectoryRepository>(Directories);
            services.AddSingleton<IDocumentRepository>(Documents);
            services.AddSingleton<IApiKeyRepository>(Keys);
        });
    }

    public HttpClient CreateAdminClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Token", AdminToken);
        return client;
    }
}

public sealed class AdminApiAuthTests
{
    [Fact]
    public async Task Admin_endpoints_reject_a_missing_token()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateClient();

        var response = await client.GetAsync("/admin/directories");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admin_endpoints_reject_a_wrong_token()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Token", "not-the-admin-token");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/directories")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/api-keys")).StatusCode);
    }

    [Fact]
    public async Task Admin_endpoints_accept_the_configured_token_from_either_header()
    {
        using var app = new KnowledgeBaseApp();

        using var headerClient = app.CreateAdminClient();
        var viaHeader = await headerClient.GetAsync("/admin/directories");
        Assert.Equal(HttpStatusCode.OK, viaHeader.StatusCode);
        Assert.Equal("[]", await viaHeader.Content.ReadAsStringAsync());

        using var bearerClient = app.CreateClient();
        bearerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", KnowledgeBaseApp.AdminToken);
        Assert.Equal(HttpStatusCode.OK, (await bearerClient.GetAsync("/admin/api-keys")).StatusCode);
    }

    [Fact]
    public async Task Mcp_endpoint_rejects_a_missing_key()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateClient();

        var response = await PostMcp(client, presentedKey: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Mcp_endpoint_rejects_an_unknown_key()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcp(client, "akb_never-issued")).StatusCode);
    }

    [Fact]
    public async Task Mcp_endpoint_rejects_a_revoked_key()
    {
        using var app = new KnowledgeBaseApp();
        const string plainText = "akb_revoked-integration-test-key";
        app.Keys.Keys.Add(new ApiKeyRecord
        {
            Id = Guid.NewGuid(),
            Name = "revoked",
            KeyHash = ApiKeyService.Hash(plainText),
            Scopes = [new KeyScope { DirectoryId = Guid.NewGuid(), Access = AccessLevel.Read }],
            CreatedAt = DateTime.UtcNow,
            RevokedAt = DateTime.UtcNow
        });

        using var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcp(client, plainText)).StatusCode);
    }

    [Fact]
    public async Task Mcp_endpoint_lets_an_active_key_through_to_the_mcp_transport()
    {
        using var app = new KnowledgeBaseApp();
        const string plainText = "akb_active-integration-test-key";
        app.Keys.Keys.Add(new ApiKeyRecord
        {
            Id = Guid.NewGuid(),
            Name = "active",
            KeyHash = ApiKeyService.Hash(plainText),
            Scopes = [new KeyScope { DirectoryId = Guid.NewGuid(), Access = AccessLevel.Read }],
            CreatedAt = DateTime.UtcNow
        });

        using var client = app.CreateClient();
        var response = await PostMcp(client, plainText);

        // The transport answers with a protocol error for a request that skipped initialize;
        // what matters here is that the key middleware did not turn it into a 401.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Task<HttpResponseMessage> PostMcp(HttpClient client, string? presentedKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (presentedKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", presentedKey);
        }

        return client.SendAsync(request);
    }
}

public sealed class AdminDocumentApiTests
{
    [Fact]
    public async Task Creating_a_document_ignores_nothing_and_honours_expectedVersion()
    {
        using var app = new KnowledgeBaseApp();
        var directory = SeedDirectory(app, "dotnet");
        using var client = app.CreateAdminClient();

        // A client that sends expectedVersion is asking to update, so creating must be refused.
        var refused = await client.PostAsJsonAsync("/admin/documents", new
        {
            directoryId = directory.Id,
            name = "async-await",
            content = "# Async await",
            expectedVersion = 1
        });
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        var created = await client.PostAsJsonAsync("/admin/documents", new
        {
            directoryId = directory.Id,
            name = "async-await",
            content = "# Async await"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<CreatedDocument>();
        Assert.NotNull(body);
        Assert.Equal(1, body.Version);
        Assert.Equal(directory.Id, body.DirectoryId);
    }

    private static KnowledgeDocumentDirectory SeedDirectory(KnowledgeBaseApp app, string name)
    {
        var directory = new KnowledgeDocumentDirectory
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = "",
            CreatedAt = DateTime.UtcNow,
            CreatedBy = "admin"
        };
        app.Directories.Directories[directory.Id] = directory;
        return directory;
    }

    private sealed record CreatedDocument(Guid Id, Guid DirectoryId, string Name, int Version, bool Created);
}
