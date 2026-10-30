using GenHTTP.Adapters.AspNetCore;
using GenHTTP.Modules.Git;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace AgentKnowledgeBase.Mcp.Git;

/// <summary>
/// Serves the knowledge base as a read-only git repository. The git protocol is provided by GenHTTP; this
/// module only supplies the content and the access check.
/// </summary>
public static class KnowledgeGitEndpoint
{
    /// <summary>
    /// Mounts the repository when the feature is enabled. The auth middleware runs before the git handler
    /// because the handler is middleware of its own, which endpoint authorization attributes cannot guard.
    /// </summary>
    public static void MapKnowledgeGit(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<IOptions<GitOptions>>().Value;
        if (!options.Enabled)
        {
            return;
        }

        var path = NormalizePath(options.Path);
        var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();
        var scopes = app.Services.GetRequiredService<IServiceScopeFactory>();
        var settings = options;

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(path),
            branch => branch
                .Use(EnsureRawTarget)
                .UseMiddleware<KnowledgeGitAuthMiddleware>());

        app.Map(
            path,
            GitServer.Create()
                .Repository(async request =>
                {
                    var context = accessor.HttpContext;
                    var caller = KnowledgeGitAuthMiddleware.Caller(context);
                    if (caller is null)
                    {
                        return null;
                    }

                    // The repositories are singletons, but resolving the snapshot in a scope keeps the loader
                    // free to depend on scoped services later without changing this wiring.
                    await using var scope = scopes.CreateAsyncScope();
                    var loader = scope.ServiceProvider.GetRequiredService<KnowledgeSnapshotLoader>();
                    var history = scope.ServiceProvider.GetRequiredService<KnowledgeGitHistory>();

                    var snapshot = await loader.LoadAsync(caller, context!.RequestAborted);
                    return new KnowledgeGitRepository(
                        KnowledgeGitHistory.ScopeKey(caller),
                        snapshot,
                        history,
                        settings);
                })
                .Agent("AgentKnowledgeBase")
                .Build());
    }

    /// <summary>
    /// The git protocol is read by GenHTTP from <see cref="IHttpRequestFeature.RawTarget"/>. Kestrel always
    /// populates it, but other hosts (the in-memory test server among them) leave it empty, which the adapter
    /// would read as an empty path and answer with 404.
    /// </summary>
    private static async Task EnsureRawTarget(HttpContext context, RequestDelegate next)
    {
        var feature = context.Features.Get<IHttpRequestFeature>();
        if (feature is not null && string.IsNullOrEmpty(feature.RawTarget))
        {
            feature.RawTarget = context.Request.Path + context.Request.QueryString;
        }

        await next(context);
    }

    /// <summary>Git clients append the protocol paths themselves, so a trailing slash would double it.</summary>
    public static string NormalizePath(string path)
    {
        var trimmed = (path ?? "").Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("Git:Path must not be empty.");
        }

        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }
}
