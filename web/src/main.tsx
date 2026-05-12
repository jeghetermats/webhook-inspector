import '@fontsource/atkinson-hyperlegible/400.css'
import '@fontsource/atkinson-hyperlegible/700.css'
import '@fontsource/jetbrains-mono/400.css'
import './index.css'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import { ApiError, replaceApi } from './api/client'
import App from './App.tsx'
import { DemoApp } from './demo/DemoApp'
import { createDemoApi } from './demo/demoApi'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Client errors (401, 404, ...) will not fix themselves on retry.
      retry: (failureCount, error) => !(error instanceof ApiError && error.status < 500) && failureCount < 2,
      refetchOnWindowFocus: true,
    },
  },
})

const root = createRoot(document.getElementById('root')!)
const isDemo = window.location.pathname === '/demo' || window.location.pathname.startsWith('/demo/')

if (isDemo) {
  // The public demo swaps in an in-memory API before anything renders, so no request
  // ever reaches the server and real captured data can never appear.
  const backend = createDemoApi()
  replaceApi(backend.api)
  root.render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <BrowserRouter basename="/demo">
          <DemoApp backend={backend} />
        </BrowserRouter>
      </QueryClientProvider>
    </StrictMode>,
  )
} else {
  root.render(
    <StrictMode>
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </QueryClientProvider>
    </StrictMode>,
  )
}
