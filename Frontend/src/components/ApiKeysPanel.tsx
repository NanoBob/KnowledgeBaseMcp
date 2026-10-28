import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import type { ApiKeySummary, Directory, KeyScope, PendingKeySecret } from '../types'
import { Empty, Field, Modal, formatDate } from './ui'
import type { Notifier } from './ui'

interface ScopeRow {
  directoryId: string
  access: 'read' | 'readwrite'
}

interface Props {
  directories: Directory[]
  notifier: Notifier
  secret: PendingKeySecret | null
  onSecretChange: (secret: PendingKeySecret | null) => void
}

function ScopeEditor({
  directories,
  scopes,
  onChange,
  defaultDirectoryId,
}: {
  directories: Directory[]
  scopes: ScopeRow[]
  onChange: (scopes: ScopeRow[]) => void
  defaultDirectoryId: string
}) {
  function updateScope(index: number, patch: Partial<ScopeRow>) {
    onChange(scopes.map((row, rowIndex) => (rowIndex === index ? { ...row, ...patch } : row)))
  }

  function addScope() {
    onChange([...scopes, { directoryId: defaultDirectoryId, access: 'read' }])
  }

  function removeScope(index: number) {
    onChange(scopes.filter((_, rowIndex) => rowIndex !== index))
  }

  return (
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
              {directories.some((directory) => directory.id === row.directoryId) ? null : (
                <option value={row.directoryId}>unknown directory</option>
              )}
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
  )
}

function toScopeRows(scopes: KeyScope[]): ScopeRow[] {
  return scopes.map((scope) => ({
    directoryId: scope.directoryId,
    access: scope.access === 'readwrite' ? 'readwrite' : 'read',
  }))
}

export function ApiKeysPanel({ directories, notifier, secret, onSecretChange }: Props) {
  const [keys, setKeys] = useState<ApiKeySummary[]>([])
  const [loading, setLoading] = useState(true)
  const [name, setName] = useState('')
  const [scopes, setScopes] = useState<ScopeRow[]>([])
  const [creating, setCreating] = useState(false)
  const [regeneratingId, setRegeneratingId] = useState<string | null>(null)
  const [editing, setEditing] = useState<{ key: ApiKeySummary; scopes: ScopeRow[] } | null>(null)
  const [savingScopes, setSavingScopes] = useState(false)
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

  function startEditScopes(key: ApiKeySummary) {
    setEditing({ key, scopes: toScopeRows(key.scopes) })
  }

  async function saveScopes() {
    if (editing === null) return

    // A scope can outlive its directory, and the API rejects unknown directories, which would make
    // the whole key unsavable. Drop those rows, but only after the user agrees.
    const known = new Set(directories.map((directory) => directory.id))
    const rows = editing.scopes.filter((row) => row.directoryId !== '' && known.has(row.directoryId))
    const dropped = editing.scopes.length - rows.length
    if (dropped > 0) {
      const names = editing.scopes
        .filter((row) => !known.has(row.directoryId))
        .map((row) => directoryNames.get(row.directoryId) ?? 'a deleted directory')
      if (
        !window.confirm(
          `Drop ${dropped} scope${dropped === 1 ? '' : 's'} pointing at ${names.join(', ')}? ` +
            'The key cannot keep permissions on a directory that no longer exists.',
        )
      ) {
        return
      }
    }

    if (rows.length === 0) {
      notifier.error(new Error('Keep at least one directory scope.'))
      return
    }

    setSavingScopes(true)
    try {
      await api.updateApiKeyScopes(
        editing.key.id,
        rows.map((row) => ({ directoryId: row.directoryId, access: row.access })),
      )
      const keyName = editing.key.name
      setEditing(null)
      await load()
      notifier.flash(`Updated directory permissions for "${keyName}".`)
    } catch (cause) {
      notifier.error(cause)
    } finally {
      setSavingScopes(false)
    }
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
      onSecretChange({ name: created.name, key: created.key, action: 'created' })
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
    if (secret === null) return
    try {
      await navigator.clipboard.writeText(secret.key)
      setCopied(true)
    } catch {
      notifier.error(new Error('Clipboard access was blocked — copy the key manually.'))
    }
  }

  async function regenerate(key: ApiKeySummary) {
    if (
      !window.confirm(
        `Regenerate "${key.name}"? The current key stops working immediately; the new key keeps the same permissions and is shown once.`,
      )
    ) {
      return
    }

    setRegeneratingId(key.id)
    try {
      const rotated = await api.regenerateApiKey(key.id)
      onSecretChange({ name: rotated.name, key: rotated.key, action: 'regenerated' })
      setCopied(false)
      await load()
      notifier.flash(`Regenerated "${rotated.name}".`)
    } catch (cause) {
      notifier.error(cause)
    } finally {
      setRegeneratingId(null)
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
            Keys grant read or read-write access per directory, and their permissions can be changed later without
            issuing a new secret. The plaintext key is shown once, right after creation or regeneration.
          </p>
        </div>
      </header>

      <div className="card">
        <h3>Create a key</h3>
        <div className="form">
          <Field label="Name" hint="How the key shows up in this list, e.g. claude-desktop.">
            <input value={name} onChange={(event) => setName(event.target.value)} placeholder="my-agent" />
          </Field>

          <ScopeEditor
            directories={directories}
            scopes={scopes}
            onChange={setScopes}
            defaultDirectoryId={firstDirectoryId}
          />

          <div className="form-actions">
            <button type="button" className="primary" onClick={() => void create()} disabled={creating || directories.length === 0}>
              {creating ? 'Creating…' : 'Create key'}
            </button>
          </div>
        </div>

        {secret !== null && secret.action === 'created' ? (
          <div className="secret">
            <div className="secret-head">
              <strong>Copy this key now</strong>
              <button type="button" onClick={() => void copyKey()}>
                {copied ? 'Copied' : 'Copy'}
              </button>
            </div>
            <code className="secret-value">{secret.key}</code>
            <p className="muted">It cannot be shown again. Store it in your MCP client configuration.</p>
          </div>
        ) : null}
      </div>

      {secret !== null && secret.action === 'regenerated' ? (
        <div className="card secret">
          <div className="secret-head">
            <strong>New key for “{secret.name}”</strong>
            <button type="button" onClick={() => void copyKey()}>
              {copied ? 'Copied' : 'Copy'}
            </button>
          </div>
          <code className="secret-value">{secret.key}</code>
          <p className="muted">
            The previous key no longer works and the permissions are unchanged. This key cannot be shown again.
          </p>
        </div>
      ) : null}

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
                          {directoryNames.get(scope.directoryId) ?? scope.directoryId}
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
                  {key.rotatedAt !== null ? (
                    <div className="muted">rotated {formatDate(key.rotatedAt)}</div>
                  ) : null}
                </td>
                <td className="row-actions">
                  {key.revokedAt === null ? (
                    <>
                      <button type="button" onClick={() => startEditScopes(key)} disabled={directories.length === 0}>
                        Edit scopes
                      </button>
                      <button
                        type="button"
                        onClick={() => void regenerate(key)}
                        disabled={regeneratingId === key.id}
                      >
                        {regeneratingId === key.id ? 'Regenerating…' : 'Regenerate'}
                      </button>
                      <button type="button" className="danger" onClick={() => void revoke(key)}>
                        Revoke
                      </button>
                    </>
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {editing !== null ? (
        <Modal title={`Permissions for "${editing.key.name}"`} onClose={() => setEditing(null)}>
          <p className="muted">
            The key itself stays the same — agents pick up the new permissions on their next call.
          </p>
          <ScopeEditor
            directories={directories}
            scopes={editing.scopes}
            onChange={(rows) => setEditing({ ...editing, scopes: rows })}
            defaultDirectoryId={firstDirectoryId}
          />
          <div className="modal-actions">
            <button type="button" className="primary" onClick={() => void saveScopes()} disabled={savingScopes}>
              {savingScopes ? 'Saving…' : 'Save permissions'}
            </button>
            <button type="button" onClick={() => setEditing(null)} disabled={savingScopes}>
              Cancel
            </button>
          </div>
        </Modal>
      ) : null}
    </section>
  )
}
