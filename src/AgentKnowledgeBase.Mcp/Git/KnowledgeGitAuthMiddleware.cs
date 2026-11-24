using System.Text;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Services;

namespace AgentKnowledgeBase.Mcp.Git;

/// <summary>
/// Authenticates git traffic with the same API keys the MCP endpoint uses. Git clients cannot send a bearer
/// token on their own, so the key is also accepted as the password of HTTP basic auth, which git asks for
/// interactively or supplies from a credential helper. Only keys granted <see cref="ApiCaller.AllowGitRead"/>
/// may clone, so a key issued for MCP calls cannot be reused to mirror the knowledge base.
/// </summary>
public sealed class KnowledgeGitAuthMiddleware(RequestDelegate next)
{
    /// <summary>Where the authenticated caller is left for the repository factory.</summary>
    public static readonly object CallerKey = new();

    private const string Realm = "knowledge-base";

    public async Task InvokeAsync(HttpContext context, ApiKeyService keys)
    {
        var caller = await keys.AuthenticateAsync(PresentedKey(context.Request), context.RequestAborted);
        if (caller is null)
        {
            // The challenge is what makes git prompt for credentials instead of failing outright.
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
            await context.Response.WriteAsJsonAsync(
                new { error = "invalid_api_key" }, context.RequestAborted);
            return;
        }

        if (!caller.AllowGitRead)
        {
            // A valid key that was never granted git access. No challenge: asking again cannot help,
            // and a 401 would make git retry the same credential.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                new { error = "git_access_denied" }, context.RequestAborted);
            return;
        }

        context.Items[CallerKey] = caller;
        await next(context);
    }

    /// <summary>Reads the key from <c>Authorization: Bearer {key}</c> or <c>Authorization: Basic base64(:{key})</c>.</summary>
    public static string? PresentedKey(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.Length == 0)
        {
            return null;
        }

        const string bearer = "Bearer ";
        if (header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return header[bearer.Length..].Trim();
        }

        const string basic = "Basic ";
        if (!header.StartsWith(basic, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(header[basic.Length..].Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        var credentials = Encoding.UTF8.GetString(decoded);

        // Git sends "{username}:{password}" where the username is arbitrary, so only the password is a key.
        var separator = credentials.IndexOf(':');
        return separator < 0 ? credentials : credentials[(separator + 1)..];
    }

    public static ApiCaller? Caller(HttpContext? context) =>
        context?.Items.TryGetValue(CallerKey, out var value) == true ? value as ApiCaller : null;
}
