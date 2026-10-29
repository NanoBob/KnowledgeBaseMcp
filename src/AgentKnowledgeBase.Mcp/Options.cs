using System.ComponentModel.DataAnnotations;

namespace AgentKnowledgeBase.Mcp;

public sealed class MongoOptions
{
    [Required]
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";

    [Required]
    public string Database { get; set; } = "agent_knowledge";
}

public sealed class AdminOptions
{
    /// <summary>Bearer token that guards the key management API. Supplied via Admin__Token.</summary>
    [Required, MinLength(16)]
    public string Token { get; set; } = "";
}

public sealed class TransferOptions
{
    /// <summary>Largest .zip accepted by the import endpoint, in bytes.</summary>
    [Range(1, int.MaxValue)]
    public int MaxArchiveBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>Largest total uncompressed content read from an import archive, in bytes.</summary>
    [Range(1, int.MaxValue)]
    public int MaxDecompressedBytes { get; set; } = 200 * 1024 * 1024;

    /// <summary>Most documents a single import may contain.</summary>
    [Range(1, 100_000)]
    public int MaxDocuments { get; set; } = 10_000;

    /// <summary>
    /// Most documents a single export may contain. The archive is streamed, so this bounds the work and the
    /// download size rather than the memory a request holds.
    /// </summary>
    [Range(1, 1_000_000)]
    public int MaxExportDocuments { get; set; } = 100_000;
}
