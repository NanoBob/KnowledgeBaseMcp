import type {
  ApiKeyCreated,
  ApiKeySummary,
  Directory,
  DocumentDetail,
  DocumentSummary,
  KeyScope,
  RevisionDetail,
  RevisionSummary,
  StoreDocumentResult,
} from './types'

const BASE: string = import.meta.env.VITE_API_BASE ?? ''
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
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  if (init.body !== undefined) headers.set('Content-Type', 'application/json')
  if (adminToken.length > 0) headers.set('X-Admin-Token', adminToken)

  const response = await fetch(`${BASE}${path}`, { ...init, headers })
  if (!response.ok) {
    let message = `Request failed with status ${response.status}.`
    let kind: string | undefined
    let latestVersion: number | null = null
    try {
      const body = (await response.json()) as ErrorBody
      if (body.error) message = body.error
      kind = body.kind
      latestVersion = body.latestVersion ?? null
    } catch {
      // Non-JSON error body, keep the status message.
    }
    if (response.status === 401) message = 'The admin token was rejected. Check the token and try again.'
    throw new ApiError(response.status, message, kind, latestVersion)
  }

  if (response.status === 204) return undefined as T
  return (await response.json()) as T
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
  deleteDocument: (id: string) => request<void>(`/admin/documents/${id}`, { method: 'DELETE' }),
  listRevisions: (documentId: string) => request<RevisionSummary[]>(`/admin/documents/${documentId}/revisions`),
  getRevision: (revisionId: string) => request<RevisionDetail>(`/admin/revisions/${revisionId}`),

  listApiKeys: () => request<ApiKeySummary[]>('/admin/api-keys'),
  createApiKey: (input: NewApiKeyInput) =>
    request<ApiKeyCreated>('/admin/api-keys', { method: 'POST', body: JSON.stringify(input) }),
  revokeApiKey: (id: string) => request<void>(`/admin/api-keys/${id}`, { method: 'DELETE' }),
}
