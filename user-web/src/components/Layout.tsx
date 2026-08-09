import { A, useNavigate } from "@solidjs/router";
import type { JSX } from "solid-js";
import { useAuth } from "../store/auth";

const links = [
  ["/", "Overview"], ["/usage", "Usage"], ["/keys", "API keys"],
  ["/billing", "Billing"], ["/profile", "Profile"],
] as const;

export default function Layout(props: { children?: JSX.Element }) {
  const auth = useAuth();
  const navigate = useNavigate();
  const logout = () => { auth.logout(); navigate("/login"); };
  return <div class="min-h-screen bg-[#f5f7fb] text-slate-900">
    <header class="border-b border-slate-200 bg-white">
      <div class="mx-auto flex h-16 max-w-7xl items-center justify-between px-5">
        <A href="/" class="text-lg font-semibold tracking-tight text-slate-950">ScalaAPI</A>
        <div class="flex items-center gap-4 text-sm">
          <span class="hidden text-slate-500 sm:inline">{auth.email()}</span>
          <button class="text-slate-600 hover:text-slate-950" onClick={logout}>Sign out</button>
        </div>
      </div>
    </header>
    <div class="mx-auto grid max-w-7xl gap-8 px-5 py-8 lg:grid-cols-[220px_1fr]">
      <aside class="lg:border-r lg:border-slate-200 lg:pr-6">
        <nav class="flex gap-2 overflow-x-auto lg:block lg:space-y-1">
          {links.map(([href, label]) => <A href={href} end={href === "/"}
            class="block whitespace-nowrap px-3 py-2 text-sm text-slate-600 hover:bg-slate-100 hover:text-slate-950"
            activeClass="bg-slate-900 text-white hover:bg-slate-900 hover:text-white">{label}</A>)}
        </nav>
      </aside>
      <main class="min-w-0">{props.children}</main>
    </div>
  </div>;
}
