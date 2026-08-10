import { A } from "@solidjs/router";
import { useI18n } from "../store/i18n";

const links = [
  { href: "/", key: "nav.dashboard" },
  { href: "/accounts", key: "nav.accounts" },
  { href: "/groups", key: "nav.groups" },
  { href: "/users", key: "nav.users" },
  { href: "/apikeys", key: "nav.apikeys" },
  { href: "/config", key: "nav.config" },
  { href: "/reconciliation", key: "nav.reconciliation" },
  { href: "/content-policy", key: "nav.contentPolicy" },
  { href: "/operations", key: "nav.operations" },
];

export default function Sidebar() {
  const { t } = useI18n();

  return (
    <aside class="w-56 border-r border-gray-200 bg-white dark:border-gray-800 dark:bg-gray-900">
      <div class="flex h-14 items-center px-4 font-bold text-lg border-b border-gray-200 dark:border-gray-800">
        ScalaAPI
      </div>
      <nav class="p-2 space-y-1">
        {links.map((l) => (
          <A
            href={l.href}
            end={l.href === "/"}
            class="block rounded-md px-3 py-2 text-sm text-gray-700 hover:bg-gray-100 dark:text-gray-300 dark:hover:bg-gray-800"
            activeClass="bg-indigo-50 text-indigo-700 dark:bg-indigo-950 dark:text-indigo-300"
          >
            {t(l.key)}
          </A>
        ))}
      </nav>
    </aside>
  );
}
