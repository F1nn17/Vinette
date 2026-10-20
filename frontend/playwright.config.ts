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
  fullyParallel: true,
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
          env: { ASPNETCORE_ENVIRONMENT: 'Development' },
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
