namespace AgentKnowledgeBase.Mcp.Git;

/// <summary>
/// The stored commits only carry the files that changed, so the files of any point in the history are rebuilt
/// by replaying those changes forward from the oldest commit that is still kept.
/// </summary>
public static class GitHistoryFold
{
    /// <summary>
    /// The files of the newest commit in <paramref name="newestFirst"/> as path → blob id. The list has to reach
    /// back to a boundary, which is a commit that carries every file instead of a difference; otherwise the
    /// result would silently miss whatever the missing commits introduced.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Files(IReadOnlyList<GitStoredCommit> newestFirst)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);

        if (newestFirst.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var oldest = newestFirst[^1];
        if (!oldest.IsBoundary && oldest.Parent is not null)
        {
            throw new InvalidOperationException(
                $"The stored history ends at {oldest.Sha} without reaching a commit that carries every file.");
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = newestFirst.Count - 1; index >= 0; index--)
        {
            foreach (var change in newestFirst[index].Changes)
            {
                if (change.BlobId is null)
                {
                    files.Remove(change.Path);
                }
                else
                {
                    files[change.Path] = change.BlobId;
                }
            }
        }

        return files;
    }

    /// <summary>
    /// The commits to keep, newest first, when only <paramref name="depth"/> may stay. The oldest kept commit is
    /// rewritten into a boundary carrying every file, because the commits it used to point at are about to be
    /// deleted and its tree still has to be rebuildable. Its id does not change: the tree, the parent, the
    /// timestamp and the message it was derived from are untouched.
    /// </summary>
    public static IReadOnlyList<GitStoredCommit> Keep(IReadOnlyList<GitStoredCommit> newestFirst, int depth)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);

        if (newestFirst.Count <= depth)
        {
            return newestFirst;
        }

        var kept = newestFirst.Take(depth).ToList();
        var oldest = kept[^1];
        if (oldest.IsBoundary)
        {
            return kept;
        }

        var files = Files([.. newestFirst.Skip(depth - 1)]);
        kept[^1] = oldest with
        {
            IsBoundary = true,
            Changes =
            [
                .. files
                    .OrderBy(file => file.Key, StringComparer.Ordinal)
                    .Select(file => new GitStoredChange(file.Key, file.Value))
            ]
        };

        return kept;
    }
}
