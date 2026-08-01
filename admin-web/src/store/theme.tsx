import { createContext, useContext, createSignal, createEffect, type ParentProps } from "solid-js";

interface ThemeCtx {
  dark: () => boolean;
  toggle: () => void;
}

const Ctx = createContext<ThemeCtx>();

export function ThemeProvider(props: ParentProps) {
  const [dark, setDark] = createSignal(
    localStorage.getItem("theme") === "dark" ||
    (!localStorage.getItem("theme") && window.matchMedia("(prefers-color-scheme: dark)").matches)
  );

  createEffect(() => {
    document.documentElement.classList.toggle("dark", dark());
    localStorage.setItem("theme", dark() ? "dark" : "light");
  });

  const toggle = () => setDark((d) => !d);

  return (
    <Ctx.Provider value={{ dark, toggle }}>
      {props.children}
    </Ctx.Provider>
  );
}

export function useTheme() {
  return useContext(Ctx)!;
}
