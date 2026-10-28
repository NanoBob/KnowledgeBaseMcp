import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import type { ApiKeySummary, Directory } from '../types'
import { Empty, Field, formatDate } from './ui'
import type { Notifier } from './ui'

interface ScopeRow {
  directoryId: string
  access: 'read' | 'readwrite'
}

interface Props {
  directories: Directory[]
  notifier: Notifier
}

export function ApiKeysPanel({ directories, notifier }: Props) {
  const [keys, setKeys] = useState<ApiKeySummary[]>([])
  const [loading, setLoading] = useState(true)
  const [name, setName] = useState('')
  const [scopes, setScopes] = useState<ScopeRow[]>([])
  const [creating, setCreating] = useState(false)
  const [createdKey, setCreatedKey] = useState<{ name: string; key: string } | null>(null)
  const [copied, setCopied] = useState(false)

  const directoryNames = useMemo(
    () => new Map(directories.map((directory) => [directory.id, directory.name])),
    [directories],
  )

  const firstDirectoryId = directories[0]?.id ?? ''

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setKeys(await api.listApiKeys())
    } catch (cause) {
      notifier.error(cause)
    } finally {
      setLoading(false)
    }
  }, [notifier])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    setScopes((current) =>
      current.length === 0 && firstDirectoryId !== '' ? [{ directoryId: firstDirectoryId, access: 'read' }] : current,
    )
  }, [firstDirectoryId])

  function updateScope(index: number, patch: Partial<ScopeRow>) {
    setScopes((current) => current.map((row, rowIndex) => (rowIndex === index ? { ...row, ...patch } : row)))
  }

  function addScope() {
    setScopes((current) => [...current, { directoryId: firstDirectoryId, access: 'read' }])
  }

  function removeScope(index: number) {
    setScopes((current) => current.filter((_, rowIndex) => rowIndex !== index))
  }

  async function create() {
    const rows = scopes.filter((row) => row.directoryId !== '')
    if (name.trim().length === 0) {
      notifier.error(new Error('Give the key a name.'))
      return
    }
    if (rows.length === 0) {
      notifier.error(new Error('Add at least one directory scope.'))
      return
    }

    setCreating(true)
    try {
      const created = await api.createApiKey({
        name: name.trim(),
        scopes: rows.map((row) => ({ directoryId: row.directoryId, access: row.access })),
      })
      setCreatedKey({ name: created.name, key: created.key })
      setCopied(false)
      setName('')
      setScopes(firstDirectoryId !== '' ? [{ directoryId: firstDirectoryId, access: 'read' }] : [])
      await load()
      notifier.flash(`API key "${created.name}" created.`)
    } catch (cause) {
      notifier.error(cause)
    } finally {
      setCreating(false)
    }
  }

  async function copyKey() {
    if (createdKey === null) return
    try {
      await navigator.clipboard.writeText(createdKey.key)
      setCopied(true)
    } catch {
      notifier.error(new Error('Clipboard access was blocked — copy the key manually.'))
    }
  }

  async function revoke(key: ApiKeySummary) {
    if (!window.confirm(`Revoke "${key.name}"? Agents using it lose access immediately.`)) return
    try {
      await api.revokeApiKey(key.id)
      await load()
      notifier.flash(`Revoked "${key.name}".`)
    } catch (cause) {
      notifier.error(cause)
    }
  }

  return (
    <section className="panel">
      <header className="panel-head">
        <div>
          <h2>API keys</h2>
          <p className="muted">
            Keys grant read or read-write access per directory. The plaintext key is shown once, right after creation.
          </p>
        </div>
      </header>

      <div className="card">
        <h3>Create a key</h3>
        <div className="form">
          <Field label="Name" hint="How the key shows up in this list, e.g. claude-desktop.">
            <input value={name} onChange={(event) => setName(event.target.value)} placeholder="my-agent" />
          </Field>

          <div className="scope-list">
            <span className="field-label">Directory scopes</span>
            {scopes.length === 0 ? (
              <p className="muted">No scopes yet — add at least one.</p>
            ) : (
              scopes.map((row, index) => (
                <div className="scope-row" key={`${row.directoryId}-${index}`}>
                  <select
                    value={row.directoryId}
                    onChange={(event) => updateScope(index, { directoryId: event.target.value })}
                    aria-label="Directory"
                  >
                    {directories.map((directory) => (
                      <option key={directory.id} value={directory.id}>
                        {directory.name}
                      </option>
                    ))}
                  </select>
                  <select
                    value={row.access}
                    onChange={(event) => updateScope(index, { access: event.target.value as ScopeRow['access'] })}
                    aria-label="Access"
                  >
                    <option value="read">read</option>
                    <option value="readwrite">read + write</option>
                  </select>
                  <button type="button" className="icon" onClick={() => removeScope(index)} aria-label="Remove scope">
                    ×
                  </button>
                </div>
              ))
            )}
            <button type="button" onClick={addScope} disabled={directories.length === 0}>
              Add scope
            </button>
          </div>

          <div className="form-actions">
            <button type="button" className="primary" onClick={() => void create()} disabled={creating || directories.length === 0}>
              {creating ? 'Creating…' : 'Create key'}
            </button>
          </div>
        </div>

        {createdKey !== null ? (
          <div className="secret">
            <div className="secret-head">
              <strong>Copy this key now</strong>
              <button type="button" onClick={() => void copyKey()}>
                {copied ? 'Copied' : 'Copy'}
              </button>
            </div>
            <code className="secret-value">{createdKey.key}</code>
            <p className="muted">It cannot be shown again. Store it in your MCP client configuration.</p>
          </div>
        ) : null}
      </div>

      {loading ? (
        <p className="muted">Loading keys…</p>
      ) : keys.length === 0 ? (
        <Empty>No API keys yet.</Empty>
      ) : (
        <table className="grid">
          <thead>
            <tr>
              <th>Name</th>
              <th>Scopes</th>
              <th>Created</th>
              <th>Status</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {keys.map((key) => (
              <tr key={key.id}>
                <td>{key.name}</td>
                <td>
                  <span className="tag-row">
                    {key.scopes.length === 0 ? (
                      <span className="muted">none</span>
                    ) : (
                      key.scopes.map((scope, index) => (
                        <span key={`${key.id}-${index}`} className="tag">
                          {scope.directoryId === null
                            ? 'all directories'
                            : (directoryNames.get(scope.directoryId) ?? scope.directoryId)}
                          <span className={scope.access === 'readwrite' ? 'access write' : 'access'}>{scope.access}</span>
                        </span>
                      ))
                    )}
                  </span>
                </td>
                <td className="muted">{formatDate(key.createdAt)}</td>
                <td>
                  {key.revokedAt === null ? (
                    <span className="status active">active</span>
                  ) : (
                    <span className="status revoked">revoked</span>
                  )}
                </td>
                <td className="row-actions">
                  {key.revokedAt === null ? (
                    <button type="button" className="danger" onClick={() => void revoke(key)}>
                      Revoke
                    </button>
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}
