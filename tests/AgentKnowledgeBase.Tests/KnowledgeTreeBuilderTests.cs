using System.Text.Json;
using AgentKnowledgeBase.Mcp.Git;
using AgentKnowledgeBase.Mcp.Services;

namespace AgentKnowledgeBase.Tests;

public class KnowledgeTreeBuilderTests
{
    private static readonly DateTimeOffset ChangedAt = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private static KnowledgeSnapshot Snapshot(params KnowledgeSnapshotDocument[] documents) =>
        new(
            [.. documents
                .Select(d => d.DirectoryName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => new TransferDirectory(name, $"{name} documents"))],
            documents,
            ChangedAt);

    private static KnowledgeSnapshotDocument Document(
        string directory,
        string name,
        string content = "body",
        string summary = "") =>
        new(directory, name, summary, [], $"{directory}/{name}.md", content);

    [Fact]
    public void The_tree_holds_the_index_the_manifest_and_one_file_per_document()
    {
        var tree = KnowledgeTreeBuilder.Build(Snapshot(
            Document("alpha", "one"),
            Document("beta", "two")));

        Assert.Equal(
            ["README.md", "alpha/one.md", "beta/two.md", "manifest.json"],
            tree.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_manifest_matches_the_archive_format()
    {
        var snapshot = Snapshot(Document("alpha", "one", summary: "a summary"));

        var manifest = JsonSerializer.Deserialize<TransferManifest>(
            KnowledgeTreeBuilder.BuildManifest(snapshot),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(manifest);
        Assert.Equal(DocumentTransferService.FormatVersion, manifest.FormatVersion);
        Assert.Equal(ChangedAt.UtcDateTime, manifest.ExportedAt!.Value);

        var directory = Assert.Single(manifest.Directories!);
        Assert.Equal("alpha", directory.Name);

        var document = Assert.Single(manifest.Documents!);
        Assert.Equal("one", document.Name);
        Assert.Equal("a summary", document.Summary);
        Assert.Equal("alpha/one.md", document.File);
    }

    [Fact]
    public void Generated_files_use_lf_so_ids_do_not_depend_on_the_platform()
    {
        var tree = KnowledgeTreeBuilder.Build(Snapshot(Document("alpha", "one")));

        foreach (var file in tree.Files)
        {
            Assert.DoesNotContain('\r', file.Path);
        }

        var readme = KnowledgeTreeBuilder.BuildReadme(Snapshot(Document("alpha", "one")));
        var manifest = KnowledgeTreeBuilder.BuildManifest(Snapshot(Document("alpha", "one")));

        Assert.DoesNotContain('\r', readme);
        Assert.DoesNotContain('\r', manifest);
    }

    [Fact]
    public async Task Document_content_is_stored_with_lf()
    {
        var tree = KnowledgeTreeBuilder.Build(Snapshot(
            new KnowledgeSnapshotDocument("alpha", "one", "", [], "alpha/one.md", "line\r\nline")));

        Assert.Equal("line\nline", await tree.GetFile("alpha/one.md")!.ReadStringAsync());
    }

    [Fact]
    public async Task The_same_snapshot_always_produces_the_same_tree()
    {
        var snapshot = Snapshot(Document("alpha", "one"), Document("beta", "two"));

        var first = await KnowledgeTreeBuilder.Build(snapshot).ComputeIdAsync();
        var second = await KnowledgeTreeBuilder.Build(snapshot).ComputeIdAsync();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Different_content_produces_a_different_tree()
    {
        var first = await KnowledgeTreeBuilder.Build(Snapshot(Document("alpha", "one", "a"))).ComputeIdAsync();
        var second = await KnowledgeTreeBuilder.Build(Snapshot(Document("alpha", "one", "b"))).ComputeIdAsync();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void The_index_links_every_document_and_shows_the_directory_description()
    {
        var readme = KnowledgeTreeBuilder.BuildReadme(Snapshot(
            Document("alpha", "one", summary: "the summary"),
            Document("beta", "two")));

        Assert.Contains("## alpha", readme, StringComparison.Ordinal);
        Assert.Contains("alpha documents", readme, StringComparison.Ordinal);
        Assert.Contains("- [one](alpha/one.md): the summary", readme, StringComparison.Ordinal);
        Assert.Contains("- [two](beta/two.md)", readme, StringComparison.Ordinal);
        Assert.Contains("2 documents in 2 directories", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_snapshot_still_produces_a_usable_tree()
    {
        var tree = KnowledgeTreeBuilder.Build(KnowledgeSnapshot.Empty);

        Assert.Equal(["README.md", "manifest.json"], tree.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Contains("0 documents in 0 directories", KnowledgeTreeBuilder.BuildReadme(KnowledgeSnapshot.Empty));
    }

    [Fact]
    public void A_directory_without_documents_is_marked_as_empty()
    {
        var readme = KnowledgeTreeBuilder.BuildReadme(new KnowledgeSnapshot(
            [new TransferDirectory("alpha", "")],
            [],
            ChangedAt));

        Assert.Contains("## alpha", readme, StringComparison.Ordinal);
        Assert.Contains("_No documents._", readme, StringComparison.Ordinal);
    }
}
