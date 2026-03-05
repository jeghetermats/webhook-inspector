/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// During development the SPA runs on Vite's dev server and proxies
// everything that belongs to the ASP.NET Core app, so cookies stay same-origin.
const api = 'http://localhost:5080'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': api,
      '/auth': api,
      '/signin-github': api,
      '/h': api,
      '/hubs': { target: api, ws: true },
      '/health': api,
    },
  },
  test: {
    environment: 'node',
    include: ['src/**/*.test.{ts,tsx}'],
  },
})
