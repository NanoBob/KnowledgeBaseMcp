import { useEffect, useMemo, useRef, useState } from 'react'
import { api } from '../api'
import type { Directory, DocumentSummary, ImportConflictMode, ImportResult } from '../types'
import { Empty, Field } from './ui'
import type { Notifier } from './ui'

interface Props {
  directories: Directory[]
  documents: DocumentSummary[]
  notifier: Notifier
  onChanged: () => Promise<void>
}

const CONFLICT_HINTS: Record<ImportConflictMode, string> = {
  overwrite: 'Replace documents whose name already exists. Their earlier content stays in the revision history.',
  skip: 'Keep the stored document and leave the archive copy out.',
  fail: 'Reject the whole import before anything is written when a name already exists.',
}

const CONFLICT_LABELS: Record<ImportConflictMode, string> = {
  overwrite: 'overwrite',
  skip: 'skip',
  fail: 'fail',
}

function plural(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? '' : 's'}`
}

export function TransferPanel({ directories, documents, notifier, onChanged }: Props) {
  const [selected, setSelected] = useState<string[]>([])
  const [exporting, setExporting] = useState(false)
  const [file, setFile] = useState<File | null>(null)
  const [conflict, setConflict] = useState<ImportConflictMode>('overwrite')
  const [importing, setImporting] = useState(false)
  const [result, setResult] = useState<ImportResult | null>(null)
  const fileInput = useRef<HTMLInputElement>(null)

  // Directories that appear later start selected, while an explicit deselection survives a reload.
  useEffect(() => {
    setSelected((current) => {
      const known = new Set(directories.map((directory) => directory.id))
      const kept = current.filter((id) => known.has(id))
      const added = directories
        .filter((directory) => !current.includes(directory.id))
        .map((directory) => directory.id)
      return [...kept, ...added]
    })
  }, [directories])

  const selectedSet = useMemo(() => new Set(selected), [selected])

  const documentCounts = useMemo(() => {
    const counts = new Map<string, number>()
    for (const document of documents) {
      counts.set(document.directoryId, (counts.get(document.directoryId) ?? 0) + 1)
    }
    return counts
  }, [documents])

  const selectedDocumentCount = useMemo(
    () => documents.filter((document) => selectedSet.has(document.directoryId)).length,
    [documents, selectedSet],
  )

  const allSelected = directories.length > 0 && selected.length === directories.length

  function toggleDirectory(id: string) {
    setSelected((current) =>
      current.includes(id) ? current.filter((value) => value !== id) : [...current, id],
    )
  }

  function toggleAll() {
    setSelected(allSelected ? [] : directories.map((directory) => directory.id))
  }

  async function runExport() {
    if (selected.length === 0) {
      notifier.error(new Error('Select at least one directory to export.'))
      return
    }

    setExporting(true)
    try {
      const archive = await api.exportDocuments(selected)
      const url = URL.createObjectURL(archive.blob)
      const link = document.createElement('a')
      link.href = url
      link.download = archive.filename
      document.body.append(link)
      link.click()
      link.remove()
      // Revoking immediately can cancel the download in some browsers.
      window.setTimeout(() => URL.revokeObjectURL(url), 1000)
      notifier.flash(`Exported ${plural(selectedDocumentCount, 'document')} to ${archive.filename}.`)
    } catch (cause) {
      notifier.error(cause)
    } finally {
      setExporting(false)
    }
  }

  async function runImport() {
    if (file === null) return
    if (!file.name.toLowerCase().endsWith('.zip')) {
      notifier.error(new Error('Choose the .zip archive produced by an export.'))
      return
    }

    setImporting(true)
    setResult(null)
    try {
      const imported = await api.importDocuments(file, conflict)
      setResult(imported)
      setFile(null)
      if (fileInput.current !== null) fileInput.current.value = ''
      await onChanged()
      notifier.flash(
        `Import finished: ${plural(imported.documentsCreated, 'document')} created, ` +
          `${imported.documentsUpdated} updated, ${imported.documentsSkipped} skipped.`,
      )
    } catch (cause) {
      notifier.error(cause)
    } finally {
      setImporting(false)
    }
  }

  return (
    <section className="panel">
      <header className="panel-head">
        <div>
          <h2>Import and export</h2>
          <p className="muted">
            An archive holds a manifest plus one markdown file per document, so names, summaries, tags and content
            survive a round trip. Ids, versions and timestamps belong to the target knowledge base.
          </p>
        </div>
      </header>

      <div className="card">
        <h3>Export</h3>
        <p className="muted">Choose the directories to include. The download starts when the archive is ready.</p>

        {directories.length === 0 ? (
          <Empty>No directories to export yet.</Empty>
        ) : (
          <>
            <div className="check-head">
              <label className="check-row">
                <input type="checkbox" checked={allSelected} onChange={toggleAll} />
                All directories
              </label>
              <span className="muted">{plural(selectedDocumentCount, 'document')} selected</span>
            </div>
            <div className="check-list">
              {directories.map((directory) => (
                <label className="check-row" key={directory.id}>
                  <input
                    type="checkbox"
                    checked={selectedSet.has(directory.id)}
                    onChange={() => toggleDirectory(directory.id)}
                    aria-label={`Include ${directory.name}`}
                  />
                  <code>{directory.name}</code>
                  <span className="doc-count">{plural(documentCounts.get(directory.id) ?? 0, 'document')}</span>
                </label>
              ))}
            </div>
          </>
        )}

        <div className="form-actions">
          <button
            type="button"
            className="primary"
            onClick={() => void runExport()}
            disabled={exporting || selected.length === 0}
          >
            {exporting ? 'Preparing…' : 'Export'}
          </button>
        </div>
      </div>

      <div className="card">
        <h3>Import</h3>
        <p className="muted">Upload a .zip produced by an export, from this instance or another one.</p>

        <Field label="Archive" hint="A .zip with manifest.json, or plain folders of .md files.">
          <input
            ref={fileInput}
            type="file"
            accept=".zip,application/zip"
            onChange={(event) => setFile(event.target.files?.[0] ?? null)}
            aria-label="Archive file"
          />
        </Field>

        <Field label="When a document already exists" hint={CONFLICT_HINTS[conflict]}>
          <select
            value={conflict}
            onChange={(event) => setConflict(event.target.value as ImportConflictMode)}
            aria-label="Conflict handling"
          >
            {(['overwrite', 'skip', 'fail'] as const).map((mode) => (
              <option key={mode} value={mode}>
                {CONFLICT_LABELS[mode]}
              </option>
            ))}
          </select>
        </Field>

        <p className="field-hint">
          Documents are written one by one; except in fail mode, an error part way through leaves the earlier documents
          in place.
        </p>

        <div className="form-actions">
          <button
            type="button"
            className="primary"
            onClick={() => void runImport()}
            disabled={importing || file === null}
          >
            {importing ? 'Importing…' : 'Import'}
          </button>
        </div>

        {result !== null ? (
          <div className="import-result">
            <div className="import-result-head">
              <strong>Last import</strong>
              <button type="button" className="icon" onClick={() => setResult(null)} aria-label="Dismiss import result">
                ×
              </button>
            </div>
            <div className="result-stats">
              <div className="result-stat">
                <strong>{result.directoriesCreated}</strong>
                <span>directories created</span>
              </div>
              <div className="result-stat">
                <strong>{result.documentsCreated}</strong>
                <span>documents created</span>
              </div>
              <div className="result-stat">
                <strong>{result.documentsUpdated}</strong>
                <span>documents updated</span>
              </div>
              <div className="result-stat">
                <strong>{result.documentsSkipped}</strong>
                <span>documents skipped</span>
              </div>
            </div>
          </div>
        ) : null}
      </div>
    </section>
  )
}
