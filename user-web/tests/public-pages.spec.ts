import { expect, test } from "@playwright/test";

test("public model catalog exposes navigable table semantics", async ({ page }) => {
  await page.route("**/v1/models", async route => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        object: "list",
        data: [{ id: "gpt-4o", object: "model", owned_by: "openai", created: 1720000000 }],
      }),
    });
  });

  await page.goto("/models");
  await expect(page.getByRole("heading", { name: "Available models" })).toBeVisible();
  await expect(page.getByRole("table", { name: "Published ScalaAPI models" })).toBeVisible();
  await expect(page.getByRole("rowheader", { name: "gpt-4o" })).toBeVisible();
  await expect(page.getByRole("navigation", { name: "Public navigation" })).toContainText("Status");
});

test("status and legal pages work without authentication", async ({ page }) => {
  await page.route("**/ready", async route => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ status: "ready" }),
    });
  });

  await page.goto("/status");
  await expect(page.getByRole("heading", { name: "ScalaAPI status" })).toBeVisible();
  await expect(page.getByText("Operational")).toBeVisible();

  await page.goto("/terms");
  await expect(page.getByRole("heading", { name: "Terms of service" })).toBeVisible();
  await expect(page.getByRole("navigation", { name: "Legal navigation" })).toBeVisible();

  await page.goto("/privacy");
  await expect(page.getByRole("heading", { name: "Privacy notice" })).toBeVisible();
});
