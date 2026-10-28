# Agent Knowledge Base MCP Server

A shared knowledge base for AI agents. Agents store learnings as markdown documents and read them in later runs. The server speaks MCP over Streamable HTTP and stores documents in MongoDB.

## Tools

| Tool | Purpose |
|------|---------|
| `list_knowledge_documents` | List documents the key can read. Optional `pathPrefix`. |
| `get_knowledge_document` | Get one document, including markdown `content`. |
| `store_knowledge_document` | Create or update a document. Updates require `expectedVersion`. |

Updates must pass the `version` you last read. A stale version returns an error with the current version, so the agent can re-read and retry. Creating a document requires `path`, `title`, and `content`. `id` defaults to a slug of the title. An update replaces `title`, `summary`, `tags`, and `content`, so omitting `summary` clears it.

## Run with Docker Compose

1. Copy `.env.example` to `.env` and set `ADMIN_TOKEN` (at least 16 characters).
2. Start the stack:

   ```
   docker compose up -d --build
   ```

3. Check health at `http://localhost:8080/health`.

In Visual Studio, open `AgentKnowledgeBase.slnx`. The `AgentKnowledgeBase.Compose` project runs the compose stack.

Data is stored in the `mongo-data` named volume. Run `docker compose down -v` to delete it.

## Configuration

| Variable | Default | Description |
|----------|---------|-------------|
| `ADMIN_TOKEN` | (required) | Admin token for the key management API. Minimum 16 characters. |
| `MCP_PORT` | `8080` | Host port. |
| `Mongo__ConnectionString` | `mongodb://mongo:27017` | MongoDB connection string. |
| `Mongo__Database` | `agent_knowledge` | Database name. |

## Managing API keys

Keys are scoped to directory prefixes. Each scope is `read` or `readwrite`. The longest matching prefix decides, so a `read` scope can carve out a subdirectory of a `readwrite` scope.

Create a key (the plaintext is returned once):

```
curl -X POST http://localhost:8080/admin/api-keys \
  -H "X-Admin-Token: $ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"build-agent","scopes":[{"pathPrefix":"dotnet","access":"readwrite"},{"pathPrefix":"dotnet/secret","access":"read"}]}'
```

Other admin calls:

- `GET /admin/api-keys` lists keys without their secrets.
- `DELETE /admin/api-keys/{id}` revokes a key. This takes effect on the next request.

To change a key's scopes, create a new key and revoke the old one.

## Connecting a client

The MCP endpoint is `http://localhost:8080/mcp`. Send the key as a bearer token:

```
Authorization: Bearer akb_...
```

Example client configuration (Streamable HTTP):

```json
{
  "mcpServers": {
    "knowledge-base": {
      "url": "http://localhost:8080/mcp",
      "headers": { "Authorization": "Bearer akb_..." }
    }
  }
}
```

## Path and content rules

- Paths are lowercase segments of `a-z`, `0-9`, `.`, `_`, `-`, separated by `/`. Up to 10 segments. `..` is rejected.
- Title up to 200 characters. Summary up to 500 characters. Content up to 100 KB.
- Documents outside a key's scopes are invisible: they do not appear in listings, and `get` returns "not found".

## Development

```
dotnet build AgentKnowledgeBase.slnx
dotnet test AgentKnowledgeBase.slnx
```

Run locally against a Mongo instance by setting `Mongo__ConnectionString` and `Admin__Token` in user secrets or the environment.
