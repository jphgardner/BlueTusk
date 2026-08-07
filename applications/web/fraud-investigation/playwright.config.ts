import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: "./e2e",
  use: { baseURL: "http://127.0.0.1:4175", trace: "retain-on-failure" },
  webServer: {
    command: "npm run dev -- --port 4175",
    url: "http://127.0.0.1:4175",
    reuseExistingServer: true
  }
});
