import { createResource } from "solid-js";
import { get } from "../api/client";
import type { DashboardStats } from "../api/types";
import { useI18n } from "../store/i18n";

export default function Dashboard() {
  const { t } = useI18n();
  const [stats] = createResource(() => get<DashboardStats>("/dashboard"));

  const cards = () => {
    const s = stats();
    if (!s) return [];
    return [
      { label: t("dashboard.accounts"), value: s.totalAccounts },
      { label: t("dashboard.groups"), value: s.totalGroups },
      { label: t("dashboard.users"), value: s.totalUsers },
      { label: t("dashboard.apikeys"), value: s.totalApiKeys },
    ];
  };

  return (
    <div>
      <h1 class="mb-6 text-xl font-bold">{t("dashboard.title")}</h1>
      <div class="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {cards().map((c) => (
          <div class="rounded-lg border border-gray-200 bg-white p-5 dark:border-gray-800 dark:bg-gray-900">
            <p class="text-sm text-gray-500 dark:text-gray-400">{c.label}</p>
            <p class="mt-1 text-2xl font-bold">{c.value}</p>
          </div>
        ))}
      </div>
    </div>
  );
}
