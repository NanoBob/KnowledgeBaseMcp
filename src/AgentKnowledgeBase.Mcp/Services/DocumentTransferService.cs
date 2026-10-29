using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Storage;
using Microsoft.Extensions.Options;

namespace AgentKnowledgeBase.Mcp.Services;

/// <summary>The manifest.json payload written into an exported archive.</summary>
public sealed record TransferManifest(
    int? FormatVersion,
    DateTime? ExportedAt,
    List<TransferDirectory>? Directories,
    List<TransferDocument>? Documents);

public sealed record TransferDirectory(Guid? Id, string? Name, string? Description);

public sealed record TransferDocument(
    Guid? Id,
    Guid? DirectoryId,
    string? DirectoryName,
    string? Name,
    string? Summary,
    string[]? Tags,
    string? File,
    int? Version,
    DateTime? CreatedAt,
    DateTime? UpdatedAt,
    string? CreatedBy,
    string? UpdatedBy);

/// <summary>What to do when an imported document already exists.</summary>
public enum ImportConflictMode
{
    /// <summary>Replace the stored document with the imported one.</summary>
    Overwrite,

    /// <summary>Keep the stored document.</summary>
    Skip,

    /// <summary>Reject the whole import.</summary>
    Fail
}

public sealed record ImportResult(int DirectoriesCreated, int DocumentsCreated, int DocumentsUpdated, int DocumentsSkipped);

/// <summary>
/// Moves documents between the knowledge base and .zip archives. Archives contain a manifest.json with the
/// full document metadata plus one markdown file per document. Archives without a manifest are also accepted,
/// in which case the first path segment of every markdown file is treated as the directory name.
/// </summary>
public sealed class DocumentTransferService(
    IDirectoryRepository directories,
    IDocumentRepository documents,
    DirectoryService directoryService,
    KnowledgeService knowledge,
    IOptions<TransferOptions> options)
{
    public const int FormatVersion = 1;
    public const string ManifestEntryName = "manifest.json";

    /// <summary>Imports have no caller, so the recorded author names the origin.</summary>
    private const string ImportAuthor = "import";

    private static readonly ApiCaller ImportCaller = new(Guid.Empty, ImportAuthor, [], IsAdmin: true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly char[] InvalidFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private TransferOptions Options => options.Value;

    /// <summary>
    /// Builds a .zip with the documents of the selected directories. The directories themselves are always
    /// included so an empty directory still round-trips.
    /// </summary>
    public async Task<byte[]> ExportAsync(
        IReadOnlyCollection<Guid>? directoryIds,
        CancellationToken ct)
    {
        var ids = (directoryIds ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            throw KnowledgeException.Validation("select at least one directory to export.");
        }

        var selected = await directories.ListByIdsAsync(ids, ct);
        var missing = ids.Except(selected.Select(d => d.Id)).ToList();
        if (missing.Count > 0)
        {
            throw KnowledgeException.NotFound($"directories not found: {string.Join(", ", missing)}.");
        }

        var directoryNames = selected.ToDictionary(d => d.Id, d => d.Name);
        var exportable = await documents.ListByDirectoryIdsAsync(ids, ct);

        var files = new List<(string Path, string Content)>();
        var manifestDocuments = new List<TransferDocument>();
        var usedFileNames = new Dictionary<Guid, HashSet<string>>();

        foreach (var document in exportable
                     .OrderBy(d => directoryNames[d.DirectoryId], StringComparer.Ordinal)
                     .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(d => d.Id))
        {
            var directoryName = directoryNames[document.DirectoryId];
            if (!usedFileNames.TryGetValue(document.DirectoryId, out var used))
            {
                used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                usedFileNames[document.DirectoryId] = used;
            }

            var path = $"{directoryName}/{UniqueFileName(document.Name, document.Id, used)}.md";
            files.Add((path, document.Content));
            manifestDocuments.Add(new TransferDocument(
                document.Id,
                document.DirectoryId,
                directoryName,
                document.Name,
                document.Summary,
                [.. document.Tags],
                path,
                document.Version,
                document.CreatedAt,
                document.UpdatedAt,
                document.CreatedBy,
                document.UpdatedBy));
        }

        var manifest = new TransferManifest(
            FormatVersion,
            DateTime.UtcNow,
            [.. selected
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .Select(d => new TransferDirectory(d.Id, d.Name, d.Description))],
            manifestDocuments);

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            await using (var manifestStream = manifestEntry.Open())
            {
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, ct);
            }

            foreach (var (path, content) in files)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var writer = new StreamWriter(entryStream, StrictUtf8);
                await writer.WriteAsync(content);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Imports every document of the archive. A manifest.json drives the import when present; otherwise each
    /// markdown file is imported into a directory named after the folder that holds it.
    /// </summary>
    public async Task<ImportResult> ImportAsync(
        Stream archiveStream,
        ImportConflictMode conflictMode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);

        var archiveBytes = await ReadBoundedAsync(
            archiveStream, Options.MaxArchiveBytes, "the uploaded archive", ct);

        using var buffer = new MemoryStream(archiveBytes);
        using var archive = OpenArchive(buffer);

        var manifestEntry = archive.Entries.FirstOrDefault(entry =>
            string.Equals(NormalizeSeparators(entry.FullName), ManifestEntryName, StringComparison.OrdinalIgnoreCase));

        var plan = manifestEntry is null
            ? await BuildPlanFromFilesAsync(archive, ct)
            : await BuildPlanFromManifestAsync(archive, manifestEntry, ct);

        return await ApplyPlanAsync(plan, conflictMode, ct);
    }

    private static ZipArchive OpenArchive(Stream stream)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw KnowledgeException.Validation("the uploaded file is not a valid .zip archive.");
        }
    }

    private async Task<ImportPlan> BuildPlanFromManifestAsync(
        ZipArchive archive,
        ZipArchiveEntry manifestEntry,
        CancellationToken ct)
    {
        var manifestBytes = await ReadBoundedAsync(
            manifestEntry.Open(), Options.MaxDecompressedBytes, ManifestEntryName, ct);

        TransferManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<TransferManifest>(manifestBytes, JsonOptions)
                ?? throw new JsonException("the manifest is empty.");
        }
        catch (JsonException)
        {
            throw KnowledgeException.Validation($"{ManifestEntryName} is not valid JSON.");
        }

        if (manifest.FormatVersion != FormatVersion)
        {
            throw KnowledgeException.Validation(
                $"unsupported manifest formatVersion '{manifest.FormatVersion}'. Expected {FormatVersion}.");
        }

        var plannedDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in manifest.Directories ?? [])
        {
            var name = DirectoryRules.NormalizeName(directory.Name);
            if (!plannedDirectories.TryAdd(name, DirectoryRules.NormalizeDescription(directory.Description)))
            {
                throw KnowledgeException.Validation($"{ManifestEntryName} declares directory '{name}' more than once.");
            }
        }

        var plannedDocuments = new List<PlannedDocument>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0;

        foreach (var document in manifest.Documents ?? [])
        {
            var directoryName = DirectoryRules.NormalizeName(document.DirectoryName);
            var name = DocumentRules.NormalizeName(document.Name);
            var path = NormalizeEntryPath(document.File);
            var entry = FindEntry(archive, path)
                ?? throw KnowledgeException.Validation($"{ManifestEntryName} references the missing file '{path}'.");

            var contentBytes = await ReadBoundedAsync(
                entry.Open(), DocumentRules.MaxContentBytes, $"document '{name}'", ct);
            totalBytes += contentBytes.Length;
            EnsureDecompressedLimit(totalBytes);

            if (!seen.Add($"{directoryName}\u0000{name}"))
            {
                throw KnowledgeException.Validation(
                    $"{ManifestEntryName} declares document '{name}' in directory '{directoryName}' more than once.");
            }

            plannedDirectories.TryAdd(directoryName, "");
            plannedDocuments.Add(new PlannedDocument(
                directoryName,
                name,
                DocumentRules.NormalizeSummary(document.Summary),
                TagRules.Normalize(document.Tags),
                DocumentRules.NormalizeContent(DecodeUtf8(contentBytes, $"document '{name}'"))));
        }

        EnsureDocumentLimit(plannedDocuments.Count);

        return new ImportPlan(
            [.. plannedDirectories.Select(pair => new PlannedDirectory(pair.Key, pair.Value))],
            plannedDocuments);
    }

    private async Task<ImportPlan> BuildPlanFromFilesAsync(ZipArchive archive, CancellationToken ct)
    {
        var plannedDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var plannedDocuments = new List<PlannedDocument>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0;

        foreach (var entry in archive.Entries)
        {
            var fullName = NormalizeSeparators(entry.FullName).TrimStart('/');
            if (fullName.Length == 0)
            {
                continue;
            }

            if (entry.Name.Length == 0)
            {
                // Directory entry: keep it so an empty directory still exists after the import.
                var directoryName = DirectoryRules.NormalizeName(Segments(fullName)[0]);
                plannedDirectories.TryAdd(directoryName, "");
                continue;
            }

            if (!fullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var segments = Segments(fullName);
            if (segments.Length < 2)
            {
                // A markdown file at the archive root has no directory to belong to.
                continue;
            }

            var documentDirectory = DirectoryRules.NormalizeName(segments[0]);
            var name = DocumentRules.NormalizeName(string.Join('/', segments[1..])[..^3]);

            var contentBytes = await ReadBoundedAsync(
                entry.Open(), DocumentRules.MaxContentBytes, $"document '{name}'", ct);
            totalBytes += contentBytes.Length;
            EnsureDecompressedLimit(totalBytes);

            if (!seen.Add($"{documentDirectory}\u0000{name}"))
            {
                throw KnowledgeException.Validation(
                    $"the archive contains document '{name}' in directory '{documentDirectory}' more than once.");
            }

            plannedDirectories.TryAdd(documentDirectory, "");
            plannedDocuments.Add(new PlannedDocument(
                documentDirectory,
                name,
                "",
                [],
                DocumentRules.NormalizeContent(DecodeUtf8(contentBytes, $"document '{name}'"))));
        }

        EnsureDocumentLimit(plannedDocuments.Count);
        if (plannedDirectories.Count == 0)
        {
            throw KnowledgeException.Validation(
                "the archive contains neither a manifest.json nor markdown files inside directories.");
        }

        return new ImportPlan(
            [.. plannedDirectories.Select(pair => new PlannedDirectory(pair.Key, pair.Value))],
            plannedDocuments);
    }

    private async Task<ImportResult> ApplyPlanAsync(
        ImportPlan plan,
        ImportConflictMode conflictMode,
        CancellationToken ct)
    {
        var directoryIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in await directories.ListAsync(ct))
        {
            directoryIds[directory.Name] = directory.Id;
        }

        if (conflictMode == ImportConflictMode.Fail)
        {
            await ThrowOnConflictsAsync(plan, directoryIds, ct);
        }

        var directoriesCreated = 0;
        foreach (var planned in plan.Directories)
        {
            if (directoryIds.ContainsKey(planned.Name))
            {
                continue;
            }

            var created = await directoryService.CreateAsync(planned.Name, planned.Description, ImportAuthor, ct);
            directoryIds[created.Name] = created.Id;
            directoriesCreated++;
        }

        var documentsCreated = 0;
        var documentsUpdated = 0;
        var documentsSkipped = 0;

        foreach (var planned in plan.Documents)
        {
            var directoryId = directoryIds[planned.DirectoryName];
            var existing = await documents.FindByNameAsync(directoryId, planned.Name, ct);

            if (existing is null)
            {
                await knowledge.StoreAsync(
                    ImportCaller,
                    new StoreRequest(null, directoryId, planned.Name, planned.Summary, planned.Tags, planned.Content, null),
                    ct);
                documentsCreated++;
                continue;
            }

            switch (conflictMode)
            {
                case ImportConflictMode.Skip:
                    documentsSkipped++;
                    break;

                case ImportConflictMode.Fail:
                    throw KnowledgeException.Conflict(
                        $"a document named '{planned.Name}' already exists in directory '{planned.DirectoryName}'.");

                default:
                    await knowledge.StoreAsync(
                        ImportCaller,
                        new StoreRequest(
                            existing.Id,
                            directoryId,
                            planned.Name,
                            planned.Summary,
                            planned.Tags,
                            planned.Content,
                            existing.Version),
                        ct);
                    documentsUpdated++;
                    break;
            }
        }

        return new ImportResult(directoriesCreated, documentsCreated, documentsUpdated, documentsSkipped);
    }

    private async Task ThrowOnConflictsAsync(
        ImportPlan plan,
        IReadOnlyDictionary<string, Guid> directoryIds,
        CancellationToken ct)
    {
        foreach (var group in plan.Documents.GroupBy(d => d.DirectoryName, StringComparer.OrdinalIgnoreCase))
        {
            if (!directoryIds.TryGetValue(group.Key, out var directoryId))
            {
                continue;
            }

            var stored = (await documents.ListSummariesAsync([directoryId], ct))
                .Select(d => d.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var conflicts = group
                .Where(d => stored.Contains(d.Name))
                .Select(d => d.Name)
                .ToList();

            if (conflicts.Count > 0)
            {
                throw KnowledgeException.Conflict(
                    $"the import would overwrite the existing document(s) {string.Join(", ", conflicts)} " +
                    $"in directory '{group.Key}'. Use conflict=skip or conflict=overwrite.");
            }
        }
    }

    private async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maxBytes,
        string description,
        CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;

        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw KnowledgeException.Validation(
                    $"{description} exceeds the maximum allowed size of {maxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private void EnsureDecompressedLimit(int totalBytes)
    {
        if (totalBytes > Options.MaxDecompressedBytes)
        {
            throw KnowledgeException.Validation(
                $"the archive expands beyond the maximum allowed size of {Options.MaxDecompressedBytes} bytes.");
        }
    }

    private void EnsureDocumentLimit(int documentCount)
    {
        if (documentCount > Options.MaxDocuments)
        {
            throw KnowledgeException.Validation(
                $"the archive contains more than {Options.MaxDocuments} documents.");
        }
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path) =>
        archive.GetEntry(path)
        ?? archive.Entries.FirstOrDefault(entry =>
            string.Equals(NormalizeSeparators(entry.FullName), path, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeEntryPath(string? path)
    {
        var normalized = NormalizeSeparators(path?.Trim() ?? "");
        if (normalized.Length == 0)
        {
            throw KnowledgeException.Validation($"{ManifestEntryName} contains a document without a file path.");
        }

        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            throw KnowledgeException.Validation(
                $"{ManifestEntryName} file path '{path}' must be relative to the archive root.");
        }

        var segments = Segments(normalized);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw KnowledgeException.Validation(
                $"{ManifestEntryName} file path '{path}' must not contain '.' or '..' segments.");
        }

        return string.Join('/', segments);
    }

    private static string[] Segments(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizeSeparators(string value) =>
        value.Replace('\\', '/');

    private static string DecodeUtf8(byte[] bytes, string description)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw KnowledgeException.Validation($"{description} is not valid UTF-8.");
        }
    }

    private static string UniqueFileName(string name, Guid id, HashSet<string> used)
    {
        var baseName = SanitizeFileName(name, id);
        if (used.Add(baseName))
        {
            return baseName;
        }

        var suffix = id.ToString("N")[..8];
        var candidate = $"{baseName}-{suffix}";
        var attempt = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{baseName}-{suffix}-{attempt++}";
        }

        return candidate;
    }

    private static string SanitizeFileName(string name, Guid id)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsControl(character) || InvalidFileNameChars.Contains(character) ? '-' : character);
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.', ' ');
        if (sanitized.Length == 0)
        {
            sanitized = "document";
        }

        if (sanitized.Length > 80)
        {
            sanitized = sanitized[..80].TrimEnd('.', ' ');
        }

        return ReservedFileNames.Contains(sanitized) ? $"{sanitized}-{id.ToString("N")[..8]}" : sanitized;
    }

    private sealed record ImportPlan(
        IReadOnlyList<PlannedDirectory> Directories,
        IReadOnlyList<PlannedDocument> Documents);

    private sealed record PlannedDirectory(string Name, string Description);

    private sealed record PlannedDocument(
        string DirectoryName,
        string Name,
        string Summary,
        IReadOnlyList<string> Tags,
        string Content);
}
