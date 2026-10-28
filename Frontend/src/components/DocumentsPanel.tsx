import { useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, api, describeError } from '../api'
import type { Directory, DocumentDetail, DocumentSummary, RevisionDetail, RevisionSummary } from '../types'
import { Empty, Field, Modal, Spinner, formatDate } from './ui'
import type { Notifier } from './ui'
import { Markdown } from './Markdown'

interface Draft {
  directoryId: string
  name: string
  summary: string
  tags: string
  content: string
}

type Selection = { kind: 'none' } | { kind: 'new' } | { kind: 'document'; id: string }

const NO_SELECTION: Selection = { kind: 'none' }

const PREVIEW_KEY = 'akb.editorPreview'

function emptyDraft(directoryId: string): Draft {
  return { directoryId, name: '', summary: '', tags: '', content: '' }
}

function fromDetail(detail: DocumentDetail): Draft {
  return {
    directoryId: detail.directoryId,
    name: detail.name,
    summary: detail.summary,
    tags: detail.tags.join(', '),
    content: detail.content,
  }
}

function parseTags(value: string): string[] {
  return value
    .split(',')
    .map((tag) => tag.trim())
    .filter((tag) => tag.length > 0)
}

interface Props {
  directories: Directory[]
  documents: DocumentSummary[]
  notifier: Notifier
  onChanged: () => Promise<void>
}

export function DocumentsPanel({ directories, documents, notifier, onChanged }: Props) {
  const [filter, setFilter] = useState('')
  const [search, setSearch] = useState('')
  const [selection, setSelection] = useState<Selection>(NO_SELECTION)
  const [detail, setDetail] = useState<DocumentDetail | null>(null)
  const [draft, setDraft] = useState<Draft>(emptyDraft(''))
  const [revisions, setRevisions] = useState<RevisionSummary[] | null>(null)
  const [revisionView, setRevisionView] = useState<RevisionDetail | null>(null)
  const [busy, setBusy] = useState(false)
  const [loadedId, setLoadedId] = useState<string | null>(null)
  const [failedId, setFailedId] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)
  const [showPreview, setShowPreview] = useState(() => window.localStorage.getItem(PREVIEW_KEY) === '1')

  useEffect(() => {
    window.localStorage.setItem(PREVIEW_KEY, showPreview ? '1' : '0')
  }, [showPreview])

  const selectedId = selection.kind === 'document' ? selection.id : null
  // Derived from state rather than a fetch flag so the spinner is up on the very first paint.
  const opening = selectedId !== null && selectedId !== loadedId && selectedId !== failedId

  useEffect(() => {
    if (selectedId === null) {
      setDetail(null)
      setRevisions(null)
      return
    }

    let cancelled = false
    api
      .getDocument(selectedId)
      .then((document) => {
        if (cancelled) return
        setDetail(document)
        setDraft(fromDetail(document))
        setRevisions(null)
        setFailedId(null)
        setLoadedId(document.id)
      })
      .catch((cause: unknown) => {
        if (cancelled) return
        setDetail(null)
        setFailedId(selectedId)
        notifier.error(cause)
      })

    return () => {
      cancelled = true
    }
  }, [selectedId, attempt, notifier])

  function retryLoad() {
    setFailedId(null)
    setAttempt((value) => value + 1)
  }

  const visible = useMemo(() => {
    const needle = search.trim().toLowerCase()
    return documents.filter(
      (document) =>
        (filter === '' || document.directoryId === filter) &&
        (needle === '' ||
          document.name.toLowerCase().includes(needle) ||
          document.summary.toLowerCase().includes(needle) ||
          document.tags.some((tag) => tag.includes(needle))),
    )
  }, [documents, filter, search])

  function startCreate() {
    setSelection({ kind: 'new' })
    setDetail(null)
    setRevisions(null)
    setDraft(emptyDraft(filter !== '' ? filter : (directories[0]?.id ?? '')))
  }

  async function save(force = false) {
    if (selection.kind === 'none') return
    if (selection.kind === 'new' && draft.directoryId === '') {
      notifier.error(new Error('Pick a directory before saving.'))
      return
    }

    setBusy(true)
    try {
      if (selection.kind === 'new') {
        const result = await api.createDocument({
          directoryId: draft.directoryId,
          name: draft.name,
          summary: draft.summary,
          tags: parseTags(draft.tags),
          content: draft.content,
        })
        await onChanged()
        setSelection({ kind: 'document', id: result.id })
        notifier.flash(`Created "${result.name}" as version ${result.version}.`)
      } else {
        const result = await api.updateDocument(selection.id, {
          name: draft.name,
          summary: draft.summary,
          tags: parseTags(draft.tags),
          content: draft.content,
          expectedVersion: force ? undefined : detail?.version,
        })
        const fresh = await api.getDocument(selection.id)
        setDetail(fresh)
        setDraft(fromDetail(fresh))
        await onChanged()
        if (revisions !== null) setRevisions(await api.listRevisions(selection.id))
        notifier.flash(`Saved "${result.name}" as version ${result.version}.`)
      }
    } catch (cause) {
      const versionConflict =
        cause instanceof ApiError &&
        cause.status === 409 &&
        cause.latestVersion !== null &&
        selection.kind === 'document' &&
        !force
      if (versionConflict) {
        const overwrite = window.confirm(`${describeError(cause)}\n\nOverwrite the stored version with your changes?`)
        if (overwrite) {
          await save(true)
          return
        }
      }
      notifier.error(cause)
    } finally {
      setBusy(false)
    }
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    void save()
  }

  async function remove() {
    if (selection.kind !== 'document' || detail === null) return
    if (!window.confirm(`Delete "${detail.name}" and its revision history?`)) return
    setBusy(true)
    try {
      await api.deleteDocument(detail.id)
      await onChanged()
      setSelection(NO_SELECTION)
      notifier.flash(`Deleted "${detail.name}".`)
    } catch (cause) {
      notifier.error(cause)
    } finally {
      setBusy(false)
    }
  }

  async function toggleRevisions() {
    if (selection.kind !== 'document') return
    if (revisions !== null) {
      setRevisions(null)
      return
    }
    try {
      setRevisions(await api.listRevisions(selection.id))
    } catch (cause) {
      notifier.error(cause)
    }
  }

  async function openRevision(revisionId: string) {
    try {
      setRevisionView(await api.getRevision(revisionId))
    } catch (cause) {
      notifier.error(cause)
    }
  }

  return (
    <>
      <div className="split">
        <aside className="list-pane">
          <div className="list-toolbar">
            <select value={filter} onChange={(event) => setFilter(event.target.value)} aria-label="Filter by directory">
              <option value="">All directories</option>
              {directories.map((directory) => (
                <option key={directory.id} value={directory.id}>
                  {directory.name}
                </option>
              ))}
            </select>
            <input
              type="search"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="Search documents"
              aria-label="Search documents"
            />
            <button type="button" className="primary" onClick={startCreate} disabled={directories.length === 0}>
              New
            </button>
          </div>

          {directories.length === 0 ? (
            <Empty>Create a directory first — every document lives inside one.</Empty>
          ) : visible.length === 0 ? (
            <Empty>No documents match this view.</Empty>
          ) : (
            <ul className="doc-list">
              {visible.map((document) => (
                <li key={document.id}>
                  <button
                    type="button"
                    className={document.id === selectedId ? 'doc-item active' : 'doc-item'}
                    onClick={() => setSelection({ kind: 'document', id: document.id })}
                  >
                    <span className="doc-name">{document.name}</span>
                    <span className="doc-meta">
                      <code>{document.directoryName}</code>
                      <span className="pill">v{document.version}</span>
                    </span>
                    {document.summary.length > 0 ? <span className="doc-summary">{document.summary}</span> : null}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </aside>

        <section className="editor-pane">
          {selection.kind === 'none' ? (
            <Empty>Select a document on the left, or create a new one.</Empty>
          ) : opening ? (
            <div className="editor">
              <Spinner label="Loading document…" />
            </div>
          ) : selection.kind === 'document' && detail === null ? (
            <Empty>
              <span>That document could not be loaded.</span>
              <button type="button" onClick={retryLoad}>
                Retry
              </button>
            </Empty>
          ) : (
            <form className="editor" onSubmit={submit}>
              <div className="editor-head">
                <h2>{selection.kind === 'new' ? 'New document' : (detail?.name ?? '')}</h2>
                {detail !== null ? <span className="pill">version {detail.version}</span> : null}
              </div>

              <div className="editor-grid">
                <Field label="Directory">
                  <select
                    value={draft.directoryId}
                    onChange={(event) => setDraft({ ...draft, directoryId: event.target.value })}
                    disabled={selection.kind === 'document'}
                  >
                    {directories.map((directory) => (
                      <option key={directory.id} value={directory.id}>
                        {directory.name}
                      </option>
                    ))}
                  </select>
                </Field>
                <Field label="Name" hint="Unique inside the directory. Max 200 characters.">
                  <input
                    value={draft.name}
                    onChange={(event) => setDraft({ ...draft, name: event.target.value })}
                    placeholder="release-checklist"
                  />
                </Field>
                <Field label="Summary" hint="One line for list views. Max 500 characters.">
                  <input
                    value={draft.summary}
                    onChange={(event) => setDraft({ ...draft, summary: event.target.value })}
                    placeholder="What this document covers"
                  />
                </Field>
                <Field label="Tags" hint="Comma separated, e.g. process, onboarding">
                  <input
                    value={draft.tags}
                    onChange={(event) => setDraft({ ...draft, tags: event.target.value })}
                    placeholder="process, onboarding"
                  />
                </Field>
              </div>

              <Field
                label="Content"
                className="content-field"
                hint="Markdown. Max 100 KB per version."
                actions={
                  <button
                    type="button"
                    onClick={() => setShowPreview((open) => !open)}
                    aria-pressed={showPreview}
                    title="Toggle the rendered markdown sidebar"
                  >
                    {showPreview ? 'Hide preview' : 'Show preview'}
                  </button>
                }
              >
                <div className={showPreview ? 'content-shell previewing' : 'content-shell'}>
                  <textarea
                    className="content"
                    value={draft.content}
                    onChange={(event) => setDraft({ ...draft, content: event.target.value })}
                    spellCheck={false}
                    placeholder={'# Heading\n\nWrite markdown here…'}
                  />
                  {showPreview ? <Markdown source={draft.content} /> : null}
                </div>
              </Field>

              <div className="editor-actions">
                <button type="submit" className="primary" disabled={busy}>
                  {busy ? 'Saving…' : 'Save'}
                </button>
                {selection.kind === 'document' ? (
                  <>
                    <button type="button" onClick={() => void toggleRevisions()} disabled={busy}>
                      {revisions === null ? 'History' : 'Hide history'}
                    </button>
                    <button type="button" className="danger" onClick={() => void remove()} disabled={busy}>
                      Delete
                    </button>
                  </>
                ) : null}
                <span className="spacer" />
                {detail !== null ? (
                  <span className="muted">
                    Updated {formatDate(detail.updatedAt)} by {detail.updatedBy}
                  </span>
                ) : null}
              </div>

              {revisions !== null ? (
                <div className="history">
                  <h3>Revision history</h3>
                  {revisions.length === 0 ? (
                    <p className="muted">No revisions recorded yet.</p>
                  ) : (
                    <ul className="history-list">
                      {revisions.map((revision) => (
                        <li key={revision.id}>
                          <button type="button" onClick={() => void openRevision(revision.id)}>
                            <span className="pill">v{revision.version}</span>
                            <span className="history-name">{revision.name}</span>
                            <span className="muted">
                              {formatDate(revision.changedAt)} · {revision.changedBy}
                            </span>
                          </button>
                        </li>
                      ))}
                    </ul>
                  )}
                </div>
              ) : null}
            </form>
          )}
        </section>
      </div>

      {revisionView !== null ? (
        <Modal
          title={`${revisionView.name} — version ${revisionView.version}`}
          onClose={() => setRevisionView(null)}
        >
          <p className="muted">
            Saved {formatDate(revisionView.changedAt)} by {revisionView.changedBy}
          </p>
          {revisionView.summary.length > 0 ? <p>{revisionView.summary}</p> : null}
          {revisionView.tags.length > 0 ? (
            <p className="tag-row">
              {revisionView.tags.map((tag) => (
                <span key={tag} className="tag">
                  {tag}
                </span>
              ))}
            </p>
          ) : null}
          <pre className="revision-content">{revisionView.content}</pre>
        </Modal>
      ) : null}
    </>
  )
}
