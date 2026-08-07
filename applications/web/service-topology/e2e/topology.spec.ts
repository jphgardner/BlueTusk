import { expect, test } from "@playwright/test";

test("operator registers a service in the topology", async ({ page }) => {
  await page.route("**/api/v1/**", async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === "/api/v1/session") {
      await route.fulfill({ json: { tenant: "pilot", name: "Topology Operator" } });
    } else if (path === "/api/v1/session/csrf") {
      await route.fulfill({ json: { token: "e2e-csrf" } });
    } else if (path === "/api/v1/topology/services" && request.method() === "GET") {
      await route.fulfill({ json: [] });
    } else if (["/api/v1/topology/dependencies", "/api/v1/topology/incidents"].includes(path)
      && request.method() === "GET") {
      await route.fulfill({ json: [] });
    } else if (path === "/api/v1/topology/services" && request.method() === "POST") {
      await route.fulfill({ status: 201, json: {
        id: "11234567-89ab-cdef-0123-456789abcdef",
        name: "billing", health: "Healthy", version: 1,
        updatedAt: "2026-08-07T10:00:00Z"
      } });
    } else {
      await route.fulfill({ status: 503, json: { title: "Live feed unavailable in browser fixture" } });
    }
  });

  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Service Topology Centre" })).toBeVisible();
  await page.getByPlaceholder("New service name").fill("billing");
  await page.getByRole("button", { name: "Register" }).click();
  await expect(page.getByText("billing", { exact: true })).toBeVisible();
  await expect(page.getByText("Topology Operator", { exact: true })).toBeVisible();
});
