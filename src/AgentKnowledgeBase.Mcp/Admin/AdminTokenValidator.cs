using System.Security.Cryptography;
using System.Text;

namespace AgentKnowledgeBase.Mcp.Admin;

/// <summary>Validates the admin token presented on a request against the configured value.</summary>
public static class AdminTokenValidator
{
    public static bool IsValid(HttpRequest request, string expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken))
        {
            return false;
        }

        var presented = PresentedToken(request);
        return presented.Length > 0
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presented),
                Encoding.UTF8.GetBytes(expectedToken));
    }

    /// <summary>The <c>X-Admin-Token</c> header wins; a bearer token is accepted as a fallback.</summary>
    public static string PresentedToken(HttpRequest request)
    {
        var token = request.Headers["X-Admin-Token"].ToString();
        if (token.Length > 0)
        {
            return token;
        }

        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        return header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : "";
    }
}
