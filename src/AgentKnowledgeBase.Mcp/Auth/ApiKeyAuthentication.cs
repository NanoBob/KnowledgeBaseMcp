using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;
using ModelContextProtocol;

namespace AgentKnowledgeBase.Mcp.Auth;

/// <summary>Holds the key that authenticated the current MCP request.</summary>
public sealed class CallerContext
{
    public ApiCaller? Caller { get; set; }

    public ApiCaller Required => Caller ?? throw new McpException("Authentication required.");
}

/// <summary>Rejects requests to /mcp that do not carry a valid, unrevoked API key as a bearer token.</summary>
public sealed class ApiKeyAuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ApiKeyService keys, CallerContext callerContext)
    {
        var caller = await keys.AuthenticateAsync(BearerToken(context.Request), context.RequestAborted);
        if (caller is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new { error = "invalid_api_key" }, context.RequestAborted);
            return;
        }

        callerContext.Caller = caller;
        await next(context);
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        return header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : null;
    }
}
