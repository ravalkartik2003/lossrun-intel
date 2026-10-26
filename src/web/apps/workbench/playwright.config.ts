import { defineConfig, devices } from "@playwright/test";

// Brief 11 acceptance: axe on all seven screens in both themes, the golden what-if parity, timings, the lineage
// screenshots. Mock mode by default (deterministic, offline); LIVE_BASE_URL + LOSSRUN_API_USER/PASSWORD run the @live
// specs against the deployed Static Web App.
export default defineConfig({
  testDir: "e2e",
  timeout: 120_000,
  fullyParallel: false,
  workers: 1,
  reporter: [["list"], ["json", { outputFile: "test-results/results.json" }]],
  use: { baseURL: "http://localhost:5174", viewport: { width: 1440, height: 900 }, trace: "retain-on-failure" },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } } }],
  webServer: { command: "npx vite --mode mock --port 5174 --strictPort", url: "http://localhost:5174", reuseExistingServer: true, timeout: 120_000 },
});
