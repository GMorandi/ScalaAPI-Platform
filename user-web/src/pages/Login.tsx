import { createSignal } from "solid-js";
import { A, useNavigate } from "@solidjs/router";
import { get, post } from "../api/client";
import { useAuth } from "../store/auth";
import { authenticationOptions, serializeCredential, webAuthnAvailable } from "../webauthn";

export default function Login() {
  const auth = useAuth();
  const navigate = useNavigate();
  const [email, setEmail] = createSignal("");
  const [password, setPassword] = createSignal("");
  const [totpCode, setTotpCode] = createSignal("");
  const [error, setError] = createSignal("");
  const [busy, setBusy] = createSignal(false);

  const submit = async (event: Event) => {
    event.preventDefault(); setBusy(true); setError("");
    try { await auth.login(email(), password(), totpCode() || undefined); navigate("/"); }
    catch (err) { setError(err instanceof Error ? err.message : "Unable to sign in"); }
    finally { setBusy(false); }
  };

  const oauth = async (provider: "github" | "google") => {
    setError("");
    try {
      const redirectUri = `${window.location.origin}/oauth/callback/${provider}`;
      const start = await get<{ authorizationUrl: string; state: string; codeVerifier: string }>(
        `/auth/oauth/${provider}/start?redirectUri=${encodeURIComponent(redirectUri)}`);
      localStorage.setItem(`oauth_verifier:${provider}:${start.state}`, start.codeVerifier);
      window.location.assign(start.authorizationUrl);
    } catch (err) { setError(err instanceof Error ? err.message : "OAuth is unavailable"); }
  };

  const passkeyLogin = async () => {
    setError(""); setBusy(true);
    if (!email().trim()) { setError("Enter your email before using a passkey"); setBusy(false); return; }
    if (!webAuthnAvailable()) { setError("Passkeys are unavailable in this browser"); setBusy(false); return; }
    try {
      const start = await post<{ challenge_id: string; options: Record<string, unknown> }>("/auth/passkeys/options", { email: email() });
      const credential = await navigator.credentials.get({ publicKey: authenticationOptions(start.options) });
      if (!(credential instanceof PublicKeyCredential)) throw new Error("No passkey assertion was returned");
      const session = await post<{ token: string; refresh_token: string; email: string; role: string }>(`/auth/passkeys/${start.challenge_id}`, serializeCredential(credential));
      auth.acceptSession(session); navigate("/");
    } catch (err) { setError(err instanceof Error ? err.message : "Unable to sign in with passkey"); }
    finally { setBusy(false); }
  };

  return <div class="flex min-h-screen items-center justify-center bg-[#0b1220] px-5">
    <div class="w-full max-w-md bg-white p-8 shadow-2xl">
      <p class="mb-2 text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">ScalaAPI</p>
      <h1 class="mb-1 text-2xl font-semibold text-slate-950">Sign in</h1>
      <p class="mb-7 text-sm text-slate-500">Manage your account, API access and usage.</p>
      {error() && <p class="mb-4 border-l-2 border-rose-600 bg-rose-50 px-3 py-2 text-sm text-rose-700">{error()}</p>}
      <form class="space-y-4" onSubmit={submit}>
        <label class="block text-sm font-medium">Email<input required type="email" value={email()} onInput={e => setEmail(e.currentTarget.value)} class="field" /></label>
        <label class="block text-sm font-medium">Password<input required type="password" value={password()} onInput={e => setPassword(e.currentTarget.value)} class="field" /></label>
        <label class="block text-sm font-medium">Authenticator code <span class="font-normal text-slate-400">(when enabled)</span><input inputmode="numeric" value={totpCode()} onInput={e => setTotpCode(e.currentTarget.value)} class="field" /></label>
        <button disabled={busy()} class="primary w-full">{busy() ? "Signing in..." : "Sign in"}</button>
      </form>
      <p class="mt-4 text-center text-sm"><A href="/recover" class="text-slate-600 underline hover:text-slate-950">Forgot your password?</A></p>
      <div class="my-6 flex items-center gap-3 text-xs text-slate-400"><span class="h-px flex-1 bg-slate-200" />OR<span class="h-px flex-1 bg-slate-200" /></div>
      <div class="grid grid-cols-2 gap-3"><button class="secondary" onClick={() => oauth("github")}>GitHub</button><button class="secondary" onClick={() => oauth("google")}>Google</button></div>
      <button disabled={busy()} class="secondary mt-3 w-full" onClick={passkeyLogin}>Sign in with passkey</button>
      <p class="mt-7 text-center text-sm text-slate-500">New here? <A href="/register" class="font-medium text-slate-950 underline">Create an account</A></p>
    </div>
  </div>;
}
