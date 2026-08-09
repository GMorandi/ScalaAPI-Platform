import { createResource, createSignal, For, Show } from "solid-js";
import { A } from "@solidjs/router";
import { get, post } from "../api/client";

type DashboardData = { profile: { email: string; displayName?: string; status: string }; balance: { balance: number; ledger_version: number }; usage: { items: UsageItem[]; total: number }; keys: { keys: KeyItem[] }; subscriptions: { items: Subscription[] } };
type UsageItem = { request_id: string; model: string; input_tokens: number; output_tokens: number; cost_usd: number; created_at: string };
type KeyItem = { id: number; keyPrefix: string; status: string };
type Subscription = { id: number; name: string; status: string; expiresAt?: string; quotaGrantedUsd: number; quotaUsedUsd: number };
type Announcement = { id: number; title: string; content: string; priority: number; createdAt: string; expiresAt?: string; readAt?: string };

export default function Dashboard() {
  const [data] = createResource(async () => Promise.all([
    get<DashboardData["profile"]>("/user/profile"), get<DashboardData["balance"]>("/user/usage/balance"),
    get<DashboardData["usage"]>("/user/usage?size=5"), get<DashboardData["keys"]>("/user/apikeys"),
    get<DashboardData["subscriptions"]>("/user/subscriptions"),
  ]).then(([profile, balance, usage, keys, subscriptions]) => ({ profile, balance, usage, keys, subscriptions })));
  const [announcements, { refetch: refetchAnnouncements }] = createResource(() => get<{ items: Announcement[] }>("/user/announcements/?limit=5"));
  const [reading, setReading] = createSignal<number | null>(null);
  const markRead = async (id: number) => {
    setReading(id);
    try { await post(`/user/announcements/${id}/read`, {}); await refetchAnnouncements(); }
    finally { setReading(null); }
  };
  return <div class="space-y-8"><div><p class="eyebrow">Overview</p><h1 class="title">Good to see you{data()?.profile.displayName ? `, ${data()?.profile.displayName}` : ""}.</h1><p class="muted">Your account activity and access at a glance.</p></div>
    <Show when={announcements()?.items.length}>
      <section class="panel"><div class="section-head"><h2>Announcements</h2></div><div class="space-y-4"><For each={announcements()?.items}>{item => <article class={item.readAt ? "border-l-2 border-slate-200 pl-4" : "border-l-2 border-emerald-600 pl-4"}><div class="flex flex-wrap items-start justify-between gap-3"><div><h3 class="font-medium text-slate-950">{item.title}</h3><p class="mt-1 whitespace-pre-wrap text-sm text-slate-600">{item.content}</p><p class="mt-2 text-xs text-slate-500">{new Date(item.createdAt).toLocaleDateString()}</p></div><Show when={!item.readAt}><button class="secondary" disabled={reading() === item.id} onClick={() => markRead(item.id)}>{reading() === item.id ? "Marking..." : "Mark as read"}</button></Show></div></article>}</For></div></section>
    </Show>
    <div class="grid gap-4 md:grid-cols-3"><section class="metric"><span class="metric-label">Available balance</span><strong>${(data()?.balance.balance ?? 0).toFixed(2)}</strong><span class="metric-note">Ledger version {data()?.balance.ledger_version ?? 0}</span></section><section class="metric"><span class="metric-label">Requests</span><strong>{data()?.usage.total ?? 0}</strong><span class="metric-note">Recorded usage events</span></section><section class="metric"><span class="metric-label">Active keys</span><strong>{data()?.keys.keys.filter(k => k.status === "active").length ?? 0}</strong><span class="metric-note"><A href="/keys" class="underline">Manage access</A></span></section></div>
    <section class="panel"><div class="section-head"><h2>Recent usage</h2><A href="/usage" class="text-sm underline">View all</A></div><Show when={data()} fallback={<p class="muted">Loading usage...</p>}><div class="overflow-x-auto"><table class="data-table"><thead><tr><th>Model</th><th>Tokens</th><th>Cost</th><th>When</th></tr></thead><tbody><For each={data()?.usage.items}>{item => <tr><td>{item.model}</td><td>{item.input_tokens + item.output_tokens}</td><td>${item.cost_usd.toFixed(4)}</td><td>{new Date(item.created_at).toLocaleString()}</td></tr>}</For></tbody></table></div></Show></section>
    <section class="panel"><div class="section-head"><h2>Subscription</h2><A href="/billing" class="text-sm underline">Billing</A></div><Show when={data()?.subscriptions.items.length} fallback={<p class="muted">No active subscription.</p>}><For each={data()?.subscriptions.items.slice(0, 1)}>{sub => <div class="flex flex-wrap items-center justify-between gap-3"><div><p class="font-medium">{sub.name}</p><p class="muted">{sub.status} · {sub.quotaUsedUsd.toFixed(2)} / {sub.quotaGrantedUsd.toFixed(2)} USD used</p></div><span class="status">{sub.expiresAt ? `Renews ${new Date(sub.expiresAt).toLocaleDateString()}` : "No expiry"}</span></div>}</For></Show></section>
  </div>;
}
