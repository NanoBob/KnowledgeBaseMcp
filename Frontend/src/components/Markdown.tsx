import { useMemo } from 'react'
import DOMPurify from 'dompurify'
import { marked } from 'marked'

/**
 * Renders untrusted markdown (documents can be written by agents through MCP),
 * so the generated HTML is sanitized before it reaches the DOM.
 */
export function Markdown({ source }: { source: string }) {
  const html = useMemo(() => {
    if (source.trim() === '') return ''
    return DOMPurify.sanitize(marked.parse(source, { async: false, gfm: true }))
  }, [source])

  return (
    <div className="preview">
      {html === '' ? (
        <p className="muted">Nothing to preview yet.</p>
      ) : (
        <div className="markdown" dangerouslySetInnerHTML={{ __html: html }} />
      )}
    </div>
  )
}
