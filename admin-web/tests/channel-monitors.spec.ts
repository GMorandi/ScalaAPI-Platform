import { expect, test } from "@playwright/test";

test("channel monitors render history and submit a health check", async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem("token", "browser-smoke-token");
    localStorage.setItem("locale", "en");
  });
  let checks = 0;
  await page.route("**/admin/channel-monitors/**", async (route) => {
    const request = route.request();
    if (request.method() === "POST") {
      checks += 1;
      expect(JSON.parse(request.postData() ?? "{}")).toEqual({
        accountId: 42,
        status: "degraded",
        latencyMs: 120,
        error: "provider slow",
      });
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ id: 17 }) });
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ items: [{
        id: 16,
        accountId: 42,
        status: "healthy",
        latencyMs: 80,
        lastError: null,
        checkedAt: "2026-08-10T10:00:00Z",
      }] }),
    });
  });

  await page.goto("/channel-monitors");
  await expect(page.getByRole("heading", { name: "Channel monitors" })).toBeVisible();
  await expect(page.getByRole("cell", { name: "healthy" })).toBeVisible();
  await page.getByLabel("Account ID").fill("42");
  await page.getByLabel("Status").selectOption("degraded");
  await page.getByLabel("Latency (ms)").fill("120");
  await page.getByLabel("Error detail").fill("provider slow");
  await page.getByRole("button", { name: "Record check" }).click();
  await expect(page.getByText("Health check recorded")).toBeVisible();
  expect(checks).toBe(1);
});
