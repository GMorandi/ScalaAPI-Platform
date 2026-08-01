import { createResource, createSignal, For } from "solid-js";
import { A } from "@solidjs/router";
import { get, patch, del } from "../../api/client";
import type { Paged, User } from "../../api/types";
import { useI18n } from "../../store/i18n";

export default function UsersList() {
  const { t } = useI18n();
  const [page, setPage] = createSignal(0);
  const [data, { refetch }] = createResource(page, (p) =>
    get<Paged<User>>(`/users?page=${p}&size=20`)
  );

  const toggleStatus = async (u: User) => {
    const next = u.status === "active" ? "disabled" : "active";
    await patch(`/users/${u.id}/status`, { status: next });
    refetch();
  };

  const remove = async (id: number) => {
    await del(`/users/${id}`);
    refetch();
  };

  return (
    <div>
      <div class="mb-4 flex items-center justify-between">
        <h1 class="text-xl font-bold">{t("nav.users")}</h1>
        <A href="/users/new" class="rounded bg-indigo-600 px-3 py-1.5 text-sm text-white hover:bg-indigo-700">
          {t("common.create")}
        </A>
      </div>
      <table class="w-full text-sm">
        <thead>
          <tr class="border-b border-gray-200 text-left dark:border-gray-800">
            <th class="py-2 px-2">ID</th>
            <th class="py-2 px-2">Role</th>
            <th class="py-2 px-2">Balance</th>
            <th class="py-2 px-2">Concurrency</th>
            <th class="py-2 px-2">{t("common.status")}</th>
            <th class="py-2 px-2">{t("common.actions")}</th>
          </tr>
        </thead>
        <tbody>
          <For each={data()?.items}>
            {(u) => (
              <tr class="border-b border-gray-100 dark:border-gray-800/50">
                <td class="py-2 px-2">{u.id}</td>
                <td class="py-2 px-2">{u.role}</td>
                <td class="py-2 px-2">${u.balance.toFixed(2)}</td>
                <td class="py-2 px-2">{u.concurrency}</td>
                <td class="py-2 px-2">
                  <span class={`rounded px-1.5 py-0.5 text-xs ${u.status === "active" ? "bg-green-100 text-green-700 dark:bg-green-900 dark:text-green-300" : "bg-red-100 text-red-700 dark:bg-red-900 dark:text-red-300"}`}>
                    {u.status}
                  </span>
                </td>
                <td class="py-2 px-2 space-x-2">
                  <A href={`/users/${u.id}`} class="text-indigo-600 hover:underline">{t("common.edit")}</A>
                  <button onClick={() => toggleStatus(u)} class="text-amber-600 hover:underline">
                    {u.status === "active" ? t("common.disabled") : t("common.active")}
                  </button>
                  <button onClick={() => remove(u.id)} class="text-red-600 hover:underline">{t("common.delete")}</button>
                </td>
              </tr>
            )}
          </For>
        </tbody>
      </table>
    </div>
  );
}
