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

public sealed class GitOptions
{
    /// <summary>
    /// Whether the read-only git endpoint is served. Off by default so an existing deployment does not start
    /// accepting git traffic without an explicit decision.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Path the repository is mounted at, e.g. <c>git clone https://host/git/knowledge.git</c>.</summary>
    [Required]
    public string Path { get; set; } = "/git/knowledge.git";

    /// <summary>Branch a clone checks out.</summary>
    [Required]
    public string Branch { get; set; } = "main";

    /// <summary>Author and committer recorded on the generated snapshot commit.</summary>
    [Required]
    public string AuthorName { get; set; } = "Knowledge Base";

    [Required, EmailAddress]
    public string AuthorEmail { get; set; } = "knowledge-base@localhost";

    /// <summary>
    /// How many commits a scope keeps. A clone only needs the commit it has plus the new one to fast-forward,
    /// so a short history is enough; the limit keeps the stored snapshots from growing without bound.
    /// </summary>
    [Range(1, 1000)]
    public int HistoryDepth { get; set; } = 20;
}
