import type { ReactNode } from 'react'

export interface Notifier {
  error: (cause: unknown) => void
  flash: (message: string) => void
}

export function Modal({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  return (
    <div className="modal-backdrop" onClick={onClose} role="presentation">
      <div
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-label={title}
        onClick={(event) => event.stopPropagation()}
      >
        <header className="modal-header">
          <h2>{title}</h2>
          <button type="button" className="icon" onClick={onClose} aria-label="Close">
            ×
          </button>
        </header>
        <div className="modal-body">{children}</div>
      </div>
    </div>
  )
}

export function Field({
  label,
  hint,
  actions,
  className,
  children,
}: {
  label: string
  hint?: string
  actions?: ReactNode
  className?: string
  children: ReactNode
}) {
  return (
    <div className={className ? `field ${className}` : 'field'}>
      <div className="field-head">
        <span className="field-label">{label}</span>
        {actions}
      </div>
      {children}
      {hint ? <span className="field-hint">{hint}</span> : null}
    </div>
  )
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>
}

export function Spinner({ label }: { label?: string }) {
  return (
    <div className="spinner-wrap" role="status" aria-live="polite">
      <span className="spinner" aria-hidden="true" />
      {label ? <span className="muted">{label}</span> : null}
    </div>
  )
}

export function formatDate(value: string): string {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  })
}
