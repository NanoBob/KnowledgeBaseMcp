using MongoDB.Driver;

namespace AgentKnowledgeBase.Mcp.Storage;

/// <summary>
/// Creates indexes before the server accepts traffic. Retries so that the app can start
/// before MongoDB is ready when running under Docker Compose.
/// </summary>
public sealed class MongoIndexInitializer(IMongoDatabase database, ILogger<MongoIndexInitializer> logger) : IHostedService
{
    private const int MaxAttempts = 30;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await MongoDirectoryRepository.EnsureIndexesAsync(database, cancellationToken);
                await MongoDocumentRepository.EnsureIndexesAsync(database, cancellationToken);
                await MongoApiKeyRepository.EnsureIndexesAsync(database, cancellationToken);
                await MongoGitHistoryStore.EnsureIndexesAsync(database, cancellationToken);
                logger.LogInformation("MongoDB indexes ensured for database {Database}.", database.DatabaseNamespace.DatabaseName);
                return;
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException && attempt < MaxAttempts)
            {
                logger.LogWarning(ex, "MongoDB not ready (attempt {Attempt}/{Max}). Retrying.", attempt, MaxAttempts);
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
