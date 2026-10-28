namespace AgentKnowledgeBase.Mcp.Domain;

public enum KnowledgeErrorKind
{
    Validation,
    NotFound,
    Forbidden,
    Conflict
}

public sealed class KnowledgeException : Exception
{
    public KnowledgeException(KnowledgeErrorKind kind, string message, int? latestVersion = null)
        : base(message)
    {
        Kind = kind;
        LatestVersion = latestVersion;
    }

    public KnowledgeErrorKind Kind { get; }

    /// <summary>The current version of the document when a conflict occurred.</summary>
    public int? LatestVersion { get; }

    public static KnowledgeException Validation(string message) =>
        new(KnowledgeErrorKind.Validation, message);

    public static KnowledgeException NotFound(string message) =>
        new(KnowledgeErrorKind.NotFound, message);

    public static KnowledgeException Forbidden(string message) =>
        new(KnowledgeErrorKind.Forbidden, message);

    public static KnowledgeException Conflict(string message, int? latestVersion = null) =>
        new(KnowledgeErrorKind.Conflict, message, latestVersion);
}
