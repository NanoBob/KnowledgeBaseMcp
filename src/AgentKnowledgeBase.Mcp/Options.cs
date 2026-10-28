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
