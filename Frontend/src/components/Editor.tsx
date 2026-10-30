import { useEffect, useRef, useState } from 'react'
import type * as Monaco from 'monaco-editor'
import { MONACO_THEME, loadMonaco } from '../monaco'

const MONO_FONT = "ui-monospace, SFMono-Regular, 'Cascadia Mono', Consolas, monospace"

const SHARED_OPTIONS: Monaco.editor.IStandaloneEditorConstructionOptions = {
  theme: MONACO_THEME,
  automaticLayout: true,
  wordWrap: 'on',
  minimap: { enabled: false },
  fontSize: 13,
  fontFamily: MONO_FONT,
  scrollBeyondLastLine: false,
  padding: { top: 10, bottom: 10 },
  tabSize: 2,
}

export function ContentEditor({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const host = useRef<HTMLDivElement | null>(null)
  const editor = useRef<Monaco.editor.IStandaloneCodeEditor | null>(null)
  // The change handler is read through a ref so a new closure does not tear the editor down.
  const onChangeRef = useRef(onChange)
  onChangeRef.current = onChange
  // The editor is created asynchronously, so it needs the value as it stands once loading finishes.
  const valueRef = useRef(value)
  valueRef.current = value

  useEffect(() => {
    let cancelled = false
    void loadMonaco().then((monaco) => {
      if (cancelled || host.current === null) return
      const instance = monaco.editor.create(host.current, {
        ...SHARED_OPTIONS,
        value: valueRef.current,
        language: 'markdown',
        renderLineHighlight: 'none',
      })
      instance.onDidChangeModelContent(() => onChangeRef.current(instance.getValue()))
      editor.current = instance
    })

    return () => {
      cancelled = true
      editor.current?.dispose()
      editor.current = null
    }
  }, [])

  // Only push values the editor did not produce itself, so typing never resets the cursor.
  useEffect(() => {
    const instance = editor.current
    if (instance !== null && instance.getValue() !== value) instance.setValue(value)
  }, [value])

  return <div className="content-editor" ref={host} />
}

export function RevisionDiff({ from, to }: { from: string; to: string }) {
  const host = useRef<HTMLDivElement | null>(null)
  const diff = useRef<Monaco.editor.IStandaloneDiffEditor | null>(null)
  const models = useRef<Monaco.editor.ITextModel[]>([])
  const [sideBySide, setSideBySide] = useState(true)

  useEffect(() => {
    let cancelled = false
    void loadMonaco().then((monaco) => {
      if (cancelled || host.current === null) return
      const instance = monaco.editor.createDiffEditor(host.current, {
        ...SHARED_OPTIONS,
        readOnly: true,
        renderSideBySide: true,
        renderOverviewRuler: false,
        ignoreTrimWhitespace: false,
      })
      const original = monaco.editor.createModel(from, 'markdown')
      const modified = monaco.editor.createModel(to, 'markdown')
      instance.setModel({ original, modified })
      diff.current = instance
      models.current = [original, modified]
    })

    return () => {
      cancelled = true
      diff.current?.dispose()
      diff.current = null
      for (const model of models.current) model.dispose()
      models.current = []
    }
  }, [from, to])

  useEffect(() => {
    diff.current?.updateOptions({ renderSideBySide: sideBySide })
  }, [sideBySide])

  return (
    <div className="diff">
      <div className="diff-actions">
        <button
          type="button"
          onClick={() => setSideBySide((current) => !current)}
          aria-pressed={sideBySide}
          title="Switch between side-by-side and inline diff"
        >
          {sideBySide ? 'Inline view' : 'Side-by-side view'}
        </button>
      </div>
      <div className="diff-editor" ref={host} />
    </div>
  )
}
