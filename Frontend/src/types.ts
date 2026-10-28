export interface Directory {
  id: string
  name: string
  description: string
  createdAt: string
}

export interface DocumentSummary {
  id: string
  directoryId: string
  directoryName: string
  name: string
  summary: string
  tags: string[]
  version: number
  updatedAt: string
}

export interface DocumentDetail extends DocumentSummary {
  content: string
  createdAt: string
  createdBy: string
  updatedBy: string
}

export interface StoreDocumentResult {
  id: string
  directoryId: string
  name: string
  version: number
  created: boolean
}

export interface RevisionSummary {
  id: string
  documentId: string
  version: number
  name: string
  changedAt: string
  changedBy: string
}

export interface RevisionDetail extends RevisionSummary {
  directoryId: string
  summary: string
  tags: string[]
  content: string
}

export interface KeyScope {
  directoryId: string | null
  access: string
}

export interface ApiKeySummary {
  id: string
  name: string
  scopes: KeyScope[]
  createdAt: string
  rotatedAt: string | null
  revokedAt: string | null
}

export interface ApiKeyCreated extends ApiKeySummary {
  key: string
}
