import type {
  ApiKeyCreated,
  ApiKeySummary,
  Directory,
  DocumentDetail,
  DocumentSummary,
  ExportArchive,
  ImportConflictMode,
  ImportResult,
  KeyScope,
  RevisionDetail,
  RevisionSummary,
  StoreDocumentResult,
} from './types'

// The dev server talks to the API directly; a production build defaults to the origin that serves it.
const BASE: string =
  import.meta.env.VITE_API_BASE ?? (import.meta.env.DEV ? 'http://localhost:8080' : '')
const TOKEN_STORAGE_KEY = 'akb.adminToken'

export class ApiError extends Error {
  readonly status: number
  readonly kind: string | undefined
  readonly latestVersion: number | null

  constructor(status: number, message: string, kind?: string, latestVersion?: number | null) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.kind = kind
    this.latestVersion = latestVersion ?? null
  }
}

export function describeError(cause: unknown): string {
  if (cause instanceof ApiError) return cause.message
  if (cause instanceof Error) return cause.message
  return String(cause)
}

function readStoredToken(): string {
  try {
    return window.localStorage.getItem(TOKEN_STORAGE_KEY) ?? ''
  } catch {
    return ''
  }
}

let adminToken = readStoredToken()

export function getAdminToken(): string {
  return adminToken
}

export function setAdminToken(token: string): void {
  adminToken = token
  try {
    if (token.length > 0) window.localStorage.setItem(TOKEN_STORAGE_KEY, token)
    else window.localStorage.removeItem(TOKEN_STORAGE_KEY)
  } catch {
    // Storage can be blocked; the token still works for this session.
  }
}

interface ErrorBody {
  error?: string
  kind?: string
  latestVersion?: number | null
  // ProblemDetails shape emitted by MVC model validation failures.
  title?: string
  detail?: string
  extensions?: { kind?: string; latestVersion?: number | null }
  errors?: Record<string, string[]>
}

interface DescribedError {
  message: string | undefined
  kind: string | undefined
  latestVersion: number | null
}

function describeErrorBody(body: ErrorBody): DescribedError {
  const kind = body.kind ?? body.extensions?.kind
  const latestVersion = body.latestVersion ?? body.extensions?.latestVersion ?? null

  const fieldErrors = body.errors
    ? Object.entries(body.errors).flatMap(([field, messages]) =>
        (messages ?? []).map((text) => `${field}: ${text}`),
      )
    : []

  if (fieldErrors.length > 0) {
    return {
      message: `${body.title ?? 'The request was rejected.'} ${fieldErrors.join(' ')}`,
      kind,
      latestVersion,
    }
  }

  return { message: body.error ?? body.detail ?? body.title, kind, latestVersion }
}

function baseHeaders(init: RequestInit): Headers {
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  // FormData has to keep the browser-generated multipart boundary, so only string bodies are JSON.
  if (typeof init.body === 'string') headers.set('Content-Type', 'application/json')
  if (adminToken.length > 0) headers.set('X-Admin-Token', adminToken)
  return headers
}

async function failure(response: Response): Promise<never> {
  let message = `Request failed with status ${response.status}.`
  let kind: string | undefined
  let latestVersion: number | null = null
  try {
    const body = (await response.json()) as ErrorBody
    const described = describeErrorBody(body)
    if (described.message) message = described.message
    kind = described.kind
    latestVersion = described.latestVersion
  } catch {
    // Non-JSON error body, keep the status message.
  }
  if (response.status === 401) message = 'The admin token was rejected. Check the token and try again.'
  throw new ApiError(response.status, message, kind, latestVersion)
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(`${BASE}${path}`, { ...init, headers: baseHeaders(init) })
  if (!response.ok) await failure(response)

  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

function filenameFrom(disposition: string | null): string {
  if (disposition === null) return 'knowledge-export.zip'
  const quoted = /filename="([^"]+)"/i.exec(disposition)
  if (quoted !== null) return quoted[1]
  const bare = /filename=([^;]+)/i.exec(disposition)
  return bare !== null ? bare[1].trim() : 'knowledge-export.zip'
}

async function download(path: string, init: RequestInit): Promise<ExportArchive> {
  const headers = baseHeaders(init)
  headers.set('Accept', 'application/zip, application/json')
  const response = await fetch(`${BASE}${path}`, { ...init, headers })
  if (!response.ok) await failure(response)

  return {
    blob: await response.blob(),
    filename: filenameFrom(response.headers.get('Content-Disposition')),
  }
}

export interface DirectoryInput {
  name: string
  description: string
}

export interface DocumentInput {
  name: string
  summary: string
  tags: string[]
  content: string
}

export interface NewDocumentInput extends DocumentInput {
  directoryId: string
}

export interface UpdateDocumentInput extends DocumentInput {
  expectedVersion?: number
}

export interface NewApiKeyInput {
  name: string
  scopes: KeyScope[]
}

export const api = {
  listDirectories: () => request<Directory[]>('/admin/directories'),
  createDirectory: (input: DirectoryInput) =>
    request<Directory>('/admin/directories', { method: 'POST', body: JSON.stringify(input) }),
  updateDirectory: (id: string, input: DirectoryInput) =>
    request<Directory>(`/admin/directories/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  deleteDirectory: (id: string) => request<void>(`/admin/directories/${id}`, { method: 'DELETE' }),

  listDocuments: () => request<DocumentSummary[]>('/admin/documents'),
  getDocument: (id: string) => request<DocumentDetail>(`/admin/documents/${id}`),
  createDocument: (input: NewDocumentInput) =>
    request<StoreDocumentResult>('/admin/documents', { method: 'POST', body: JSON.stringify(input) }),
  updateDocument: (id: string, input: UpdateDocumentInput) =>
    request<StoreDocumentResult>(`/admin/documents/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  moveDocument: (id: string, directoryId: string, expectedVersion?: number) =>
    request<StoreDocumentResult>(`/admin/documents/${id}/move`, {
      method: 'POST',
      body: JSON.stringify({ directoryId, expectedVersion }),
    }),
  deleteDocument: (id: string) => request<void>(`/admin/documents/${id}`, { method: 'DELETE' }),
  listRevisions: (documentId: string) => request<RevisionSummary[]>(`/admin/documents/${documentId}/revisions`),
  getRevision: (revisionId: string) => request<RevisionDetail>(`/admin/revisions/${revisionId}`),
  restoreRevision: (revisionId: string, expectedVersion?: number) =>
    request<StoreDocumentResult>(`/admin/revisions/${revisionId}/restore`, {
      method: 'POST',
      body: JSON.stringify({ expectedVersion }),
    }),

  listApiKeys: () => request<ApiKeySummary[]>('/admin/api-keys'),
  createApiKey: (input: NewApiKeyInput) =>
    request<ApiKeyCreated>('/admin/api-keys', { method: 'POST', body: JSON.stringify(input) }),
  revokeApiKey: (id: string) => request<void>(`/admin/api-keys/${id}`, { method: 'DELETE' }),
  regenerateApiKey: (id: string) =>
    request<ApiKeyCreated>(`/admin/api-keys/${id}/regenerate`, { method: 'POST' }),
  updateApiKeyScopes: (id: string, scopes: KeyScope[]) =>
    request<ApiKeySummary>(`/admin/api-keys/${id}/scopes`, { method: 'PUT', body: JSON.stringify({ scopes }) }),

  exportDocuments: (directoryIds: string[]) =>
    download('/admin/transfer/export', { method: 'POST', body: JSON.stringify({ directoryIds }) }),
  importDocuments: (file: File, conflict: ImportConflictMode) => {
    const body = new FormData()
    body.append('file', file)
    return request<ImportResult>(`/admin/transfer/import?conflict=${encodeURIComponent(conflict)}`, {
      method: 'POST',
      body,
    })
  },
}
