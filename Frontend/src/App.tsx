import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, api, describeError, getAdminToken, setAdminToken } from './api'
import type { Directory, DocumentSummary, PendingKeySecret } from './types'
import { ApiKeysPanel } from './components/ApiKeysPanel'
import { DirectoriesPanel } from './components/DirectoriesPanel'
import { DocumentsPanel } from './components/DocumentsPanel'
import { ToastStack } from './components/ui'
import type { Notifier, ToastItem, ToastKind } from './components/ui'

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
  const [toasts, setToasts] = useState<ToastItem[]>([])
  const [pendingSecret, setPendingSecret] = useState<PendingKeySecret | null>(null)
  const [unauthorized, setUnauthorized] = useState(false)
  const nextToastId = useRef(0)
  const scheduledToasts = useRef(new Set<number>())

  const dismissToast = useCallback((id: number) => {
    scheduledToasts.current.delete(id)
    setToasts((current) => current.filter((toast) => toast.id !== id))
  }, [])

  const pushToast = useCallback((kind: ToastKind, message: string) => {
    nextToastId.current += 1
    const id = nextToastId.current
    setToasts((current) => {
      // Repeating the same message (e.g. retrying a failing save) shouldn't pile up.
      if (current.some((toast) => toast.kind === kind && toast.message === message)) return current
      return [...current, { id, kind, message }].slice(-4)
    })
  }, [])

  const clearToasts = useCallback((kind: ToastKind) => {
    setToasts((current) => current.filter((toast) => toast.kind !== kind))
  }, [])

  const notifier = useMemo<Notifier>(
    () => ({
      error: (cause: unknown) => {
        setUnauthorized(cause instanceof ApiError && cause.status === 401)
        pushToast('error', describeError(cause))
      },
      flash: (message: string) => {
        pushToast('success', message)
      },
    }),
    [pushToast],
  )

  const reload = useCallback(async () => {
    if (getAdminToken().length === 0) return
    setLoading(true)
    try {
      const [nextDirectories, nextDocuments] = await Promise.all([api.listDirectories(), api.listDocuments()])
      setDirectories(nextDirectories)
      setDocuments(nextDocuments)
      setUnauthorized(false)
      clearToasts('error')
    } catch (cause) {
      notifier.error(cause)
    } finally {
      setLoading(false)
    }
  }, [clearToasts, notifier])

  useEffect(() => {
    void reload()
  }, [reload, token])

  useEffect(() => {
    const live = new Set(toasts.map((toast) => toast.id))
    for (const id of scheduledToasts.current) {
      if (!live.has(id)) scheduledToasts.current.delete(id)
    }
    for (const toast of toasts) {
      if (scheduledToasts.current.has(toast.id)) continue
      scheduledToasts.current.add(toast.id)
      window.setTimeout(() => dismissToast(toast.id), toast.kind === 'error' ? 8000 : 4500)
    }
  }, [dismissToast, toasts])

  function signIn(event: FormEvent) {
    event.preventDefault()
    const value = tokenInput.trim()
    if (value.length === 0) return
    setAdminToken(value)
    setToken(value)
    setTokenInput('')
    setUnauthorized(false)
    clearToasts('error')
  }

  function signOut() {
    setAdminToken('')
    setToken('')
    setDirectories([])
    setDocuments([])
    setPendingSecret(null)
    setToasts([])
    scheduledToasts.current.clear()
  }

  if (token.length === 0 || unauthorized) {
    return (
      <>
        <div className="gate">
          <form className="gate-card" onSubmit={signIn}>
            <h1>Agent Knowledge Base</h1>
            <p className="muted">
              Enter the admin token from the server configuration to manage directories, documents and API keys.
            </p>
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
        <ToastStack toasts={toasts} onDismiss={dismissToast} />
      </>
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
        {tab === 'keys' ? (
          <ApiKeysPanel
            directories={directories}
            notifier={notifier}
            secret={pendingSecret}
            onSecretChange={setPendingSecret}
          />
        ) : null}
      </main>

      <ToastStack toasts={toasts} onDismiss={dismissToast} />
    </div>
  )
}
