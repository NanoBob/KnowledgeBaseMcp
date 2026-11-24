/** Configuration written by the container before the bundle loads; see config-entrypoint.sh. */
interface RuntimeConfig {
  apiBase?: string
}

declare global {
  interface Window {
    __APP_CONFIG__?: RuntimeConfig
  }
}

/**
 * API base from the container's runtime configuration, then the build-time value, then the
 * caller's default.
 */
export function apiBase(fallback: string): string {
  return window.__APP_CONFIG__?.apiBase || import.meta.env.VITE_API_BASE || fallback
}
