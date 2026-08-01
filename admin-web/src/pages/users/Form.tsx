import { createSignal } from "solid-js";
import { useNavigate, useParams } from "@solidjs/router";
import { post, put } from "../../api/client";
import { useI18n } from "../../store/i18n";

export default function UserForm() {
  const { t } = useI18n();
  const params = useParams();
  const navigate = useNavigate();
  const isNew = () => params.id === undefined;

  const [form, setForm] = createSignal({
    role: "user", balance: 0, concurrency: 1, rpmLimit: 0, allowedGroups: [] as number[],
  });

  const set = (key: string, value: any) => setForm((f) => ({ ...f, [key]: value }));

  const submit = async (e: Event) => {
    e.preventDefault();
    if (isNew()) await post("/users", form());
    else await put(`/users/${params.id}`, form());
    navigate("/users");
  };

  return (
    <div class="max-w-lg">
      <h1 class="mb-4 text-xl font-bold">
        {isNew() ? t("common.create") : t("common.edit")} - {t("nav.users")}
      </h1>
      <form onSubmit={submit} class="space-y-3">
        <select class="w-full rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" value={form().role} onChange={(e) => set("role", e.currentTarget.value)}>
          <option value="user">user</option>
          <option value="admin">admin</option>
        </select>
        <div class="flex gap-3">
          <input type="number" step="0.01" class="w-1/2 rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="Balance" value={form().balance} onInput={(e) => set("balance", +e.currentTarget.value)} />
          <input type="number" class="w-1/2 rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="Concurrency" value={form().concurrency} onInput={(e) => set("concurrency", +e.currentTarget.value)} />
        </div>
        <input type="number" class="w-full rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="RPM Limit (0=∞)" value={form().rpmLimit} onInput={(e) => set("rpmLimit", +e.currentTarget.value)} />
        <div class="flex gap-4">
          <button type="submit" class="rounded bg-indigo-600 px-4 py-2 text-sm text-white hover:bg-indigo-700">{t("common.save")}</button>
          <button type="button" onClick={() => navigate("/users")} class="rounded border border-gray-300 px-4 py-2 text-sm dark:border-gray-700">{t("common.cancel")}</button>
        </div>
      </form>
    </div>
  );
}
