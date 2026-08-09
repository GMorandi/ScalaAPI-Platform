import { createResource, createSignal, Show } from "solid-js";
import { del, get, post } from "../api/client";
import { registrationOptions, serializeCredential, webAuthnAvailable } from "../webauthn";

type Profile = { totpEnabled: boolean };
type Setup = { secret: string; qrUri: string };
type Passkey = { id: string; displayName: string; createdAt: string; lastUsedAt?: string };
export default function Security() {
  const [profile, { refetch }] = createResource(() => get<Profile>("/user/profile"));
  const [passkeys, { refetch: refetchPasskeys }] = createResource(() => get<{ items: Passkey[] }>("/user/passkeys"));
  const [setup, setSetup] = createSignal<Setup>(); const [code, setCode] = createSignal("");
  const [backup, setBackup] = createSignal<string[]>([]); const [message, setMessage] = createSignal(""); const [error, setError] = createSignal("");
  const start = async () => { setError(""); try { setSetup(await post<Setup>("/user/totp/setup", {})); } catch (err) { setError(err instanceof Error ? err.message : "Unable to start TOTP setup"); } };
  const verify = async (event: Event) => { event.preventDefault(); setError(""); try { const result = await post<{ backup_codes: string[] }>("/user/totp/verify", { code: code() }); setBackup(result.backup_codes); setSetup(undefined); setCode(""); setMessage("Authenticator enabled. Store the backup codes securely."); refetch(); } catch (err) { setError(err instanceof Error ? err.message : "Invalid authenticator code"); } };
  const disable = async () => { setError(""); try { await post("/user/totp/disable", { code: code() }); setCode(""); setMessage("Authenticator disabled."); refetch(); } catch (err) { setError(err instanceof Error ? err.message : "Invalid authenticator code"); } };
  const registerPasskey = async () => {
    setError(""); setMessage("");
    if (!webAuthnAvailable()) { setError("Passkeys are unavailable in this browser"); return; }
    try {
      const start = await post<{ challenge_id: string; options: Record<string, unknown> }>("/user/passkeys/register/options", {});
      const credential = await navigator.credentials.create({ publicKey: registrationOptions(start.options) });
      if (!(credential instanceof PublicKeyCredential)) throw new Error("No passkey was created");
      await post(`/user/passkeys/register/${start.challenge_id}`, serializeCredential(credential));
      setMessage("Passkey registered."); refetchPasskeys();
    } catch (err) { setError(err instanceof Error ? err.message : "Unable to register passkey"); }
  };
  const revokePasskey = async (id: string) => {
    setError("");
    try { await del(`/user/passkeys/${id}`); setMessage("Passkey revoked."); refetchPasskeys(); }
    catch (err) { setError(err instanceof Error ? err.message : "Unable to revoke passkey"); }
  };
  return <div class="space-y-8"><div><p class="eyebrow">Security</p><h1 class="title">Authenticator</h1><p class="muted">Protect sign-in with replay-resistant authenticators and passkeys.</p></div>{message() && <p class="notice">{message()}</p>}{error() && <p class="error">{error()}</p>}<section class="panel"><div class="section-head"><h2>Two-factor authentication</h2><span class={profile()?.totpEnabled ? "status" : "status status-muted"}>{profile()?.totpEnabled ? "Enabled" : "Not enabled"}</span></div><Show when={!profile()?.totpEnabled && !setup()}><button class="primary" onClick={start}>Set up authenticator</button></Show><Show when={setup()} keyed>{current => <div class="space-y-4"><p class="muted">Add this secret to your authenticator app. A QR renderer can use the URI directly.</p><code class="block break-all border border-slate-200 bg-slate-50 p-3 text-xs">{current.qrUri}</code><p class="text-sm">Manual secret: <code>{current.secret}</code></p><form class="flex max-w-sm gap-3" onSubmit={verify}><input required inputmode="numeric" class="field mt-0" placeholder="6-digit code" value={code()} onInput={e => setCode(e.currentTarget.value)} /><button class="primary">Verify</button></form></div>}</Show><Show when={profile()?.totpEnabled}><div class="max-w-sm space-y-3"><p class="muted">Enter a current authenticator code to disable two-factor authentication.</p><input inputmode="numeric" class="field mt-0" placeholder="6-digit code" value={code()} onInput={e => setCode(e.currentTarget.value)} /><button class="secondary" onClick={disable}>Disable authenticator</button></div></Show></section><section class="panel"><div class="section-head"><div><h2>Passkeys</h2><p class="muted">Use a device authenticator instead of a password.</p></div><button class="primary" onClick={registerPasskey}>Register passkey</button></div><Show when={(passkeys()?.items.length ?? 0) > 0} fallback={<p class="muted">No passkeys registered.</p>}><div class="divide-y divide-slate-200">{passkeys()?.items.map(item => <div class="flex items-center justify-between gap-4 py-3"><div><p class="font-medium">{item.displayName}</p><p class="text-xs text-slate-500">Added {new Date(item.createdAt).toLocaleDateString()}{item.lastUsedAt ? ` · Used ${new Date(item.lastUsedAt).toLocaleDateString()}` : ""}</p></div><button class="secondary" onClick={() => revokePasskey(item.id)}>Revoke</button></div>)}</div></Show></section><Show when={backup().length > 0}><section class="notice"><p class="font-medium">Backup codes</p><p class="mt-1 text-sm">Each code works once. Save these before leaving this page.</p><div class="mt-3 grid grid-cols-2 gap-2 font-mono text-sm">{backup().map(item => <code>{item}</code>)}</div></section></Show></div>;
}
