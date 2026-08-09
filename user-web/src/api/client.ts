let refreshInFlight: Promise<boolean> | undefined;

async function refreshSession(): Promise<boolean> {
  const refreshToken = localStorage.getItem("refresh_token");
  if (!refreshToken) return false;
  refreshInFlight ??= fetch("/auth/refresh", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ refreshToken }),
  }).then(async (response) => {
    if (!response.ok) return false;
    const data = await response.json() as { token: string; refresh_token: string };
    localStorage.setItem("token", data.token);
    localStorage.setItem("refresh_token", data.refresh_token);
    return true;
  }).catch(() => false).finally(() => { refreshInFlight = undefined; });
  return refreshInFlight;
}

export async function api<T>(path: string, opts: RequestInit = {}, retry = true): Promise<T> {
  const token = localStorage.getItem("token");
  const response = await fetch(path, {
    ...opts,
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...opts.headers,
    },
  });

  if (response.status === 401 && retry && !path.startsWith("/auth/")) {
    if (await refreshSession()) return api<T>(path, opts, false);
    localStorage.removeItem("token");
    localStorage.removeItem("refresh_token");
    window.location.href = "/login";
    throw new Error("Session expired");
  }
  if (!response.ok) {
    let message = `Request failed (${response.status})`;
    try {
      const body = await response.json() as { error?: string };
      if (body.error) message = body.error;
    } catch { /* retain status message for non-JSON errors */ }
    throw new Error(message);
  }
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

export const get = <T>(path: string) => api<T>(path);
export const post = <T>(path: string, body: unknown, headers: HeadersInit = {}) =>
  api<T>(path, { method: "POST", headers, body: JSON.stringify(body) });
export const put = <T>(path: string, body: unknown) =>
  api<T>(path, { method: "PUT", body: JSON.stringify(body) });
export const del = <T>(path: string) => api<T>(path, { method: "DELETE" });
