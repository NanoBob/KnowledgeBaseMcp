using AgentKnowledgeBase.Mcp;
using AgentKnowledgeBase.Mcp.Admin;
using AgentKnowledgeBase.Mcp.Auth;
using AgentKnowledgeBase.Mcp.Domain;
using AgentKnowledgeBase.Mcp.Mcp;
using AgentKnowledgeBase.Mcp.Services;
using AgentKnowledgeBase.Mcp.Storage;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<MongoOptions>()
    .Bind(builder.Configuration.GetSection("Mongo"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<AdminOptions>()
    .Bind(builder.Configuration.GetSection("Admin"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<TransferOptions>()
    .Bind(builder.Configuration.GetSection("Transfer"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IMongoClient>(sp =>
    new MongoClient(sp.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString));
builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(sp.GetRequiredService<IOptions<MongoOptions>>().Value.Database));

builder.Services.AddSingleton<IDirectoryRepository, MongoDirectoryRepository>();
builder.Services.AddSingleton<IDocumentRepository, MongoDocumentRepository>();
builder.Services.AddSingleton<IApiKeyRepository, MongoApiKeyRepository>();
builder.Services.AddSingleton<DirectoryService>();
builder.Services.AddSingleton<KnowledgeService>();
builder.Services.AddSingleton<DocumentTransferService>();
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddHostedService<MongoIndexInitializer>();

// The admin UI is served separately (Vite dev server or any static host), so it needs CORS.
var allowedOrigins = builder.Configuration.GetSection("Frontend:Origins").Get<string[]>() is { Length: > 0 } configured
    ? configured
    : ["http://localhost:5173", "http://localhost:4173"];

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddScoped<CallerContext>();
builder.Services.AddControllers(options =>
{
    // The transfer import action reads the form itself so the configured archive limit is applied before
    // the body is buffered. MVC's form providers would read the form first, which both defeats that limit
    // and turns a malformed body into a model-state error instead of the documented validation error.
    for (var i = options.ValueProviderFactories.Count - 1; i >= 0; i--)
    {
        if (options.ValueProviderFactories[i] is FormValueProviderFactory
            or FormFileValueProviderFactory
            or JQueryFormValueProviderFactory)
        {
            options.ValueProviderFactories.RemoveAt(i);
        }
    }
});
builder.Services.AddAdminOpenApi();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<KnowledgeTools>();

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (error is not KnowledgeException known)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Unhandled")
            .LogError(error, "Unhandled exception for {Path}.", context.Request.Path);
    }

    context.Response.StatusCode = error switch
    {
        KnowledgeException { Kind: KnowledgeErrorKind.Validation } => StatusCodes.Status400BadRequest,
        KnowledgeException { Kind: KnowledgeErrorKind.NotFound } => StatusCodes.Status404NotFound,
        KnowledgeException { Kind: KnowledgeErrorKind.Forbidden } => StatusCodes.Status403Forbidden,
        KnowledgeException { Kind: KnowledgeErrorKind.Conflict } => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    };

    await context.Response.WriteAsJsonAsync(new
    {
        error = error is KnowledgeException message ? message.Message : "an unexpected error occurred.",
        kind = error is KnowledgeException kind ? kind.Kind.ToString().ToLowerInvariant() : "internal",
        latestVersion = (error as KnowledgeException)?.LatestVersion
    });
}));

app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "swagger";
    options.DocumentTitle = "Agent Knowledge Base Admin API";
    options.SwaggerEndpoint(AdminOpenApi.DocumentPath, "Agent Knowledge Base Admin API");
    // Keeps the admin token that was pasted into "Authorize" while the page is reloaded.
    options.EnablePersistAuthorization();
});

app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/mcp"),
    branch => branch.UseMiddleware<ApiKeyAuthMiddleware>());

app.MapMcp("/mcp");
app.MapControllers();

app.Run();

/// <summary>Exposed so integration tests can boot the real pipeline with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
