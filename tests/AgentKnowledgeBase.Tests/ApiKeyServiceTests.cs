using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;

namespace AgentKnowledgeBase.Tests;

public class ApiKeyServiceTests
{
    private readonly FakeApiKeyRepository _repo = new();
    private readonly ApiKeyService _service;

    public ApiKeyServiceTests()
    {
        _service = new ApiKeyService(_repo);
    }

    [Fact]
    public async Task Create_ReturnsPlainTextKeyOnceAndStoresOnlyHash()
    {
        var (record, plain) = await _service.CreateAsync("agent-a",
            [new KeyScopeInput("dotnet", "readwrite")], default);

        Assert.StartsWith("akb_", plain);
        Assert.NotEqual(plain, record.KeyHash);
        Assert.Equal(ApiKeyService.Hash(plain), record.KeyHash);
        Assert.Equal(AccessLevel.ReadWrite, record.Scopes[0].Access);
    }

    [Fact]
    public async Task Authenticate_ValidKey_ReturnsCaller()
    {
        var (record, plain) = await _service.CreateAsync("agent-a", [new KeyScopeInput("", "read")], default);

        var caller = await _service.AuthenticateAsync(plain, default);

        Assert.NotNull(caller);
        Assert.Equal(record.Id, caller.KeyId);
        Assert.Equal("agent-a", caller.Name);
    }

    [Fact]
    public async Task Authenticate_UnknownOrMalformedKey_ReturnsNull()
    {
        await _service.CreateAsync("agent-a", [new KeyScopeInput("", "read")], default);

        Assert.Null(await _service.AuthenticateAsync("akb_not-a-real-key", default));
        Assert.Null(await _service.AuthenticateAsync("not-prefixed", default));
        Assert.Null(await _service.AuthenticateAsync(null, default));
    }

    [Fact]
    public async Task Authenticate_RevokedKey_ReturnsNull()
    {
        var (record, plain) = await _service.CreateAsync("agent-a", [new KeyScopeInput("", "read")], default);

        await _service.RevokeAsync(record.Id, default);

        Assert.Null(await _service.AuthenticateAsync(plain, default));
    }

    [Fact]
    public async Task Revoke_UnknownOrAlreadyRevoked_IsNotFound()
    {
        var (record, _) = await _service.CreateAsync("agent-a", [new KeyScopeInput("", "read")], default);
        await _service.RevokeAsync(record.Id, default);

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => _service.RevokeAsync(record.Id, default));
        Assert.Equal(KnowledgeErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Create_RejectsInvalidScopeAccess()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.CreateAsync("agent-a", [new KeyScopeInput("", "admin")], default));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public async Task Create_RequiresAtLeastOneScope()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            _service.CreateAsync("agent-a", [], default));

        Assert.Equal(KnowledgeErrorKind.Validation, ex.Kind);
    }
}
