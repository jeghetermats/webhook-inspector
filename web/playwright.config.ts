import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig, devices } from '@playwright/test'

// End-to-end tests run the real ASP.NET Core app (serving the built SPA from web/dist)
// against Postgres, with test sign-in enabled. Locally that is the docker-compose database;
// CI passes E2E_POSTGRES. Run with `npm run e2e`.
const port = 5199
const webRoot = path.join(path.dirname(fileURLToPath(import.meta.url)), 'dist')

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : 'list',
  use: {
    // localhost (not 127.0.0.1): browsers accept Secure cookies from http://localhost.
    baseURL: `http://localhost:${port}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'dotnet run --project ../src/WebhookInspector.Api --no-launch-profile',
    url: `http://localhost:${port}/health/ready`,
    reuseExistingServer: !process.env.CI,
    timeout: 180_000,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Testing',
      ASPNETCORE_URLS: `http://localhost:${port}`,
      ASPNETCORE_WEBROOT: webRoot,
      TestSignIn__Enabled: 'true',
      // The reconnect test sends 120 requests to one endpoint in a few seconds.
      Capture__PermitsPerTokenPerMinute: '1000',
      Capture__PermitsPerIpPerMinute: '5000',
      ConnectionStrings__Postgres:
        process.env.E2E_POSTGRES ?? 'Host=localhost;Port=5432;Database=webhook_inspector;Username=webhook;Password=webhook',
    },
  },
})
