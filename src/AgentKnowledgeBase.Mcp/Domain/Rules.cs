using System.Text.RegularExpressions;

namespace AgentKnowledgeBase.Mcp.Domain;

/// <summary>Virtual directory paths such as <c>dotnet/efcore</c>. An empty prefix denotes the root.</summary>
public static class PathRules
{
    public const int MaxDepth = 10;
    private static readonly Regex SegmentPattern = new(@"^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);

    public static string NormalizeDocumentPath(string? path)
    {
        var normalized = Normalize(path);
        if (normalized.Length == 0)
        {
            throw KnowledgeException.Validation("path is required, e.g. 'dotnet/efcore'.");
        }

        return normalized;
    }

    public static string NormalizePrefix(string? prefix) => Normalize(prefix);

    /// <summary>True when <paramref name="path"/> equals the prefix or lies beneath it, matched on whole segments.</summary>
    public static bool IsWithin(string prefix, string path) =>
        prefix.Length == 0 || path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var segments = path.Trim().Trim('/').ToLowerInvariant().Split('/');
        if (segments.Length > MaxDepth)
        {
            throw KnowledgeException.Validation($"path must have at most {MaxDepth} segments.");
        }

        foreach (var segment in segments)
        {
            if (!SegmentPattern.IsMatch(segment))
            {
                throw KnowledgeException.Validation(
                    $"invalid path segment '{segment}'. Use letters, digits, '.', '_' or '-'.");
            }
        }

        return string.Join('/', segments);
    }
}

public static class TagRules
{
    public const int MaxTags = 20;
    private static readonly Regex TagPattern = new(@"^[a-z0-9][a-z0-9-]{0,49}$", RegexOptions.Compiled);

    public static List<string> Normalize(IEnumerable<string>? tags)
    {
        var result = new List<string>();
        foreach (var raw in tags ?? [])
        {
            var tag = raw.Trim().ToLowerInvariant();
            if (tag.Length == 0 || result.Contains(tag))
            {
                continue;
            }

            if (!TagPattern.IsMatch(tag))
            {
                throw KnowledgeException.Validation(
                    $"invalid tag '{raw}'. Use lowercase letters, digits and '-' (max 50 characters).");
            }

            result.Add(tag);
        }

        if (result.Count > MaxTags)
        {
            throw KnowledgeException.Validation($"a document may have at most {MaxTags} tags.");
        }

        return result;
    }
}

public static class IdRules
{
    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9-]{0,99}$", RegexOptions.Compiled);
    private static readonly Regex NonSlugChars = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    public static string Validate(string id)
    {
        if (!IdPattern.IsMatch(id))
        {
            throw KnowledgeException.Validation(
                "id must be 1-100 characters of lowercase letters, digits and '-', starting with a letter or digit.");
        }

        return id;
    }

    /// <summary>Builds a URL-safe id from a title. Returns an empty string when nothing usable remains.</summary>
    public static string Slugify(string title)
    {
        var slug = NonSlugChars.Replace(title.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 80)
        {
            slug = slug[..80].TrimEnd('-');
        }

        return slug;
    }
}

public static class AccessPolicy
{
    /// <summary>
    /// The most specific matching scope (longest path prefix) decides access, so a read-only
    /// scope can carve a protected subdirectory out of a broader read-write scope.
    /// </summary>
    public static AccessLevel? GetLevel(IEnumerable<KeyScope> scopes, string path)
    {
        KeyScope? best = null;
        foreach (var scope in scopes)
        {
            if (!PathRules.IsWithin(scope.PathPrefix, path))
            {
                continue;
            }

            if (best is null
                || scope.PathPrefix.Length > best.PathPrefix.Length
                || (scope.PathPrefix.Length == best.PathPrefix.Length && scope.Access > best.Access))
            {
                best = scope;
            }
        }

        return best?.Access;
    }
}
