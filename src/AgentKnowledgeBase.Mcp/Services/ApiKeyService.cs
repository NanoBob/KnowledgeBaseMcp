using System.Security.Cryptography;
using System.Text;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Storage;

namespace AgentKnowledgeBase.Mcp.Services;

public sealed record KeyScopeInput(Guid? DirectoryId, string? Access);

public sealed class ApiKeyService(IApiKeyRepository keys, IDirectoryRepository directories)
{
    public const string KeyPrefix = "akb_";
    public const int MaxScopes = 50;

    public async Task<(ApiKeyRecord Record, string PlainTextKey)> CreateAsync(
        string? name,
        IEnumerable<KeyScopeInput>? scopes,
        CancellationToken ct)
    {
        var trimmedName = name?.Trim() ?? "";
        if (trimmedName.Length is 0 or > 100)
        {
            throw KnowledgeException.Validation("name is required and must be at most 100 characters.");
        }

        var parsedScopes = await ParseScopesAsync(scopes, ct);
        var plainText = KeyPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        var record = new ApiKeyRecord
        {
            Id = Guid.NewGuid(),
            Name = trimmedName,
            KeyHash = Hash(plainText),
            Scopes = parsedScopes,
            CreatedAt = DateTime.UtcNow
        };

        await keys.InsertAsync(record, ct);
        return (record, plainText);
    }

    public Task<List<ApiKeyRecord>> ListAsync(CancellationToken ct) => keys.ListAsync(ct);

    public async Task RevokeAsync(Guid id, CancellationToken ct)
    {
        if (!await keys.TryRevokeAsync(id, DateTime.UtcNow, ct))
        {
            throw KnowledgeException.NotFound($"no active API key with id '{id}'.");
        }
    }

    /// <summary>Resolves a presented key to its caller. Returns null for unknown or revoked keys.</summary>
    public async Task<ApiCaller?> AuthenticateAsync(string? presentedKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(presentedKey) || !presentedKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var record = await keys.FindByHashAsync(Hash(presentedKey), ct);
        if (record is null || record.RevokedAt is not null)
        {
            return null;
        }

        return new ApiCaller(record.Id, record.Name, record.Scopes);
    }

    public static string Hash(string plainText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainText))).ToLowerInvariant();

    private async Task<List<KeyScope>> ParseScopesAsync(IEnumerable<KeyScopeInput>? inputs, CancellationToken ct)
    {
        var scopes = new Dictionary<Guid, AccessLevel>();
        foreach (var input in inputs ?? [])
        {
            if (input.DirectoryId is null || input.DirectoryId == Guid.Empty)
            {
                throw KnowledgeException.Validation("each scope requires a directoryId.");
            }

            if (!Enum.TryParse<AccessLevel>(input.Access, ignoreCase: true, out var access))
            {
                throw KnowledgeException.Validation("scope access must be 'read' or 'readwrite'.");
            }

            var directoryId = input.DirectoryId.Value;
            if (await directories.FindAsync(directoryId, ct) is null)
            {
                throw KnowledgeException.NotFound($"directory '{directoryId}' was not found.");
            }

            if (!scopes.TryGetValue(directoryId, out var existing) || access > existing)
            {
                scopes[directoryId] = access;
            }
        }

        if (scopes.Count == 0)
        {
            throw KnowledgeException.Validation("at least one scope is required.");
        }

        if (scopes.Count > MaxScopes)
        {
            throw KnowledgeException.Validation($"a key may have at most {MaxScopes} scopes.");
        }

        return scopes.Select(s => new KeyScope { DirectoryId = s.Key, Access = s.Value }).ToList();
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
