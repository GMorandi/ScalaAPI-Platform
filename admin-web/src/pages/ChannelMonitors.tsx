import { createResource, createSignal, For, Show } from "solid-js";
import { get, post } from "../api/client";
import type { ChannelMonitor } from "../api/types";
import { useI18n } from "../store/i18n";

interface MonitorResponse {
  items: ChannelMonitor[];
}

const formatDate = (value: string) =>
  new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(value));

export default function ChannelMonitors() {
  const { t } = useI18n();
  const [filterAccountId, setFilterAccountId] = createSignal("");
  const [refresh, setRefresh] = createSignal(0);
  const [accountId, setAccountId] = createSignal("");
  const [status, setStatus] = createSignal("healthy");
  const [latencyMs, setLatencyMs] = createSignal("0");
  const [lastError, setLastError] = createSignal("");
  const [busy, setBusy] = createSignal(false);
  const [message, setMessage] = createSignal("");
  const [error, setError] = createSignal("");

  const [monitors, { refetch }] = createResource(
    () => `${refresh()}|${filterAccountId()}`,
    (key) => {
      const [, selectedAccount] = key.split("|");
      const params = new URLSearchParams({ page: "1", size: "100" });
      if (selectedAccount.trim()) params.set("accountId", selectedAccount.trim());
      return get<MonitorResponse>(`/channel-monitors/?${params.toString()}`);
    },
  );

  const runCheck = async (event: Event) => {
    event.preventDefault();
    const parsedAccount = Number.parseInt(accountId(), 10);
    const parsedLatency = Number.parseInt(latencyMs(), 10);
    if (!Number.isSafeInteger(parsedAccount) || parsedAccount <= 0) {
      setError(t("monitors.invalidAccount"));
      return;
    }
    if (!Number.isSafeInteger(parsedLatency) || parsedLatency < 0 || parsedLatency > 600000) {
      setError(t("monitors.invalidLatency"));
      return;
    }
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await post("/channel-monitors/check", {
        accountId: parsedAccount,
        status: status(),
        latencyMs: parsedLatency,
        error: lastError().trim() || null,
      });
      setMessage(t("monitors.checkAccepted"));
      setFilterAccountId(String(parsedAccount));
      setLastError("");
      setRefresh((value) => value + 1);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setBusy(false);
    }
  };

  const refreshList = () => {
    refetch();
    setMessage("");
    setError("");
  };

  return (
    <div class="space-y-6">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 class="text-xl font-bold">{t("monitors.title")}</h1>
          <p class="mt-1 text-sm text-gray-500 dark:text-gray-400">{t("monitors.subtitle")}</p>
        </div>
        <button
          type="button"
          onClick={refreshList}
          disabled={monitors.loading || busy()}
          class="rounded-md border border-gray-300 px-3 py-2 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:hover:bg-gray-800"
        >
          {t("monitors.refresh")}
        </button>
      </div>

      <Show when={message()}>
        <div class="rounded border border-green-200 bg-green-50 px-3 py-2 text-sm text-green-800 dark:border-green-900 dark:bg-green-950 dark:text-green-200">{message()}</div>
      </Show>
      <Show when={error()}>
        <div class="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800 dark:border-red-900 dark:bg-red-950 dark:text-red-200">{error()}</div>
      </Show>

      <section aria-labelledby="monitor-check-heading" class="rounded-lg border border-gray-200 bg-white p-4 dark:border-gray-800 dark:bg-gray-900">
        <h2 id="monitor-check-heading" class="text-base font-semibold">{t("monitors.checkTitle")}</h2>
        <form class="mt-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-5" onSubmit={runCheck}>
          <label class="text-sm">
            <span class="mb-1 block text-gray-600 dark:text-gray-300">{t("monitors.accountId")}</span>
            <input required inputMode="numeric" value={accountId()} onInput={(event) => setAccountId(event.currentTarget.value)} class="w-full rounded border border-gray-300 bg-white px-3 py-2 dark:border-gray-700 dark:bg-gray-950" />
          </label>
          <label class="text-sm">
            <span class="mb-1 block text-gray-600 dark:text-gray-300">{t("monitors.status")}</span>
            <select value={status()} onChange={(event) => setStatus(event.currentTarget.value)} class="w-full rounded border border-gray-300 bg-white px-3 py-2 dark:border-gray-700 dark:bg-gray-950">
              <option value="healthy">healthy</option>
              <option value="degraded">degraded</option>
              <option value="unreachable">unreachable</option>
              <option value="unknown">unknown</option>
            </select>
          </label>
          <label class="text-sm">
            <span class="mb-1 block text-gray-600 dark:text-gray-300">{t("monitors.latency")}</span>
            <input required inputMode="numeric" value={latencyMs()} onInput={(event) => setLatencyMs(event.currentTarget.value)} class="w-full rounded border border-gray-300 bg-white px-3 py-2 dark:border-gray-700 dark:bg-gray-950" />
          </label>
          <label class="text-sm lg:col-span-2">
            <span class="mb-1 block text-gray-600 dark:text-gray-300">{t("monitors.error")}</span>
            <input value={lastError()} onInput={(event) => setLastError(event.currentTarget.value)} maxLength={1000} class="w-full rounded border border-gray-300 bg-white px-3 py-2 dark:border-gray-700 dark:bg-gray-950" />
          </label>
          <button type="submit" disabled={busy()} class="rounded bg-indigo-600 px-3 py-2 text-sm text-white hover:bg-indigo-700 disabled:opacity-50 lg:col-span-5 lg:justify-self-start">{t("monitors.submit")}</button>
        </form>
      </section>

      <section aria-labelledby="monitor-history-heading">
        <div class="mb-3 flex flex-wrap items-center justify-between gap-3">
          <h2 id="monitor-history-heading" class="text-base font-semibold">{t("monitors.history")}</h2>
          <label class="flex items-center gap-2 text-sm">
            <span>{t("monitors.filterAccount")}</span>
            <input inputMode="numeric" value={filterAccountId()} onInput={(event) => setFilterAccountId(event.currentTarget.value)} class="w-28 rounded border border-gray-300 bg-white px-2 py-1.5 dark:border-gray-700 dark:bg-gray-950" />
          </label>
        </div>
        <div class="overflow-x-auto rounded-lg border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900">
          <table class="min-w-full text-left text-sm">
            <thead class="border-b border-gray-200 text-xs uppercase text-gray-500 dark:border-gray-800">
              <tr>
                <th scope="col" class="px-4 py-3">{t("monitors.accountId")}</th>
                <th scope="col" class="px-4 py-3">{t("monitors.status")}</th>
                <th scope="col" class="px-4 py-3">{t("monitors.latency")}</th>
                <th scope="col" class="px-4 py-3">{t("monitors.error")}</th>
                <th scope="col" class="px-4 py-3">{t("monitors.checkedAt")}</th>
              </tr>
            </thead>
            <tbody>
              <For each={monitors()?.items} fallback={<tr><td colSpan={5} class="px-4 py-8 text-center text-gray-500">{t("monitors.noHistory")}</td></tr>}>
                {(monitor) => (
                  <tr class="border-b border-gray-100 last:border-0 dark:border-gray-800/60">
                    <th scope="row" class="px-4 py-3 font-medium">{monitor.accountId}</th>
                    <td class="px-4 py-3">{monitor.status}</td>
                    <td class="px-4 py-3">{monitor.latencyMs} ms</td>
                    <td class="max-w-xs truncate px-4 py-3 text-gray-500">{monitor.lastError ?? "-"}</td>
                    <td class="whitespace-nowrap px-4 py-3 text-gray-500">{formatDate(monitor.checkedAt)}</td>
                  </tr>
                )}
              </For>
            </tbody>
          </table>
        </div>
      </section>
    </div>
  );
}
