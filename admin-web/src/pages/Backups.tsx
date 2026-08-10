import { createResource, createSignal, For, Show } from "solid-js";
import { get, postWithHeaders } from "../api/client";
import type { BackupJob, BackupListResponse, RestoreRun } from "../api/types";
import { useI18n } from "../store/i18n";

const formatDate = (value: string | null) => value
  ? new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value))
  : "-";

const formatBytes = (value: number | null, suffix: string) =>
  value === null ? "-" : `${value.toLocaleString()} ${suffix}`;

export default function Backups() {
  const { t } = useI18n();
  const [refresh, setRefresh] = createSignal(0);
  const [retention, setRetention] = createSignal(14);
  const [message, setMessage] = createSignal("");
  const [error, setError] = createSignal("");
  const [creating, setCreating] = createSignal(false);
  const [restoring, setRestoring] = createSignal<string | null>(null);
  const [data] = createResource(refresh, () => get<BackupListResponse>("/backups/?page=1&size=100"));

  const reload = () => setRefresh((value) => value + 1);
  const commandKey = (prefix: string) => `${prefix}-${crypto.randomUUID()}`;

  const createBackup = async () => {
    setMessage("");
    setError("");
    setCreating(true);
    try {
      await postWithHeaders<BackupJob>("/backups/", { kind: "postgres", retentionDays: retention() }, {
        "Idempotency-Key": commandKey("admin-backup"),
      });
      setMessage(t("backups.createdMessage"));
      reload();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause));
    } finally {
      setCreating(false);
    }
  };

  const restore = async (job: BackupJob) => {
    setMessage("");
    setError("");
    setRestoring(job.id);
    try {
      await postWithHeaders<RestoreRun>(`/backups/${job.id}/restore`, {}, {
        "Idempotency-Key": commandKey(`admin-restore-${job.id}`),
      });
      setMessage(t("backups.restoreAccepted"));
      reload();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause));
    } finally {
      setRestoring(null);
    }
  };

  return (
    <div class="space-y-6">
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 class="text-xl font-bold">{t("backups.title")}</h1>
          <p class="mt-1 text-sm text-gray-500 dark:text-gray-400">{t("backups.subtitle")}</p>
        </div>
        <div class="flex flex-wrap items-center gap-2">
          <span class={`rounded-md px-3 py-2 text-sm ${data()?.restoreConfigured
            ? "bg-emerald-50 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300"
            : "bg-amber-50 text-amber-700 dark:bg-amber-950 dark:text-amber-300"}`}>
            {data()?.restoreConfigured ? t("backups.restoreReady") : t("backups.restoreMissing")}
          </span>
          <button type="button" class="rounded-md border border-gray-300 px-3 py-2 text-sm hover:bg-gray-50 dark:border-gray-700 dark:hover:bg-gray-800" onClick={reload}>
            {t("backups.refresh")}
          </button>
        </div>
      </div>

      <Show when={message()}><p role="status" class="border-l-2 border-emerald-600 bg-emerald-50 px-3 py-2 text-sm text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300">{message()}</p></Show>
      <Show when={error()}><p role="alert" class="border-l-2 border-red-600 bg-red-50 px-3 py-2 text-sm text-red-700 dark:bg-red-950 dark:text-red-300">{error()}</p></Show>

      <section class="rounded-lg border border-gray-200 bg-white p-4 dark:border-gray-800 dark:bg-gray-900">
        <div class="flex flex-wrap items-end justify-between gap-4">
          <div>
            <label for="backup-retention" class="mb-1 block text-sm font-medium">{t("backups.retention")}</label>
            <input id="backup-retention" type="number" min="1" max="365" value={retention()} onInput={(event) => setRetention(Number(event.currentTarget.value))} class="w-32 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-900" />
          </div>
          <button type="button" disabled={creating()} onClick={createBackup} class="rounded-md bg-indigo-600 px-4 py-2 text-sm text-white hover:bg-indigo-700 disabled:cursor-not-allowed disabled:opacity-60">
            {creating() ? t("common.loading") : t("backups.create")}
          </button>
        </div>
      </section>

      <section class="overflow-x-auto rounded-lg border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900">
        <table class="min-w-full text-left text-sm">
          <thead class="border-b border-gray-200 text-xs uppercase text-gray-500 dark:border-gray-800">
            <tr>
              <th class="px-4 py-3">{t("backups.id")}</th>
              <th class="px-4 py-3">{t("backups.kind")}</th>
              <th class="px-4 py-3">{t("backups.status")}</th>
              <th class="px-4 py-3">{t("backups.size")}</th>
              <th class="px-4 py-3">{t("backups.checksum")}</th>
              <th class="px-4 py-3">{t("backups.created")}</th>
              <th class="px-4 py-3">{t("common.actions")}</th>
            </tr>
          </thead>
          <tbody>
            <For each={data()?.items} fallback={<tr><td colSpan={7} class="px-4 py-8 text-center text-gray-500">{data.loading ? t("common.loading") : t("backups.noJobs")}</td></tr>}>
              {(job) => <tr class="border-b border-gray-100 last:border-0 dark:border-gray-800/60">
                <td class="whitespace-nowrap px-4 py-3 font-mono text-xs">{job.id}</td>
                <td class="px-4 py-3">{job.kind}</td>
                <td class="px-4 py-3"><span class={`rounded px-2 py-1 text-xs ${job.status === "completed" ? "bg-emerald-50 text-emerald-700 dark:bg-emerald-950 dark:text-emerald-300" : job.status === "failed" ? "bg-red-50 text-red-700 dark:bg-red-950 dark:text-red-300" : "bg-amber-50 text-amber-700 dark:bg-amber-950 dark:text-amber-300"}`}>{job.status}</span><Show when={job.errorCode}><div class="mt-1 text-xs text-red-600">{job.errorCode}</div></Show></td>
                <td class="whitespace-nowrap px-4 py-3">{formatBytes(job.sizeBytes, t("backups.bytes"))}</td>
                <td class="max-w-48 truncate px-4 py-3 font-mono text-xs" title={job.sha256 ?? ""}>{job.sha256 ? `${job.sha256.slice(0, 12)}...` : "-"}</td>
                <td class="whitespace-nowrap px-4 py-3 text-gray-500">{formatDate(job.createdAt)}</td>
                <td class="px-4 py-3"><Show when={job.status === "completed" && data()?.restoreConfigured}><button type="button" disabled={restoring() === job.id} onClick={() => restore(job)} class="rounded-md border border-gray-300 px-3 py-1.5 text-sm hover:bg-gray-50 disabled:opacity-60 dark:border-gray-700 dark:hover:bg-gray-800">{restoring() === job.id ? t("common.loading") : t("backups.restore")}</button></Show></td>
              </tr>}
            </For>
          </tbody>
        </table>
      </section>
    </div>
  );
}
