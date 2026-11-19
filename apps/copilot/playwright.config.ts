import { defineConfig } from '@playwright/test';

/**
 * End-to-end tests drive the real Electron app built into dist/ (`npm run test:e2e` builds first).
 * One worker: every test launches its own Electron instance with visible windows.
 */
export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.spec.ts',
  workers: 1,
  fullyParallel: false,
  timeout: 90_000,
  expect: { timeout: 15_000 },
  retries: 0,
  forbidOnly: process.env.CI !== undefined && process.env.CI !== '',
  reporter: 'list',
  outputDir: 'test-results',
});
