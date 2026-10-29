using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AgentKnowledgeBase.Mcp.Domain;

namespace AgentKnowledgeBase.Tests;

public sealed class DocumentTransferApiTests
{
    [Fact]
    public async Task Export_returns_a_zip_with_the_manifest_and_markdown_files()
    {
        using var app = new KnowledgeBaseApp();
        var directory = SeedDirectory(app, "team");
        using var client = app.CreateAdminClient();
        await CreateDocumentAsync(client, directory.Id, "EF Core Tips", "# tips", "query notes");

        var response = await client.PostAsJsonAsync(
            "/admin/transfer/export",
            new { directoryIds = new[] { directory.Id } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Content.Headers.ContentDisposition?.FileName);

        var entries = ReadEntries(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("manifest.json", entries.Keys);
        Assert.Contains("team/EF Core Tips.md", entries.Keys);
        Assert.Equal("# tips", entries["team/EF Core Tips.md"]);
    }

    [Fact]
    public async Task Export_requires_the_admin_token()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateClient();

        var response = await client.PostAsJsonAsync("/admin/transfer/export", new { directoryIds = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Export_without_directories_returns_bad_request()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateAdminClient();

        var response = await client.PostAsJsonAsync("/admin/transfer/export", new { directoryIds = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Export_of_an_unknown_directory_returns_not_found()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/admin/transfer/export",
            new { directoryIds = new[] { Guid.NewGuid() } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Import_creates_directories_and_documents_from_a_manifest_less_archive()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateAdminClient();
        using var upload = CreateUpload(CreateZip(("team/notes.md", "# imported")));

        var response = await client.PostAsync("/admin/transfer/import", upload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportResponse>();
        Assert.NotNull(result);
        Assert.Equal(1, result.DirectoriesCreated);
        Assert.Equal(1, result.DocumentsCreated);
        Assert.Equal(0, result.DocumentsUpdated);
        Assert.Equal(0, result.DocumentsSkipped);

        var stored = Assert.Single(app.Documents.Documents.Values);
        Assert.Equal("notes", stored.Name);
        Assert.Equal("# imported", stored.Content);
    }

    [Fact]
    public async Task Import_overwrites_existing_documents_by_default()
    {
        using var app = new KnowledgeBaseApp();
        var directory = SeedDirectory(app, "team");
        using var client = app.CreateAdminClient();
        await CreateDocumentAsync(client, directory.Id, "notes", "old body");

        using var upload = CreateUpload(CreateZip(("team/notes.md", "new body")));
        var response = await client.PostAsync("/admin/transfer/import", upload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportResponse>();
        Assert.NotNull(result);
        Assert.Equal(0, result.DocumentsCreated);
        Assert.Equal(1, result.DocumentsUpdated);

        var stored = Assert.Single(app.Documents.Documents.Values);
        Assert.Equal("new body", stored.Content);
        Assert.Equal(2, stored.Version);
    }

    [Fact]
    public async Task Import_with_conflict_skip_keeps_the_stored_document()
    {
        using var app = new KnowledgeBaseApp();
        var directory = SeedDirectory(app, "team");
        using var client = app.CreateAdminClient();
        await CreateDocumentAsync(client, directory.Id, "notes", "old body");

        using var upload = CreateUpload(CreateZip(("team/notes.md", "new body")));
        var response = await client.PostAsync("/admin/transfer/import?conflict=skip", upload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ImportResponse>();
        Assert.NotNull(result);
        Assert.Equal(1, result.DocumentsSkipped);
        Assert.Equal("old body", Assert.Single(app.Documents.Documents.Values).Content);
    }

    [Fact]
    public async Task Import_with_conflict_fail_returns_conflict()
    {
        using var app = new KnowledgeBaseApp();
        var directory = SeedDirectory(app, "team");
        using var client = app.CreateAdminClient();
        await CreateDocumentAsync(client, directory.Id, "notes", "old body");

        using var upload = CreateUpload(CreateZip(("team/notes.md", "new body")));
        var response = await client.PostAsync("/admin/transfer/import?conflict=fail", upload);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("old body", Assert.Single(app.Documents.Documents.Values).Content);
    }

    [Fact]
    public async Task Import_rejects_an_unknown_conflict_mode()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateAdminClient();
        using var upload = CreateUpload(CreateZip(("team/notes.md", "body")));

        var response = await client.PostAsync("/admin/transfer/import?conflict=merge", upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_rejects_a_file_that_is_not_a_zip()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateAdminClient();
        using var upload = CreateUpload(Encoding.UTF8.GetBytes("not a zip"));

        var response = await client.PostAsync("/admin/transfer/import", upload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Documents.Documents);
    }

    [Fact]
    public async Task Import_rejects_a_request_without_a_file()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateAdminClient();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("missing"), "note");

        var response = await client.PostAsync("/admin/transfer/import", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Import_requires_the_admin_token()
    {
        using var app = new KnowledgeBaseApp();
        using var client = app.CreateClient();
        using var upload = CreateUpload(CreateZip(("team/notes.md", "body")));

        var response = await client.PostAsync("/admin/transfer/import", upload);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Exported_archive_can_be_imported_into_another_instance()
    {
        using var source = new KnowledgeBaseApp();
        var directory = SeedDirectory(source, "team");
        using var sourceClient = source.CreateAdminClient();
        await CreateDocumentAsync(sourceClient, directory.Id, "EF Core Tips", "# tips", "query notes");

        var exportResponse = await sourceClient.PostAsJsonAsync(
            "/admin/transfer/export",
            new { directoryIds = new[] { directory.Id } });
        var archive = await exportResponse.Content.ReadAsByteArrayAsync();

        using var target = new KnowledgeBaseApp();
        using var targetClient = target.CreateAdminClient();
        using var upload = CreateUpload(archive);
        var importResponse = await targetClient.PostAsync("/admin/transfer/import", upload);

        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);
        var result = await importResponse.Content.ReadFromJsonAsync<ImportResponse>();
        Assert.NotNull(result);
        Assert.Equal(1, result.DirectoriesCreated);
        Assert.Equal(1, result.DocumentsCreated);

        var restored = Assert.Single(target.Documents.Documents.Values);
        Assert.Equal("EF Core Tips", restored.Name);
        Assert.Equal("# tips", restored.Content);
        Assert.Equal("query notes", restored.Summary);
        Assert.Equal("team", Assert.Single(target.Directories.Directories.Values).Name);
    }

    private static async Task CreateDocumentAsync(
        HttpClient client,
        Guid directoryId,
        string name,
        string content,
        string? summary = null)
    {
        var response = await client.PostAsJsonAsync("/admin/documents", new
        {
            directoryId,
            name,
            summary,
            content
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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

    private static MultipartFormDataContent CreateUpload(byte[] archive, string fileName = "knowledge.zip")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(archive);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", fileName);
        return form;
    }

    private static byte[] CreateZip(params (string Path, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static Dictionary<string, string> ReadEntries(byte[] archive)
    {
        using var buffer = new MemoryStream(archive);
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(entry => entry.FullName, entry =>
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    private sealed record ImportResponse(
        int DirectoriesCreated,
        int DocumentsCreated,
        int DocumentsUpdated,
        int DocumentsSkipped);
}
