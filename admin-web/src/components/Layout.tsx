import type { JSX } from "solid-js";
import { useI18n } from "../store/i18n";
import Sidebar from "./Sidebar";
import Topbar from "./Topbar";

export default function Layout(props: { children?: JSX.Element }) {
  return (
    <div class="flex h-screen overflow-hidden">
      <Sidebar />
      <div class="flex flex-1 flex-col overflow-hidden">
        <Topbar />
        <main class="flex-1 overflow-y-auto p-6">
          {props.children}
        </main>
      </div>
    </div>
  );
}
