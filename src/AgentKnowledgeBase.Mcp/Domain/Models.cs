using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace AgentKnowledgeBase.Mcp.Domain;

public sealed class KnowledgeDocument
{
    [BsonId]
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public string Content { get; set; } = "";
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
}

public sealed class DocumentRevision
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DocumentId { get; set; } = "";
    public int Version { get; set; }
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
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
    public string PathPrefix { get; set; } = "";

    [BsonRepresentation(BsonType.String)]
    public AccessLevel Access { get; set; }
}

public sealed class ApiKeyRecord
{
    [BsonId]
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public List<KeyScope> Scopes { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>An authenticated API key for the current request.</summary>
public sealed record ApiCaller(string KeyId, string Name, IReadOnlyList<KeyScope> Scopes)
{
    public AccessLevel? AccessFor(string path) => AccessPolicy.GetLevel(Scopes, path);

    public bool CanRead(string path) => AccessFor(path) is not null;

    public bool CanWrite(string path) => AccessFor(path) == AccessLevel.ReadWrite;
}
