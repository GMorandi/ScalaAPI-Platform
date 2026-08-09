type JsonObject = Record<string, unknown>;

function decodeBase64Url(value: string): ArrayBuffer {
  const normalized = value.replace(/-/g, "+").replace(/_/g, "/")
    .padEnd(Math.ceil(value.length / 4) * 4, "=");
  const bytes = atob(normalized);
  const output = new Uint8Array(bytes.length);
  for (let index = 0; index < bytes.length; index += 1) output[index] = bytes.charCodeAt(index);
  return output.buffer;
}

function encodeBase64Url(value: ArrayBuffer): string {
  const bytes = new Uint8Array(value);
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function credentialIds(items: unknown): PublicKeyCredentialDescriptor[] | undefined {
  if (!Array.isArray(items)) return undefined;
  return items.map((item) => {
    const value = item as JsonObject;
    return { ...value, id: decodeBase64Url(String(value.id)) } as PublicKeyCredentialDescriptor;
  });
}

export function registrationOptions(raw: JsonObject): PublicKeyCredentialCreationOptions {
  const options = { ...raw } as JsonObject;
  const user = { ...(options.user as JsonObject) };
  options.challenge = decodeBase64Url(String(options.challenge));
  user.id = decodeBase64Url(String(user.id));
  options.user = user;
  options.excludeCredentials = credentialIds(options.excludeCredentials);
  return options as unknown as PublicKeyCredentialCreationOptions;
}

export function authenticationOptions(raw: JsonObject): PublicKeyCredentialRequestOptions {
  const options = { ...raw } as JsonObject;
  options.challenge = decodeBase64Url(String(options.challenge));
  options.allowCredentials = credentialIds(options.allowCredentials);
  return options as unknown as PublicKeyCredentialRequestOptions;
}

export function serializeCredential(credential: PublicKeyCredential): JsonObject {
  const response = credential.response;
  const result: JsonObject = {
    id: credential.id,
    rawId: encodeBase64Url(credential.rawId),
    type: credential.type,
  };
  if (response instanceof AuthenticatorAttestationResponse) {
    result.response = {
      clientDataJSON: encodeBase64Url(response.clientDataJSON),
      attestationObject: encodeBase64Url(response.attestationObject),
      transports: response.getTransports?.() ?? [],
    };
  } else if (response instanceof AuthenticatorAssertionResponse) {
    result.response = {
      clientDataJSON: encodeBase64Url(response.clientDataJSON),
      authenticatorData: encodeBase64Url(response.authenticatorData),
      signature: encodeBase64Url(response.signature),
      userHandle: response.userHandle ? encodeBase64Url(response.userHandle) : null,
    };
  } else {
    throw new Error("Unsupported WebAuthn response");
  }
  return result;
}

export function webAuthnAvailable(): boolean {
  return typeof window !== "undefined" && "PublicKeyCredential" in window
    && typeof navigator.credentials?.create === "function"
    && typeof navigator.credentials?.get === "function";
}
