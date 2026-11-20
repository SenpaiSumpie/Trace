import { defineConfig, devices } from '@playwright/test'

// End-to-end smoke tests: real browser, real web app, real API, real PostgreSQL.
// The database must already be running (docker compose, or the CI service container).
// The API runs in Development, so it migrates, indexes the fixture repository and uses the
// deterministic fake models: results are reproducible.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: process.env.CI ? [['list'], ['github']] : 'list',
  use: {
    baseURL: 'http://localhost:5173',
    trace: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: 'dotnet run --project ../../src/Trace.Api --launch-profile http',
      url: 'http://localhost:5120/health/ready',
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
    },
    {
      command: 'npm run dev -- --port 5173 --strictPort',
      url: 'http://localhost:5173',
      reuseExistingServer: !process.env.CI,
      timeout: 60_000,
    },
  ],
})
