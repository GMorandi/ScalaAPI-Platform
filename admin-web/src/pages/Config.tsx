import { createResource, createSignal, For } from "solid-js";
import { get, put } from "../api/client";
import { useI18n } from "../store/i18n";

export default function Config() {
  const { t } = useI18n();
  const [data, { refetch }] = createResource(() =>
    get<Record<string, string>>("/config")
  );
  const [key, setKey] = createSignal("");
  const [value, setValue] = createSignal("");

  const addEntry = async (e: Event) => {
    e.preventDefault();
    if (!key()) return;
    await put("/config", { key: key(), value: value() });
    setKey("");
    setValue("");
    refetch();
  };

  return (
    <div class="max-w-2xl">
      <h1 class="mb-4 text-xl font-bold">{t("nav.config")}</h1>
      <form onSubmit={addEntry} class="mb-6 flex gap-2">
        <input class="flex-1 rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="Key" value={key()} onInput={(e) => setKey(e.currentTarget.value)} />
        <input class="flex-1 rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800" placeholder="Value" value={value()} onInput={(e) => setValue(e.currentTarget.value)} />
        <button type="submit" class="rounded bg-indigo-600 px-4 py-2 text-sm text-white hover:bg-indigo-700">{t("common.save")}</button>
      </form>
      <table class="w-full text-sm">
        <thead>
          <tr class="border-b border-gray-200 text-left dark:border-gray-800">
            <th class="py-2 px-2">Key</th>
            <th class="py-2 px-2">Value</th>
          </tr>
        </thead>
        <tbody>
          <For each={Object.entries(data() ?? {})}>
            {([k, v]) => (
              <tr class="border-b border-gray-100 dark:border-gray-800/50">
                <td class="py-2 px-2 font-mono text-xs">{k}</td>
                <td class="py-2 px-2">{v}</td>
              </tr>
            )}
          </For>
        </tbody>
      </table>
    </div>
  );
}
