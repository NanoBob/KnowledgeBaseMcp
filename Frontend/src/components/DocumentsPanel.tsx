import { useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, api, describeError } from '../api'
import type { Directory, DocumentDetail, DocumentSummary, RevisionDetail, RevisionSummary } from '../types'
import { Empty, Field, Modal, Skeleton, formatDate } from './ui'
import type { Notifier } from './ui'
import { Markdown } from './Markdown'
import { ContentEditor, RevisionDiff } from './Editor'

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
const COLLAPSED_KEY = 'akb.collapsedDirectories'
const COMPACT_KEY = 'akb.compactFileList'

// localStorage throws in private modes and when storage is full, and a preference is not worth
// breaking the editor over.
function readFlag(key: string): boolean {
  try {
    return window.localStorage.getItem(key) === '1'
  } catch {
    return false
  }
}

function writeStored(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value)
  } catch {
    // Preferences are best-effort.
  }
}

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

// The real form with skeletonised content inside its controls, so nothing shifts when the
// document arrives.
function LoadingEditor({ name, version }: { name?: string; version?: number }) {
  return (
    <div className="editor" aria-busy="true" aria-label="Loading document">
      <div className="editor-head">
        {name === undefined ? <Skeleton className="skeleton-title" /> : <h2>{name}</h2>}
        {version === undefined ? null : <span className="pill">version {version}</span>}
      </div>

      <div className="editor-grid">
        <Field label="Directory">
          <SkeletonControl kind="select" />
        </Field>
        <Field label="Name" hint="Unique inside the directory. Max 200 characters.">
          <SkeletonControl />
        </Field>
        <Field label="Summary" hint="One line for list views. Max 500 characters.">
          <SkeletonControl />
        </Field>
        <Field label="Tags" hint="Comma separated, e.g. process, onboarding">
          <SkeletonControl />
        </Field>
      </div>

      <div className="editor-actions">
        <button type="button" className="primary" disabled>
          Save
        </button>
        <button type="button" disabled>
          Move…
        </button>
        <button type="button" disabled>
          History
        </button>
        <button type="button" disabled>
          Delete
        </button>
      </div>

      <Field label="Content" className="content-field" hint="Markdown. Max 100 KB per version.">
        <div className="skeleton-content">
          {[92, 78, 86, 61, 88, 70, 44].map((width, line) => (
            <Skeleton key={line} className="skeleton-line" style={{ width: `${width}%` }} />
          ))}
        </div>
      </Field>
    </div>
  )
}

function SkeletonControl({ kind }: { kind?: 'select' }) {
  return (
    <span className={kind === 'select' ? 'control-skeleton select' : 'control-skeleton'} aria-hidden="true">
      <Skeleton className="skeleton-text" />
      {kind === 'select' ? <span className="control-chevron" /> : null}
    </span>
  )
}

export function DocumentsPanel({ directories, documents, notifier, onChanged }: Props) {
  const [filter, setFilter] = useState('')
  const [search, setSearch] = useState('')
  const [selection, setSelection] = useState<Selection>(NO_SELECTION)
  const [detail, setDetail] = useState<DocumentDetail | null>(null)
  const [draft, setDraft] = useState<Draft>(emptyDraft(''))
  const [revisions, setRevisions] = useState<RevisionSummary[] | null>(null)
  const [revisionView, setRevisionView] = useState<RevisionDetail | null>(null)
  const [picks, setPicks] = useState<string[]>([])
  const [compare, setCompare] = useState<{ from: RevisionDetail; to: RevisionDetail } | null>(null)
  const [busy, setBusy] = useState(false)
  const [refreshing, setRefreshing] = useState(false)
  const [loadedId, setLoadedId] = useState<string | null>(null)
  const [failedId, setFailedId] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)
  const [showPreview, setShowPreview] = useState(() => readFlag(PREVIEW_KEY))
  const [compact, setCompact] = useState(() => readFlag(COMPACT_KEY))
  const [collapsed, setCollapsed] = useState<string[]>(() => {
    try {
      const stored: unknown = JSON.parse(window.localStorage.getItem(COLLAPSED_KEY) ?? '[]')
      return Array.isArray(stored) ? stored.filter((id): id is string => typeof id === 'string') : []
    } catch {
      return []
    }
  })
  const [moveTarget, setMoveTarget] = useState<string | null>(null)

  useEffect(() => {
    writeStored(PREVIEW_KEY, showPreview ? '1' : '0')
  }, [showPreview])

  useEffect(() => {
    writeStored(COLLAPSED_KEY, JSON.stringify(collapsed))
  }, [collapsed])

  useEffect(() => {
    writeStored(COMPACT_KEY, compact ? '1' : '0')
  }, [compact])

  const selectedId = selection.kind === 'document' ? selection.id : null
  // Derived from state rather than a fetch flag so the skeleton is up on the very first paint.
  const opening = selectedId !== null && selectedId !== loadedId && selectedId !== failedId
  // The list already knows the name and version, so the loading form can show them.
  const openingSummary = documents.find((document) => document.id === selectedId)

  useEffect(() => {
    if (selectedId === null) {
      setDetail(null)
      setRevisions(null)
      setMoveTarget(null)
      setPicks([])
      return
    }

    setMoveTarget(null)
    let cancelled = false
    api
      .getDocument(selectedId)
      .then((document) => {
        if (cancelled) return
        setDetail(document)
        setDraft(fromDetail(document))
        setRevisions(null)
        setPicks([])
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

  const needle = search.trim().toLowerCase()
  const searching = needle !== ''

  const groups = useMemo(() => {
    const query = search.trim().toLowerCase()
    const matched = documents.filter(
      (document) =>
        query === '' ||
        document.name.toLowerCase().includes(query) ||
        document.summary.toLowerCase().includes(query) ||
        document.tags.some((tag) => tag.toLowerCase().includes(query)),
    )

    const byDirectory = new Map<string, DocumentSummary[]>()
    for (const document of matched) {
      const bucket = byDirectory.get(document.directoryId)
      if (bucket === undefined) {
        byDirectory.set(document.directoryId, [document])
      } else {
        bucket.push(document)
      }
    }

    const known = directories
      .filter((directory) => filter === '' || directory.id === filter)
      .map((directory) => ({
        id: directory.id,
        name: directory.name,
        description: directory.description,
        documents: (byDirectory.get(directory.id) ?? []).sort((a, b) => a.name.localeCompare(b.name)),
      }))

    // Documents whose directory was deleted still need somewhere to live.
    const orphans = [...byDirectory.entries()]
      .filter(([directoryId]) => !directories.some((directory) => directory.id === directoryId))
      .map(([directoryId, entries]) => ({
        id: directoryId,
        name: 'Unknown directory',
        description: '',
        documents: entries.sort((a, b) => a.name.localeCompare(b.name)),
      }))

    return [...known, ...orphans].filter((group) => query === '' || group.documents.length > 0)
  }, [documents, directories, filter, search])

  function toggleDirectory(directoryId: string) {
    setCollapsed((current) =>
      current.includes(directoryId)
        ? current.filter((id) => id !== directoryId)
        : [...current, directoryId],
    )
  }

  // Keep the open document visible, including after a move into a collapsed directory.
  const selectedDirectoryId =
    detail?.directoryId ?? documents.find((document) => document.id === selectedId)?.directoryId ?? null

  useEffect(() => {
    if (selectedDirectoryId === null) return
    setCollapsed((current) => current.filter((id) => id !== selectedDirectoryId))
  }, [selectedDirectoryId])

  async function refresh() {
    setRefreshing(true)
    try {
      await onChanged()
    } catch (cause) {
      notifier.error(cause)
    } finally {
      setRefreshing(false)
    }
  }

  function startCreate() {
    setSelection({ kind: 'new' })
    setDetail(null)
    setRevisions(null)
    setPicks([])
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

  const moveOptions = useMemo(
    () => directories.filter((directory) => directory.id !== detail?.directoryId),
    [directories, detail],
  )

  function startMove() {
    if (detail === null) return
    const first = directories.find((directory) => directory.id !== detail.directoryId)
    if (first === undefined) {
      notifier.error(new Error('Create another directory before moving this document.'))
      return
    }
    setMoveTarget(first.id)
  }

  async function move() {
    if (selection.kind !== 'document' || detail === null || moveTarget === null) return
    const targetName = directories.find((directory) => directory.id === moveTarget)?.name ?? 'the new directory'
    setBusy(true)
    try {
      const result = await api.moveDocument(detail.id, moveTarget, detail.version)
      const fresh = await api.getDocument(detail.id)
      setDetail(fresh)
      setDraft(fromDetail(fresh))
      await onChanged()
      if (revisions !== null) setRevisions(await api.listRevisions(detail.id))
      setMoveTarget(null)
      notifier.flash(`Moved "${result.name}" to ${targetName} as version ${result.version}.`)
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
      setPicks([])
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

  // Only two revisions can be compared, so a third pick pushes out the oldest one.
  function togglePick(revisionId: string) {
    setPicks((current) =>
      current.includes(revisionId)
        ? current.filter((id) => id !== revisionId)
        : [...current, revisionId].slice(-2),
    )
  }

  async function compareRevisions() {
    if (picks.length !== 2) return
    setBusy(true)
    try {
      const [first, second] = await Promise.all(picks.map((id) => api.getRevision(id)))
      // The older revision is always the original side, whatever order they were ticked in.
      const [from, to] = first.version <= second.version ? [first, second] : [second, first]
      setCompare({ from, to })
    } catch (cause) {
      notifier.error(cause)
    } finally {
      setBusy(false)
    }
  }

  async function reloadDocument(id: string) {
    const fresh = await api.getDocument(id)
    setDetail(fresh)
    setDraft(fromDetail(fresh))
    await onChanged()
    setRevisions(await api.listRevisions(id))
    return fresh
  }

  async function restoreRevision(revision: RevisionDetail) {
    if (detail === null) return
    if (!window.confirm(`Replace the current content with v${revision.version}'s content?`)) return

    setBusy(true)
    try {
      const result = await api.restoreRevision(revision.id, detail.version)
      await reloadDocument(detail.id)
      setRevisionView(null)
      setPicks([])
      notifier.flash(`Restored v${revision.version} as v${result.version}.`)
    } catch (cause) {
      if (cause instanceof ApiError && cause.status === 409) {
        // Someone saved meanwhile; show their version so the user can decide again.
        try {
          await reloadDocument(detail.id)
        } catch (refreshCause) {
          notifier.error(refreshCause)
        }
      }
      notifier.error(cause)
    } finally {
      setBusy(false)
    }
  }

  const pickedRevisions = (revisions ?? [])
    .filter((revision) => picks.includes(revision.id))
    .sort((a, b) => a.version - b.version)

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
            <button
              type="button"
              onClick={() => void refresh()}
              disabled={refreshing}
              title="Reload directories and documents"
            >
              {refreshing ? 'Refreshing…' : 'Refresh'}
            </button>
            <button
              type="button"
              className={compact ? 'list-mode active' : 'list-mode'}
              onClick={() => setCompact((value) => !value)}
              aria-pressed={compact}
              title={compact ? 'Switch to detailed list' : 'Switch to compact file list'}
            >
              Compact
            </button>
            <button type="button" className="primary" onClick={startCreate} disabled={directories.length === 0}>
              New
            </button>
          </div>

          {directories.length === 0 ? (
            <Empty>Create a directory first — every document lives inside one.</Empty>
          ) : groups.length === 0 ? (
            <Empty>No documents match this view.</Empty>
          ) : (
            <div className="doc-groups">
              {groups.map((group) => {
                const open = searching || !collapsed.includes(group.id)
                return (
                  <section className="doc-group" key={group.id}>
                    <button
                      type="button"
                      className="doc-group-head"
                      onClick={() => toggleDirectory(group.id)}
                      aria-expanded={open}
                      title={group.description.length > 0 ? group.description : undefined}
                    >
                      <span className={open ? 'doc-group-chevron open' : 'doc-group-chevron'} aria-hidden="true" />
                      <span className="doc-group-name">{group.name}</span>
                      <span className="pill">{group.documents.length}</span>
                    </button>

                    {open ? (
                      group.documents.length === 0 ? (
                        <p className="doc-group-empty">No documents</p>
                      ) : (
                        <ul className={compact ? 'doc-list compact' : 'doc-list'}>
                          {group.documents.map((document) => (
                            <li key={document.id}>
                              <button
                                type="button"
                                className={document.id === selectedId ? 'doc-item active' : 'doc-item'}
                                onClick={() => setSelection({ kind: 'document', id: document.id })}
                                title={compact ? document.summary || undefined : undefined}
                              >
                                <span className="doc-name">{document.name}</span>
                                {compact ? null : (
                                  <>
                                    <span className="doc-meta">
                                      <span className="pill">v{document.version}</span>
                                      {document.tags.length > 0 ? <span>{document.tags.join(', ')}</span> : null}
                                    </span>
                                    {document.summary.length > 0 ? (
                                      <span className="doc-summary">{document.summary}</span>
                                    ) : null}
                                  </>
                                )}
                              </button>
                            </li>
                          ))}
                        </ul>
                      )
                    ) : null}
                  </section>
                )
              })}
            </div>
          )}
        </aside>

        <section className="editor-pane">
          {selection.kind === 'none' ? (
            <Empty>Select a document on the left, or create a new one.</Empty>
          ) : opening ? (
            <LoadingEditor name={openingSummary?.name} version={openingSummary?.version} />
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

              <div className="editor-actions">
                <button type="submit" className="primary" disabled={busy}>
                  {busy ? 'Saving…' : 'Save'}
                </button>
                {selection.kind === 'document' ? (
                  <>
                    <button
                      type="button"
                      onClick={startMove}
                      disabled={busy || directories.length < 2}
                      title={directories.length < 2 ? 'Create another directory first' : 'Move to another directory'}
                    >
                      Move…
                    </button>
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
                  <div className="history-head">
                    <h3>Revision history</h3>
                    <button
                      type="button"
                      onClick={() => void compareRevisions()}
                      disabled={pickedRevisions.length !== 2 || busy}
                      title={
                        pickedRevisions.length === 2
                          ? 'Open the difference between the two ticked revisions'
                          : 'Tick two revisions to compare them'
                      }
                    >
                      {pickedRevisions.length === 2
                        ? `Compare v${pickedRevisions[0].version} → v${pickedRevisions[1].version}`
                        : 'Compare'}
                    </button>
                  </div>
                  {revisions.length === 0 ? (
                    <p className="muted">No revisions recorded yet.</p>
                  ) : (
                    <ul className="history-list">
                      {revisions.map((revision) => (
                        <li key={revision.id}>
                          <input
                            type="checkbox"
                            className="history-pick"
                            checked={picks.includes(revision.id)}
                            onChange={() => togglePick(revision.id)}
                            aria-label={`Select version ${revision.version} for comparison`}
                          />
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
                  <ContentEditor
                    value={draft.content}
                    onChange={(content) => setDraft((current) => ({ ...current, content }))}
                  />
                  {showPreview ? <Markdown source={draft.content} /> : null}
                </div>
              </Field>
            </form>
          )}
        </section>
      </div>

      {moveTarget !== null && detail !== null ? (
        <Modal title={`Move "${detail.name}"`} onClose={() => setMoveTarget(null)}>
          <Field
            label="Destination directory"
            hint="The name, content and revision history travel with the document. The move records a new revision."
          >
            <select value={moveTarget} onChange={(event) => setMoveTarget(event.target.value)}>
              {moveOptions.map((directory) => (
                <option key={directory.id} value={directory.id}>
                  {directory.name}
                </option>
              ))}
            </select>
          </Field>
          <div className="modal-actions">
            <button type="button" className="primary" onClick={() => void move()} disabled={busy}>
              {busy ? 'Moving…' : 'Move document'}
            </button>
            <button type="button" onClick={() => setMoveTarget(null)} disabled={busy}>
              Cancel
            </button>
          </div>
        </Modal>
      ) : null}

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
          <div className="modal-actions">
            <button
              type="button"
              className="primary"
              onClick={() => void restoreRevision(revisionView)}
              disabled={busy}
              title="Write this revision's content as a new revision"
            >
              {busy ? 'Restoring…' : 'Restore content'}
            </button>
            <button type="button" onClick={() => setRevisionView(null)} disabled={busy}>
              Close
            </button>
          </div>
        </Modal>
      ) : null}

      {compare !== null ? (
        <Modal
          className="wide"
          title={`Compare v${compare.from.version} → v${compare.to.version}`}
          onClose={() => setCompare(null)}
        >
          <div className="diff-meta">
            <span>
              <span className="pill">v{compare.from.version}</span> {formatDate(compare.from.changedAt)} ·{' '}
              {compare.from.changedBy}
            </span>
            <span>
              <span className="pill">v{compare.to.version}</span> {formatDate(compare.to.changedAt)} ·{' '}
              {compare.to.changedBy}
            </span>
          </div>
          <RevisionDiff from={compare.from.content} to={compare.to.content} />
        </Modal>
      ) : null}
    </>
  )
}
