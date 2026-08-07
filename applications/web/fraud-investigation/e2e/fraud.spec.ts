import { expect, test } from "@playwright/test";

test("investigator opens an auditable fraud case", async ({ page }) => {
  await page.route("**/api/v1/**", async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === "/api/v1/session") {
      await route.fulfill({ json: { tenant: "pilot", name: "Fraud Investigator" } });
    } else if (path === "/api/v1/session/csrf") {
      await route.fulfill({ json: { token: "e2e-csrf" } });
    } else if (["/api/v1/fraud/accounts", "/api/v1/fraud/transfers", "/api/v1/fraud/alert-rules"].includes(path)
      && request.method() === "GET") {
      await route.fulfill({ json: [] });
    } else if (path === "/api/v1/fraud/cases" && request.method() === "GET") {
      await route.fulfill({ json: [] });
    } else if (path === "/api/v1/fraud/cases" && request.method() === "POST") {
      await route.fulfill({ status: 201, json: {
        id: "21234567-89ab-cdef-0123-456789abcdef",
        reason: "Rapid transfer fan-out", assignee: null, decision: "Open", version: 1
      } });
    } else if (path === "/api/v1/fraud/accounts" && request.method() === "POST") {
      await route.fulfill({ status: 201, json: {
        id: "31234567-89ab-cdef-0123-456789abcdef", displayName: "Treasury"
      } });
    } else {
      await route.fulfill({ status: 503, json: { title: "Live feed unavailable in browser fixture" } });
    }
  });

  await page.goto("/");
  await expect(page.getByRole("heading", { name: /Follow the money/ })).toBeVisible();
  await page.getByLabel("Display name").fill("Treasury");
  await page.getByRole("button", { name: "Register account" }).click();
  await expect(page.getByRole("listitem").filter({ hasText: "Treasury" })).toBeVisible();
  await page.getByLabel("Evidence summary").fill("Rapid transfer fan-out");
  await page.getByRole("button", { name: "Open case" }).click();
  await expect(page.getByText("Rapid transfer fan-out", { exact: true })).toBeVisible();
  await expect(page.getByText("Fraud Investigator", { exact: true })).toBeVisible();
});
