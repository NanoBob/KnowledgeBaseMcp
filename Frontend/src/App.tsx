import { useCallback, useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, api, describeError, getAdminToken, setAdminToken } from './api'
import type { Directory, DocumentSummary } from './types'
import { ApiKeysPanel } from './components/ApiKeysPanel'
import { DirectoriesPanel } from './components/DirectoriesPanel'
import { DocumentsPanel } from './components/DocumentsPanel'
import type { Notifier } from './components/ui'

type Tab = 'documents' | 'directories' | 'keys'

const TABS: { id: Tab; label: string }[] = [
  { id: 'documents', label: 'Documents' },
  { id: 'directories', label: 'Directories' },
  { id: 'keys', label: 'API keys' },
]

export default function App() {
  const [token, setToken] = useState(getAdminToken())
  const [tokenInput, setTokenInput] = useState('')
  const [tab, setTab] = useState<Tab>('documents')
  const [directories, setDirectories] = useState<Directory[]>([])
  const [documents, setDocuments] = useState<DocumentSummary[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [unauthorized, setUnauthorized] = useState(false)

  const notifier = useMemo<Notifier>(
    () => ({
      error: (cause: unknown) => {
        setUnauthorized(cause instanceof ApiError && cause.status === 401)
        setError(describeError(cause))
        setNotice(null)
      },
      flash: (message: string) => {
        setNotice(message)
        setError(null)
      },
    }),
    [],
  )

  const reload = useCallback(async () => {
    if (getAdminToken().length === 0) return
    setLoading(true)
    try {
      const [nextDirectories, nextDocuments] = await Promise.all([api.listDirectories(), api.listDocuments()])
      setDirectories(nextDirectories)
      setDocuments(nextDocuments)
      setError(null)
      setUnauthorized(false)
    } catch (cause) {
      notifier.error(cause)
    } finally {
      setLoading(false)
    }
  }, [notifier])

  useEffect(() => {
    void reload()
  }, [reload, token])

  useEffect(() => {
    if (notice === null) return
    const timer = window.setTimeout(() => setNotice(null), 4000)
    return () => window.clearTimeout(timer)
  }, [notice])

  function signIn(event: FormEvent) {
    event.preventDefault()
    const value = tokenInput.trim()
    if (value.length === 0) return
    setAdminToken(value)
    setToken(value)
    setTokenInput('')
    setUnauthorized(false)
    setError(null)
  }

  function signOut() {
    setAdminToken('')
    setToken('')
    setDirectories([])
    setDocuments([])
    setError(null)
    setNotice(null)
  }

  if (token.length === 0 || unauthorized) {
    return (
      <div className="gate">
        <form className="gate-card" onSubmit={signIn}>
          <h1>Agent Knowledge Base</h1>
          <p className="muted">
            Enter the admin token from the server configuration to manage directories, documents and API keys.
          </p>
          {error !== null ? <div className="banner error">{error}</div> : null}
          <input
            type="password"
            value={tokenInput}
            onChange={(event) => setTokenInput(event.target.value)}
            placeholder="Admin token"
            aria-label="Admin token"
            autoFocus
          />
          <button type="submit" className="primary" disabled={tokenInput.trim().length === 0}>
            Unlock
          </button>
        </form>
      </div>
    )
  }

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <span className="brand-mark" role="img" aria-label="Agent Knowledge Base" />
          <div>
            <h1>Agent Knowledge Base</h1>
            <p>Directory-scoped documents for your agents</p>
          </div>
        </div>
        <div className="topbar-actions">
          <button type="button" onClick={() => void reload()} disabled={loading}>
            {loading ? 'Refreshing…' : 'Refresh'}
          </button>
          <button type="button" onClick={signOut}>
            Sign out
          </button>
        </div>
      </header>

      <nav className="tabs">
        {TABS.map((item) => (
          <button
            key={item.id}
            type="button"
            className={tab === item.id ? 'tab active' : 'tab'}
            onClick={() => setTab(item.id)}
          >
            {item.label}
            {item.id === 'directories' ? <span className="count">{directories.length}</span> : null}
            {item.id === 'documents' ? <span className="count">{documents.length}</span> : null}
          </button>
        ))}
      </nav>

      {error !== null ? <div className="banner error">{error}</div> : null}
      {notice !== null ? <div className="banner success">{notice}</div> : null}

      <main>
        {tab === 'directories' ? (
          <DirectoriesPanel
            directories={directories}
            documents={documents}
            notifier={notifier}
            onChanged={reload}
          />
        ) : null}
        {tab === 'documents' ? (
          <DocumentsPanel
            directories={directories}
            documents={documents}
            notifier={notifier}
            onChanged={reload}
          />
        ) : null}
        {tab === 'keys' ? <ApiKeysPanel directories={directories} notifier={notifier} /> : null}
      </main>
    </div>
  )
}
