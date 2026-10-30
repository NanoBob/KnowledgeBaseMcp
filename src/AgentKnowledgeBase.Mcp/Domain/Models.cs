using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace AgentKnowledgeBase.Mcp.Domain;

/// <summary>A flat container for documents. Directories never contain other directories.</summary>
public sealed class KnowledgeDocumentDirectory
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; }

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
}

public sealed class KnowledgeDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; }

    [BsonRepresentation(BsonType.String)]
    public Guid DirectoryId { get; set; }

    /// <summary>Human-readable name, unique within its directory (case-insensitive).</summary>
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string Content { get; set; } = "";
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
}

public sealed class KnowledgeDocumentRevision
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [BsonRepresentation(BsonType.String)]
    public Guid DocumentId { get; set; }

    [BsonRepresentation(BsonType.String)]
    public Guid DirectoryId { get; set; }

    public int Version { get; set; }
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string Content { get; set; } = "";
    public DateTime ChangedAt { get; set; }
    public string ChangedBy { get; set; } = "";
}

public enum AccessLevel
{
    Read,
    ReadWrite
}

public sealed class KeyScope
{
    [BsonRepresentation(BsonType.String)]
    public Guid DirectoryId { get; set; }

    [BsonRepresentation(BsonType.String)]
    public AccessLevel Access { get; set; }
}

public sealed class ApiKeyRecord
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public List<KeyScope> Scopes { get; set; } = [];

    /// <summary>
    /// Whether the key may clone the read-only git repository. Keys issued before the endpoint existed
    /// deserialize to false, which keeps the git endpoint closed to them until it is granted.
    /// </summary>
    public bool AllowGitRead { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? RotatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>An authenticated API key for the current request.</summary>
public sealed record ApiCaller(
    Guid KeyId,
    string Name,
    IReadOnlyList<KeyScope> Scopes,
    bool IsAdmin = false,
    bool AllowGitRead = false)
{
    /// <summary>Full access, used by the admin API which authenticates with the admin token instead of a key.</summary>
    public static ApiCaller Admin { get; } = new(Guid.Empty, "admin", [], IsAdmin: true, AllowGitRead: true);

    public AccessLevel? AccessFor(Guid directoryId) =>
        IsAdmin ? AccessLevel.ReadWrite : AccessPolicy.GetLevel(Scopes, directoryId);

    /// <summary>
    /// Directories this caller can read, for pushing the access check into the query.
    /// Null means "every directory" (the admin caller).
    /// </summary>
    public IReadOnlyCollection<Guid>? ReadableDirectoryIds =>
        IsAdmin ? null : Scopes.Select(scope => scope.DirectoryId).Distinct().ToList();

    public bool CanRead(Guid directoryId) => AccessFor(directoryId) is not null;

    public bool CanWrite(Guid directoryId) => AccessFor(directoryId) == AccessLevel.ReadWrite;
}
