import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  use: { baseURL: process.env.VINETTE_E2E_URL ?? 'http://localhost:5173' },
  webServer: process.env.VINETTE_E2E_URL
    ? undefined
    : { command: 'pnpm dev', url: 'http://localhost:5173', reuseExistingServer: true },
});
