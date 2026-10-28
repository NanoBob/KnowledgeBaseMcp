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
        var plainText = NewPlainTextKey();

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

    /// <summary>
    /// Issues a new secret for an existing key, keeping its name and permissions.
    /// The previous secret stops authenticating immediately.
    /// </summary>
    public async Task<(ApiKeyRecord Record, string PlainTextKey)> RegenerateAsync(Guid id, CancellationToken ct)
    {
        var plainText = NewPlainTextKey();
        var record = await keys.TryRotateAsync(id, Hash(plainText), DateTime.UtcNow, ct)
            ?? throw KnowledgeException.NotFound($"no active API key with id '{id}'.");

        return (record, plainText);
    }

    public Task<List<ApiKeyRecord>> ListAsync(CancellationToken ct) => keys.ListAsync(ct);

    /// <summary>
    /// Replaces the directory permissions of an existing key. The secret is untouched, so
    /// agents pick up the new scopes on their next call.
    /// </summary>
    public async Task<ApiKeyRecord> UpdateScopesAsync(
        Guid id,
        IEnumerable<KeyScopeInput>? scopes,
        CancellationToken ct)
    {
        var parsedScopes = await ParseScopesAsync(scopes, ct);
        return await keys.TryUpdateScopesAsync(id, parsedScopes, ct)
            ?? throw KnowledgeException.NotFound($"no active API key with id '{id}'.");
    }

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

            var access = ParseAccess(input.Access);

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

    /// <summary>
    /// Maps the wire value to an access level. Deliberately explicit rather than
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>, which also accepts numbers
    /// ("1" would silently become ReadWrite) and comma-separated flag combinations.
    /// </summary>
    private static AccessLevel ParseAccess(string? access)
    {
        var trimmed = access?.Trim();
        if (string.Equals(trimmed, "read", StringComparison.OrdinalIgnoreCase))
        {
            return AccessLevel.Read;
        }

        if (string.Equals(trimmed, "readwrite", StringComparison.OrdinalIgnoreCase))
        {
            return AccessLevel.ReadWrite;
        }

        throw KnowledgeException.Validation("scope access must be 'read' or 'readwrite'.");
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NewPlainTextKey() =>
        KeyPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
}
