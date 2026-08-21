import { defineConfig, devices } from "@playwright/test";

const liveBaseUrl = process.env.PUBLIC_UI_BASE_URL;

export default defineConfig({
  testDir: "./tests",
  fullyParallel: true,
  reporter: [["list"], ["json", { outputFile: "test-results/results.json" }]],
  use: {
    baseURL: liveBaseUrl ?? "http://127.0.0.1:5174",
    trace: "retain-on-failure",
  },
  ...(liveBaseUrl ? {} : {
    webServer: {
      command: "npm run dev -- --host 127.0.0.1",
      url: "http://127.0.0.1:5174",
      reuseExistingServer: true,
    },
  }),
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
