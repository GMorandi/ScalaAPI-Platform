import { createContext, createSignal, useContext, type ParentProps } from "solid-js";
import { post } from "../api/client";

interface SessionResponse {
  token: string;
  refresh_token: string;
  email: string;
  role: string;
}

interface AuthContext {
  token: () => string | null;
  email: () => string | null;
  login: (email: string, password: string, totpCode?: string) => Promise<void>;
  register: (email: string, password: string, displayName: string) => Promise<void>;
  acceptSession: (session: SessionResponse) => void;
  logout: () => void;
}

const Context = createContext<AuthContext>();

export function AuthProvider(props: ParentProps) {
  const [token, setToken] = createSignal(localStorage.getItem("token"));
  const [email, setEmail] = createSignal(localStorage.getItem("email"));

  const acceptSession = (session: SessionResponse) => {
    localStorage.setItem("token", session.token);
    localStorage.setItem("refresh_token", session.refresh_token);
    localStorage.setItem("email", session.email);
    setToken(session.token);
    setEmail(session.email);
  };

  const login = async (address: string, password: string, totpCode?: string) => {
    const session = await post<SessionResponse>("/auth/login", {
      email: address.trim().toLowerCase(), password, totpCode,
    });
    acceptSession(session);
  };

  const register = async (address: string, password: string, displayName: string) => {
    await post("/auth/register", {
      email: address.trim().toLowerCase(), password, displayName: displayName || null,
    });
    await login(address, password);
  };

  const logout = () => {
    const currentToken = token();
    if (currentToken) void post("/user/logout", {});
    localStorage.removeItem("token");
    localStorage.removeItem("refresh_token");
    localStorage.removeItem("email");
    setToken(null);
    setEmail(null);
  };

  return <Context.Provider value={{ token, email, login, register, acceptSession, logout }}>
    {props.children}
  </Context.Provider>;
}

export function useAuth(): AuthContext {
  const context = useContext(Context);
  if (!context) throw new Error("useAuth must be used within AuthProvider");
  return context;
}
