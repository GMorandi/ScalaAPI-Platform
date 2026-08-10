import { expect, test } from "@playwright/test";

test("backup page creates and restores a completed artifact", async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem("token", "browser-smoke-token");
    localStorage.setItem("locale", "en");
  });

  let jobs = [{
    id: "bak_0123456789abcdef0123456789abcdef",
    kind: "postgres",
    status: "completed",
    artifactName: "bak_0123456789abcdef0123456789abcdef.dump",
    sizeBytes: 4096,
    sha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    retentionUntil: "2026-08-24T10:00:00Z",
    createdBy: 1,
    createdAt: "2026-08-10T10:00:00Z",
    completedAt: "2026-08-10T10:01:00Z",
    errorCode: null,
    errorDetail: null,
  }];

  await page.route("**/admin/backups/**", async (route) => {
    const request = route.request();
    if (request.method() === "GET") {
      await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ items: jobs, restoreConfigured: true }) });
      return;
    }
    if (request.method() === "POST" && request.url().endsWith("/restore")) {
      await route.fulfill({ status: 202, contentType: "application/json", body: JSON.stringify({ id: "rst_1", backupId: jobs[0].id, status: "running", createdAt: "2026-08-10T10:02:00Z", completedAt: null, errorCode: null, errorDetail: null }) });
      return;
    }
    await route.fulfill({ status: 404, body: "not found" });
  });
  await page.route("**/admin/backups/", async (route) => {
    if (route.request().method() === "POST") {
      jobs = [{ ...jobs[0], id: "bak_new", status: "completed" }, ...jobs];
      await route.fulfill({ status: 201, contentType: "application/json", body: JSON.stringify(jobs[0]) });
      return;
    }
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ items: jobs, restoreConfigured: true }) });
  });

  await page.goto("/backups");
  await expect(page.getByRole("heading", { name: "Backups and restore" })).toBeVisible();
  await expect(page.getByText("bak_0123456789abcdef0123456789abcdef")).toBeVisible();
  await page.getByRole("button", { name: "Create backup" }).click();
  await expect(page.getByRole("status")).toContainText("Backup job completed");
  await page.getByRole("button", { name: "Restore" }).first().click();
  await expect(page.getByRole("status")).toContainText("Restore job accepted");
});
