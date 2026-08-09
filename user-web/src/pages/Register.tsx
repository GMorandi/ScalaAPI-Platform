import { createSignal } from "solid-js";
import { A, useNavigate } from "@solidjs/router";
import { useAuth } from "../store/auth";

export default function Register() {
  const auth = useAuth(); const navigate = useNavigate();
  const [email, setEmail] = createSignal(""); const [password, setPassword] = createSignal("");
  const [name, setName] = createSignal(""); const [error, setError] = createSignal(""); const [busy, setBusy] = createSignal(false);
  const submit = async (event: Event) => {
    event.preventDefault(); setBusy(true); setError("");
    try { await auth.register(email(), password(), name()); navigate("/"); }
    catch (err) { setError(err instanceof Error ? err.message : "Unable to create account"); }
    finally { setBusy(false); }
  };
  return <div class="flex min-h-screen items-center justify-center bg-[#0b1220] px-5"><form onSubmit={submit} class="w-full max-w-md bg-white p-8 shadow-2xl">
    <p class="mb-2 text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">ScalaAPI</p><h1 class="mb-1 text-2xl font-semibold">Create your account</h1><p class="mb-7 text-sm text-slate-500">Start with a clean account and zero balance.</p>
    {error() && <p class="mb-4 border-l-2 border-rose-600 bg-rose-50 px-3 py-2 text-sm text-rose-700">{error()}</p>}
    <div class="space-y-4"><label class="block text-sm font-medium">Name <input class="field" value={name()} onInput={e => setName(e.currentTarget.value)} /></label><label class="block text-sm font-medium">Email <input required type="email" class="field" value={email()} onInput={e => setEmail(e.currentTarget.value)} /></label><label class="block text-sm font-medium">Password <input required minlength="12" type="password" class="field" value={password()} onInput={e => setPassword(e.currentTarget.value)} /></label><button disabled={busy()} class="primary w-full">{busy() ? "Creating..." : "Create account"}</button></div>
    <p class="mt-7 text-center text-sm text-slate-500">Already registered? <A href="/login" class="font-medium text-slate-950 underline">Sign in</A></p>
  </form></div>;
}
