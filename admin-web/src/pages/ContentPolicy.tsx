import { createResource, createSignal, For, Show } from "solid-js";
import { del, get, post, put } from "../api/client";
import type {
  ContentPolicyAlert,
  ContentPolicyChange,
  ContentPolicyRule,
  ContentPolicyRuleRequest,
  Paged,
} from "../api/types";
import { useI18n } from "../store/i18n";

type Tab = "rules" | "changes" | "alerts";

const emptyRule = (): ContentPolicyRuleRequest => ({
  pattern: "",
  actionType: "log",
  scope: null,
  status: "active",
  stage: "request",
  evaluatorVersion: "unicode-confusable-v1",
  classifier: "local",
  redactContent: true,
});

const date = (value: string | null | undefined) =>
  value ? new Date(value).toLocaleString() : "-";

const badge = (value: string, tone: "green" | "amber" | "red" | "gray" = "gray") => {
  const colors = {
    green: "bg-green-100 text-green-800 dark:bg-green-950 dark:text-green-200",
    amber: "bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-200",
    red: "bg-red-100 text-red-800 dark:bg-red-950 dark:text-red-200",
    gray: "bg-gray-100 text-gray-700 dark:bg-gray-800 dark:text-gray-300",
  };
  return <span class={`inline-flex rounded px-2 py-0.5 text-xs ${colors[tone]}`}>{value}</span>;
};

export default function ContentPolicy() {
  const { t } = useI18n();
  const [tab, setTab] = createSignal<Tab>("rules");
  const [editing, setEditing] = createSignal<ContentPolicyRule | null | undefined>();
  const [form, setForm] = createSignal<ContentPolicyRuleRequest>(emptyRule());
  const [busy, setBusy] = createSignal(false);
  const [message, setMessage] = createSignal("");
  const [error, setError] = createSignal("");
  const [changeAction, setChangeAction] = createSignal("");
  const [pendingOnly, setPendingOnly] = createSignal(false);
  const [alertKind, setAlertKind] = createSignal("");
  const [alertSeverity, setAlertSeverity] = createSignal("");

  const [rules, { refetch: refetchRules }] = createResource(() =>
    get<{ items: ContentPolicyRule[] }>("/content-audit/rules")
  );
  const [changes, { refetch: refetchChanges }] = createResource(
    () => `${changeAction()}|${pendingOnly()}`,
    (key) => {
      const [action, pending] = key.split("|");
      const params = new URLSearchParams({ page: "1", size: "100" });
      if (action) params.set("action", action);
      if (pending === "true") params.set("pending", "true");
      return get<Paged<ContentPolicyChange>>(`/content-audit/changes?${params}`);
    }
  );
  const [alerts, { refetch: refetchAlerts }] = createResource(
    () => `${alertKind()}|${alertSeverity()}`,
    (key) => {
      const [kind, severity] = key.split("|");
      const params = new URLSearchParams({ page: "1", size: "100" });
      if (kind) params.set("kind", kind);
      if (severity) params.set("severity", severity);
      return get<Paged<ContentPolicyAlert>>(`/content-audit/alerts?${params}`);
    }
  );

  const setField = <K extends keyof ContentPolicyRuleRequest>(key: K, value: ContentPolicyRuleRequest[K]) =>
    setForm((current) => ({ ...current, [key]: value }));

  const beginCreate = () => {
    setEditing(null);
    setForm(emptyRule());
    setMessage("");
    setError("");
  };

  const beginEdit = (rule: ContentPolicyRule) => {
    setEditing(rule);
    setForm({
      pattern: rule.pattern,
      actionType: rule.actionType,
      scope: rule.scope,
      status: rule.status,
      stage: rule.stage,
      evaluatorVersion: rule.evaluatorVersion,
      classifier: rule.classifier,
      redactContent: rule.redactContent,
    });
    setMessage("");
    setError("");
  };

  const closeEditor = () => setEditing(undefined);

  const saveRule = async (event: Event) => {
    event.preventDefault();
    if (!form().pattern.trim()) {
      setError(t("contentPolicy.required"));
      return;
    }
    setBusy(true);
    setError("");
    setMessage("");
    const payload = { ...form(), pattern: form().pattern.trim(), scope: form().scope?.trim() || null };
    try {
      if (editing()?.id) {
        await put(`/content-audit/rules/${editing()!.id}`, payload);
      } else {
        await post("/content-audit/rules", payload);
      }
      closeEditor();
      setMessage(t("common.save"));
      refetchRules();
      refetchChanges();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setBusy(false);
    }
  };

  const removeRule = async (rule: ContentPolicyRule) => {
    if (!window.confirm(t("contentPolicy.deleteConfirm"))) return;
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await del(`/content-audit/rules/${rule.id}`);
      setMessage(t("common.delete"));
      refetchRules();
      refetchChanges();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setBusy(false);
    }
  };

  const refresh = () => {
    if (tab() === "rules") refetchRules();
    if (tab() === "changes") refetchChanges();
    if (tab() === "alerts") refetchAlerts();
  };

  return (
    <div class="space-y-6">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h1 class="text-xl font-bold">{t("contentPolicy.title")}</h1>
        <div class="flex items-center gap-2">
          <button type="button" onClick={refresh} disabled={busy()} class="rounded border border-gray-300 px-3 py-2 text-sm hover:bg-gray-50 disabled:opacity-50 dark:border-gray-700 dark:hover:bg-gray-800">
            {t("contentPolicy.refresh")}
          </button>
          <Show when={tab() === "rules"}>
            <button type="button" onClick={beginCreate} class="rounded bg-indigo-600 px-3 py-2 text-sm text-white hover:bg-indigo-700">
              {t("contentPolicy.newRule")}
            </button>
          </Show>
        </div>
      </div>

      <Show when={message()}>
        <div class="rounded border border-green-200 bg-green-50 px-3 py-2 text-sm text-green-800 dark:border-green-900 dark:bg-green-950 dark:text-green-200">{message()}</div>
      </Show>
      <Show when={error()}>
        <div class="rounded border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-800 dark:border-red-900 dark:bg-red-950 dark:text-red-200">{error()}</div>
      </Show>

      <div class="flex gap-1 border-b border-gray-200 dark:border-gray-800">
        {(["rules", "changes", "alerts"] as Tab[]).map((value) => (
          <button type="button" onClick={() => { setTab(value); setMessage(""); setError(""); }} class={`border-b-2 px-3 py-2 text-sm ${tab() === value ? "border-indigo-600 text-indigo-700 dark:text-indigo-300" : "border-transparent text-gray-500 hover:text-gray-900 dark:hover:text-gray-200"}`}>
            {t(`contentPolicy.${value}`)}
          </button>
        ))}
      </div>

      <Show when={tab() === "rules"}>
        <div class="overflow-x-auto rounded border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900">
          <table class="w-full min-w-[980px] text-sm">
            <thead><tr class="border-b border-gray-200 text-left dark:border-gray-800">
              <th class="px-3 py-2">{t("contentPolicy.pattern")}</th>
              <th class="px-3 py-2">{t("contentPolicy.action")}</th>
              <th class="px-3 py-2">{t("contentPolicy.stage")}</th>
              <th class="px-3 py-2">{t("contentPolicy.scope")}</th>
              <th class="px-3 py-2">{t("contentPolicy.classifier")}</th>
              <th class="px-3 py-2">{t("contentPolicy.status")}</th>
              <th class="px-3 py-2">{t("contentPolicy.created")}</th>
              <th class="px-3 py-2">{t("common.actions")}</th>
            </tr></thead>
            <tbody>
              <For each={rules()?.items} fallback={<tr><td colSpan={8} class="px-3 py-6 text-gray-500">{t("contentPolicy.noRules")}</td></tr>}>
                {(rule) => <tr class="border-b border-gray-100 dark:border-gray-800/50">
                  <td class="max-w-xs truncate px-3 py-2 font-mono text-xs" title={rule.pattern}>{rule.pattern}</td>
                  <td class="px-3 py-2">{badge(rule.actionType, rule.actionType === "block" ? "red" : "amber")}</td>
                  <td class="px-3 py-2">{rule.stage}</td>
                  <td class="px-3 py-2">{rule.scope || "-"}</td>
                  <td class="px-3 py-2">{rule.classifier}</td>
                  <td class="px-3 py-2">{badge(rule.status, rule.status === "active" ? "green" : "gray")}</td>
                  <td class="whitespace-nowrap px-3 py-2">{date(rule.createdAt)}</td>
                  <td class="whitespace-nowrap px-3 py-2">
                    <button type="button" onClick={() => beginEdit(rule)} class="mr-3 text-indigo-600 hover:underline">{t("common.edit")}</button>
                    <button type="button" onClick={() => removeRule(rule)} class="text-red-600 hover:underline">{t("common.delete")}</button>
                  </td>
                </tr>}
              </For>
            </tbody>
          </table>
        </div>

        <Show when={editing() !== undefined}>
          <form onSubmit={saveRule} class="max-w-3xl space-y-4 rounded border border-gray-200 bg-white p-4 dark:border-gray-800 dark:bg-gray-900">
            <div class="flex items-center justify-between">
              <h2 class="font-semibold">{editing()?.id ? t("contentPolicy.editRule") : t("contentPolicy.createRule")}</h2>
              <button type="button" onClick={closeEditor} class="text-sm text-gray-500 hover:text-gray-900 dark:hover:text-gray-100">{t("contentPolicy.cancel")}</button>
            </div>
            <label class="block text-sm">{t("contentPolicy.pattern")}
              <input required maxLength={512} class="mt-1 w-full rounded border border-gray-300 px-3 py-2 font-mono text-sm dark:border-gray-700 dark:bg-gray-800" value={form().pattern} onInput={(event) => setField("pattern", event.currentTarget.value)} />
            </label>
            <div class="grid gap-3 sm:grid-cols-2">
              <label class="text-sm">{t("contentPolicy.action")}
                <select class="mt-1 w-full rounded border border-gray-300 bg-white px-3 py-2 dark:border-gray-700 dark:bg-gray-800" value={form().actionType} onChange={(event) => setField("actionType", event.currentTarget.value as "log" | "block")}>
                  <option value="log">{t("contentPolicy.log")}</option><option value="block">{t("contentPolicy.block")}</option>
                </select>
              </label>
              <label class="text-sm">{t("contentPolicy.stage")}
                <select class="mt-1 w-full rounded border border-gray-300 bg-white px-3 py-2 dark:border-gray-700 dark:bg-gray-800" value={form().stage} onChange={(event) => setField("stage", event.currentTarget.value as "request" | "response" | "both")}>
                  <option value="request">{t("contentPolicy.request")}</option><option value="response">{t("contentPolicy.response")}</option><option value="both">{t("contentPolicy.both")}</option>
                </select>
              </label>
              <label class="text-sm">{t("contentPolicy.scope")}
                <input maxLength={128} class="mt-1 w-full rounded border border-gray-300 px-3 py-2 dark:border-gray-700 dark:bg-gray-800" value={form().scope ?? ""} onInput={(event) => setField("scope", event.currentTarget.value)} />
              </label>
              <label class="text-sm">{t("contentPolicy.classifier")}
                <select class="mt-1 w-full rounded border border-gray-300 bg-white px-3 py-2 dark:border-gray-700 dark:bg-gray-800" value={form().classifier} onChange={(event) => setField("classifier", event.currentTarget.value as "local" | "external" | "openai")}>
                  <option value="local">{t("contentPolicy.local")}</option><option value="external">{t("contentPolicy.external")}</option><option value="openai">{t("contentPolicy.openai")}</option>
                </select>
              </label>
              <label class="text-sm">{t("contentPolicy.evaluator")}
                <input class="mt-1 w-full rounded border border-gray-300 px-3 py-2 dark:border-gray-700 dark:bg-gray-800" value={form().evaluatorVersion} onInput={(event) => setField("evaluatorVersion", event.currentTarget.value)} />
              </label>
              <label class="text-sm">{t("contentPolicy.status")}
                <select class="mt-1 w-full rounded border border-gray-300 bg-white px-3 py-2 dark:border-gray-700 dark:bg-gray-800" value={form().status} onChange={(event) => setField("status", event.currentTarget.value as "active" | "disabled")}>
                  <option value="active">{t("contentPolicy.active")}</option><option value="disabled">{t("contentPolicy.disabled")}</option>
                </select>
              </label>
            </div>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" checked={form().redactContent} onChange={(event) => setField("redactContent", event.currentTarget.checked)} />{t("contentPolicy.redact")}</label>
            <button type="submit" disabled={busy()} class="rounded bg-indigo-600 px-4 py-2 text-sm text-white hover:bg-indigo-700 disabled:opacity-50">{t("contentPolicy.saveRule")}</button>
          </form>
        </Show>
      </Show>

      <Show when={tab() === "changes"}>
        <div class="flex flex-wrap items-center gap-3">
          <label class="text-sm">{t("contentPolicy.actionFilter")}
            <select class="ml-2 rounded border border-gray-300 bg-white px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" value={changeAction()} onChange={(event) => setChangeAction(event.currentTarget.value)}>
              <option value="">{t("common.status")}</option><option value="created">created</option><option value="updated">updated</option><option value="deleted">deleted</option>
            </select>
          </label>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" checked={pendingOnly()} onChange={(event) => setPendingOnly(event.currentTarget.checked)} />{t("contentPolicy.pendingOnly")}</label>
        </div>
        <div class="overflow-x-auto rounded border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900">
          <table class="w-full min-w-[900px] text-sm"><thead><tr class="border-b border-gray-200 text-left dark:border-gray-800">
            <th class="px-3 py-2">{t("contentPolicy.revision")}</th><th class="px-3 py-2">{t("contentPolicy.action")}</th><th class="px-3 py-2">{t("contentPolicy.rule")}</th><th class="px-3 py-2">{t("contentPolicy.status")}</th><th class="px-3 py-2">{t("contentPolicy.attempts")}</th><th class="px-3 py-2">{t("contentPolicy.error")}</th><th class="px-3 py-2">{t("contentPolicy.created")}</th>
          </tr></thead><tbody><For each={changes()?.items} fallback={<tr><td colSpan={7} class="px-3 py-6 text-gray-500">{t("contentPolicy.noChanges")}</td></tr>}>{(change) => <tr class="border-b border-gray-100 dark:border-gray-800/50">
            <td class="px-3 py-2 font-mono">{change.revision}</td><td class="px-3 py-2">{change.action}</td><td class="px-3 py-2">{change.ruleId ?? "-"}</td><td class="px-3 py-2">{change.propagatedAt ? badge(t("contentPolicy.propagated"), "green") : badge(t("contentPolicy.pending"), "amber")}</td><td class="px-3 py-2">{change.attempts}</td><td class="max-w-sm truncate px-3 py-2 text-red-600" title={change.lastError ?? ""}>{change.lastError || "-"}</td><td class="whitespace-nowrap px-3 py-2">{date(change.createdAt)}</td>
          </tr>}</For></tbody></table>
        </div>
      </Show>

      <Show when={tab() === "alerts"}>
        <div class="flex flex-wrap items-center gap-3">
          <label class="text-sm">{t("contentPolicy.kind")}<input class="ml-2 rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" value={alertKind()} onInput={(event) => setAlertKind(event.currentTarget.value)} /></label>
          <label class="text-sm">{t("contentPolicy.severity")}
            <select class="ml-2 rounded border border-gray-300 bg-white px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" value={alertSeverity()} onChange={(event) => setAlertSeverity(event.currentTarget.value)}><option value="">{t("common.status")}</option><option value="info">info</option><option value="warning">warning</option><option value="critical">critical</option></select>
          </label>
        </div>
        <div class="overflow-x-auto rounded border border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900">
          <table class="w-full min-w-[1000px] text-sm"><thead><tr class="border-b border-gray-200 text-left dark:border-gray-800">
            <th class="px-3 py-2">{t("contentPolicy.kind")}</th><th class="px-3 py-2">{t("contentPolicy.severity")}</th><th class="px-3 py-2">{t("contentPolicy.rule")}</th><th class="px-3 py-2">{t("contentPolicy.user")}</th><th class="px-3 py-2">{t("contentPolicy.stage")}</th><th class="px-3 py-2">{t("contentPolicy.code")}</th><th class="px-3 py-2">{t("contentPolicy.revision")}</th><th class="px-3 py-2">{t("contentPolicy.created")}</th>
          </tr></thead><tbody><For each={alerts()?.items} fallback={<tr><td colSpan={8} class="px-3 py-6 text-gray-500">{t("contentPolicy.noAlerts")}</td></tr>}>{(alert) => <tr class="border-b border-gray-100 dark:border-gray-800/50">
            <td class="px-3 py-2">{alert.kind}</td><td class="px-3 py-2">{badge(alert.severity, alert.severity === "critical" ? "red" : alert.severity === "warning" ? "amber" : "gray")}</td><td class="px-3 py-2">{alert.ruleId ?? "-"}</td><td class="px-3 py-2">{alert.userId ?? "-"}</td><td class="px-3 py-2">{alert.stage}</td><td class="px-3 py-2 font-mono text-xs">{alert.code}</td><td class="px-3 py-2">{alert.policyRevision}</td><td class="whitespace-nowrap px-3 py-2">{date(alert.createdAt)}</td>
          </tr>}</For></tbody></table>
        </div>
      </Show>
    </div>
  );
}
