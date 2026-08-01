import { createSignal, Show } from "solid-js";
import { useNavigate } from "@solidjs/router";
import { post } from "../../api/client";
import { useI18n } from "../../store/i18n";

export default function ApiKeyForm() {
  const { t } = useI18n();
  const navigate = useNavigate();

  const [form, setForm] = createSignal({
    userId: 0, groupId: 0, quota: 0, expiresAt: null as number | null,
    ipWhitelist: [] as string[], ipBlacklist: [] as string[],
    rateLimit5h: 0, rateLimit1d: 0, rateLimit7d: 0,
  });
  const [createdKey, setCreatedKey] = createSignal("");

  const set = (key: string, value: any) => setForm((f) => ({ ...f, [key]: value }));

  const submit = async (e: Event) => {
    e.preventDefault();
    const res = await post<{ key: string }>("/apikeys", form());
    setCreatedKey(res.key);
  };

  return (
    <div class="max-w-lg">
      <h1 class="mb-4 text-xl font-bold">{t("common.create")} - {t("nav.apikeys")}</h1>
      <Show when={createdKey()} keyed>
        {(key) => (
          <div class="mb-4 rounded border border-green-300 bg-green-50 p-4 dark:border-green-800 dark:bg-green-950">
            <p class="text-sm font-medium text-green-800 dark:text-green-200">API Key created (shown once):</p>
            <code class="mt-1 block break-all text-sm font-mono">{key}</code>
          </div>
        )}
      </Show>
      <form onSubmit={submit} class="space-y-3">
        <div class="flex gap-3">
          <input type="number" class="w-1/2 rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="User ID" value={form().userId} onInput={(e) => set("userId", +e.currentTarget.value)} />
          <input type="number" class="w-1/2 rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="Group ID" value={form().groupId} onInput={(e) => set("groupId", +e.currentTarget.value)} />
        </div>
        <input type="number" step="0.01" class="w-full rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="Quota (USD)" value={form().quota} onInput={(e) => set("quota", +e.currentTarget.value)} />
        <div class="flex gap-4">
          <button type="submit" class="rounded bg-indigo-600 px-4 py-2 text-sm text-white hover:bg-indigo-700">{t("common.save")}</button>
          <button type="button" onClick={() => navigate("/apikeys")} class="rounded border border-gray-300 px-4 py-2 text-sm dark:border-gray-700">{t("common.cancel")}</button>
        </div>
      </form>
    </div>
  );
}
