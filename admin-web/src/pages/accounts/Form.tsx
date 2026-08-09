import { createEffect, createResource, createSignal, Show } from "solid-js";
import { useNavigate, useParams } from "@solidjs/router";
import { get, post, put } from "../../api/client";
import { useI18n } from "../../store/i18n";

type OAuthMetadata = {
  tokenEndpoint: string; clientId: string; expiresAtUnixSeconds: number;
  headerName: string; headerScheme: string; scope?: string;
  version: number; lastRefreshedAtUnixSeconds?: number; lastRefreshError?: string;
};
type AccountDetails = {
  id: number; name: string; platform: string; type: string; baseUrl: string;
  priority: number; concurrency: number; loadFactor: number; rateMultiplier: number;
  schedulable: boolean; hasStaticCredentials: boolean;
  modelMapping: Record<string, string>; supportedModels: string[];
  proxyUrl?: string; tlsFingerprint: boolean; oauth?: OAuthMetadata;
};

const field = "w-full rounded border border-gray-300 px-3 py-2 text-sm dark:border-gray-700 dark:bg-gray-800";
const futureExpiry = () => toLocalDateTime(Math.floor(Date.now() / 1000) + 3600);
function toLocalDateTime(epoch: number) {
  const date = new Date(epoch * 1000);
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000)
    .toISOString().slice(0, 16);
}

export default function AccountForm() {
  const { t } = useI18n(); const params = useParams(); const navigate = useNavigate();
  const isNew = () => params.id === undefined; const [loaded, setLoaded] = createSignal(false);
  const [details] = createResource(() => params.id, id => get<AccountDetails>(`/accounts/${id}`));
  const [error, setError] = createSignal("");
  const [form, setForm] = createSignal({
    name: "", platform: "openai", type: "api_key", baseUrl: "https://api.openai.com",
    priority: 50, concurrency: 1, loadFactor: 1, rateMultiplier: 1,
    schedulable: true, supportedModels: "", proxyUrl: "", tlsFingerprint: false,
    staticHeaderName: "Authorization", staticHeaderValue: "",
    tokenEndpoint: "", clientId: "", clientSecret: "", refreshToken: "",
    accessToken: "", oauthExpiresAt: futureExpiry(), oauthHeaderName: "Authorization",
    oauthHeaderScheme: "Bearer", oauthScope: "",
    modelMapping: {} as Record<string, string>,
  });
  const set = (key: string, value: unknown) => setForm(current => ({ ...current, [key]: value }));

  createEffect(() => {
    const value = details(); if (!value || loaded()) return;
    setForm(current => ({ ...current,
      name: value.name, platform: value.platform, type: value.type,
      baseUrl: value.baseUrl, priority: value.priority, concurrency: value.concurrency,
      loadFactor: value.loadFactor, rateMultiplier: value.rateMultiplier,
      schedulable: value.schedulable, supportedModels: value.supportedModels.join(", "),
      proxyUrl: value.proxyUrl ?? "", tlsFingerprint: value.tlsFingerprint,
      tokenEndpoint: value.oauth?.tokenEndpoint ?? "",
      clientId: value.oauth?.clientId ?? "",
      oauthExpiresAt: value.oauth ? toLocalDateTime(value.oauth.expiresAtUnixSeconds) : futureExpiry(),
      oauthHeaderName: value.oauth?.headerName ?? "Authorization",
      oauthHeaderScheme: value.oauth?.headerScheme ?? "Bearer",
      oauthScope: value.oauth?.scope ?? "", modelMapping: value.modelMapping,
    })); setLoaded(true);
  });

  const submit = async (event: Event) => {
    event.preventDefault(); setError(""); const value = form();
    const credentials = value.type === "api_key" && value.staticHeaderValue
      ? { [value.staticHeaderName]: value.staticHeaderValue } : {};
    const replaceOAuth = value.type === "oauth"
      && [value.accessToken, value.refreshToken].some(Boolean);
    const oauth = value.type === "oauth" && (isNew() || replaceOAuth) ? {
      tokenEndpoint: value.tokenEndpoint, clientId: value.clientId,
      clientSecret: value.clientSecret, refreshToken: value.refreshToken,
      accessToken: value.accessToken,
      expiresAtUnixSeconds: Math.floor(new Date(value.oauthExpiresAt).getTime() / 1000),
      headerName: value.oauthHeaderName, headerScheme: value.oauthHeaderScheme,
      scope: value.oauthScope || null,
    } : null;
    const payload = {
      name: value.name, platform: value.platform, type: value.type, baseUrl: value.baseUrl,
      priority: value.priority, concurrency: value.concurrency, loadFactor: value.loadFactor,
      rateMultiplier: value.rateMultiplier, schedulable: value.schedulable, credentials,
      modelMapping: value.modelMapping,
      supportedModels: value.supportedModels.split(",").map(item => item.trim()).filter(Boolean),
      proxyUrl: value.proxyUrl || null, tlsFingerprint: value.tlsFingerprint, oauth,
    };
    try {
      if (isNew()) await post("/accounts", payload); else await put(`/accounts/${params.id}`, payload);
      navigate("/accounts");
    } catch (err) { setError(err instanceof Error ? err.message : "Unable to save account"); }
  };

  return <div class="max-w-3xl"><h1 class="mb-4 text-xl font-bold">{isNew() ? t("common.create") : t("common.edit")} - {t("nav.accounts")}</h1>{error() && <p class="mb-4 border-l-2 border-red-600 bg-red-50 px-3 py-2 text-sm text-red-700">{error()}</p>}<form onSubmit={submit} class="space-y-6">
    <section class="grid gap-3 md:grid-cols-2"><label class="text-sm">Name<input required class={field} value={form().name} onInput={e => set("name", e.currentTarget.value)} /></label><label class="text-sm">Platform<select class={field} value={form().platform} onChange={e => set("platform", e.currentTarget.value)}><option value="openai">OpenAI</option><option value="anthropic">Anthropic</option><option value="gemini">Gemini</option></select></label><label class="text-sm md:col-span-2">Base URL<input required type="url" class={field} value={form().baseUrl} onInput={e => set("baseUrl", e.currentTarget.value)} /></label><label class="text-sm">Credential mode<select class={field} value={form().type} onChange={e => set("type", e.currentTarget.value)}><option value="api_key">Static headers</option><option value="oauth">OAuth refresh</option></select></label><label class="text-sm">Supported models<input class={field} placeholder="gpt-4o, gpt-4.1" value={form().supportedModels} onInput={e => set("supportedModels", e.currentTarget.value)} /></label></section>
    <Show when={form().type === "api_key"}><section class="grid gap-3 border-t border-gray-200 pt-5 dark:border-gray-800 md:grid-cols-2"><h2 class="font-semibold md:col-span-2">Static credential</h2><label class="text-sm">Header name<input required={isNew()} class={field} value={form().staticHeaderName} onInput={e => set("staticHeaderName", e.currentTarget.value)} /></label><label class="text-sm">Header value<input required={isNew()} type="password" autocomplete="new-password" class={field} value={form().staticHeaderValue} onInput={e => set("staticHeaderValue", e.currentTarget.value)} /></label><Show when={!isNew() && details()?.hasStaticCredentials}><p class="text-xs text-gray-500 md:col-span-2">Existing encrypted headers are retained when no replacement value is entered.</p></Show></section></Show>
    <Show when={form().type === "oauth"}><section class="grid gap-3 border-t border-gray-200 pt-5 dark:border-gray-800 md:grid-cols-2"><div class="md:col-span-2"><h2 class="font-semibold">OAuth credential</h2><Show when={details()?.oauth}><p class="mt-1 text-xs text-gray-500">Version {details()?.oauth?.version} · expires {new Date((details()?.oauth?.expiresAtUnixSeconds ?? 0) * 1000).toLocaleString()}</p></Show></div><label class="text-sm md:col-span-2">Token endpoint<input required={isNew()} type="url" class={field} value={form().tokenEndpoint} onInput={e => set("tokenEndpoint", e.currentTarget.value)} /></label><label class="text-sm">Client ID<input required={isNew()} class={field} value={form().clientId} onInput={e => set("clientId", e.currentTarget.value)} /></label><label class="text-sm">Client secret<input type="password" autocomplete="new-password" class={field} value={form().clientSecret} onInput={e => set("clientSecret", e.currentTarget.value)} /></label><label class="text-sm">Refresh token<input required={isNew()} type="password" autocomplete="new-password" class={field} value={form().refreshToken} onInput={e => set("refreshToken", e.currentTarget.value)} /></label><label class="text-sm">Access token<input required={isNew()} type="password" autocomplete="new-password" class={field} value={form().accessToken} onInput={e => set("accessToken", e.currentTarget.value)} /></label><label class="text-sm">Expires at<input required={isNew()} type="datetime-local" class={field} value={form().oauthExpiresAt} onInput={e => set("oauthExpiresAt", e.currentTarget.value)} /></label><label class="text-sm">Scope<input class={field} value={form().oauthScope} onInput={e => set("oauthScope", e.currentTarget.value)} /></label><label class="text-sm">Header name<input class={field} value={form().oauthHeaderName} onInput={e => set("oauthHeaderName", e.currentTarget.value)} /></label><label class="text-sm">Header scheme<input class={field} value={form().oauthHeaderScheme} onInput={e => set("oauthHeaderScheme", e.currentTarget.value)} /></label><Show when={!isNew() && details()?.oauth}><p class="text-xs text-gray-500 md:col-span-2">Stored tokens remain encrypted and unchanged unless replacement access and refresh tokens are entered.</p></Show><Show when={details()?.oauth?.lastRefreshError}><p class="text-sm text-red-600 md:col-span-2">{details()?.oauth?.lastRefreshError}</p></Show></section></Show>
    <section class="grid gap-3 border-t border-gray-200 pt-5 dark:border-gray-800 md:grid-cols-4"><label class="text-sm">Priority<input type="number" class={field} value={form().priority} onInput={e => set("priority", +e.currentTarget.value)} /></label><label class="text-sm">Concurrency<input min="1" type="number" class={field} value={form().concurrency} onInput={e => set("concurrency", +e.currentTarget.value)} /></label><label class="text-sm">Load factor<input min="1" type="number" class={field} value={form().loadFactor} onInput={e => set("loadFactor", +e.currentTarget.value)} /></label><label class="text-sm">Rate multiplier<input min="0" step="0.01" type="number" class={field} value={form().rateMultiplier} onInput={e => set("rateMultiplier", +e.currentTarget.value)} /></label><label class="text-sm md:col-span-2">Proxy URL<input type="url" class={field} value={form().proxyUrl} onInput={e => set("proxyUrl", e.currentTarget.value)} /></label><label class="flex items-center gap-2 text-sm"><input type="checkbox" checked={form().schedulable} onChange={e => set("schedulable", e.currentTarget.checked)} />Schedulable</label><label class="flex items-center gap-2 text-sm"><input type="checkbox" checked={form().tlsFingerprint} onChange={e => set("tlsFingerprint", e.currentTarget.checked)} />TLS fingerprint</label></section>
    <div class="flex gap-4"><button type="submit" class="rounded bg-indigo-600 px-4 py-2 text-sm text-white hover:bg-indigo-700">{t("common.save")}</button><button type="button" onClick={() => navigate("/accounts")} class="rounded border border-gray-300 px-4 py-2 text-sm dark:border-gray-700">{t("common.cancel")}</button></div>
  </form></div>;
}
