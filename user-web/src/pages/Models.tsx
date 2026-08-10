import { For, Show, createResource } from "solid-js";
import PublicLayout from "../components/PublicLayout";

type Model = {
  id: string;
  object?: string;
  created?: number;
  owned_by?: string;
};

type ModelList = { object?: string; data?: Model[] };

async function loadModels(): Promise<ModelList> {
  const response = await fetch("/v1/models", { headers: { Accept: "application/json" } });
  if (!response.ok) throw new Error(`Model directory unavailable (${response.status})`);
  return response.json() as Promise<ModelList>;
}

export default function Models() {
  const [models] = createResource(loadModels);
  return <PublicLayout>
    <div class="space-y-8">
      <section class="max-w-3xl">
        <p class="eyebrow">Public catalog</p>
        <h1 class="title">Available models</h1>
        <p class="mt-3 text-base text-slate-600">Inspect the model names currently published by ScalaAPI before creating an API key.</p>
      </section>
      <Show when={!models.loading} fallback={<p role="status" class="notice">Loading the model directory...</p>}>
        <Show when={!models.error} fallback={<p role="alert" class="error">The model directory is temporarily unavailable. Try again shortly.</p>}>
          <section aria-labelledby="model-list-heading" class="panel">
            <div class="section-head">
              <h2 id="model-list-heading">Model directory</h2>
              <span class="status status-muted">{models()?.data?.length ?? 0} published</span>
            </div>
            <Show when={(models()?.data?.length ?? 0) > 0} fallback={<p class="muted">No models are currently published.</p>}>
              <div class="overflow-x-auto">
                <table class="data-table">
                  <caption class="sr-only">Published ScalaAPI models</caption>
                  <thead><tr><th scope="col">Model</th><th scope="col">Owner</th><th scope="col">Object</th><th scope="col">Created</th></tr></thead>
                  <tbody><For each={models()?.data}>{model => <tr>
                    <th scope="row" class="font-mono text-sm font-medium text-slate-900">{model.id}</th>
                    <td>{model.owned_by || "ScalaAPI"}</td>
                    <td>{model.object || "model"}</td>
                    <td>{model.created ? new Date(model.created * 1000).toLocaleDateString() : "-"}</td>
                  </tr>}</For></tbody>
                </table>
              </div>
            </Show>
          </section>
        </Show>
      </Show>
    </div>
  </PublicLayout>;
}
