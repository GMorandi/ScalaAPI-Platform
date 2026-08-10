import { expect, test } from "@playwright/test";

const email = process.env.PUBLIC_UI_USER_EMAIL;
const password = process.env.PUBLIC_UI_USER_PASSWORD;

test.skip(!process.env.PUBLIC_UI_BASE_URL || !email || !password,
  "Requires a source-built User Web container and a seeded user");

test("source-built User Web signs in and navigates the authenticated portal", async ({ page }) => {
  await page.goto("/login");
  await page.getByLabel("Email").fill(email!);
  await page.getByLabel("Password").fill(password!);
  await Promise.all([
    page.waitForURL(url => url.pathname === "/"),
    page.getByRole("button", { name: "Sign in", exact: true }).click(),
  ]);

  await expect(page.getByRole("heading", { name: /Good to see you/ })).toBeVisible();
  await expect(page.getByText("Available balance")).toBeVisible();
  await expect(page.getByText(email!)).toBeVisible();

  await page.getByRole("link", { name: "Usage", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Usage" })).toBeVisible();

  await page.getByRole("link", { name: "API keys", exact: true }).click();
  await expect(page.getByRole("heading", { name: "API keys" })).toBeVisible();

  await page.getByRole("link", { name: "Profile", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Profile" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Account", exact: true })).toBeVisible();
});
