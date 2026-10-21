import { defineConfig, devices } from '@playwright/test';

/**
 * e2e runs against the real app: the backend (Development, talks to the dev mail server) and the Vite dev server,
 * which proxies /api and /hubs to the backend. Set VINETTE_E2E_URL to test an already running instance instead
 * (e.g. a container); no servers are started in that case.
 */
const external = process.env.VINETTE_E2E_URL;
const backendUrl = 'http://localhost:5080';
const frontendUrl = 'http://localhost:5173';

export default defineConfig({
  testDir: './e2e',
  // One worker: the scenarios share one backend and one mail server, and Dovecot delays logins per address.
  workers: 1,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL: external ?? frontendUrl,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: external
    ? undefined
    : [
        {
          command:
            'dotnet run --project ../backend/src/Vinette.Api --no-launch-profile --urls http://localhost:5080',
          url: `${backendUrl}/api/v1/health`,
          env: {
            ASPNETCORE_ENVIRONMENT: 'Development',
            // The scenarios sign in from one address and fail on purpose; lift the per-address limits.
            VINETTE_Security__LoginRateLimit__MaxFailuresPerIp: '1000',
            VINETTE_Security__LoginRateLimit__MaxAttemptsPerMinutePerIp: '1000',
            // Dovecot delays failed logins (2 s growing to ~15 s per address): fewer attempts to lock, a patient timeout.
            VINETTE_Security__LoginRateLimit__MaxFailuresPerAccount: '3',
            VINETTE_Imap__Timeout: '00:00:40',
          },
          reuseExistingServer: !process.env.CI,
          timeout: 120_000,
        },
        {
          command: 'pnpm dev',
          url: frontendUrl,
          env: { VINETTE_BACKEND_URL: backendUrl },
          reuseExistingServer: !process.env.CI,
          timeout: 60_000,
        },
      ],
});
