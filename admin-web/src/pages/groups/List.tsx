import { createResource, createSignal, For } from "solid-js";
import { A } from "@solidjs/router";
import { get, patch, del } from "../../api/client";
import type { Paged, Group } from "../../api/types";
import { useI18n } from "../../store/i18n";

export default function GroupsList() {
  const { t } = useI18n();
  const [page, setPage] = createSignal(0);
  const [data, { refetch }] = createResource(page, (p) =>
    get<Paged<Group>>(`/groups?page=${p}&size=20`)
  );

  const remove = async (id: number) => {
    await del(`/groups/${id}`);
    refetch();
  };

  return (
    <div>
      <div class="mb-4 flex items-center justify-between">
        <h1 class="text-xl font-bold">{t("nav.groups")}</h1>
        <A href="/groups/new" class="rounded bg-indigo-600 px-3 py-1.5 text-sm text-white hover:bg-indigo-700">
          {t("common.create")}
        </A>
      </div>
      <table class="w-full text-sm">
        <thead>
          <tr class="border-b border-gray-200 text-left dark:border-gray-800">
            <th class="py-2 px-2">ID</th>
            <th class="py-2 px-2">Platform</th>
            <th class="py-2 px-2">Rate ×</th>
            <th class="py-2 px-2">RPM</th>
            <th class="py-2 px-2">Routing</th>
            <th class="py-2 px-2">{t("common.actions")}</th>
          </tr>
        </thead>
        <tbody>
          <For each={data()?.items}>
            {(g) => (
              <tr class="border-b border-gray-100 dark:border-gray-800/50">
                <td class="py-2 px-2">{g.id}</td>
                <td class="py-2 px-2">{g.platform}</td>
                <td class="py-2 px-2">{g.rateMultiplier}</td>
                <td class="py-2 px-2">{g.rpmLimit || "∞"}</td>
                <td class="py-2 px-2">{g.modelRoutingEnabled ? "✓" : "—"}</td>
                <td class="py-2 px-2 space-x-2">
                  <A href={`/groups/${g.id}`} class="text-indigo-600 hover:underline">{t("common.edit")}</A>
                  <button onClick={() => remove(g.id)} class="text-red-600 hover:underline">{t("common.delete")}</button>
                </td>
              </tr>
            )}
          </For>
        </tbody>
      </table>
    </div>
  );
}
