import { createResource, Show } from "solid-js";
import { useNavigate, useParams } from "@solidjs/router";
import { post } from "../api/client";
import { useAuth } from "../store/auth";

export default function OAuthCallback() {
  const params = useParams(); const auth = useAuth(); const navigate = useNavigate();
  const [result] = createResource(async () => {
    const query = new URLSearchParams(window.location.search);
    const state = query.get("state") ?? ""; const code = query.get("code") ?? "";
    const provider = params.provider; const redirectUri = `${window.location.origin}/oauth/callback/${provider}`;
    const codeVerifier = localStorage.getItem(`oauth_verifier:${provider}:${state}`) ?? "";
    if (!state || !code || !codeVerifier) throw new Error("The OAuth state is missing or expired");
    const session = await post<{ token: string; refresh_token: string; email: string; role: string }>(
      "/auth/oauth/callback", { provider, code, redirectUri, state, codeVerifier });
    localStorage.removeItem(`oauth_verifier:${provider}:${state}`); auth.acceptSession(session); navigate("/");
    return true;
  });
  return <div class="flex min-h-screen items-center justify-center bg-[#0b1220] px-5"><div class="bg-white p-8 text-center shadow-2xl"><Show when={!result.error} fallback={<><h1 class="text-xl font-semibold">OAuth sign-in failed</h1><p class="mt-2 text-sm text-rose-700">{result.error?.message}</p><a class="mt-5 inline-block underline" href="/login">Return to sign in</a></>}><p class="text-sm text-slate-600">Completing sign-in...</p></Show></div></div>;
}
