import { A } from "@solidjs/router";
import type { JSX } from "solid-js";

const links = [
  ["/models", "Models"],
  ["/status", "Status"],
  ["/terms", "Terms"],
  ["/privacy", "Privacy"],
] as const;

export default function PublicLayout(props: { children?: JSX.Element }) {
  return <div class="min-h-screen bg-[#f5f7fb] text-slate-900">
    <header class="border-b border-slate-200 bg-white">
      <div class="mx-auto flex min-h-16 max-w-7xl flex-wrap items-center justify-between gap-4 px-5 py-3">
        <A href="/models" class="text-lg font-semibold tracking-tight text-slate-950">ScalaAPI</A>
        <nav aria-label="Public navigation" class="flex flex-wrap items-center gap-1 text-sm">
          {links.map(([href, label]) => <A href={href}
            class="px-3 py-2 text-slate-600 hover:bg-slate-100 hover:text-slate-950"
            activeClass="bg-slate-900 text-white hover:bg-slate-900 hover:text-white">{label}</A>)}
          <A href="/login" class="ml-2 border border-slate-300 px-3 py-2 font-medium text-slate-700 hover:border-slate-900 hover:text-slate-950">Sign in</A>
          <A href="/register" class="bg-slate-900 px-3 py-2 font-medium text-white hover:bg-slate-700">Create account</A>
        </nav>
      </div>
    </header>
    <main class="mx-auto max-w-7xl px-5 py-10">{props.children}</main>
    <footer class="border-t border-slate-200 bg-white">
      <div class="mx-auto flex max-w-7xl flex-wrap items-center justify-between gap-3 px-5 py-6 text-sm text-slate-500">
        <span>ScalaAPI public service information</span>
        <nav aria-label="Legal navigation" class="flex gap-4">
          <A href="/terms" class="underline hover:text-slate-900">Terms</A>
          <A href="/privacy" class="underline hover:text-slate-900">Privacy</A>
        </nav>
      </div>
    </footer>
  </div>;
}
