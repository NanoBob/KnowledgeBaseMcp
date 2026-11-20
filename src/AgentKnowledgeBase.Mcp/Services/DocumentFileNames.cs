using System.Text;

namespace AgentKnowledgeBase.Mcp.Services;

/// <summary>
/// Maps document names to file names. Shared by the archive export and the git snapshot so a document has the
/// same path in both, which lets a checkout be re-imported as an archive without translation.
/// </summary>
public static class DocumentFileNames
{
    private static readonly char[] InvalidFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// The path of a document inside an archive or a git tree: <c>{directory}/{file}.md</c>. The extension
    /// keeps names that git refuses (such as <c>.git</c>) harmless.
    /// </summary>
    public static string RelativePath(string directoryName, string name, Guid id, HashSet<string> used) =>
        $"{directoryName}/{Unique(name, id, used)}.md";

    /// <summary>Sanitizes the name and resolves clashes within one directory by appending part of the id.</summary>
    public static string Unique(string name, Guid id, HashSet<string> used)
    {
        var baseName = Sanitize(name, id);
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

    /// <summary>Replaces characters a file system refuses and keeps the name short enough to be usable.</summary>
    public static string Sanitize(string name, Guid id)
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
}
