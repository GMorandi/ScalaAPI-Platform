import { expect, test } from "@playwright/test";

test.skip(!process.env.PUBLIC_UI_BASE_URL, "Requires a source-built User Web container");

test("source-built User Web proxies the live Gateway catalog and readiness", async ({ page }) => {
  const modelResponse = page.waitForResponse(response =>
    new URL(response.url()).pathname === "/v1/models");
  await page.goto("/models");
  expect((await modelResponse).status()).toBe(200);
  await expect(page.getByRole("heading", { name: "Available models" })).toBeVisible();
  await expect(page.getByRole("status")).toContainText("published");

  const readyResponse = page.waitForResponse(response =>
    new URL(response.url()).pathname === "/ready");
  await page.goto("/status");
  expect((await readyResponse).status()).toBe(200);
  await expect(page.getByText("Operational")).toBeVisible();

  await page.goto("/terms");
  await expect(page.getByRole("heading", { name: "Terms of service" })).toBeVisible();
  await page.goto("/privacy");
  await expect(page.getByRole("heading", { name: "Privacy notice" })).toBeVisible();
});
