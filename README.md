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
| `list_knowledge_directories` | List the directories the key can read, with its `access` level (`read` or `readwrite`) per directory. Results are ordered by name. |
| `list_knowledge_documents` | List documents the key can read. Optional `directoryId`. |
| `get_knowledge_document` | Get one document by `id`, including markdown `content`. |
| `store_knowledge_document` | Create or update a document. Updates require `expectedVersion`. |

Creating a document requires `directoryId`, `name`, and `content`; the `id` is generated, so any agent with write access can add documents to its directories. Updating requires the `id` and the `version` you last read. A stale version returns a `409` telling the agent the document changed since it read it, so it refetches the document and re-applies its changes rather than overwriting the other agent's edit. An update replaces `summary`, `tags`, and `content`, so omitting `summary` clears it; omitting `name` keeps the current one. The agent tools never move a document between directories — that is an admin action (`POST /admin/documents/{id}/move` or the admin UI).

## Run with Docker Compose

1. Copy `.env.example` to `.env` and set `ADMIN_TOKEN` (at least 16 characters).
2. Start the stack:

   ```
   docker compose up -d --build
   ```

3. Check health at `http://localhost:8080/health`.

In Visual Studio, open `AgentKnowledgeBase.slnx`. The `AgentKnowledgeBase.Compose` project runs the compose stack.

Data is stored in the `agent-knowledge-base-data` named volume. The volume name is pinned in `docker-compose.yml`, so it does not depend on the Compose project name — otherwise starting the stack under a different project name (for example a new Visual Studio Compose launch, which generates a project name per run) attaches a fresh empty volume and existing data appears to vanish. Run `docker compose down -v` to delete the volume and all data.

### Publish the container image

`docker compose build mcp` produces the local image `agent-knowledge-base-mcp`. Push it to a registry with:

```
scripts\publish-container.bat [tag]
```

The tag defaults to `latest`. Set `AGENT_KNOWLEDGE_BASE_REGISTRY_IMAGE` to the target image without a tag — either in the environment or as a line in the git-ignored `.env` file — for example `AGENT_KNOWLEDGE_BASE_REGISTRY_IMAGE=registry.example.com/namespace/agent-knowledge-base-mcp`. The registry host is never stored in the repository.

## Configuration

| Variable | Default | Description |
|----------|---------|-------------|
| `ADMIN_TOKEN` | (required) | Admin token for the key management API. Minimum 16 characters. |
| `MCP_PORT` | `8080` | Host port. |
| `Mongo__ConnectionString` | `mongodb://mongo:27017` | MongoDB connection string. |
| `Mongo__Database` | `agent_knowledge` | Database name. |
| `Frontend__Origins__0`, `…__1`, … | `http://localhost:5173`, `http://localhost:4173` | Browser origins allowed to call the admin API. |
| `Git__Enabled` | `false` | Serves the knowledge base as a read-only git repository. |
| `Git__Path` | `/git/knowledge.git` | Path the repository is mounted at, which is also the clone URL. |
| `Git__Branch` | `main` | Branch a clone checks out. |
| `Git__AuthorName`, `Git__AuthorEmail` | `Knowledge Base`, `knowledge-base@localhost` | Author and committer of the generated commit. |
| `Git__HistoryDepth` | `20` | How many commits per key scope stay available for `git pull`. |

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

To change a key's scopes, call `PUT /admin/api-keys/{id}/scopes` with the full replacement scope list; the secret and
name stay the same, so agents pick up the new permissions on their next call. To rotate a leaked secret while keeping
its name and scopes, call `POST /admin/api-keys/{id}/regenerate`; the previous secret stops authenticating immediately.

Cloning the git repository needs a separate permission: send `"allowGitRead": true` when creating the key, or call
`PUT /admin/api-keys/{id}/git-access` with `{ "allowGitRead": true }` to grant it to an existing key. Keys without it
are refused on the git endpoint with `403` while continuing to work against `/mcp`.

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
| `POST` | `/admin/documents/{id}/move` | Move a document to another directory from `{ directoryId, expectedVersion? }`. |
| `DELETE` | `/admin/documents/{id}` | Delete a document and its revisions. |
| `GET` | `/admin/documents/{id}/revisions` | List revision metadata, newest first. |
| `GET` | `/admin/revisions/{id}` | Read one revision snapshot, including content. |
| `GET` | `/admin/api-keys` | List keys without their secrets. |
| `POST` | `/admin/api-keys` | Create a key; the plaintext key is returned once. |
| `POST` | `/admin/api-keys/{id}/regenerate` | Issue a new secret for the same key and scopes; the old secret stops working. Returns the new plaintext once. |
| `PUT` | `/admin/api-keys/{id}/scopes` | Replace a key's directory scopes in place, keeping its secret. |
| `PUT` | `/admin/api-keys/{id}/git-access` | Grant or withdraw git clone access from `{ allowGitRead }`, keeping the key's secret and scopes. |
| `DELETE` | `/admin/api-keys/{id}` | Revoke a key. This takes effect on the next request. |
| `POST` | `/admin/transfer/export` | Download a `.zip` with the documents of `{ directoryIds }`. |
| `POST` | `/admin/transfer/import` | Upload a `.zip` as `multipart/form-data` in the `file` field; `?conflict=overwrite\|skip\|fail` (default `overwrite`). |

`PUT /admin/documents/{id}` accepts an optional `expectedVersion`. Omit it to overwrite whatever is stored, or send the version you last read to get a `409` when someone else changed the document first.

`POST /admin/documents` rejects a request that carries `expectedVersion` with a `404`, because a create has no prior version to compare against. Omit it to create the document.

`POST /admin/documents/{id}/move` keeps the document's name, content and revision history, so the name must be free in the target directory; it bumps the version and records a revision that carries the new `directoryId`.

### Swagger UI

The API is also browsable at `/swagger`, driven by the OpenAPI document at `/openapi/v1.json`. Both are served in every environment, including the container, and need no token themselves; only the calls made from the page do.

Click **Authorize** and paste the admin token into the `X-Admin-Token` field. The UI keeps it across reloads, so every request can be sent from the page. Error responses carry the `AdminErrorResponse` envelope; a request the framework cannot bind answers with ProblemDetails as `application/problem+json`.

### Exporting and importing

`POST /admin/transfer/export` takes `{ "directoryIds": ["…"] }` and returns a `.zip` holding `manifest.json` plus one `.md` file per document, filed under a folder named after its directory:

```
manifest.json
dotnet/async-await.md
dotnet/minimal-apis.md
team/onboarding.md
```

The manifest carries the directory descriptions and each document's `name`, `summary` and `tags`, so a round trip restores names, summaries, tags and content. Ids, versions, timestamps and authors belong to the target knowledge base and are not carried. Empty directories are listed too, so they survive a round trip. The archive is streamed, so an export never holds the whole `.zip` in memory; `Transfer:MaxExportDocuments` (default `100000`) caps how many documents one export may contain. Because the status line and headers go out before the first byte of the archive, a failure while streaming (a database error, say) reaches the client as a truncated `200` zip rather than an error response.

`POST /admin/transfer/import` accepts that archive back. Directories that do not exist are created, an existing directory's description is updated from the manifest, and each document is stored with a new revision. `conflict` decides what happens when a document with the same name already exists in the directory: `overwrite` (default) replaces it, `skip` keeps the stored document, and `fail` rejects the whole import with a `409` before anything is written. The import is not transactional, so a failure part way through `overwrite` or `skip` leaves the documents already written in place. The response reports the counts:

```json
{ "directoriesCreated": 1, "documentsCreated": 2, "documentsUpdated": 0, "documentsSkipped": 0 }
```

An archive without a `manifest.json` is also accepted: every `.md` file is imported into a directory named after the folder that holds it, with the document name taken from the file name (sub-folders become part of the name). Folder names are slugified to fit the directory rules, so `Team Docs` becomes `team-docs` and `Café` becomes `cafe`; two folders that slugify to the same name are rejected. A folder name with no ASCII letters or digits left after slugifying (a Chinese or Japanese name, for example) has no usable directory name, so such an archive is rejected and needs a manifest. Such documents get an empty summary and no tags. A `.md` file that sits outside any folder has no directory to belong to, so an archive containing one is rejected rather than silently dropping it.

```bash
curl -X POST http://localhost:8080/admin/transfer/export \
  -H "X-Admin-Token: $ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"directoryIds":["<directory-id>"]}' -o knowledge.zip

curl -X POST "http://localhost:8080/admin/transfer/import?conflict=skip" \
  -H "X-Admin-Token: $ADMIN_TOKEN" -F "file=@knowledge.zip"
```

Errors come back as JSON with an HTTP status that matches the failure:

```json
{ "error": "document '…' has been updated since you read it: expected version 2, current version 3. Fetch it again with get_knowledge_document, re-apply your changes, and retry with expectedVersion=3.", "kind": "conflict", "latestVersion": 3 }
```

`kind` is one of `validation` (400), `notfound` (404), `forbidden` (403) or `conflict` (409).

A request that cannot be bound at all — malformed JSON, a `directoryId` that is not a GUID — is rejected before
the action runs and comes back as a `400` `application/problem+json` `ValidationProblemDetails` body instead of
the envelope above.

## Admin UI

`Frontend/` holds a small React + Vite single-page app for managing directories, documents, revisions and API keys.

```
cd Frontend
npm install
npm run dev
```

Open `http://localhost:5173` and paste the admin token; it is kept in `localStorage` and sent as `X-Admin-Token`. The dev server calls the API directly at `http://localhost:8080`, which the server allows through CORS for the origins in `Frontend:Origins` (`http://localhost:5173` and `http://localhost:4173` by default) — add any other UI origin there. Override the API URL with `VITE_API_BASE` in `Frontend/.env`.

Errors and confirmations appear as dismissible toasts stacked in the bottom-right corner, so they stay visible wherever you have scrolled and above any open dialog.

Screens:

- **Documents** — filter and search by directory, edit metadata and markdown content in an editor that fills the window, toggle a sanitized markdown preview sidebar next to the textarea, move a document to another directory, delete documents, and open the revision history to read any snapshot. A skeleton placeholder fills the editor while a document loads.
- **Directories** — create, rename and delete directories. A directory can only be deleted once it holds no documents.
- **API keys** — create keys with one `read` or `readwrite` scope per directory, edit the scopes of an existing key,
  copy the plaintext key once, regenerate a key to rotate its secret in place, and revoke keys.

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

## Read-only git repository

Setting `Git__Enabled=true` serves the knowledge base as a git repository at `http://localhost:8080/git/knowledge.git`, so a workspace can pin a snapshot as a submodule instead of calling the MCP tools on every read:

```bash
git clone http://git:<api-key>@localhost:8080/git/knowledge.git knowledge
git submodule add http://localhost:8080/git/knowledge.git knowledge
```

Every request builds the commit from the documents the key can read: `README.md` lists the directories, `manifest.json` uses the same format as the export, and each document is a `.md` file under a folder named after its directory. The key is accepted as a bearer token or as the password of HTTP basic auth (`git:<api-key>`); a request without a valid key is answered with `401` and a `Basic` challenge, which is what makes git prompt for credentials.

Only keys created with `allowGitRead` (or granted it later through `PUT /admin/api-keys/{id}/git-access`) may clone. Any other key gets `403` without a challenge, so a key handed to an agent for MCP calls cannot be reused to mirror the knowledge base.

A clone always contains the entire repository, but that repository only holds what the presenting key may read: a key scoped to two directories clones those two directories and nothing else, and documents outside them are never fetched. There is no shared repository with hidden parts, so two keys with different scopes see different commits at the same URL, and a submodule pinned by one key cannot be fetched with another. Granting a key more scopes changes its commit on the next fetch.

The repository is read-only for every client. Only `git-upload-pack` is served, and `git-receive-pack` answers `403`, so documents change through the admin API and the MCP tools, never through a push.

The server remembers the commits it handed out, so a clone keeps a real history: `git pull` fast-forwards to the current documents instead of reporting unrelated histories. A new commit is only created when the rendered tree actually changes, and its timestamp is pushed past its parent so history stays ordered. History is tracked per key scope, which means keys that can read the same directories share a history and keys with different scopes never see each other's commits. `Git__HistoryDepth` (default `20`, `1`–`1000`) is how many commits per scope stay available; older ones are pruned together with the content only they used, and a clone that fell further behind than that has to be re-cloned.

That history is the only data the git view adds, and it is written on the first clone rather than when documents change. Leaving `Git__Enabled=false`, or never cloning, keeps all three collections empty:

| Collection | Holds | Grows with |
| --- | --- | --- |
| `git_blobs` | File content, keyed by its hash | One copy per *distinct* version of a file, shared by every commit and scope |
| `git_commits` | The commit object plus its `path → blob` map | `Git__HistoryDepth` commits per scope, one entry per file |
| `git_heads` | `{ _id: scope, sha }` | One small document per key scope |

Content is shared, so a 100-document snapshot that changes one file stores one new blob, not a second copy of the tree; the commits themselves carry only paths and hashes. A scope therefore costs roughly `HistoryDepth` commit objects plus the versions of the files that changed inside that window.

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
