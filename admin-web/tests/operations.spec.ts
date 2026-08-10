import { expect, test } from "@playwright/test";

test("operations dashboard renders summaries and filters policy alerts", async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem("token", "browser-smoke-token");
    localStorage.setItem("locale", "en");
  });
  await page.route("**/admin/ops-metrics/**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith("/policy-alerts")) {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({ items: [{
          id: 21,
          eventKey: "policy:21",
          kind: "classifier_unavailable",
          severity: "critical",
          ruleId: 7,
          userId: 42,
          requestId: "req-ops-1",
          stage: "response",
          code: "openai_timeout",
          policyRevision: 19,
          details: "{}",
          createdAt: "2026-08-10T10:02:00Z",
        }] }),
      });
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ items: [{
        metricName: "platform_reconciliation_open_incidents",
        latestValue: 2,
        averageValue: 1.5,
        samples: 4,
        latestAt: "2026-08-10T10:03:00Z",
      }] }),
    });
  });

  await page.goto("/operations");
  await expect(page.getByRole("heading", { name: "Operations" })).toBeVisible();
  await expect(page.getByText("platform_reconciliation_open_incidents")).toBeVisible();
  await expect(page.getByText("openai_timeout")).toBeVisible();
  await page.getByLabel("Severity").selectOption("critical");
  await expect(page.getByLabel("Severity")).toHaveValue("critical");
  await page.getByRole("button", { name: "Refresh" }).click();
  await expect(page.getByText("req-ops-1")).toBeVisible();
});
