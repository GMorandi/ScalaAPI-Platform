import { createResource, createSignal, For, Show } from "solid-js";
import { del, get, post } from "../api/client";

type KeyItem = { id: number; keyPrefix: string; name?: string; status: string; createdAt: string; lastUsedAt?: string };
type KeyList = { keys: KeyItem[] };
export default function Keys() {
  const [data, { refetch }] = createResource(() => get<KeyList>("/user/apikeys"));
  const [groupId, setGroupId] = createSignal(""); const [name, setName] = createSignal(""); const [quota, setQuota] = createSignal("0");
  const [newKey, setNewKey] = createSignal(""); const [error, setError] = createSignal("");
  const create = async (event: Event) => { event.preventDefault(); setError(""); try { const result = await post<{ key: string }>("/user/apikeys", { name: name() || null, groupId: Number(groupId()), quota: Number(quota()) }); setNewKey(result.key); setName(""); refetch(); } catch (err) { setError(err instanceof Error ? err.message : "Unable to create key"); } };
  const revoke = async (id: number) => { if (!window.confirm("Revoke this key?")) return; await del(`/user/apikeys/${id}`); refetch(); };
  const rotate = async (id: number) => { setError(""); try { const result = await post<{ key: string }>(`/user/apikeys/${id}/rotate`, {}); setNewKey(result.key); refetch(); } catch (err) { setError(err instanceof Error ? err.message : "Unable to rotate key"); } };
  return <div class="space-y-8"><div><p class="eyebrow">Access</p><h1 class="title">API keys</h1><p class="muted">Keys are shown in full only once. Revoke compromised access immediately.</p></div>
    <Show when={newKey()} keyed>{key => <section class="notice"><p class="font-medium">New key, copy it now</p><code class="mt-2 block break-all text-sm">{key}</code></section>}</Show>{error() && <p class="error">{error()}</p>}
    <section class="panel"><div class="section-head"><h2>Create key</h2></div><form class="grid gap-3 md:grid-cols-[1fr_1fr_140px_auto]" onSubmit={create}><input class="field" required placeholder="Group ID" inputmode="numeric" value={groupId()} onInput={e => setGroupId(e.currentTarget.value)} /><input class="field" placeholder="Name (optional)" value={name()} onInput={e => setName(e.currentTarget.value)} /><input class="field" type="number" min="0" step="0.00000001" value={quota()} onInput={e => setQuota(e.currentTarget.value)} /><button class="primary">Create</button></form><p class="muted mt-2">Use a group assigned to your account. A zero quota means no absolute key quota.</p></section>
    <section class="panel"><div class="section-head"><h2>Your keys</h2></div><div class="overflow-x-auto"><table class="data-table"><thead><tr><th>Name</th><th>Prefix</th><th>Status</th><th>Created</th><th /></tr></thead><tbody><For each={data()?.keys}>{key => <tr><td>{key.name || "Untitled key"}</td><td><code>{key.keyPrefix}...</code></td><td><span class={key.status === "active" ? "status" : "status status-muted"}>{key.status}</span></td><td>{new Date(key.createdAt).toLocaleDateString()}</td><td class="whitespace-nowrap text-right"><Show when={key.status === "active"}><button class="link-button" onClick={() => rotate(key.id)}>Rotate</button><button class="danger-link" onClick={() => revoke(key.id)}>Revoke</button></Show></td></tr>}</For></tbody></table></div></section>
  </div>;
}
