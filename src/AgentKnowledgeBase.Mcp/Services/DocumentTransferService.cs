using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Storage;
using Microsoft.Extensions.Options;

namespace AgentKnowledgeBase.Mcp.Services;

/// <summary>
/// The manifest.json payload written into an exported archive. It carries what an import restores: the
/// directory descriptions and each document's name, summary, tags and file. Ids, versions, timestamps and
/// authors belong to the target knowledge base, so they are deliberately not part of the format.
/// </summary>
public sealed record TransferManifest(
    int? FormatVersion,
    DateTime? ExportedAt,
    List<TransferDirectory>? Directories,
    List<TransferDocument>? Documents);

public sealed record TransferDirectory(string? Name, string? Description);

public sealed record TransferDocument(
    string? DirectoryName,
    string? Name,
    string? Summary,
    string[]? Tags,
    string? File);

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
/// Everything an export needs before the first byte reaches the response, so a rejected export still
/// produces a normal error response instead of a truncated download.
/// </summary>
public sealed record ExportPlan(
    IReadOnlyList<Guid> DirectoryIds,
    IReadOnlyList<TransferDirectory> Directories,
    IReadOnlyList<TransferDocument> Documents,
    IReadOnlyDictionary<Guid, string> FileNames,
    DateTime ExportedAt);

/// <summary>
/// Moves documents between the knowledge base and .zip archives. Archives contain a manifest.json describing
/// the directories and documents plus one markdown file per document. Archives without a manifest are also
/// accepted, in which case the first path segment of every markdown file is treated as the directory name.
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

    /// <summary>
    /// Matches names the way the document index does. The Mongo unique index uses the ICU 'en' collation at
    /// secondary strength, so plain ordinal comparison would let through pairs the database treats as equal.
    /// </summary>
    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("en"), CompareOptions.IgnoreCase);

    private static readonly char[] InvalidFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private TransferOptions Options => options.Value;

    /// <summary>
    /// Resolves an export and validates it before any byte is written. The directories themselves are always
    /// included so an empty directory still round-trips. Document content is not read here.
    /// </summary>
    public async Task<ExportPlan> PlanExportAsync(
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
        var summaries = await documents.ListSummariesAsync(ids, ct);
        if (summaries.Count > Options.MaxExportDocuments)
        {
            throw KnowledgeException.Validation(
                $"the selection contains more than {Options.MaxExportDocuments} documents. " +
                "Export fewer directories at a time.");
        }

        var manifestDocuments = new List<TransferDocument>(summaries.Count);
        var fileNames = new Dictionary<Guid, string>(summaries.Count);
        var usedFileNames = new Dictionary<Guid, HashSet<string>>();

        foreach (var document in summaries
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
            fileNames[document.Id] = path;
            manifestDocuments.Add(new TransferDocument(
                directoryName,
                document.Name,
                document.Summary,
                [.. document.Tags],
                path));
        }

        return new ExportPlan(
            [.. ids],
            [.. selected
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .Select(d => new TransferDirectory(d.Name, d.Description))],
            manifestDocuments,
            fileNames,
            DateTime.UtcNow);
    }

    /// <summary>
    /// Writes the planned archive to <paramref name="destination"/>, streaming one document at a time so the
    /// archive is never buffered whole.
    /// </summary>
    public async Task WriteArchiveAsync(ExportPlan plan, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destination);

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
        await using (var manifestStream = manifestEntry.Open())
        {
            await JsonSerializer.SerializeAsync(
                manifestStream,
                new TransferManifest(FormatVersion, plan.ExportedAt, [.. plan.Directories], [.. plan.Documents]),
                JsonOptions,
                ct);
        }

        await foreach (var document in documents
                           .StreamByDirectoryIdsAsync(plan.DirectoryIds, ct)
                           .WithCancellation(ct))
        {
            if (!plan.FileNames.TryGetValue(document.Id, out var path))
            {
                // The document was deleted between planning and streaming; leaving it out keeps the archive valid.
                continue;
            }

            var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(entryStream, StrictUtf8);
            await writer.WriteAsync(document.Content.AsMemory(), ct);
        }
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

        // A null description means the archive says nothing about it, so an existing directory keeps its own.
        var plannedDirectories = new Dictionary<string, string?>(NameComparer);
        foreach (var directory in manifest.Directories ?? [])
        {
            var name = DirectoryRules.NormalizeName(directory.Name);
            var description = directory.Description is null
                ? null
                : DirectoryRules.NormalizeDescription(directory.Description);
            if (!plannedDirectories.TryAdd(name, description))
            {
                throw KnowledgeException.Validation($"{ManifestEntryName} declares directory '{name}' more than once.");
            }
        }

        EnsureDocumentLimit(manifest.Documents?.Count ?? 0);

        var plannedDocuments = new List<PlannedDocument>();
        var seen = new HashSet<string>(NameComparer);
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

            plannedDirectories.TryAdd(directoryName, null);
            plannedDocuments.Add(new PlannedDocument(
                directoryName,
                name,
                DocumentRules.NormalizeSummary(document.Summary),
                TagRules.Normalize(document.Tags),
                DocumentRules.NormalizeContent(DecodeUtf8(contentBytes, $"document '{name}'"))));
        }

        return new ImportPlan(
            [.. plannedDirectories.Select(pair => new PlannedDirectory(pair.Key, pair.Value))],
            plannedDocuments);
    }

    private async Task<ImportPlan> BuildPlanFromFilesAsync(ZipArchive archive, CancellationToken ct)
    {
        var plannedDirectories = new Dictionary<string, string?>(NameComparer);
        var plannedDocuments = new List<PlannedDocument>();
        var seen = new HashSet<string>(NameComparer);
        var slugOwners = new Dictionary<string, string>(NameComparer);
        var rootMarkdown = new List<string>();
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
                var directoryName = ResolveDirectoryName(Segments(fullName)[0], slugOwners);
                plannedDirectories.TryAdd(directoryName, null);
                continue;
            }

            if (!fullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var segments = Segments(fullName);
            if (segments.Length < 2)
            {
                // A markdown file at the archive root has no directory to belong to; report it rather than
                // dropping it silently.
                rootMarkdown.Add(fullName);
                continue;
            }

            var documentDirectory = ResolveDirectoryName(segments[0], slugOwners);
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

            plannedDirectories.TryAdd(documentDirectory, null);
            plannedDocuments.Add(new PlannedDocument(
                documentDirectory,
                name,
                "",
                [],
                DocumentRules.NormalizeContent(DecodeUtf8(contentBytes, $"document '{name}'"))));
            EnsureDocumentLimit(plannedDocuments.Count);
        }

        if (rootMarkdown.Count > 0)
        {
            var listed = string.Join(", ", rootMarkdown.Take(5));
            var more = rootMarkdown.Count > 5 ? $" and {rootMarkdown.Count - 5} more" : "";
            throw KnowledgeException.Validation(
                $"the archive contains markdown files outside any directory: {listed}{more}. " +
                "Put every file in a folder named after the directory it belongs to.");
        }

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
        var directoryIds = new Dictionary<string, Guid>(NameComparer);
        var directoryDescriptions = new Dictionary<string, string>(NameComparer);
        foreach (var directory in await directories.ListAsync(ct))
        {
            directoryIds[directory.Name] = directory.Id;
            directoryDescriptions[directory.Name] = directory.Description;
        }

        if (conflictMode == ImportConflictMode.Fail)
        {
            await ThrowOnConflictsAsync(plan, directoryIds, ct);
        }

        var directoriesCreated = 0;
        foreach (var planned in plan.Directories)
        {
            if (directoryIds.TryGetValue(planned.Name, out var existingDirectoryId))
            {
                // A null description means the archive said nothing about it, so the existing one is kept.
                if (planned.Description is not null
                    && !string.Equals(directoryDescriptions[planned.Name], planned.Description, StringComparison.Ordinal))
                {
                    await directoryService.UpdateAsync(existingDirectoryId, planned.Name, planned.Description, ct);
                    directoryDescriptions[planned.Name] = planned.Description;
                }

                continue;
            }

            var created = await directoryService.CreateAsync(planned.Name, planned.Description ?? "", ImportAuthor, ct);
            directoryIds[created.Name] = created.Id;
            directoryDescriptions[created.Name] = created.Description;
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
        foreach (var group in plan.Documents.GroupBy(d => d.DirectoryName, NameComparer))
        {
            if (!directoryIds.TryGetValue(group.Key, out var directoryId))
            {
                continue;
            }

            var stored = (await documents.ListSummariesAsync([directoryId], ct))
                .Select(d => d.Name)
                .ToHashSet(NameComparer);

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

    /// <summary>
    /// Derives a directory name from a folder in a manifest-less archive. Two folders that would land in the
    /// same directory are rejected instead of being merged.
    /// </summary>
    private static string ResolveDirectoryName(string folder, Dictionary<string, string> slugOwners)
    {
        var slug = SlugifyDirectoryName(folder);
        if (slugOwners.TryGetValue(slug, out var owner))
        {
            if (!string.Equals(owner, folder, StringComparison.Ordinal))
            {
                throw KnowledgeException.Validation(
                    $"the archive contains folders '{owner}' and '{folder}', which both map to directory '{slug}'.");
            }

            return slug;
        }

        slugOwners[slug] = folder;
        return slug;
    }

    private static string SlugifyDirectoryName(string folder)
    {
        var decomposed = folder.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lowered = char.ToLowerInvariant(character);
            if (lowered is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-')
            {
                builder.Append(lowered);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString();
        var start = 0;
        while (start < slug.Length && !char.IsAsciiLetterOrDigit(slug[start]))
        {
            start++;
        }

        var end = slug.Length;
        while (end > start && !char.IsAsciiLetterOrDigit(slug[end - 1]))
        {
            end--;
        }

        slug = slug[start..end];
        if (slug.Length == 0)
        {
            throw KnowledgeException.Validation($"cannot derive a directory name from the folder '{folder}'.");
        }

        if (slug.Length > DirectoryRules.MaxNameLength)
        {
            slug = slug[..DirectoryRules.MaxNameLength].TrimEnd('-', '.', '_');
        }

        return DirectoryRules.NormalizeName(slug);
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
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw KnowledgeException.Validation($"{description} is not valid UTF-8.");
        }

        // A byte order mark is an encoding artefact, not part of the document.
        return text.StartsWith('\uFEFF') ? text[1..] : text;
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

    private sealed record PlannedDirectory(string Name, string? Description);

    private sealed record PlannedDocument(
        string DirectoryName,
        string Name,
        string Summary,
        IReadOnlyList<string> Tags,
        string Content);
}
