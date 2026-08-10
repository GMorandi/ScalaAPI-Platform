import { createResource, createSignal, For, Show } from "solid-js";
import { get } from "../api/client";
import type { OpsMetricSummary, OpsPolicyAlert } from "../api/types";
import { useI18n } from "../store/i18n";

interface MetricResponse {
  items: OpsMetricSummary[];
}

interface AlertResponse {
  items: OpsPolicyAlert[];
}

const formatDate = (value: string) =>
  new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(value));

export default function Operations() {
  const { t } = useI18n();
  const [kind, setKind] = createSignal("");
  const [severity, setSeverity] = createSignal("");
  const [refresh, setRefresh] = createSignal(0);
  const [metrics] = createResource(
    refresh,
    () => get<MetricResponse>("/ops-metrics/?limit=100"),
  );
  const [alerts] = createResource(
    () => `${refresh()}|${kind()}|${severity()}`,
    async (key) => {
      const [, selectedKind, selectedSeverity] = key.split("|");
      const params = new URLSearchParams({ limit: "100" });
      if (selectedKind) params.set("kind", selectedKind);
      if (selectedSeverity) params.set("severity", selectedSeverity);
      return get<AlertResponse>(`/ops-metrics/policy-alerts?${params.toString()}`);
    },
  );

  return (
    <div class="space-y-6">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 class="text-xl font-bold">{t("operations.title")}</h1>
          <p class="mt-1 text-sm text-gray-500 dark:text-gray-400">
            {t("operations.subtitle")}
          </p>
        </div>
        <button
          type="button"
          class="rounded-md border border-gray-300 px-3 py-2 text-sm hover:bg-gray-50 dark:border-gray-700 dark:hover:bg-gray-800"
          onClick={() => setRefresh((value) => value + 1)}
        >
          {t("operations.refresh")}
        </button>
      </div>

      <section aria-labelledby="metric-summary-heading">
        <div class="mb-3 flex items-center justify-between">
          <h2 id="metric-summary-heading" class="text-base font-semibold">
            {t("operations.metrics")}
          </h2>
          <Show when={metrics.loading}>
            <span class="text-sm text-gray-500">{t("common.loading")}</span>
          </Show>
        </div>
        <div class="overflow-x-auto rounded-lg border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900">
          <table class="min-w-full text-left text-sm">
            <thead class="border-b border-gray-200 text-xs uppercase text-gray-500 dark:border-gray-800">
              <tr>
                <th scope="col" class="px-4 py-3">{t("operations.metricName")}</th>
                <th scope="col" class="px-4 py-3">{t("operations.latest")}</th>
                <th scope="col" class="px-4 py-3">{t("operations.average")}</th>
                <th scope="col" class="px-4 py-3">{t("operations.samples")}</th>
                <th scope="col" class="px-4 py-3">{t("operations.latestAt")}</th>
              </tr>
            </thead>
            <tbody>
              <For each={metrics()?.items} fallback={
                <tr><td colSpan={5} class="px-4 py-8 text-center text-gray-500">{t("operations.noMetrics")}</td></tr>
              }>
                {(metric) => (
                  <tr class="border-b border-gray-100 last:border-0 dark:border-gray-800/60">
                    <th scope="row" class="whitespace-nowrap px-4 py-3 font-medium">{metric.metricName}</th>
                    <td class="px-4 py-3">{metric.latestValue}</td>
                    <td class="px-4 py-3">{metric.averageValue}</td>
                    <td class="px-4 py-3">{metric.samples}</td>
                    <td class="whitespace-nowrap px-4 py-3 text-gray-500">{formatDate(metric.latestAt)}</td>
                  </tr>
                )}
              </For>
            </tbody>
          </table>
        </div>
      </section>

      <section aria-labelledby="policy-alerts-heading">
        <div class="mb-3 flex flex-wrap items-center justify-between gap-3">
          <h2 id="policy-alerts-heading" class="text-base font-semibold">
            {t("operations.policyAlerts")}
          </h2>
          <div class="flex flex-wrap gap-2">
            <label class="sr-only" for="operations-kind">{t("operations.kind")}</label>
            <select
              id="operations-kind"
              class="rounded-md border border-gray-300 bg-white px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-900"
              value={kind()}
              onChange={(event) => setKind(event.currentTarget.value)}
            >
              <option value="">{t("operations.allKinds")}</option>
              <option value="policy_block">policy_block</option>
              <option value="classifier_unavailable">classifier_unavailable</option>
            </select>
            <label class="sr-only" for="operations-severity">{t("operations.severity")}</label>
            <select
              id="operations-severity"
              class="rounded-md border border-gray-300 bg-white px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-900"
              value={severity()}
              onChange={(event) => setSeverity(event.currentTarget.value)}
            >
              <option value="">{t("operations.allSeverities")}</option>
              <option value="warning">warning</option>
              <option value="critical">critical</option>
            </select>
          </div>
        </div>
        <div class="overflow-x-auto rounded-lg border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900">
          <table class="min-w-full text-left text-sm">
            <thead class="border-b border-gray-200 text-xs uppercase text-gray-500 dark:border-gray-800">
              <tr>
                <th scope="col" class="px-4 py-3">{t("operations.kind")}</th>
                <th scope="col" class="px-4 py-3">{t("operations.severity")}</th>
                <th scope="col" class="px-4 py-3">{t("operations.code")}</th>
                <th scope="col" class="px-4 py-3">{t("operations.requestId")}</th>
                <th scope="col" class="px-4 py-3">{t("operations.createdAt")}</th>
              </tr>
            </thead>
            <tbody>
              <For each={alerts()?.items} fallback={
                <tr><td colSpan={5} class="px-4 py-8 text-center text-gray-500">{t("operations.noAlerts")}</td></tr>
              }>
                {(alert) => (
                  <tr class="border-b border-gray-100 last:border-0 dark:border-gray-800/60">
                    <td class="px-4 py-3 font-medium">{alert.kind}</td>
                    <td class="px-4 py-3">{alert.severity}</td>
                    <td class="px-4 py-3">{alert.code}</td>
                    <td class="px-4 py-3 font-mono text-xs">{alert.requestId ?? "-"}</td>
                    <td class="whitespace-nowrap px-4 py-3 text-gray-500">{formatDate(alert.createdAt)}</td>
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
