import { useAuth } from "../store/auth";
import { useTheme } from "../store/theme";
import { useI18n } from "../store/i18n";
import { useNavigate } from "@solidjs/router";

export default function Topbar() {
  const { logout } = useAuth();
  const { dark, toggle } = useTheme();
  const { locale, setLocale, t } = useI18n();
  const navigate = useNavigate();

  const handleLogout = () => {
    logout();
    navigate("/login");
  };

  return (
    <header class="flex h-14 items-center justify-end gap-3 border-b border-gray-200 px-4 dark:border-gray-800">
      <button
        onClick={() => setLocale(locale() === "zh" ? "en" : "zh")}
        class="rounded px-2 py-1 text-xs border border-gray-300 dark:border-gray-700"
      >
        {locale() === "zh" ? "EN" : "中文"}
      </button>
      <button
        onClick={toggle}
        class="rounded px-2 py-1 text-xs border border-gray-300 dark:border-gray-700"
      >
        {dark() ? "☀️" : "🌙"}
      </button>
      <button
        onClick={handleLogout}
        class="rounded px-2 py-1 text-xs text-red-600 border border-red-300 dark:border-red-800"
      >
        {t("nav.logout")}
      </button>
    </header>
  );
}
