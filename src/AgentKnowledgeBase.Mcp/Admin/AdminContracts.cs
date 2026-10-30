namespace AgentKnowledgeBase.Mcp.Admin;

/// <summary>
/// The body the exception handler writes for a failed admin call. <c>kind</c> is one of
/// <c>validation</c>, <c>notfound</c>, <c>forbidden</c>, <c>conflict</c> or <c>internal</c>, and
/// <c>latestVersion</c> is only set for a stale expected version.
/// </summary>
public sealed record AdminErrorResponse(string? Error, string? Kind, int? LatestVersion);

public sealed record CreateApiKeyRequest(string? Name, List<CreateKeyScope>? Scopes);

public sealed record CreateKeyScope(Guid? DirectoryId, string? Access);

/// <summary>A stored scope. Unlike the request shape, both fields are always present.</summary>
public sealed record KeyScopeResponse(Guid DirectoryId, string Access);

public sealed record UpdateApiKeyScopesRequest(List<CreateKeyScope>? Scopes);

public sealed record ApiKeyCreatedResponse(Guid Id, string Name, string Key, IReadOnlyList<KeyScopeResponse> Scopes, DateTime CreatedAt);

public sealed record ApiKeySummaryResponse(Guid Id, string Name, IReadOnlyList<KeyScopeResponse> Scopes, DateTime CreatedAt, DateTime? RotatedAt, DateTime? RevokedAt);

public sealed record CreateDirectoryRequest(string? Name, string? Description);

public sealed record UpdateDirectoryRequest(string? Name, string? Description);

public sealed record StoreDocumentRequest(
    Guid? DirectoryId,
    string? Name,
    string? Summary,
    string[]? Tags,
    string? Content,
    int? ExpectedVersion);

public sealed record StoreDocumentResponse(Guid Id, Guid DirectoryId, string Name, int Version, bool Created);

public sealed record MoveDocumentRequest(Guid? DirectoryId, int? ExpectedVersion);

public sealed record RestoreRevisionRequest(int? ExpectedVersion);

public sealed record ExportDocumentsRequest(List<Guid>? DirectoryIds);

public sealed record ImportDocumentsResponse(
    int DirectoriesCreated,
    int DocumentsCreated,
    int DocumentsUpdated,
    int DocumentsSkipped);
