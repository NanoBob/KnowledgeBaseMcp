using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentKnowledgeBase.Mcp;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Git;
using AgentKnowledgeBase.Mcp.Services;
using AgentKnowledgeBase.Mcp.Storage;
using GenHTTP.Modules.Git;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentKnowledgeBase.Tests;

public partial class KnowledgeGitEndpointTests
{
    private const string RepositoryPath = "/git/knowledge.git";

    private static Guid AddDirectory(KnowledgeBaseApp app, string name)
    {
        var directory = new KnowledgeDocumentDirectory
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"{name} documents",
            CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedBy = "admin"
        };

        app.Directories.Directories[directory.Id] = directory;
        return directory.Id;
    }

    private static void AddDocument(KnowledgeBaseApp app, Guid directoryId, string name, string content = "body")
    {
        var document = new KnowledgeDocument
        {
            Id = Guid.NewGuid(),
            DirectoryId = directoryId,
            Name = name,
            Summary = $"{name} summary",
            Content = content,
            Version = 1,
            CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            CreatedBy = "admin",
            UpdatedBy = "admin"
        };

        app.Documents.Documents[document.Id] = document;
    }

    private static Task<string> CreateKeyAsync(KnowledgeBaseApp app, params Guid[] directoryIds) =>
        CreateKeyAsync(app, allowGitRead: true, directoryIds);

    private static async Task<string> CreateKeyAsync(
        KnowledgeBaseApp app,
        bool allowGitRead,
        params Guid[] directoryIds)
    {
        var keys = app.Services.GetRequiredService<ApiKeyService>();
        var (_, plainText) = await keys.CreateAsync(
            "git",
            [.. directoryIds.Select(id => new KeyScopeInput(id, "read"))],
            allowGitRead, default);

        return plainText;
    }

    private static HttpRequestMessage Advertisement(string? key, string service = "git-upload-pack")
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{RepositoryPath}/info/refs?service={service}");

        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return request;
    }

    [Fact]
    public async Task Git_traffic_without_a_key_is_challenged()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateClient();

        var response = await client.SendAsync(Advertisement(null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.StartsWith("Basic ", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Git_traffic_with_an_unknown_key_is_rejected()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateClient();

        var response = await client.SendAsync(Advertisement("akb_not-a-real-key"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_key_without_git_access_is_refused()
    {
        using var app = new KnowledgeBaseApp();
        var directory = AddDirectory(app, "alpha");
        AddDocument(app, directory, "one");
        var key = await CreateKeyAsync(app, allowGitRead: false, directory);

        using var client = app.CreateClient();
        var response = await client.SendAsync(Advertisement(key));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // No challenge: asking for another credential cannot help, and git would retry the same one.
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Contains("git_access_denied", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Granting_git_access_opens_the_endpoint_to_an_existing_key()
    {
        using var app = new KnowledgeBaseApp();
        var directory = AddDirectory(app, "alpha");
        AddDocument(app, directory, "one");
        var key = await CreateKeyAsync(app, allowGitRead: false, directory);

        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Advertisement(key))).StatusCode);

        var keys = app.Services.GetRequiredService<ApiKeyService>();
        await keys.UpdateGitAccessAsync(app.Keys.Keys.Single().Id, true, default);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Advertisement(key))).StatusCode);
    }

    [Fact]
    public async Task The_repository_is_advertised_to_a_bearer_key()
    {
        using var app = new KnowledgeBaseApp();
        var directory = AddDirectory(app, "alpha");
        AddDocument(app, directory, "one");
        var key = await CreateKeyAsync(app, directory);

        using var client = app.CreateClient();
        var response = await client.SendAsync(Advertisement(key));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "application/x-git-upload-pack-advertisement",
            response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("# service=git-upload-pack", body, StringComparison.Ordinal);
        Assert.Contains("refs/heads/main", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_basic_auth_password_is_accepted_as_the_key()
    {
        using var app = new KnowledgeBaseApp();
        var directory = AddDirectory(app, "alpha");
        AddDocument(app, directory, "one");
        var key = await CreateKeyAsync(app, directory);

        using var client = app.CreateClient();
        using var request = Advertisement(null);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"git:{key}")));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_advertised_commit_only_covers_the_directories_of_the_key()
    {
        using var app = new KnowledgeBaseApp();
        var mine = AddDirectory(app, "mine");
        var other = AddDirectory(app, "other");
        AddDocument(app, mine, "visible");
        AddDocument(app, other, "hidden");
        var key = await CreateKeyAsync(app, mine);

        using var client = app.CreateClient();
        var response = await client.SendAsync(Advertisement(key));
        var body = await response.Content.ReadAsStringAsync();

        var advertised = AdvertisedCommit().Match(body);
        Assert.True(advertised.Success, "the advertisement did not contain a commit");

        var expected = await new KnowledgeGitRepository(
                await new KnowledgeSnapshotLoader(app.Directories, app.Documents)
                    .LoadAsync(CallerFor(mine), default),
                new GitOptions())
            .GetCommitAsync();

        Assert.Equal(expected.Id.ToString(), advertised.Groups[1].Value);
    }

    [Fact]
    public async Task Pushes_are_refused()
    {
        using var app = new KnowledgeBaseApp();
        var directory = AddDirectory(app, "alpha");
        AddDocument(app, directory, "one");
        var key = await CreateKeyAsync(app, directory);

        using var client = app.CreateClient();

        var advertisement = await client.SendAsync(Advertisement(key, "git-receive-pack"));
        Assert.Equal(HttpStatusCode.Forbidden, advertisement.StatusCode);

        using var push = new HttpRequestMessage(HttpMethod.Post, $"{RepositoryPath}/git-receive-pack")
        {
            Content = new ByteArrayContent("0000"u8.ToArray())
        };
        push.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var response = await client.SendAsync(push);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void Basic_credentials_are_read_from_the_password()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("any:akb_key"));

        Assert.Equal("akb_key", KnowledgeGitAuthMiddleware.PresentedKey(request));
    }

    [Fact]
    public void A_bearer_token_is_read_from_the_header()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = "Bearer akb_key";

        Assert.Equal("akb_key", KnowledgeGitAuthMiddleware.PresentedKey(request));
    }

    [Fact]
    public void An_unreadable_header_yields_no_key()
    {
        var request = new DefaultHttpContext().Request;

        Assert.Null(KnowledgeGitAuthMiddleware.PresentedKey(request));

        request.Headers.Authorization = "Basic !!!not-base64!!!";
        Assert.Null(KnowledgeGitAuthMiddleware.PresentedKey(request));

        request.Headers.Authorization = "Digest abc";
        Assert.Null(KnowledgeGitAuthMiddleware.PresentedKey(request));
    }

    [Theory]
    [InlineData("/git/knowledge.git", "/git/knowledge.git")]
    [InlineData("git/knowledge.git", "/git/knowledge.git")]
    [InlineData("/git/knowledge.git/", "/git/knowledge.git")]
    [InlineData("  /git/knowledge.git  ", "/git/knowledge.git")]
    public void The_mount_path_is_normalized(string configured, string expected)
    {
        Assert.Equal(expected, KnowledgeGitEndpoint.NormalizePath(configured));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void An_empty_mount_path_is_rejected(string configured)
    {
        Assert.Throws<InvalidOperationException>(() => KnowledgeGitEndpoint.NormalizePath(configured));
    }

    private static ApiCaller CallerFor(params Guid[] directoryIds) =>
        new(
            Guid.NewGuid(),
            "git",
            [.. directoryIds.Select(id => new KeyScope { DirectoryId = id, Access = AccessLevel.Read })]);

    [GeneratedRegex("([0-9a-f]{40}) refs/heads/main")]
    private static partial Regex AdvertisedCommit();

    [Fact]
    public async Task A_real_git_client_can_clone_but_not_push()
    {
        if (!GitCli.IsAvailable)
        {
            // The protocol tests above cover the server without depending on a git installation.
            return;
        }

        using var app = new KnowledgeBaseApp();
        var directory = AddDirectory(app, "alpha");
        AddDocument(app, directory, "one", "the content");
        var other = AddDirectory(app, "other");
        AddDocument(app, other, "hidden", "not for this key");
        var key = await CreateKeyAsync(app, directory);

        await using var host = await GitTestHost.StartAsync(app);
        var workspace = Directory.CreateTempSubdirectory("kb-git-");
        var checkout = Path.Combine(workspace.FullName, "checkout");

        try
        {
            var clone = await GitCli.RunAsync(
                workspace.FullName,
                "clone",
                host.AuthenticatedUrl(key),
                checkout);

            Assert.True(clone.ExitCode == 0, clone.Error);

            var document = Path.Combine(checkout, "alpha", "one.md");
            Assert.True(File.Exists(document), $"expected {document} to exist");
            Assert.Equal("the content", File.ReadAllText(document).Trim());
            Assert.True(File.Exists(Path.Combine(checkout, "README.md")));
            Assert.True(File.Exists(Path.Combine(checkout, "manifest.json")));

            // A clone holds the whole repository, but that repository only contains what this key may read.
            Assert.False(Directory.Exists(Path.Combine(checkout, "other")));

            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(checkout, "manifest.json")));
            var directories = manifest.RootElement.GetProperty("directories").EnumerateArray().ToList();
            Assert.Equal("alpha", Assert.Single(directories).GetProperty("name").GetString());

            var documents = manifest.RootElement.GetProperty("documents").EnumerateArray().ToList();
            Assert.Equal("alpha/one.md", Assert.Single(documents).GetProperty("file").GetString());

            // A push must be refused by the server, not merely ignored by the client.
            File.AppendAllText(document, "\nlocal change\n");
            await GitCli.RunAsync(checkout, "add", ".");
            await GitCli.RunAsync(checkout, "commit", "-m", "local change");

            var push = await GitCli.RunAsync(checkout, "push", "origin", "HEAD:refs/heads/main");

            Assert.NotEqual(0, push.ExitCode);
            Assert.Contains("403", push.Error + push.Output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    /// <summary>
    /// Git marks pack files read-only, which Windows refuses to delete. Cleanup is best effort so a failure
    /// here cannot mask the assertions above.
    /// </summary>
    private static void DeleteWorkspace(DirectoryInfo workspace)
    {
        try
        {
            foreach (var file in workspace.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes = FileAttributes.Normal;
            }

            workspace.Delete(recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing the test for.
        }
    }
}

/// <summary>Runs the git command line client, which is the only way to prove the protocol works end to end.</summary>
internal static class GitCli
{
    public static bool IsAvailable { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            start.ArgumentList.Add("--version");

            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            return process.WaitForExit(30_000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_AUTHOR_NAME"] = "Test";
        start.Environment["GIT_AUTHOR_EMAIL"] = "test@example.com";
        start.Environment["GIT_COMMITTER_NAME"] = "Test";
        start.Environment["GIT_COMMITTER_EMAIL"] = "test@example.com";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("git could not be started.");

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await output, await error);
    }
}

/// <summary>
/// Boots a real Kestrel server with just the git endpoint, because the in-memory test server cannot be reached
/// by an external process.
/// </summary>
internal sealed class GitTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private GitTestHost(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    /// <summary>Git reads credentials from the URL, which avoids depending on an interactive prompt.</summary>
    public string AuthenticatedUrl(string key)
    {
        var uri = new Uri(Url);
        return $"{uri.Scheme}://git:{Uri.EscapeDataString(key)}@{uri.Authority}/git/knowledge.git";
    }

    public static async Task<GitTestHost> StartAsync(KnowledgeBaseApp app)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<IDirectoryRepository>(app.Directories);
        builder.Services.AddSingleton<IDocumentRepository>(app.Documents);
        builder.Services.AddSingleton<IApiKeyRepository>(app.Keys);
        builder.Services.AddSingleton<KnowledgeSnapshotLoader>();
        builder.Services.AddSingleton<ApiKeyService>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddOptions<GitOptions>()
            .Configure(options => options.Enabled = true);

        var application = builder.Build();
        application.MapKnowledgeGit();

        await application.StartAsync();

        return new GitTestHost(application, application.Urls.First());
    }
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
