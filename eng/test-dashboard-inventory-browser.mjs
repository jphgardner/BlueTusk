import assert from "node:assert/strict";
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";
import { createRequire } from "node:module";

const values = new Map();
for (let index = 2; index < process.argv.length; index += 2) values.set(process.argv[index], process.argv[index + 1]);
const baseUrl = values.get("--base-url");
const modulePath = values.get("--module");
const output = values.get("--output");
assert(baseUrl && modulePath && output, "--base-url, --module, and --output are required.");
assert(["localhost", "127.0.0.1", "[::1]"].includes(new URL(baseUrl).hostname), "The browser fixture must use a loopback server.");
const require = createRequire(import.meta.url);
const { chromium } = require(path.resolve(modulePath));
const browser = await chromium.launch({ headless: true });
await mkdir(output, { recursive: true });
const results = { schemaVersion: 1, browser: browser.version(), fixtureRows: 10_000, checks: [], passed: false };
const errors = [];
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, extraHTTPHeaders: { "X-Test-Role": "reader" } });
  const page = await context.newPage();
  page.on("pageerror", error => errors.push(error.message));
  await page.goto(`${baseUrl}/operations/deployments?tenant=tenant-a&pageSize=17`);
  assert.equal(await page.locator("tbody tr").count(), 17);
  assert.equal(await page.getByLabel("Tenant", { exact: true }).inputValue(), "tenant-a");
  assert.equal(await page.getByLabel("Page size", { exact: true }).inputValue(), "17");
  assert(!await page.locator("[data-operation-name]").count(), "The read-only role must not receive mutation controls.");
  results.checks.push("desktop page renders only 17 rows from a 10,000-deployment fixture");
  await page.screenshot({ path: path.join(output, "desktop.png"), fullPage: true });

  const next = page.getByRole("link", { name: "Next page", exact: true });
  await next.focus();
  await Promise.all([page.waitForURL(url => url.searchParams.get("after") === "deployment-000032"), page.keyboard.press("Enter")]);
  assert.equal(new URL(page.url()).searchParams.get("tenant"), "tenant-a");
  assert.equal(new URL(page.url()).searchParams.get("pageSize"), "17");
  assert.match(await page.locator("tbody tr").first().innerText(), /deployment-000034/);
  results.checks.push("keyboard pagination preserves the server filters and advances the exclusive cursor");

  await page.getByLabel("Tenant", { exact: true }).focus();
  await page.keyboard.press("Tab");
  assert(await page.getByLabel("Provider", { exact: true }).evaluate(element => element === document.activeElement));
  await page.getByLabel("Tenant", { exact: true }).fill("tenant-b");
  await Promise.all([page.waitForURL(url => url.searchParams.get("tenant") === "tenant-b" && !url.searchParams.has("after")),
    page.getByRole("button", { name: "Apply filters", exact: true }).click()]);
  assert.match(await page.locator("tbody tr").first().innerText(), /deployment-000001/);
  results.checks.push("labelled filter controls have a usable tab order and applying a filter restarts pagination");

  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`${baseUrl}/operations/deployments?tenant=tenant-a&pageSize=17`);
  const menu = page.getByRole("button", { name: "Menu", exact: true });
  await menu.focus();
  await page.keyboard.press("Enter");
  assert.equal(await menu.getAttribute("aria-expanded"), "true");
  await page.locator("[data-navigation] a").first().focus();
  await page.keyboard.press("Escape");
  assert.equal(await menu.getAttribute("aria-expanded"), "false");
  assert(await menu.evaluate(element => element === document.activeElement));
  const layout = await page.evaluate(() => ({ viewport: innerWidth, width: document.documentElement.scrollWidth,
    tableWidth: document.querySelector(".table-wrap").clientWidth, tableScroll: document.querySelector(".table-wrap").scrollWidth }));
  await page.screenshot({ path: path.join(output, "mobile.png"), fullPage: true });
  if (layout.width > layout.viewport + 1) {
    results.overflow = await page.evaluate(() => [...document.querySelectorAll("body *")].map(element => ({
      tag: element.tagName, class: element.className, width: element.getBoundingClientRect().width,
      right: element.getBoundingClientRect().right, text: element.textContent.slice(0, 80),
    })).filter(element => element.right > innerWidth + 1).slice(0, 15));
  }
  assert(layout.width <= layout.viewport + 1, `Mobile document overflows: ${JSON.stringify(layout)}`);
  assert(layout.tableScroll > layout.tableWidth, "The wide deployment table must scroll inside its container.");
  await page.locator("tbody tr").first().getByRole("link", { name: "View →", exact: true }).click();
  await page.getByRole("heading", { name: "deployment-000000", exact: true }).waitFor();
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
  results.checks.push("390px mobile layout confines horizontal scrolling to the table; the menu works with Enter/Escape and details remain accessible");

  const noScript = await browser.newContext({ javaScriptEnabled: false, viewport: { width: 1440, height: 1000 },
    extraHTTPHeaders: { "X-Test-Role": "reader" } });
  const staticPage = await noScript.newPage();
  await staticPage.goto(`${baseUrl}/operations/deployments?tenant=tenant-a&pageSize=17`);
  await staticPage.getByRole("link", { name: "Next page", exact: true }).click();
  assert.match(await staticPage.locator("tbody tr").first().innerText(), /deployment-000034/);
  await staticPage.getByLabel("Tenant", { exact: true }).fill("tenant-b");
  await staticPage.getByRole("button", { name: "Apply filters", exact: true }).click();
  assert.match(await staticPage.locator("tbody tr").first().innerText(), /deployment-000001/);
  results.checks.push("pagination and server filter submission work with JavaScript disabled");
  assert.deepEqual(errors, []);
  results.passed = true;
} catch (error) {
  results.error = error.stack ?? String(error);
  process.exitCode = 1;
} finally {
  await browser.close();
  await writeFile(path.join(output, "result.json"), `${JSON.stringify(results, null, 2)}\n`);
  process.stdout.write(`${JSON.stringify(results, null, 2)}\n`);
}
