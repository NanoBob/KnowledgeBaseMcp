# Agent Knowledge Base MCP Server

A shared knowledge base for AI agents. Agents store learnings as markdown documents and read them in later runs. The server speaks MCP over Streamable HTTP and stores documents in MongoDB.

## Model

Three entities, all identified by GUID:

- **Directory** — a flat grouping of documents. Directories never nest, so a document belongs to exactly one directory.
- **Document** — a `directoryId`, a human-readable `name` that is unique within its directory (case-insensitive), plus `summary`, `tags` and Markdown `content`.
- **Revision** — an immutable snapshot written on every create and update.

## Tools

| Tool | Purpose |
|------|---------|
| `list_knowledge_directories` | List the directories the key can read, with its `access` level (`read` or `readwrite`) per directory. |
| `list_knowledge_documents` | List documents the key can read. Optional `directoryId`. |
| `get_knowledge_document` | Get one document by `id`, including markdown `content`. |
| `store_knowledge_document` | Create or update a document. Updates require `expectedVersion`. |

Creating a document requires `directoryId`, `name`, and `content`; the `id` is generated, so any agent with write access can add documents to its directories. Updating requires the `id` and the `version` you last read. A stale version returns an error with the current version, so the agent can re-read and retry. An update replaces `summary`, `tags`, and `content`, so omitting `summary` clears it; omitting `name` keeps the current one. A document cannot be moved to another directory.

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
| `Frontend__Origins__0`, `…__1`, … | `http://localhost:5173`, `http://localhost:4173` | Browser origins allowed to call the admin API. |

## Managing directories and API keys

Keys are scoped to whole directories. Each scope names a `directoryId` and is either `read` or `readwrite`. A key can only read or write documents inside the directories it lists; documents elsewhere behave as if they do not exist.

Create a directory:

```
curl -X POST http://localhost:8080/admin/directories \
  -H "X-Admin-Token: $ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"dotnet","description":"Reusable .NET knowledge"}'
```

Create a key (the plaintext is returned once):

```
curl -X POST http://localhost:8080/admin/api-keys \
  -H "X-Admin-Token: $ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"build-agent","scopes":[{"directoryId":"<directory-guid>","access":"readwrite"}]}'
```

To change a key's scopes, create a new key and revoke the old one. To rotate a leaked secret while keeping its name
and scopes, call `POST /admin/api-keys/{id}/regenerate`; the previous secret stops authenticating immediately.

## Admin REST API

Every call needs the admin token in an `X-Admin-Token` header (or `Authorization: Bearer`). The token is not scoped: it reaches every directory.

| Method | Path | Purpose |
|--------|------|---------|
| `GET` | `/admin/directories` | List all directories. |
| `POST` | `/admin/directories` | Create a directory from `{ name, description }`. |
| `GET` | `/admin/directories/{id}` | Read one directory. |
| `PUT` | `/admin/directories/{id}` | Rename or re-describe a directory. |
| `DELETE` | `/admin/directories/{id}` | Delete an empty directory. |
| `GET` | `/admin/documents` | List documents, optionally filtered by `?directoryId=`. |
| `POST` | `/admin/documents` | Create a document from `{ directoryId, name, summary, tags, content }`. |
| `GET` | `/admin/documents/{id}` | Read a document, including content. |
| `PUT` | `/admin/documents/{id}` | Update a document. Sends a new revision. |
| `DELETE` | `/admin/documents/{id}` | Delete a document and its revisions. |
| `GET` | `/admin/documents/{id}/revisions` | List revision metadata, newest first. |
| `GET` | `/admin/revisions/{id}` | Read one revision snapshot, including content. |
| `GET` | `/admin/api-keys` | List keys without their secrets. |
| `POST` | `/admin/api-keys` | Create a key; the plaintext key is returned once. |
| `POST` | `/admin/api-keys/{id}/regenerate` | Issue a new secret for the same key and scopes; the old secret stops working. Returns the new plaintext once. |
| `DELETE` | `/admin/api-keys/{id}` | Revoke a key. This takes effect on the next request. |

`PUT /admin/documents/{id}` accepts an optional `expectedVersion`. Omit it to overwrite whatever is stored, or send the version you last read to get a `409` when someone else changed the document first.

Errors come back as JSON with an HTTP status that matches the failure:

```json
{ "error": "document '…' is at version 3. Retry with expectedVersion=3 to update it.", "kind": "conflict", "latestVersion": 3 }
```

`kind` is one of `validation` (400), `notfound` (404), `forbidden` (403) or `conflict` (409).

## Admin UI

`Frontend/` holds a small React + Vite single-page app for managing directories, documents, revisions and API keys.

```
cd Frontend
npm install
npm run dev
```

Open `http://localhost:5173` and paste the admin token; it is kept in `localStorage` and sent as `X-Admin-Token`. The dev server proxies `/admin` and `/health` to `http://localhost:8080`, so no CORS setup is needed. Point it elsewhere with `AKB_API_URL`, or call the API directly by setting `VITE_API_BASE` in `Frontend/.env` — the server allows the origins in `Frontend:Origins` (`http://localhost:5173` and `http://localhost:4173` by default).

Screens:

- **Documents** — filter and search by directory, edit metadata and markdown content, delete documents, and open the revision history to read any snapshot.
- **Directories** — create, rename and delete directories. A directory can only be deleted once it holds no documents.
- **API keys** — create keys with one `read` or `readwrite` scope per directory, copy the plaintext key once, regenerate a key to rotate its secret in place, and revoke keys.

`npm run build` produces a static bundle in `Frontend/dist`.

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

## Name and content rules

- Directory names are lowercase `a-z`, `0-9`, `.`, `_`, `-`, up to 64 characters, and unique. Directories never nest.
- Document names are free text up to 200 characters and unique per directory, ignoring case. Summary up to 500 characters. Content up to 100 KB.
- Documents outside a key's directories are invisible: they do not appear in listings, and `get` returns "not found".

## Development

```
dotnet build AgentKnowledgeBase.slnx
dotnet test AgentKnowledgeBase.slnx
```

Run locally against a Mongo instance by setting `Mongo__ConnectionString` and `Admin__Token` in user secrets or the environment.
