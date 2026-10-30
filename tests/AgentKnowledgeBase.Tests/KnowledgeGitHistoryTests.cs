using System.Text;
using AgentKnowledgeBase.Mcp;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Git;
using AgentKnowledgeBase.Mcp.Services;
using GenHTTP.Modules.Git;

namespace AgentKnowledgeBase.Tests;

public class KnowledgeGitHistoryTests
{
    private const string Scope = "scope";

    private static readonly DateTimeOffset ChangedAt = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private static KnowledgeSnapshot Snapshot(string content = "body", DateTimeOffset? changedAt = null) =>
        new(
            [new TransferDirectory("alpha", "alpha documents")],
            [new KnowledgeSnapshotDocument("alpha", "one", "a summary", [], "alpha/one.md", content)],
            changedAt ?? ChangedAt);

    private static string BlobId(string content) => GitObjectId.ForBlob(Encoding.UTF8.GetBytes(content)).ToString();

    [Fact]
    public async Task An_unchanged_snapshot_resolves_to_the_stored_commit()
    {
        var store = new FakeGitHistoryStore();
        var history = new KnowledgeGitHistory(store, new GitOptions());

        var first = await history.ResolveAsync(Scope, Snapshot(), default);
        var second = await history.ResolveAsync(Scope, Snapshot(), default);

        Assert.Equal(first.Commit.Id, second.Commit.Id);
        Assert.Equal(await first.Tree.ComputeIdAsync(), await second.Tree.ComputeIdAsync());
        Assert.Single(store.Commits(Scope));
    }

    [Fact]
    public async Task A_changed_snapshot_appends_a_child_of_the_previous_commit()
    {
        var store = new FakeGitHistoryStore();
        var history = new KnowledgeGitHistory(store, new GitOptions());

        var first = await history.ResolveAsync(Scope, Snapshot("one"), default);
        var second = await history.ResolveAsync(Scope, Snapshot("two"), default);

        Assert.Equal(first.Commit.Id, Assert.Single(second.Commit.Parents));
        Assert.Equal(2, store.Commits(Scope).Count);
        Assert.Equal(second.Commit.Id.ToString(), store.Commits(Scope)[0].Sha);
    }

    [Fact]
    public async Task A_new_commit_is_never_older_than_its_parent()
    {
        var history = new KnowledgeGitHistory(new FakeGitHistoryStore(), new GitOptions());

        var first = await history.ResolveAsync(Scope, Snapshot("one", ChangedAt), default);
        var second = await history.ResolveAsync(Scope, Snapshot("two", ChangedAt), default);

        Assert.True(
            second.Commit.Author.When > first.Commit.Author.When,
            $"expected {second.Commit.Author.When} to be newer than {first.Commit.Author.When}");
    }

    [Fact]
    public async Task Returning_to_earlier_content_appends_a_commit_instead_of_reusing_the_old_one()
    {
        var store = new FakeGitHistoryStore();
        var history = new KnowledgeGitHistory(store, new GitOptions());

        var first = await history.ResolveAsync(Scope, Snapshot("one"), default);
        var second = await history.ResolveAsync(Scope, Snapshot("two"), default);
        var third = await history.ResolveAsync(Scope, Snapshot("one"), default);

        Assert.NotEqual(first.Commit.Id, third.Commit.Id);
        Assert.Equal(second.Commit.Id, Assert.Single(third.Commit.Parents));
    }

    [Fact]
    public async Task The_content_of_an_older_commit_stays_available()
    {
        var store = new FakeGitHistoryStore();
        var history = new KnowledgeGitHistory(store, new GitOptions());

        var first = await history.ResolveAsync(Scope, Snapshot("one"), default);
        await history.ResolveAsync(Scope, Snapshot("two"), default);

        var stored = await history.FindAsync(Scope, first.Commit.Id, default);

        Assert.NotNull(stored);

        var tree = await history.GetTreeAsync(Scope, stored, default);
        var document = tree.GetFile("alpha/one.md");

        Assert.NotNull(document);
        Assert.Equal("one", (await document.ReadStringAsync()).Trim());
    }

    [Fact]
    public async Task History_beyond_the_configured_depth_is_dropped()
    {
        var store = new FakeGitHistoryStore();
        var history = new KnowledgeGitHistory(store, new GitOptions { HistoryDepth = 2 });

        var first = await history.ResolveAsync(Scope, Snapshot("one"), default);
        var second = await history.ResolveAsync(Scope, Snapshot("two"), default);
        await history.ResolveAsync(Scope, Snapshot("three"), default);

        Assert.Equal(2, store.Commits(Scope).Count);
        Assert.Null(await history.FindAsync(Scope, first.Commit.Id, default));
        Assert.NotNull(await history.FindAsync(Scope, second.Commit.Id, default));
    }

    [Fact]
    public async Task Content_only_the_dropped_history_used_is_released()
    {
        var store = new FakeGitHistoryStore();
        var history = new KnowledgeGitHistory(store, new GitOptions { HistoryDepth = 1 });

        await history.ResolveAsync(Scope, Snapshot("one"), default);
        await history.ResolveAsync(Scope, Snapshot("two"), default);

        Assert.False(store.Blobs.ContainsKey(BlobId("one")));
        Assert.True(store.Blobs.ContainsKey(BlobId("two")));
    }

    [Fact]
    public async Task Scopes_keep_separate_histories()
    {
        var store = new FakeGitHistoryStore();
        var history = new KnowledgeGitHistory(store, new GitOptions());

        var mine = await history.ResolveAsync("mine", Snapshot("one"), default);
        var other = await history.ResolveAsync("other", Snapshot("two"), default);

        Assert.Empty(mine.Commit.Parents);
        Assert.Empty(other.Commit.Parents);
        Assert.Single(store.Commits("mine"));
        Assert.Single(store.Commits("other"));
        Assert.Null(await history.FindAsync("other", mine.Commit.Id, default));
    }

    [Fact]
    public void The_scope_key_of_an_admin_covers_every_directory()
    {
        Assert.Equal("all", KnowledgeGitHistory.ScopeKey(ApiCaller.Admin));
    }

    [Fact]
    public void The_scope_key_of_a_key_without_scopes_is_distinct()
    {
        var caller = new ApiCaller(Guid.NewGuid(), "git", []);

        Assert.Equal("none", KnowledgeGitHistory.ScopeKey(caller));
    }

    [Fact]
    public void The_scope_key_does_not_depend_on_the_order_of_the_scopes()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var one = new ApiCaller(Guid.NewGuid(), "git",
            [new KeyScope { DirectoryId = first, Access = AccessLevel.Read },
             new KeyScope { DirectoryId = second, Access = AccessLevel.Read }]);
        var other = new ApiCaller(Guid.NewGuid(), "git",
            [new KeyScope { DirectoryId = second, Access = AccessLevel.Read },
             new KeyScope { DirectoryId = first, Access = AccessLevel.ReadWrite }]);

        Assert.Equal(KnowledgeGitHistory.ScopeKey(one), KnowledgeGitHistory.ScopeKey(other));
    }

    [Fact]
    public void The_scope_key_differs_when_the_scopes_differ()
    {
        var one = new ApiCaller(Guid.NewGuid(), "git", [new KeyScope { DirectoryId = Guid.NewGuid(), Access = AccessLevel.Read }]);
        var other = new ApiCaller(Guid.NewGuid(), "git", [new KeyScope { DirectoryId = Guid.NewGuid(), Access = AccessLevel.Read }]);

        Assert.NotEqual(KnowledgeGitHistory.ScopeKey(one), KnowledgeGitHistory.ScopeKey(other));
    }

    [Fact]
    public void The_commit_message_describes_the_snapshot()
    {
        Assert.Equal("Knowledge base snapshot: 1 document in 1 directory", KnowledgeGitHistory.Message(Snapshot()));
        Assert.Equal(
            "Knowledge base snapshot: 0 documents in 0 directories",
            KnowledgeGitHistory.Message(KnowledgeSnapshot.Empty));
    }

    [Fact]
    public async Task A_stored_commit_rebuilds_to_the_id_it_was_served_with()
    {
        var store = new FakeGitHistoryStore();
        var history = new KnowledgeGitHistory(store, new GitOptions());

        var served = await history.ResolveAsync(Scope, Snapshot("one"), default);
        await history.ResolveAsync(Scope, Snapshot("two"), default);

        var stored = await history.FindAsync(Scope, served.Commit.Id, default);

        Assert.NotNull(stored);
        Assert.Equal(served.Commit.Id, history.Derive(stored).Id);
    }

    [Fact]
    public void A_stored_commit_that_no_longer_rebuilds_is_refused()
    {
        var history = new KnowledgeGitHistory(new FakeGitHistoryStore(), new GitOptions());

        var stored = new GitStoredCommit(
            new string('0', 40),
            null,
            new string('1', 40),
            ChangedAt,
            1,
            1,
            [],
            IsBoundary: true);

        Assert.Throws<InvalidOperationException>(() => history.Derive(stored));
    }

    [Fact]
    public async Task A_removed_document_is_remembered_as_a_removal()
    {
        var store = new FakeGitHistoryStore();
        var history = new KnowledgeGitHistory(store, new GitOptions());

        await history.ResolveAsync(Scope, TwoDocuments(), default);
        await history.ResolveAsync(Scope, Snapshot("one"), default);

        var head = store.Commits(Scope)[0];

        Assert.Contains(head.Changes, change => change.Path == "alpha/two.md" && change.BlobId is null);
        Assert.DoesNotContain(head.Changes, change => change.Path == "alpha/one.md");
    }

    [Fact]
    public async Task The_oldest_retained_commit_carries_every_file_after_pruning()
    {
        var store = new FakeGitHistoryStore();
        var history = new KnowledgeGitHistory(store, new GitOptions { HistoryDepth = 2 });

        await history.ResolveAsync(Scope, Snapshot("one"), default);
        var middle = await history.ResolveAsync(Scope, Snapshot("two"), default);
        await history.ResolveAsync(Scope, Snapshot("three"), default);

        var retained = store.Commits(Scope);

        Assert.Equal(2, retained.Count);
        Assert.True(retained[^1].IsBoundary);
        Assert.Equal(middle.Commit.Id.ToString(), retained[^1].Sha);

        var tree = await history.GetTreeAsync(Scope, retained[^1], default);
        var document = tree.GetFile("alpha/one.md");

        Assert.NotNull(document);
        Assert.Equal("two", (await document.ReadStringAsync()).Trim());
    }

    [Fact]
    public async Task Pruning_does_not_append_a_commit_for_an_unchanged_snapshot()
    {
        var store = new FakeGitHistoryStore();
        var history = new KnowledgeGitHistory(store, new GitOptions { HistoryDepth = 2 });

        await history.ResolveAsync(Scope, Snapshot("one"), default);
        await history.ResolveAsync(Scope, Snapshot("two"), default);
        var newest = await history.ResolveAsync(Scope, Snapshot("three"), default);
        var again = await history.ResolveAsync(Scope, Snapshot("three"), default);

        Assert.Equal(newest.Commit.Id, again.Commit.Id);
        Assert.Equal(2, store.Commits(Scope).Count);
    }

    [Fact]
    public void A_history_that_never_reaches_a_boundary_is_refused()
    {
        var changes = new List<GitStoredCommit>
        {
            new("head", "parent", "tree", ChangedAt, 1, 1, [new GitStoredChange("alpha/one.md", "blob")], false),
            new("parent", "grandparent", "tree", ChangedAt, 1, 1, [], false)
        };

        Assert.Throws<InvalidOperationException>(() => GitHistoryFold.Files(changes));
    }

    private static KnowledgeSnapshot TwoDocuments() =>
        new(
            [new TransferDirectory("alpha", "alpha documents")],
            [
                new KnowledgeSnapshotDocument("alpha", "one", "a summary", [], "alpha/one.md", "one"),
                new KnowledgeSnapshotDocument("alpha", "two", "a summary", [], "alpha/two.md", "two")
            ],
            ChangedAt);
}
