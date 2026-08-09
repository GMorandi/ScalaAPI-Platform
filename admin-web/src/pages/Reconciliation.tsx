import { createResource, createSignal, For, Show } from "solid-js";
import { get, post, postWithHeaders } from "../api/client";
import type {
  Paged,
  ReconciliationIncident,
  ReconciliationResolutionResult,
  ReconciliationRunResult,
} from "../api/types";
import { useI18n } from "../store/i18n";

type Action = "settle" | "release";

const emptyNumber = 0;

export default function Reconciliation() {
  const { t } = useI18n();
  const [status, setStatus] = createSignal("open");
  const [selected, setSelected] = createSignal<ReconciliationIncident>();
  const [action, setAction] = createSignal<Action>("settle");
  const [evidenceType, setEvidenceType] = createSignal("");
  const [evidence, setEvidence] = createSignal("");
  const [reason, setReason] = createSignal("");
  const [inputTokens, setInputTokens] = createSignal(emptyNumber);
  const [outputTokens, setOutputTokens] = createSignal(emptyNumber);
  const [idempotencyKey, setIdempotencyKey] = createSignal("");
  const [busy, setBusy] = createSignal(false);
  const [message, setMessage] = createSignal("");
  const [error, setError] = createSignal("");
  const [data, { refetch }] = createResource(status, (value) =>
    get<Paged<ReconciliationIncident>>(
      `/reconciliation/incidents?status=${encodeURIComponent(value)}&page=1&size=100`
    )
  );

  const selectIncident = (incident: ReconciliationIncident) => {
    setSelected(incident);
    setAction("settle");
    setEvidenceType("");
    setEvidence("");
    setReason("");
    setInputTokens(emptyNumber);
    setOutputTokens(emptyNumber);
    setIdempotencyKey(crypto.randomUUID());
    setMessage("");
    setError("");
  };

  const run = async () => {
    setBusy(true);
    setError("");
    try {
      await post<ReconciliationRunResult>("/reconciliation/run", {});
      setMessage(t("reconciliation.success"));
      refetch();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setBusy(false);
    }
  };

  const resolve = async (event: Event) => {
    event.preventDefault();
    const incident = selected();
    if (!incident || !evidenceType().trim() || !evidence().trim() || !reason().trim()) {
      setError("Evidence type, evidence, and reason are required");
      return;
    }

    setBusy(true);
    setError("");
    try {
      const result = await postWithHeaders<ReconciliationResolutionResult>(
        `/reconciliation/incidents/${incident.id}/resolve`,
        {
          action: action(),
          evidenceType: evidenceType().trim(),
          evidence: evidence().trim(),
          reason: reason().trim(),
          inputTokens: inputTokens(),
          outputTokens: outputTokens(),
        },
        { "Idempotency-Key": idempotencyKey() }
      );
      if (result.status !== "applied" && result.status !== "duplicate") {
        throw new Error(result.error_code || result.status);
      }
      setMessage(t("reconciliation.success"));
      setSelected(undefined);
      setIdempotencyKey("");
      refetch();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div class="space-y-6">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h1 class="text-xl font-bold">{t("reconciliation.title")}</h1>
        <div class="flex items-center gap-2">
          <select
            class="rounded border border-gray-300 bg-white px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800"
            value={status()}
            onChange={(event) => setStatus(event.currentTarget.value)}
          >
            <option value="open">{t("reconciliation.open")}</option>
            <option value="resolved">{t("reconciliation.resolved")}</option>
          </select>
          <button
            type="button"
            disabled={busy()}
            onClick={run}
            class="rounded bg-indigo-600 px-3 py-2 text-sm text-white hover:bg-indigo-700 disabled:opacity-50"
          >
            {t("reconciliation.run")}
          </button>
        </div>
      </div>

      <Show when={message()}>
        <div class="rounded border border-green-200 bg-green-50 px-3 py-2 text-sm text-green-800 dark:border-green-900 dark:bg-green-950 dark:text-green-200">
          {message()}
        </div>
      </Show>
      <Show when={error()}>
        <div class="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800 dark:border-red-900 dark:bg-red-950 dark:text-red-200">
          {error()}
        </div>
      </Show>

      <div class="overflow-x-auto rounded border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900">
        <table class="w-full min-w-[760px] text-sm">
          <thead>
            <tr class="border-b border-gray-200 text-left dark:border-gray-800">
              <th class="px-3 py-2">{t("reconciliation.incident")}</th>
              <th class="px-3 py-2">{t("reconciliation.kind")}</th>
              <th class="px-3 py-2">{t("reconciliation.severity")}</th>
              <th class="px-3 py-2">{t("reconciliation.user")}</th>
              <th class="px-3 py-2">{t("reconciliation.occurrences")}</th>
              <th class="px-3 py-2">{t("reconciliation.lastSeen")}</th>
              <th class="px-3 py-2">{t("common.actions")}</th>
            </tr>
          </thead>
          <tbody>
            <For each={data()?.items} fallback={<tr><td class="px-3 py-4 text-gray-500" colSpan={7}>{t("reconciliation.noIncidents")}</td></tr>}>
              {(incident) => (
                <tr class="border-b border-gray-100 dark:border-gray-800/50">
                  <td class="max-w-xs truncate px-3 py-2 font-mono text-xs" title={incident.incident_key}>{incident.incident_key}</td>
                  <td class="px-3 py-2">{incident.kind}</td>
                  <td class="px-3 py-2">{incident.severity}</td>
                  <td class="px-3 py-2">{incident.user_id ?? "-"}</td>
                  <td class="px-3 py-2">{incident.occurrences}</td>
                  <td class="px-3 py-2 whitespace-nowrap">{new Date(incident.last_seen_at).toLocaleString()}</td>
                  <td class="px-3 py-2">
                    <Show when={incident.status === "open"} fallback={<span class="text-gray-500">{t("reconciliation.resolved")}</span>}>
                      <button type="button" class="text-indigo-600 hover:underline" onClick={() => selectIncident(incident)}>
                        {t("reconciliation.resolve")}
                      </button>
                    </Show>
                  </td>
                </tr>
              )}
            </For>
          </tbody>
        </table>
      </div>

      <Show when={selected()}>
        {(incident) => (
          <form onSubmit={resolve} class="max-w-3xl space-y-4 rounded border border-gray-200 bg-white p-4 dark:border-gray-800 dark:bg-gray-900">
            <div class="flex items-center justify-between">
              <h2 class="font-semibold">{t("reconciliation.resolve")}: {incident().incident_key}</h2>
              <button type="button" class="text-sm text-gray-500 hover:text-gray-900 dark:hover:text-gray-100" onClick={() => setSelected(undefined)}>
                {t("reconciliation.cancel")}
              </button>
            </div>
            <div class="grid gap-3 sm:grid-cols-2">
              <label class="text-sm">{t("reconciliation.action")}
                <select class="mt-1 w-full rounded border border-gray-300 bg-white px-3 py-2 dark:border-gray-700 dark:bg-gray-800" value={action()} onChange={(event) => setAction(event.currentTarget.value as Action)}>
                  <option value="settle">{t("reconciliation.settle")}</option>
                  <option value="release">{t("reconciliation.release")}</option>
                </select>
              </label>
              <label class="text-sm">{t("reconciliation.evidenceType")}
                <input required class="mt-1 w-full rounded border border-gray-300 px-3 py-2 dark:border-gray-700 dark:bg-gray-800" value={evidenceType()} onInput={(event) => setEvidenceType(event.currentTarget.value)} />
              </label>
            </div>
            <label class="block text-sm">{t("reconciliation.evidence")}
              <textarea required rows={3} class="mt-1 w-full rounded border border-gray-300 px-3 py-2 dark:border-gray-700 dark:bg-gray-800" value={evidence()} onInput={(event) => setEvidence(event.currentTarget.value)} />
            </label>
            <label class="block text-sm">{t("reconciliation.reason")}
              <textarea required rows={2} class="mt-1 w-full rounded border border-gray-300 px-3 py-2 dark:border-gray-700 dark:bg-gray-800" value={reason()} onInput={(event) => setReason(event.currentTarget.value)} />
            </label>
            <Show when={action() === "settle"}>
              <div class="grid gap-3 sm:grid-cols-2">
                <label class="text-sm">{t("reconciliation.inputTokens")}
                  <input type="number" min="0" class="mt-1 w-full rounded border border-gray-300 px-3 py-2 dark:border-gray-700 dark:bg-gray-800" value={inputTokens()} onInput={(event) => setInputTokens(Math.max(0, Number(event.currentTarget.value) || 0))} />
                </label>
                <label class="text-sm">{t("reconciliation.outputTokens")}
                  <input type="number" min="0" class="mt-1 w-full rounded border border-gray-300 px-3 py-2 dark:border-gray-700 dark:bg-gray-800" value={outputTokens()} onInput={(event) => setOutputTokens(Math.max(0, Number(event.currentTarget.value) || 0))} />
                </label>
              </div>
            </Show>
            <button type="submit" disabled={busy()} class="rounded bg-indigo-600 px-4 py-2 text-sm text-white hover:bg-indigo-700 disabled:opacity-50">
              {t("reconciliation.submit")}
            </button>
          </form>
        )}
      </Show>
    </div>
  );
}
