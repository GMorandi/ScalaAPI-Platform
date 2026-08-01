import { createContext, useContext, createSignal, type ParentProps } from "solid-js";
import { post } from "../api/client";

interface AuthCtx {
  token: () => string | null;
  login: (username: string, password: string) => Promise<void>;
  logout: () => void;
}

const Ctx = createContext<AuthCtx>();

export function AuthProvider(props: ParentProps) {
  const [token, setToken] = createSignal(localStorage.getItem("token"));

  const login = async (username: string, password: string) => {
    const res = await post<{ token: string }>("/auth/login", { username, password });
    localStorage.setItem("token", res.token);
    setToken(res.token);
  };

  const logout = () => {
    localStorage.removeItem("token");
    setToken(null);
  };

  return (
    <Ctx.Provider value={{ token, login, logout }}>
      {props.children}
    </Ctx.Provider>
  );
}

export function useAuth() {
  return useContext(Ctx)!;
}
