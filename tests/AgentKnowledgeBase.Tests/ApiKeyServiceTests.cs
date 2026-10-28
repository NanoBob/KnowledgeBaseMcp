using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;

namespace AgentKnowledgeBase.Tests;

public class ApiKeyServiceTests
{
    private readonly FakeApiKeyRepository _repo = new();
    private readonly FakeDirectoryRepository _directoryRepo = new();
    private readonly FakeDocumentRepository _documentRepo = new();
    private readonly DirectoryService _directories;
    private readonly ApiKeyService _service;

    public ApiKeyServiceTests()
    {
        _directories = new DirectoryService(_directoryRepo, _documentRepo);
        _service = new ApiKeyService(_repo, _directoryRepo);
    }

    private async Task<Guid> CreateDirectoryAsync(string name = "team")
    {
        var directory = await _directories.CreateAsync(name, "", "admin", default);
        return directory.Id;
    }

    [Fact]
    public async Task Create_ReturnsPlainTextKeyOnceAndStoresOnlyHash()
    {
        var directoryId = await CreateDirectoryAsync();

        var (record, plain) = await _service.CreateAsync("agent-a",
            [new KeyScopeInput(directoryId, "readwrite")], default);

        Assert.StartsWith("akb_", plain);
        Assert.NotEqual(plain, record.KeyHash);
        Assert.Equal(ApiKeyService.Hash(plain), record.KeyHash);
        Assert.NotEqual(Guid.Empty, record.Id);
        Assert.Equal(directoryId, record.Scopes[0].DirectoryId);
        Assert.Equal(AccessLevel.ReadWrite, record.Scopes[0].Access);
    }

    [Fact]
    public async Task Authenticate_ValidKey_ReturnsCaller()
    {
        var directoryId = await CreateDirectoryAsync();
        var (record, plain) = await _service.CreateAsync("agent-a", [new KeyScopeInput(directoryId, "read")], default);

        var caller = await _service.AuthenticateAsync(plain, default);

        Assert.NotNull(caller);
        Assert.Equal(record.Id, caller.KeyId);
        Assert.Equal("agent-a", caller.Name);
        Assert.True(caller.CanRead(directoryId));
        Assert.False(caller.CanWrite(directoryId));
    }

    [Fact]
    public async Task Authenticate_UnknownOrMalformedKey_ReturnsNull()
    {
        var directoryId = await CreateDirectoryAsync();
        await _service.CreateAsync("agent-a", [new KeyScopeInput(directoryId, "read")], default);

        Assert.Null(await _service.AuthenticateAsync("akb_not-a-real-key", default));
        Assert.Null(await _service.AuthenticateAsync("not-prefixed", default));
        Assert.Null(await _service.AuthenticateAsync(null, default));
    }

    [Fact]
    public async Task Authenticate_RevokedKey_ReturnsNull()
    {
        var directoryId = await CreateDirectoryAsync();
        var (record, plain) = await _service.CreateAsync("agent-a", [new KeyScopeInput(directoryId, "read")], default);

        await _service.RevokeAsync(record.Id, default);

        Assert.Null(await _service.AuthenticateAsync(plain, default));
    }

    [Fact]
    public async Task Revoke_AlreadyRevoked_IsNotFound()
    {
        var directoryId = await CreateDirectoryAsync();
        var (record, _) = await _service.CreateAsync("agent-a", [new KeyScopeInput(directoryId, "read")], default);
        await _service.RevokeAsync(record.Id, default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => _service.RevokeAsync(record.Id, default));
        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Create_RejectsInvalidScopeAccess()
    {
        var directoryId = await CreateDirectoryAsync();

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.CreateAsync("agent-a", [new KeyScopeInput(directoryId, "admin")], default));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Create_RequiresAtLeastOneScope()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.CreateAsync("agent-a", [], default));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Create_RejectsUnknownDirectory()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.CreateAsync("agent-a", [new KeyScopeInput(Guid.NewGuid(), "read")], default));

        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Create_RequiresDirectoryOnEachScope()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.CreateAsync("agent-a", [new KeyScopeInput(null, "read")], default));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Create_DedupesScopesKeepingHighestAccess()
    {
        var directoryId = await CreateDirectoryAsync();

        var (record, _) = await _service.CreateAsync("agent-a",
            [new KeyScopeInput(directoryId, "read"), new KeyScopeInput(directoryId, "readwrite")], default);

        var scope = Assert.Single(record.Scopes);
        Assert.Equal(AccessLevel.ReadWrite, scope.Access);
    }
}
