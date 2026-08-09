import { createSignal } from "solid-js";
import { A } from "@solidjs/router";
import { post } from "../api/client";

export default function Verification() {
  const [email, setEmail] = createSignal(localStorage.getItem("email") ?? "");
  const [token, setToken] = createSignal("");
  const [message, setMessage] = createSignal("");
  const [error, setError] = createSignal("");
  const request = async (event: Event) => {
    event.preventDefault(); setMessage(""); setError("");
    try {
      const result = await post<{ accepted: boolean; debug_token?: string }>(
        "/auth/email-verification/request", { email: email().trim().toLowerCase() });
      if (result.debug_token) setToken(result.debug_token);
      setMessage(result.debug_token
        ? `Development token: ${result.debug_token}`
        : "If the account exists, a verification message has been sent.");
    } catch (err) { setError(err instanceof Error ? err.message : "Unable to request verification"); }
  };
  const confirm = async (event: Event) => {
    event.preventDefault(); setMessage(""); setError("");
    try {
      await post("/auth/email-verification/confirm", { token: token().trim() });
      setToken(""); setMessage("Email verified. Return to your profile to continue.");
    } catch (err) { setError(err instanceof Error ? err.message : "The verification token is invalid or expired"); }
  };
  return <div class="flex min-h-screen items-center justify-center bg-[#0b1220] px-5"><div class="w-full max-w-md bg-white p-8 shadow-2xl"><p class="mb-2 text-xs font-semibold uppercase tracking-[0.18em] text-slate-500">ScalaAPI</p><h1 class="mb-1 text-2xl font-semibold">Verify email</h1><p class="mb-7 text-sm text-slate-500">Confirm the address used for account security notifications.</p>{message() && <p class="notice mb-4">{message()}</p>}{error() && <p class="error mb-4">{error()}</p>}<form class="space-y-3" onSubmit={request}><h2 class="font-medium">Request verification</h2><input required type="email" class="field" placeholder="Email" value={email()} onInput={e => setEmail(e.currentTarget.value)} /><button class="primary w-full">Send verification request</button></form><div class="my-7 h-px bg-slate-200" /><form class="space-y-3" onSubmit={confirm}><h2 class="font-medium">Confirm address</h2><input required class="field" placeholder="Verification token" value={token()} onInput={e => setToken(e.currentTarget.value)} /><button class="secondary w-full">Confirm email</button></form><p class="mt-7 text-center text-sm text-slate-500"><A href="/profile" class="font-medium text-slate-950 underline">Return to profile</A></p></div></div>;
}
