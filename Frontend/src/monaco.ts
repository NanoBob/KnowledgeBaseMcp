import type * as Monaco from 'monaco-editor'

export type MonacoApi = typeof Monaco

export const MONACO_THEME = 'akb-dark'

let loading: Promise<MonacoApi> | null = null

// Monaco is a few megabytes, so it is only fetched once an editor actually mounts. The worker and the
// theme have to exist before the first model is created, which is why they are set up inside the loader.
export function loadMonaco(): Promise<MonacoApi> {
  loading ??= import('monaco-editor').then(async (monaco) => {
    const { default: EditorWorker } = await import('monaco-editor/editor/editor.worker?worker')
    ;(globalThis as { MonacoEnvironment?: { getWorker: () => Worker } }).MonacoEnvironment = {
      getWorker: () => new EditorWorker(),
    }

    monaco.editor.defineTheme(MONACO_THEME, {
      base: 'vs-dark',
      inherit: true,
      rules: [
        { token: 'comment', foreground: '8b949e', fontStyle: 'italic' },
        { token: 'keyword', foreground: '4f9dff' },
        { token: 'string', foreground: '3fb950' },
        { token: 'keyword.md', foreground: '4f9dff', fontStyle: 'bold' },
        { token: 'string.link.md', foreground: '58a6ff' },
        { token: 'variable.md', foreground: 'e6edf3' },
      ],
      colors: {
        'editor.background': '#151b23',
        'editor.foreground': '#e6edf3',
        'editorCursor.foreground': '#4f9dff',
        'editor.lineHighlightBackground': '#1c242e',
        'editorLineNumber.foreground': '#5b6675',
        'editorLineNumber.activeForeground': '#8b949e',
        'editorGutter.background': '#151b23',
        'editor.selectionBackground': '#264f78',
        'editorWidget.background': '#151b23',
        'editorWidget.border': '#2a3441',
        'editorHoverWidget.background': '#151b23',
        'scrollbarSlider.background': '#2a344166',
        'diffEditor.insertedTextBackground': '#3fb95026',
        'diffEditor.removedTextBackground': '#f8514926',
        'diffEditor.insertedLineBackground': '#3fb95015',
        'diffEditor.removedLineBackground': '#f8514915',
      },
    })

    return monaco
  })

  return loading
}
