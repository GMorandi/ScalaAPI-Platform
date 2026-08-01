import { createSignal } from "solid-js";
import { useNavigate } from "@solidjs/router";
import { useAuth } from "../store/auth";
import { useI18n } from "../store/i18n";

export default function Login() {
  const { login } = useAuth();
  const { t } = useI18n();
  const navigate = useNavigate();
  const [username, setUsername] = createSignal("");
  const [password, setPassword] = createSignal("");
  const [error, setError] = createSignal("");
  const [loading, setLoading] = createSignal(false);

  const submit = async (e: Event) => {
    e.preventDefault();
    setLoading(true);
    setError("");
    try {
      await login(username(), password());
      navigate("/");
    } catch {
      setError("Invalid credentials");
    } finally {
      setLoading(false);
    }
  };

  return (
    <div class="flex min-h-screen items-center justify-center">
      <form onSubmit={submit} class="w-80 space-y-4 rounded-lg border border-gray-200 bg-white p-6 shadow dark:border-gray-800 dark:bg-gray-900">
        <h1 class="text-center text-lg font-bold">{t("login.title")}</h1>
        {error() && <p class="text-sm text-red-500">{error()}</p>}
        <input
          type="text"
          placeholder={t("login.username")}
          value={username()}
          onInput={(e) => setUsername(e.currentTarget.value)}
          class="w-full rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800"
        />
        <input
          type="password"
          placeholder={t("login.password")}
          value={password()}
          onInput={(e) => setPassword(e.currentTarget.value)}
          class="w-full rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800"
        />
        <button
          type="submit"
          disabled={loading()}
          class="w-full rounded bg-indigo-600 py-2 text-sm text-white hover:bg-indigo-700 disabled:opacity-50"
        >
          {loading() ? "..." : t("login.submit")}
        </button>
      </form>
    </div>
  );
}
