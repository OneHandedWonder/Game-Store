import { defineConfig, devices } from '@playwright/test'
export default defineConfig({
  testDir: './tests/production', reporter: 'list',
  outputDir: './playwright-report/production-results',
  use: { baseURL: 'http://127.0.0.1:5175', trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: { command: 'node node_modules/vite/bin/vite.js preview --host 127.0.0.1 --port 5175 --strictPort', url: 'http://127.0.0.1:5175', reuseExistingServer: false },
})
