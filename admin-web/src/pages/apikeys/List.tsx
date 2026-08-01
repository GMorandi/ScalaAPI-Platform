import { createResource, createSignal, For } from "solid-js";
import { A } from "@solidjs/router";
import { get, del } from "../../api/client";
import type { Paged, ApiKeyEntry } from "../../api/types";
import { useI18n } from "../../store/i18n";

export default function ApiKeysList() {
  const { t } = useI18n();
  const [page, setPage] = createSignal(0);
  const [data, { refetch }] = createResource(page, (p) =>
    get<Paged<ApiKeyEntry>>(`/apikeys?page=${p}&size=20`)
  );

  const revoke = async (hash: string) => {
    await del(`/apikeys/${hash}`);
    refetch();
  };

  return (
    <div>
      <div class="mb-4 flex items-center justify-between">
        <h1 class="text-xl font-bold">{t("nav.apikeys")}</h1>
        <A href="/apikeys/new" class="rounded bg-indigo-600 px-3 py-1.5 text-sm text-white hover:bg-indigo-700">
          {t("common.create")}
        </A>
      </div>
      <table class="w-full text-sm">
        <thead>
          <tr class="border-b border-gray-200 text-left dark:border-gray-800">
            <th class="py-2 px-2">Hash</th>
            <th class="py-2 px-2">Version</th>
            <th class="py-2 px-2">{t("common.actions")}</th>
          </tr>
        </thead>
        <tbody>
          <For each={data()?.items}>
            {(k) => (
              <tr class="border-b border-gray-100 dark:border-gray-800/50">
                <td class="py-2 px-2 font-mono text-xs">{k.hash.slice(0, 16)}...</td>
                <td class="py-2 px-2">{k.version}</td>
                <td class="py-2 px-2">
                  <button onClick={() => revoke(k.hash)} class="text-red-600 hover:underline">Revoke</button>
                </td>
              </tr>
            )}
          </For>
        </tbody>
      </table>
    </div>
  );
}
