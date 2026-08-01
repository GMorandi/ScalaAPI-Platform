import { createResource, createSignal, For } from "solid-js";
import { A } from "@solidjs/router";
import { get, patch, del } from "../../api/client";
import type { Paged, Account } from "../../api/types";
import { useI18n } from "../../store/i18n";

export default function AccountsList() {
  const { t } = useI18n();
  const [page, setPage] = createSignal(0);
  const [data, { refetch }] = createResource(page, (p) =>
    get<Paged<Account>>(`/accounts?page=${p}&size=20`)
  );

  const toggleStatus = async (acc: Account) => {
    const next = acc.status === "active" ? "disabled" : "active";
    await patch(`/accounts/${acc.id}/status`, { status: next });
    refetch();
  };

  const remove = async (id: number) => {
    await del(`/accounts/${id}`);
    refetch();
  };

  return (
    <div>
      <div class="mb-4 flex items-center justify-between">
        <h1 class="text-xl font-bold">{t("nav.accounts")}</h1>
        <A href="/accounts/new" class="rounded bg-indigo-600 px-3 py-1.5 text-sm text-white hover:bg-indigo-700">
          {t("common.create")}
        </A>
      </div>
      <table class="w-full text-sm">
        <thead>
          <tr class="border-b border-gray-200 text-left dark:border-gray-800">
            <th class="py-2 px-2">ID</th>
            <th class="py-2 px-2">Name</th>
            <th class="py-2 px-2">Platform</th>
            <th class="py-2 px-2">Priority</th>
            <th class="py-2 px-2">Load</th>
            <th class="py-2 px-2">{t("common.status")}</th>
            <th class="py-2 px-2">{t("common.actions")}</th>
          </tr>
        </thead>
        <tbody>
          <For each={data()?.items}>
            {(acc) => (
              <tr class="border-b border-gray-100 dark:border-gray-800/50">
                <td class="py-2 px-2">{acc.id}</td>
                <td class="py-2 px-2">{acc.name}</td>
                <td class="py-2 px-2">{acc.platform}</td>
                <td class="py-2 px-2">{acc.priority}</td>
                <td class="py-2 px-2">{acc.currentLoad}/{acc.concurrency}</td>
                <td class="py-2 px-2">
                  <span class={`rounded px-1.5 py-0.5 text-xs ${acc.status === "active" ? "bg-green-100 text-green-700 dark:bg-green-900 dark:text-green-300" : "bg-red-100 text-red-700 dark:bg-red-900 dark:text-red-300"}`}>
                    {acc.status}
                  </span>
                </td>
                <td class="py-2 px-2 space-x-2">
                  <A href={`/accounts/${acc.id}`} class="text-indigo-600 hover:underline">{t("common.edit")}</A>
                  <button onClick={() => toggleStatus(acc)} class="text-amber-600 hover:underline">
                    {acc.status === "active" ? t("common.disabled") : t("common.active")}
                  </button>
                  <button onClick={() => remove(acc.id)} class="text-red-600 hover:underline">{t("common.delete")}</button>
                </td>
              </tr>
            )}
          </For>
        </tbody>
      </table>
    </div>
  );
}
