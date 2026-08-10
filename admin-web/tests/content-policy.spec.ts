import { expect, test } from "@playwright/test";

test("content policy rules, changes, and alerts render", async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem("token", "browser-smoke-token");
    localStorage.setItem("locale", "en");
  });
  await page.route("**/admin/content-audit/**", async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith("/rules")) {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({
          items: [{
            id: 7,
            pattern: "credential theft",
            actionType: "block",
            scope: "public-api",
            status: "active",
            stage: "request",
            evaluatorVersion: "unicode-confusable-v1",
            classifier: "openai",
            redactContent: true,
            createdAt: "2026-08-10T10:00:00Z",
          }],
        }),
      });
      return;
    }
    if (path.endsWith("/changes")) {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({
          items: [{
            id: 11,
            revision: 19,
            action: "updated",
            ruleId: 7,
            actorId: 2,
            ipAddress: "127.0.0.1",
            details: "{}",
            createdAt: "2026-08-10T10:01:00Z",
            propagatedAt: "2026-08-10T10:01:01Z",
            attempts: 1,
            lastError: null,
          }],
          total: 1,
          page: 1,
          size: 100,
        }),
      });
      return;
    }
    if (path.endsWith("/alerts")) {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({
          items: [{
            id: 21,
            eventKey: "policy:21",
            kind: "classifier_unavailable",
            severity: "warning",
            ruleId: 7,
            userId: 42,
            requestId: "req-browser-1",
            stage: "request",
            code: "openai_timeout",
            policyRevision: 19,
            details: "{}",
            createdAt: "2026-08-10T10:02:00Z",
          }],
          total: 1,
          page: 1,
          size: 100,
        }),
      });
      return;
    }
    await route.fulfill({ status: 404, body: "not found" });
  });

  await page.goto("/content-policy");
  await expect(page.getByRole("heading", { name: "Content policy" })).toBeVisible();
  await expect(page.getByText("credential theft")).toBeVisible();
  await page.getByRole("button", { name: "Changes" }).click();
  await expect(page.getByText("19")).toBeVisible();
  await page.getByRole("button", { name: "Alerts" }).click();
  await expect(page.getByText("classifier_unavailable")).toBeVisible();
  await page.screenshot({ path: "test-results/content-policy.png", fullPage: true });
});
