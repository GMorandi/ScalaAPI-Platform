import { createResource, createSignal, For, Show } from "solid-js";
import { A } from "@solidjs/router";
import { api, get, patch, del } from "../../api/client";
import type { Paged, User } from "../../api/types";
import { useI18n } from "../../store/i18n";

export default function UsersList() {
  const { t } = useI18n();
  const [page, setPage] = createSignal(0);
  const [adjusting, setAdjusting] = createSignal<User>();
  const [delta, setDelta] = createSignal("");
  const [reason, setReason] = createSignal("");
  const [adjustmentError, setAdjustmentError] = createSignal("");
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

  const openAdjustment = (user: User) => {
    setAdjusting(user);
    setDelta("");
    setReason("");
    setAdjustmentError("");
  };

  const submitAdjustment = async (event: Event) => {
    event.preventDefault();
    const user = adjusting();
    const amount = Number(delta());
    if (!user || !Number.isFinite(amount) || amount === 0 || reason().trim().length < 3)
      return;
    setAdjustmentError("");
    try {
      await api(`/users/${user.id}/balance`, {
        method: "POST",
        headers: { "Idempotency-Key": crypto.randomUUID() },
        body: JSON.stringify({ delta: amount, reason: reason().trim() }),
      });
      setAdjusting(undefined);
      refetch();
    } catch (error) {
      setAdjustmentError(error instanceof Error ? error.message : String(error));
    }
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
                  <button onClick={() => openAdjustment(u)} class="text-emerald-700 hover:underline dark:text-emerald-400">
                    {t("users.adjustBalance")}
                  </button>
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
      <Show when={adjusting()}>
        {(user) => (
          <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
            <form onSubmit={submitAdjustment} class="w-full max-w-md rounded bg-white p-5 shadow-xl dark:bg-gray-900">
              <h2 class="mb-4 text-base font-semibold">{t("users.adjustBalance")} #{user().id}</h2>
              <div class="space-y-3">
                <input
                  type="number"
                  step="0.00000001"
                  required
                  class="w-full rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800"
                  placeholder={t("users.delta")}
                  value={delta()}
                  onInput={(event) => setDelta(event.currentTarget.value)}
                />
                <input
                  type="text"
                  required
                  minLength={3}
                  maxLength={500}
                  class="w-full rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800"
                  placeholder={t("users.reason")}
                  value={reason()}
                  onInput={(event) => setReason(event.currentTarget.value)}
                />
                <Show when={adjustmentError()}>
                  <p class="text-sm text-red-600">{adjustmentError()}</p>
                </Show>
              </div>
              <div class="mt-5 flex justify-end gap-2">
                <button type="button" onClick={() => setAdjusting(undefined)} class="rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700">
                  {t("common.cancel")}
                </button>
                <button type="submit" class="rounded bg-emerald-700 px-3 py-2 text-sm text-white hover:bg-emerald-800">
                  {t("common.apply")}
                </button>
              </div>
            </form>
          </div>
        )}
      </Show>
    </div>
  );
}
