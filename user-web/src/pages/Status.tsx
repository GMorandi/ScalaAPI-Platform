import { Show, createResource } from "solid-js";
import PublicLayout from "../components/PublicLayout";

type ReadyResponse = { status?: string };

async function loadStatus(): Promise<{ online: boolean; detail: string }> {
  const response = await fetch("/ready", { headers: { Accept: "application/json" } });
  if (!response.ok) return { online: false, detail: `Gateway returned HTTP ${response.status}` };
  const body = await response.json() as ReadyResponse;
  return { online: body.status === "ready", detail: body.status || "ready" };
}

export default function Status() {
  const [health, { refetch }] = createResource(loadStatus);
  return <PublicLayout>
    <div class="space-y-8">
      <section class="max-w-3xl">
        <p class="eyebrow">Service health</p>
        <h1 class="title">ScalaAPI status</h1>
        <p class="mt-3 text-base text-slate-600">A public, read-only check of the API gateway availability.</p>
      </section>
      <section aria-labelledby="status-heading" class="panel max-w-2xl">
        <div class="section-head">
          <h2 id="status-heading">API gateway</h2>
          <Show when={!health.loading} fallback={<span role="status" class="status status-muted">Checking</span>}>
            <span class={health()?.online ? "status" : "status bg-rose-50 text-rose-700"}>
              {health()?.online ? "Operational" : "Unavailable"}
            </span>
          </Show>
        </div>
        <Show when={!health.loading} fallback={<p role="status" class="muted">Contacting the gateway...</p>}>
          <p role="status" class="text-sm text-slate-600">{health()?.detail}</p>
          <button type="button" class="secondary mt-5" onClick={() => refetch()}>Check again</button>
        </Show>
      </section>
    </div>
  </PublicLayout>;
}
