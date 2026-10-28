import { useState } from 'react'
import type { FormEvent } from 'react'
import { api } from '../api'
import type { Directory, DocumentSummary } from '../types'
import { Empty, Field, Modal, formatDate } from './ui'
import type { Notifier } from './ui'

interface Props {
  directories: Directory[]
  documents: DocumentSummary[]
  notifier: Notifier
  onChanged: () => Promise<void>
}

export function DirectoriesPanel({ directories, documents, notifier, onChanged }: Props) {
  const [editing, setEditing] = useState<Directory | 'new' | null>(null)
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [busy, setBusy] = useState(false)

  function openCreate() {
    setEditing('new')
    setName('')
    setDescription('')
  }

  function openEdit(directory: Directory) {
    setEditing(directory)
    setName(directory.name)
    setDescription(directory.description)
  }

  async function save(event: FormEvent) {
    event.preventDefault()
    if (editing === null) return
    setBusy(true)
    try {
      if (editing === 'new') {
        const created = await api.createDirectory({ name, description })
        notifier.flash(`Directory "${created.name}" created.`)
      } else {
        const updated = await api.updateDirectory(editing.id, { name, description })
        notifier.flash(`Directory "${updated.name}" updated.`)
      }
      setEditing(null)
      await onChanged()
    } catch (cause) {
      notifier.error(cause)
    } finally {
      setBusy(false)
    }
  }

  async function remove(directory: Directory) {
    const documentCount = documents.filter((document) => document.directoryId === directory.id).length
    if (documentCount > 0) {
      notifier.error(new Error(`"${directory.name}" still holds ${documentCount} document(s). Delete them first.`))
      return
    }
    if (!window.confirm(`Delete directory "${directory.name}"?`)) return
    setBusy(true)
    try {
      await api.deleteDirectory(directory.id)
      await onChanged()
      notifier.flash(`Directory "${directory.name}" deleted.`)
    } catch (cause) {
      notifier.error(cause)
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="panel">
      <header className="panel-head">
        <div>
          <h2>Directories</h2>
          <p className="muted">
            API keys are scoped to directories, and documents live directly inside one. Directories cannot be nested.
          </p>
        </div>
        <button type="button" className="primary" onClick={openCreate}>
          New directory
        </button>
      </header>

      {directories.length === 0 ? (
        <Empty>No directories yet. Create one to start storing documents.</Empty>
      ) : (
        <table className="grid">
          <thead>
            <tr>
              <th>Name</th>
              <th>Description</th>
              <th className="numeric">Documents</th>
              <th>Created</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {directories.map((directory) => (
              <tr key={directory.id}>
                <td>
                  <code>{directory.name}</code>
                </td>
                <td>{directory.description.length > 0 ? directory.description : <span className="muted">—</span>}</td>
                <td className="numeric">{documents.filter((document) => document.directoryId === directory.id).length}</td>
                <td className="muted">{formatDate(directory.createdAt)}</td>
                <td className="row-actions">
                  <button type="button" onClick={() => openEdit(directory)} disabled={busy}>
                    Edit
                  </button>
                  <button type="button" className="danger" onClick={() => void remove(directory)} disabled={busy}>
                    Delete
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {editing !== null ? (
        <Modal title={editing === 'new' ? 'New directory' : `Edit ${editing.name}`} onClose={() => setEditing(null)}>
          <form className="form" onSubmit={save}>
            <Field label="Name" hint="Lowercase letters, digits, dots, dashes and underscores. Max 64 characters.">
              <input
                value={name}
                onChange={(event) => setName(event.target.value)}
                placeholder="engineering"
                autoFocus
                required
              />
            </Field>
            <Field label="Description" hint="Optional. Shown in the admin UI only.">
              <input
                value={description}
                onChange={(event) => setDescription(event.target.value)}
                placeholder="Runbooks and architecture notes"
              />
            </Field>
            <div className="form-actions">
              <button type="button" onClick={() => setEditing(null)} disabled={busy}>
                Cancel
              </button>
              <button type="submit" className="primary" disabled={busy}>
                {busy ? 'Saving…' : 'Save'}
              </button>
            </div>
          </form>
        </Modal>
      ) : null}
    </section>
  )
}
