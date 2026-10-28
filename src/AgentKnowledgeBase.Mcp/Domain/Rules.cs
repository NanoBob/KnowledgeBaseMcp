using System.Text;
using System.Text.RegularExpressions;

namespace AgentKnowledgeBase.Mcp.Domain;

public static class DirectoryRules
{
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 500;
    private static readonly Regex NamePattern = new(@"^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);

    public static string NormalizeName(string? name)
    {
        var normalized = (name?.Trim().Trim('/') ?? "").ToLowerInvariant();
        if (normalized.Length == 0)
        {
            throw KnowledgeException.Validation("directory name is required, e.g. 'dotnet'.");
        }

        if (!NamePattern.IsMatch(normalized))
        {
            throw KnowledgeException.Validation(
                "invalid directory name. Use letters, digits, '.', '_' or '-' (max 64 characters).");
        }

        return normalized;
    }

    public static string NormalizeDescription(string? description)
    {
        var trimmed = description?.Trim() ?? "";
        if (trimmed.Length > MaxDescriptionLength)
        {
            throw KnowledgeException.Validation($"description must be at most {MaxDescriptionLength} characters.");
        }

        return trimmed;
    }
}

public static class DocumentRules
{
    public const int MaxNameLength = 200;
    public const int MaxSummaryLength = 500;
    public const int MaxContentBytes = 100 * 1024;

    public static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > MaxNameLength)
        {
            throw KnowledgeException.Validation($"name is required and must be at most {MaxNameLength} characters.");
        }

        if (trimmed.Any(char.IsControl))
        {
            throw KnowledgeException.Validation("name must not contain control characters.");
        }

        return trimmed;
    }

    public static string NormalizeSummary(string? summary)
    {
        var trimmed = summary?.Trim() ?? "";
        if (trimmed.Length > MaxSummaryLength)
        {
            throw KnowledgeException.Validation($"summary must be at most {MaxSummaryLength} characters.");
        }

        return trimmed;
    }

    public static string NormalizeContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw KnowledgeException.Validation("content is required.");
        }

        if (Encoding.UTF8.GetByteCount(content) > MaxContentBytes)
        {
            throw KnowledgeException.Validation($"content must be at most {MaxContentBytes / 1024} KB.");
        }

        return content;
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

public static class AccessPolicy
{
    /// <summary>Scopes grant access to whole directories. When several scopes match, the highest level wins.</summary>
    public static AccessLevel? GetLevel(IEnumerable<KeyScope> scopes, Guid directoryId)
    {
        AccessLevel? level = null;
        foreach (var scope in scopes)
        {
            if (scope.DirectoryId != directoryId)
            {
                continue;
            }

            if (level is null || scope.Access > level)
            {
                level = scope.Access;
            }
        }

        return level;
    }
}
