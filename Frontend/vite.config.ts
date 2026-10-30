import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
  },
  preview: {
    port: 4173,
  },
  // Monaco's editor worker is an ES module, and the default IIFE worker format cannot bundle it.
  worker: {
    format: 'es',
  },
})
