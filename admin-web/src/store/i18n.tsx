import { createContext, useContext, createSignal, type ParentProps } from "solid-js";
import { zh } from "../i18n/zh";
import { en } from "../i18n/en";

type Dict = Record<string, string>;
const dicts: Record<string, Dict> = { zh, en };

interface I18nCtx {
  locale: () => string;
  setLocale: (l: string) => void;
  t: (key: string) => string;
}

const Ctx = createContext<I18nCtx>();

export function I18nProvider(props: ParentProps) {
  const [locale, setLocale] = createSignal(localStorage.getItem("locale") || "zh");

  const t = (key: string) => dicts[locale()]?.[key] ?? key;

  const changeLocale = (l: string) => {
    setLocale(l);
    localStorage.setItem("locale", l);
  };

  return (
    <Ctx.Provider value={{ locale, setLocale: changeLocale, t }}>
      {props.children}
    </Ctx.Provider>
  );
}

export function useI18n() {
  return useContext(Ctx)!;
}
