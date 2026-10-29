using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace AgentKnowledgeBase.Mcp.Admin;

/// <summary>
/// Describes the admin REST API as an OpenAPI document so the Swagger UI at <c>/swagger</c> can drive it.
/// </summary>
public static class AdminOpenApi
{
    public const string DocumentName = "v1";
    public const string DocumentPath = "/openapi/v1.json";
    public const string AdminTokenScheme = "AdminToken";

    private const string ImportRelativePath = "admin/transfer/import";

    public static IServiceCollection AddAdminOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            // The document describes the admin REST surface only; MCP is a protocol endpoint and the health
            // probe is not part of the API.
            options.ShouldInclude = description => IsAdminPath(description.RelativePath);

            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Agent Knowledge Base Admin API",
                    Version = DocumentName,
                    Description = $"""
                        Administration API for directories, documents, revisions, API keys and archive transfer.
                        Every call needs the admin token, sent either in the `X-Admin-Token` header or as a bearer token.
                        Error responses carry the `AdminErrorResponse` envelope, except a request the framework cannot
                        bind (a malformed body or a route value of the wrong type), which answers with ProblemDetails as
                        `{AdminMediaTypes.ProblemJson}`.
                        """
                };

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[AdminTokenScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = "X-Admin-Token",
                    Description = "The admin token configured through Admin__Token."
                };

                return Task.CompletedTask;
            });

            options.AddOperationTransformer((operation, context, _) =>
            {
                if (!IsAdminPath(context.Description.RelativePath))
                {
                    return Task.CompletedTask;
                }

                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(AdminTokenScheme, context.Document)] = []
                });

                operation.Responses ??= new OpenApiResponses();
                const string unauthorized = "The admin token is missing or wrong.";
                if (operation.Responses.TryGetValue("401", out var existing) && existing is OpenApiResponse response)
                {
                    // The controllers declare the response schema; only the wording is added here.
                    response.Description = unauthorized;
                }
                else
                {
                    operation.Responses.TryAdd("401", new OpenApiResponse { Description = unauthorized });
                }

                if (context.Description is { HttpMethod: "POST", RelativePath: ImportRelativePath })
                {
                    operation.RequestBody = ImportRequestBody();
                }

                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>
    /// The import action reads the form from the request itself rather than through model binding, so the
    /// generated document has no body for it. It is described here so the Swagger UI can still upload a zip.
    /// </summary>
    private static OpenApiRequestBody ImportRequestBody() => new()
    {
        Required = true,
        Content = new Dictionary<string, OpenApiMediaType>
        {
            ["multipart/form-data"] = new()
            {
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Required = new HashSet<string> { "file" },
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["file"] = new OpenApiSchema
                        {
                            Type = JsonSchemaType.String,
                            Format = "binary",
                            Description = "A zip archive produced by the export action."
                        }
                    }
                }
            }
        }
    };

    private static bool IsAdminPath(string? relativePath) =>
        relativePath is not null
        && (relativePath.Equals("admin", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("admin/", StringComparison.OrdinalIgnoreCase));
}
