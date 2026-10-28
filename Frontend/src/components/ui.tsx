import type { CSSProperties, ReactNode } from 'react'

export interface Notifier {
  error: (cause: unknown) => void
  flash: (message: string) => void
}

export type ToastKind = 'error' | 'success'

export interface ToastItem {
  id: number
  kind: ToastKind
  message: string
}

export function ToastStack({ toasts, onDismiss }: { toasts: ToastItem[]; onDismiss: (id: number) => void }) {
  return (
    <div className="toast-stack">
      {toasts.map((toast) => (
        <div
          key={toast.id}
          className={`toast ${toast.kind}`}
          role={toast.kind === 'error' ? 'alert' : 'status'}
        >
          <span className="toast-message">{toast.message}</span>
          <button type="button" className="toast-close" onClick={() => onDismiss(toast.id)} aria-label="Dismiss">
            ×
          </button>
        </div>
      ))}
    </div>
  )
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

export function Skeleton({ className, style }: { className?: string; style?: CSSProperties }) {
  return (
    <span className={className ? `skeleton ${className}` : 'skeleton'} style={style} aria-hidden="true" />
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
