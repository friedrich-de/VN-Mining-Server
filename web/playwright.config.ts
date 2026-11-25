import { defineConfig, devices } from '@playwright/test';

// End-to-end tests of the page in a real browser, against the built server: each test starts a
// server of its own on a free port, with an empty data folder that is removed afterwards.
// Start them with dev/e2e.sh, which builds the page and the server and runs this.
const chromium = process.env.VN_MINING_E2E_CHROMIUM;
const launchOptions = chromium === undefined || chromium === '' ? {} : { executablePath: chromium };

export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/support/globalSetup.ts',
  // Every test has a server of its own, so they can run side by side.
  fullyParallel: true,
  workers: process.env.VN_MINING_E2E_WORKERS ? Number(process.env.VN_MINING_E2E_WORKERS) : 2,
  forbidOnly: process.env.CI !== undefined,
  retries: 0,
  reporter: [['list']],
  timeout: 30_000,
  expect: { timeout: 10_000 },
  use: {
    trace: 'off',
    screenshot: 'only-on-failure',
    launchOptions,
  },
  projects: [
    {
      name: 'desktop',
      testMatch: 'browser/**/*.spec.ts',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } },
    },
    {
      name: 'phone',
      testMatch: 'browser/**/*.spec.ts',
      use: { ...devices['Pixel 7'] },
    },
  ],
});
