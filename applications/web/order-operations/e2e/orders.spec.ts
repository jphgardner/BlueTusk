import { expect, test } from "@playwright/test";

test("operator creates an order from the authenticated workspace", async ({ page }) => {
  await page.route("**/api/v1/**", async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === "/api/v1/session") {
      await route.fulfill({ json: { tenant: "pilot", name: "Pilot Operator" } });
    } else if (path === "/api/v1/session/csrf") {
      await route.fulfill({ json: { token: "e2e-csrf" } });
    } else if (path === "/api/v1/orders" && request.method() === "GET") {
      await route.fulfill({ json: [] });
    } else if (path === "/api/v1/orders" && request.method() === "POST") {
      await route.fulfill({ status: 201, json: {
        id: "01234567-89ab-cdef-0123-456789abcdef",
        customerReference: "PILOT-1001", state: "Created", version: 1,
        updatedAt: "2026-08-07T10:00:00Z"
      } });
    } else {
      await route.fulfill({ status: 503, json: { title: "Live feed unavailable in browser fixture" } });
    }
  });

  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Order fulfilment operations" })).toBeVisible();
  await page.getByLabel("Customer reference").fill("PILOT-1001");
  await page.getByRole("button", { name: "Create order" }).click();
  await expect(page.getByRole("cell", { name: "PILOT-1001" })).toBeVisible();
  await expect(page.getByText("Pilot Operator", { exact: false })).toBeVisible();
});
