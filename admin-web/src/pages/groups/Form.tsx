import { createSignal } from "solid-js";
import { useNavigate, useParams } from "@solidjs/router";
import { post, put } from "../../api/client";
import { useI18n } from "../../store/i18n";

export default function GroupForm() {
  const { t } = useI18n();
  const params = useParams();
  const navigate = useNavigate();
  const isNew = () => params.id === undefined;

  const [form, setForm] = createSignal({
    platform: "anthropic", rateMultiplier: 1.0, isExclusive: false,
    dailyLimitUsd: null as number | null, claudeCodeOnly: false,
    fallbackGroupId: null as number | null, modelRoutingEnabled: false,
    modelRouting: {} as Record<string, number[]>, memberAccountIds: [] as number[],
    rpmLimit: 0, peakMultiplier: null as number | null,
    peakStartHour: null as number | null, peakEndHour: null as number | null,
  });

  const set = (key: string, value: any) => setForm((f) => ({ ...f, [key]: value }));

  const submit = async (e: Event) => {
    e.preventDefault();
    if (isNew()) await post("/groups", form());
    else await put(`/groups/${params.id}`, form());
    navigate("/groups");
  };

  return (
    <div class="max-w-lg">
      <h1 class="mb-4 text-xl font-bold">
        {isNew() ? t("common.create") : t("common.edit")} - {t("nav.groups")}
      </h1>
      <form onSubmit={submit} class="space-y-3">
        <input class="w-full rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="Platform" value={form().platform} onInput={(e) => set("platform", e.currentTarget.value)} />
        <div class="flex gap-3">
          <input type="number" step="0.1" class="w-1/2 rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="Rate Multiplier" value={form().rateMultiplier} onInput={(e) => set("rateMultiplier", +e.currentTarget.value)} />
          <input type="number" class="w-1/2 rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="RPM Limit (0=∞)" value={form().rpmLimit} onInput={(e) => set("rpmLimit", +e.currentTarget.value)} />
        </div>
        <div class="flex gap-4">
          <button type="submit" class="rounded bg-indigo-600 px-4 py-2 text-sm text-white hover:bg-indigo-700">{t("common.save")}</button>
          <button type="button" onClick={() => navigate("/groups")} class="rounded border border-gray-300 px-4 py-2 text-sm dark:border-gray-700">{t("common.cancel")}</button>
        </div>
      </form>
    </div>
  );
}
