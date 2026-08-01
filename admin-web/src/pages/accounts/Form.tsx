import { createSignal } from "solid-js";
import { useNavigate, useParams } from "@solidjs/router";
import { post, put } from "../../api/client";
import { useI18n } from "../../store/i18n";

export default function AccountForm() {
  const { t } = useI18n();
  const params = useParams();
  const navigate = useNavigate();
  const isNew = () => params.id === undefined;

  const [form, setForm] = createSignal({
    name: "", platform: "anthropic", type: "api_key", baseUrl: "",
    priority: 0, concurrency: 1, loadFactor: 1, rateMultiplier: 1.0,
    schedulable: true, credentials: {} as Record<string, string>,
    modelMapping: {} as Record<string, string>, supportedModels: [] as string[],
    proxyUrl: null as string | null, tlsFingerprint: false,
  });

  const set = (key: string, value: any) => setForm((f) => ({ ...f, [key]: value }));

  const submit = async (e: Event) => {
    e.preventDefault();
    if (isNew()) {
      await post("/accounts", form());
    } else {
      await put(`/accounts/${params.id}`, form());
    }
    navigate("/accounts");
  };

  return (
    <div class="max-w-lg">
      <h1 class="mb-4 text-xl font-bold">
        {isNew() ? t("common.create") : t("common.edit")} - {t("nav.accounts")}
      </h1>
      <form onSubmit={submit} class="space-y-3">
        <input class="w-full rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="Name" value={form().name} onInput={(e) => set("name", e.currentTarget.value)} />
        <input class="w-full rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="Platform" value={form().platform} onInput={(e) => set("platform", e.currentTarget.value)} />
        <input class="w-full rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="Base URL" value={form().baseUrl} onInput={(e) => set("baseUrl", e.currentTarget.value)} />
        <div class="flex gap-3">
          <input type="number" class="w-1/3 rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="Priority" value={form().priority} onInput={(e) => set("priority", +e.currentTarget.value)} />
          <input type="number" class="w-1/3 rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="Concurrency" value={form().concurrency} onInput={(e) => set("concurrency", +e.currentTarget.value)} />
          <input type="number" step="0.1" class="w-1/3 rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="Rate Multiplier" value={form().rateMultiplier} onInput={(e) => set("rateMultiplier", +e.currentTarget.value)} />
        </div>
        <div class="flex gap-4">
          <button type="submit" class="rounded bg-indigo-600 px-4 py-2 text-sm text-white hover:bg-indigo-700">{t("common.save")}</button>
          <button type="button" onClick={() => navigate("/accounts")} class="rounded border border-gray-300 px-4 py-2 text-sm dark:border-gray-700">{t("common.cancel")}</button>
        </div>
      </form>
    </div>
  );
}
