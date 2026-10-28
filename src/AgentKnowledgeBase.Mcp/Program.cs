using AgentKnowledgeBase.Mcp;
using AgentKnowledgeBase.Mcp.Admin;
using AgentKnowledgeBase.Mcp.Auth;
using AgentKnowledgeBase.Mcp.Mcp;
using AgentKnowledgeBase.Mcp.Services;
using AgentKnowledgeBase.Mcp.Storage;
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

builder.Services.AddSingleton<IMongoClient>(sp =>
    new MongoClient(sp.GetRequiredService<IOptions<MongoOptions>>().Value.ConnectionString));
builder.Services.AddSingleton<IMongoDatabase>(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(sp.GetRequiredService<IOptions<MongoOptions>>().Value.Database));

builder.Services.AddSingleton<IDirectoryRepository, MongoDirectoryRepository>();
builder.Services.AddSingleton<IDocumentRepository, MongoDocumentRepository>();
builder.Services.AddSingleton<IApiKeyRepository, MongoApiKeyRepository>();
builder.Services.AddSingleton<DirectoryService>();
builder.Services.AddSingleton<KnowledgeService>();
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddHostedService<MongoIndexInitializer>();

builder.Services.AddScoped<CallerContext>();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<KnowledgeTools>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/mcp"),
    branch => branch.UseMiddleware<ApiKeyAuthMiddleware>());

app.MapMcp("/mcp");
app.MapAdminApi();

app.Run();
