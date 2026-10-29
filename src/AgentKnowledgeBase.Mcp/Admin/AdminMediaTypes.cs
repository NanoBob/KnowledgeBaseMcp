namespace AgentKnowledgeBase.Mcp.Admin;

/// <summary>
/// The media types the admin API answers with. Naming them in <c>[ProducesResponseType]</c> keeps the
/// OpenAPI document to what is really sent, instead of the text/plain, application/json, text/json triple
/// that MVC assumes when no content type is declared.
/// </summary>
public static class AdminMediaTypes
{
    /// <summary>JSON bodies: every admin contract and the <see cref="AdminErrorResponse"/> envelope.</summary>
    public const string Json = "application/json";

    /// <summary>ProblemDetails, produced by <c>[ApiController]</c> when a request cannot be bound.</summary>
    public const string ProblemJson = "application/problem+json";

    /// <summary>The zip archive returned by the export action.</summary>
    public const string Zip = "application/zip";
}
