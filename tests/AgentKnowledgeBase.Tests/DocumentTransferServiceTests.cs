using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AgentKnowledgeBase.Mcp;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using Microsoft.Extensions.Options;

namespace AgentKnowledgeBase.Tests;

public class DocumentTransferServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FakeDirectoryRepository _directoryRepo = new();
    private readonly FakeDocumentRepository _documentRepo = new();
    private readonly DirectoryService _directories;
    private readonly KnowledgeService _knowledge;
    private readonly TransferOptions _options = new();
    private readonly DocumentTransferService _transfer;

    public DocumentTransferServiceTests()
    {
        _directories = new DirectoryService(_directoryRepo, _documentRepo);
        _knowledge = new KnowledgeService(_documentRepo, _directoryRepo);
        _transfer = new DocumentTransferService(
            _directoryRepo,
            _documentRepo,
            _directories,
            _knowledge,
            Options.Create(_options));
    }

    private static ApiCaller Caller(params KeyScope[] scopes) => new(Guid.NewGuid(), "tester", scopes);

    private static KeyScope Rw(Guid directoryId) => new() { DirectoryId = directoryId, Access = AccessLevel.ReadWrite };

    private async Task<Guid> CreateDirectoryAsync(string name)
    {
        var directory = await _directories.CreateAsync(name, $"{name} documents", "admin", default);
        return directory.Id;
    }

    private async Task<Guid> CreateDocumentAsync(
        Guid directoryId,
        string name,
        string content,
        string summary = "",
        params string[] tags)
    {
        var result = await _knowledge.StoreAsync(
            Caller(Rw(directoryId)),
            new StoreRequest(null, directoryId, name, summary, tags, content, null),
            default);

        return result.Id;
    }

    private Task<ImportResult> ImportAsync(byte[] archive, ImportConflictMode mode = ImportConflictMode.Overwrite)
    {
        using var stream = new MemoryStream(archive);
        return _transfer.ImportAsync(stream, mode, default);
    }

    private static byte[] CreateZip(params (string Path, string? Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                if (content is null)
                {
                    continue;
                }

                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
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

    private async Task<byte[]> ExportAsync(IReadOnlyCollection<Guid> directoryIds)
    {
        var plan = await _transfer.PlanExportAsync(directoryIds, default);
        using var buffer = new MemoryStream();
        await _transfer.WriteArchiveAsync(plan, buffer, default);
        return buffer.ToArray();
    }

    private static TransferManifest ReadManifest(byte[] archive) =>
        JsonSerializer.Deserialize<TransferManifest>(ReadEntries(archive)[DocumentTransferService.ManifestEntryName], JsonOptions)!;

    [Fact]
    public async Task Export_WritesManifestAndOneMarkdownFilePerDocument()
    {
        var team = await CreateDirectoryAsync("team");
        await CreateDocumentAsync(team, "EF Core Tips", "# tips", "query notes", "dotnet", "efcore");
        await CreateDocumentAsync(team, "Deploy Notes", "# deploy");

        var archive = await ExportAsync([team]);
        var entries = ReadEntries(archive);
        var manifest = ReadManifest(archive);

        Assert.Contains("team/EF Core Tips.md", entries.Keys);
        Assert.Contains("team/Deploy Notes.md", entries.Keys);
        Assert.Equal("# tips", entries["team/EF Core Tips.md"]);
        Assert.Equal(DocumentTransferService.FormatVersion, manifest.FormatVersion);
        Assert.Equal(2, manifest.Documents!.Count);

        var exported = Assert.Single(manifest.Documents, d => d.Name == "EF Core Tips");
        Assert.Equal("query notes", exported.Summary);
        Assert.Equal(new[] { "dotnet", "efcore" }, exported.Tags);
        Assert.Equal("team", exported.DirectoryName);
        Assert.Equal("team/EF Core Tips.md", exported.File);
    }

    [Fact]
    public async Task Export_OnlyIncludesSelectedDirectories()
    {
        var team = await CreateDirectoryAsync("team");
        var other = await CreateDirectoryAsync("other");
        await CreateDocumentAsync(team, "Team Notes", "team body");
        await CreateDocumentAsync(other, "Other Notes", "other body");

        var archive = await ExportAsync([team]);
        var entries = ReadEntries(archive);
        var manifest = ReadManifest(archive);

        Assert.DoesNotContain(entries.Keys, key => key.StartsWith("other/", StringComparison.Ordinal));
        Assert.Single(manifest.Directories!);
        var document = Assert.Single(manifest.Documents!);
        Assert.Equal("Team Notes", document.Name);
    }

    [Fact]
    public async Task Export_EmptyDirectory_IsStillListedInManifest()
    {
        var team = await CreateDirectoryAsync("team");

        var archive = await ExportAsync([team]);
        var manifest = ReadManifest(archive);

        var directory = Assert.Single(manifest.Directories!);
        Assert.Equal("team", directory.Name);
        Assert.Empty(manifest.Documents!);
    }

    [Fact]
    public async Task Export_SanitizesDocumentNamesIntoFileNames()
    {
        var team = await CreateDirectoryAsync("team");
        await CreateDocumentAsync(team, "guides/setup", "body");

        var archive = await ExportAsync([team]);
        var manifest = ReadManifest(archive);

        var document = Assert.Single(manifest.Documents!);
        Assert.Equal("guides/setup", document.Name);
        Assert.Equal("team/guides-setup.md", document.File);
    }

    [Fact]
    public async Task Export_RequiresAtLeastOneDirectory()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => _transfer.PlanExportAsync([], default));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Export_UnknownDirectory_IsNotFound()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => _transfer.PlanExportAsync([Guid.NewGuid()], default));

        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Import_RoundTripsAnExportedArchive()
    {
        var team = await CreateDirectoryAsync("team");
        await CreateDocumentAsync(team, "EF Core Tips", "# tips", "query notes", "dotnet");
        await CreateDocumentAsync(team, "Deploy Notes", "# deploy");
        var archive = await ExportAsync([team]);

        var sourceDirectories = new FakeDirectoryRepository();
        var sourceDocuments = new FakeDocumentRepository();
        var targetDirectories = new DirectoryService(sourceDirectories, sourceDocuments);
        var target = new DocumentTransferService(
            sourceDirectories,
            sourceDocuments,
            targetDirectories,
            new KnowledgeService(sourceDocuments, sourceDirectories),
            Options.Create(_options));

        var result = await target.ImportAsync(new MemoryStream(archive), ImportConflictMode.Overwrite, default);

        Assert.Equal(1, result.DirectoriesCreated);
        Assert.Equal(2, result.DocumentsCreated);
        Assert.Equal(0, result.DocumentsUpdated);
        Assert.Equal(0, result.DocumentsSkipped);

        var directory = Assert.Single(sourceDirectories.Directories.Values);
        Assert.Equal("team", directory.Name);
        Assert.Equal("team documents", directory.Description);

        var tips = sourceDocuments.Documents.Values.Single(d => d.Name == "EF Core Tips");
        Assert.Equal("# tips", tips.Content);
        Assert.Equal("query notes", tips.Summary);
        Assert.Equal(new[] { "dotnet" }, tips.Tags);
        Assert.Equal("import", tips.CreatedBy);
    }

    [Fact]
    public async Task Import_FromEmptyKnowledgeBase_RestoresDirectoriesAndDocuments()
    {
        var archive = CreateZip(
            ("manifest.json", """
                {
                  "formatVersion": 1,
                  "directories": [{ "name": "team", "description": "team documents" }],
                  "documents": [{
                    "directoryName": "team",
                    "name": "EF Core Tips",
                    "summary": "query notes",
                    "tags": ["dotnet"],
                    "file": "team/EF Core Tips.md"
                  }]
                }
                """),
            ("team/EF Core Tips.md", "# tips"));

        var result = await ImportAsync(archive);

        Assert.Equal(1, result.DirectoriesCreated);
        Assert.Equal(1, result.DocumentsCreated);

        var directory = Assert.Single(_directoryRepo.Directories.Values);
        Assert.Equal("team", directory.Name);
        Assert.Equal("team documents", directory.Description);

        var document = Assert.Single(_documentRepo.Documents.Values);
        Assert.Equal("EF Core Tips", document.Name);
        Assert.Equal("# tips", document.Content);
        Assert.Equal("query notes", document.Summary);
        Assert.Equal(new[] { "dotnet" }, document.Tags);
    }

    [Fact]
    public async Task Import_WithoutManifest_ImportsMarkdownFilesFromTheirFolders()
    {
        var archive = CreateZip(
            ("team/", null),
            ("team/notes.md", "team notes"),
            ("other/deep/readme.md", "deep readme"),
            ("ignored.txt", "not markdown"));

        var result = await ImportAsync(archive);

        Assert.Equal(2, result.DirectoriesCreated);
        Assert.Equal(2, result.DocumentsCreated);
        Assert.Equal(2, _documentRepo.Documents.Count);

        var team = _directoryRepo.Directories.Values.Single(d => d.Name == "team");
        var notes = _documentRepo.Documents.Values.Single(d => d.DirectoryId == team.Id);
        Assert.Equal("notes", notes.Name);
        Assert.Equal("team notes", notes.Content);

        var other = _directoryRepo.Directories.Values.Single(d => d.Name == "other");
        var deep = _documentRepo.Documents.Values.Single(d => d.DirectoryId == other.Id);
        Assert.Equal("deep/readme", deep.Name);
        Assert.Equal("deep readme", deep.Content);
        Assert.Empty(deep.Summary);
        Assert.Empty(deep.Tags);
    }

    [Fact]
    public async Task Import_WithoutManifest_KeepsEmptyDirectories()
    {
        var archive = CreateZip(("team/", null));

        var result = await ImportAsync(archive);

        Assert.Equal(1, result.DirectoriesCreated);
        Assert.Equal(0, result.DocumentsCreated);
        Assert.Equal("team", Assert.Single(_directoryRepo.Directories.Values).Name);
    }

    [Fact]
    public async Task Import_OverwritesExistingDocumentsByDefault()
    {
        var team = await CreateDirectoryAsync("team");
        var documentId = await CreateDocumentAsync(team, "notes", "old body");

        var result = await ImportAsync(CreateZip(("team/notes.md", "new body")));

        Assert.Equal(0, result.DirectoriesCreated);
        Assert.Equal(0, result.DocumentsCreated);
        Assert.Equal(1, result.DocumentsUpdated);
        Assert.Equal(0, result.DocumentsSkipped);

        var stored = Assert.Single(_documentRepo.Documents.Values);
        Assert.Equal(documentId, stored.Id);
        Assert.Equal("new body", stored.Content);
        Assert.Equal(2, stored.Version);
    }

    [Fact]
    public async Task Import_Skip_KeepsExistingDocuments()
    {
        var team = await CreateDirectoryAsync("team");
        await CreateDocumentAsync(team, "notes", "old body");

        var result = await ImportAsync(CreateZip(("team/notes.md", "new body")), ImportConflictMode.Skip);

        Assert.Equal(1, result.DocumentsSkipped);
        Assert.Equal(0, result.DocumentsUpdated);
        Assert.Equal("old body", Assert.Single(_documentRepo.Documents.Values).Content);
    }

    [Fact]
    public async Task Import_Fail_ReportsConflictAndChangesNothing()
    {
        var team = await CreateDirectoryAsync("team");
        await CreateDocumentAsync(team, "notes", "old body");

        // The fresh document and its new directory come first, so they would already be written if the
        // conflict were detected while applying rather than before.
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            ImportAsync(
                CreateZip(("fresh/new.md", "fresh body"), ("team/notes.md", "new body")),
                ImportConflictMode.Fail));

        Assert.Equal(KnowledgeErrorKind.Conflict, ex.Kind);
        Assert.Equal("team", Assert.Single(_directoryRepo.Directories.Values).Name);
        var stored = Assert.Single(_documentRepo.Documents.Values);
        Assert.Equal("notes", stored.Name);
        Assert.Equal("old body", stored.Content);
        Assert.Equal("old body", Assert.Single(_documentRepo.Revisions).Content);
    }

    [Fact]
    public async Task Import_IsCaseInsensitiveWhenMatchingExistingDocuments()
    {
        var team = await CreateDirectoryAsync("team");
        await CreateDocumentAsync(team, "Notes", "old body");

        var result = await ImportAsync(CreateZip(("team/notes.md", "new body")));

        Assert.Equal(1, result.DocumentsUpdated);
        Assert.Equal("new body", Assert.Single(_documentRepo.Documents.Values).Content);
    }

    [Fact]
    public async Task Import_RejectsFilesThatAreNotZipArchives()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            ImportAsync(Encoding.UTF8.GetBytes("this is not a zip")));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Import_RejectsArchiveWithoutUsableContent()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            ImportAsync(CreateZip(("readme.md", "root level markdown"))));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Import_RejectsDuplicateDocumentsInTheSameDirectory()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            ImportAsync(CreateZip(("team/notes.md", "one"), ("team/Notes.md", "two"))));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Import_RejectsManifestReferencingMissingFile()
    {
        var archive = CreateZip(
            ("manifest.json", """
                {
                  "formatVersion": 1,
                  "documents": [{ "directoryName": "team", "name": "notes", "file": "team/notes.md" }]
                }
                """));

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => ImportAsync(archive));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
        Assert.Contains("team/notes.md", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_RejectsManifestPathTraversal()
    {
        var archive = CreateZip(
            ("manifest.json", """
                {
                  "formatVersion": 1,
                  "documents": [{ "directoryName": "team", "name": "notes", "file": "../evil.md" }]
                }
                """));

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => ImportAsync(archive));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Import_RejectsUnsupportedManifestVersion()
    {
        var archive = CreateZip(
            ("manifest.json", """{ "formatVersion": 99, "documents": [] }"""),
            ("team/notes.md", "body"));

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => ImportAsync(archive));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
        Assert.Contains("formatVersion", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_RejectsInvalidDocumentNames()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            ImportAsync(CreateZip(("team/   .md", "body"))));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Import_RejectsArchivesLargerThanTheConfiguredLimit()
    {
        _options.MaxArchiveBytes = 32;

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            ImportAsync(CreateZip(("team/notes.md", new string('x', 500)))));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Import_RejectsArchivesThatExpandBeyondTheConfiguredLimit()
    {
        _options.MaxDecompressedBytes = 64;

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            ImportAsync(CreateZip(("team/notes.md", new string('x', 500)))));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Import_RejectsArchivesWithTooManyDocuments()
    {
        _options.MaxDocuments = 1;

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            ImportAsync(CreateZip(("team/one.md", "one"), ("team/two.md", "two"))));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Import_RecordsARevisionForEveryImportedDocument()
    {
        var archive = CreateZip(("team/notes.md", "body"));

        await ImportAsync(archive);

        var revision = Assert.Single(_documentRepo.Revisions);
        Assert.Equal("body", revision.Content);
        Assert.Equal(1, revision.Version);
    }

    [Fact]
    public async Task Import_WithoutManifest_RejectsMarkdownFilesOutsideAnyDirectory()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            ImportAsync(CreateZip(("readme.md", "root"), ("team/guides.md", "guides"))));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
        Assert.Contains("readme.md", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_documentRepo.Documents);
        Assert.Empty(_directoryRepo.Directories);
    }

    [Fact]
    public async Task Import_WithoutManifest_SlugifiesFolderNames()
    {
        var result = await ImportAsync(CreateZip(("Team Docs/notes.md", "docs"), ("Café/notes.md", "cafe")));

        Assert.Equal(2, result.DirectoriesCreated);
        Assert.Contains(_directoryRepo.Directories.Values, d => d.Name == "team-docs");
        Assert.Contains(_directoryRepo.Directories.Values, d => d.Name == "cafe");
    }

    [Fact]
    public async Task Import_WithoutManifest_RejectsFoldersThatShareADirectoryName()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            ImportAsync(CreateZip(("Team Docs/notes.md", "docs"), ("team-docs/other.md", "other"))));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
        Assert.Contains("team-docs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_StripsTheUtf8ByteOrderMarkFromContent()
    {
        await ImportAsync(CreateZip(("team/notes.md", "\uFEFFbody")));

        Assert.Equal("body", Assert.Single(_documentRepo.Documents.Values).Content);
    }

    [Fact]
    public async Task Import_UpdatesTheDescriptionOfAnExistingDirectory()
    {
        var team = await CreateDirectoryAsync("team");

        var archive = CreateZip(
            ("manifest.json", """
                {
                  "formatVersion": 1,
                  "directories": [{ "name": "team", "description": "updated description" }],
                  "documents": []
                }
                """));

        var result = await ImportAsync(archive);

        Assert.Equal(0, result.DirectoriesCreated);
        var directory = Assert.Single(_directoryRepo.Directories.Values);
        Assert.Equal(team, directory.Id);
        Assert.Equal("updated description", directory.Description);
    }

    [Fact]
    public async Task Import_ClearsAnExistingDirectoryDescriptionWhenTheManifestSaysItIsEmpty()
    {
        await CreateDirectoryAsync("team");

        var archive = CreateZip(
            ("manifest.json", """
                {
                  "formatVersion": 1,
                  "directories": [{ "name": "team", "description": "" }],
                  "documents": []
                }
                """));

        await ImportAsync(archive);

        Assert.Equal("", Assert.Single(_directoryRepo.Directories.Values).Description);
    }

    [Fact]
    public async Task Import_WithoutManifest_LeavesExistingDirectoryDescriptionsAlone()
    {
        await CreateDirectoryAsync("team");

        await ImportAsync(CreateZip(("team/notes.md", "body")));

        Assert.Equal("team documents", Assert.Single(_directoryRepo.Directories.Values).Description);
    }

    [Fact]
    public async Task Import_RejectsManifestNamesTheDatabaseTreatsAsDuplicates()
    {
        // 'ﬁ' and 'fi' are distinct strings but equal under the ICU 'en' secondary collation of the
        // document name index, so the archive must be rejected before anything is stored.
        var archive = CreateZip(
            ("manifest.json", """
                {
                  "formatVersion": 1,
                  "documents": [
                    { "directoryName": "team", "name": "\uFB01le", "file": "team/a.md" },
                    { "directoryName": "team", "name": "file", "file": "team/b.md" }
                  ]
                }
                """),
            ("team/a.md", "one"),
            ("team/b.md", "two"));

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => ImportAsync(archive));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
        Assert.Empty(_documentRepo.Documents);
    }

    [Fact]
    public async Task Import_RejectsArchivesWithTooManyDocumentsWhenAManifestDeclaresThem()
    {
        _options.MaxDocuments = 1;

        var archive = CreateZip(
            ("manifest.json", """
                {
                  "formatVersion": 1,
                  "documents": [
                    { "directoryName": "team", "name": "one", "file": "team/one.md" },
                    { "directoryName": "team", "name": "two", "file": "team/two.md" }
                  ]
                }
                """),
            ("team/one.md", "one"),
            ("team/two.md", "two"));

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => ImportAsync(archive));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Export_RejectsSelectionsLargerThanTheConfiguredLimit()
    {
        var team = await CreateDirectoryAsync("team");
        await CreateDocumentAsync(team, "one", "one");
        await CreateDocumentAsync(team, "two", "two");
        _options.MaxExportDocuments = 1;

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => _transfer.PlanExportAsync([team], default));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Export_StreamsContentThatWasNotReadWhilePlanning()
    {
        var team = await CreateDirectoryAsync("team");
        await CreateDocumentAsync(team, "notes", "body");

        var plan = await _transfer.PlanExportAsync([team], default);

        // The fake mirrors Mongo: summaries carry no content, so the body below can only come from the stream.
        Assert.All(await _documentRepo.ListSummariesAsync([team], default), s => Assert.Empty(s.Content));

        await using var destination = new MemoryStream();
        await _transfer.WriteArchiveAsync(plan, destination, default);

        Assert.Single(plan.Documents);
        Assert.Equal("body", ReadEntries(destination.ToArray())["team/notes.md"]);
    }
}
