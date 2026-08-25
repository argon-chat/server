/**
 * Talking to the panel.
 *
 * Everything the page knows arrives through here, and the one rule it enforces is where "here" is.
 */

/**
 * Where the API is, relative to wherever this page is being served from.
 *
 * Two places, and the difference is not cosmetic. During setup the page is at `/`; afterwards the edge
 * serves it at `/panel/` with the prefix stripped before the container sees it. A hardcoded `/api`
 * works all through the install and breaks the moment `/` becomes Argon — which is the worst possible
 * time, because by then nobody is watching the install any more.
 *
 * `URL` against `location.href` does the resolution a browser would do for a link, which is the point:
 * the edge guarantees `/panel/`'s trailing slash precisely so this comes out right.
 */
export function url(path: string): string {
    return new URL(path, location.href).toString();
}

export interface Answer<T> {
    readonly status: number;
    readonly ok: boolean;
    readonly body: T | undefined;
}

export async function call<T = unknown>(path: string, options?: RequestInit): Promise<Answer<T>> {
    const response = await fetch(url(path), {
        credentials: "same-origin",
        ...options,
        headers: { ...(options?.body === undefined ? {} : { "content-type": "application/json" }), ...options?.headers },
    });

    // Every route answers JSON except the ones that answer nothing. A body that will not parse is a
    // proxy or a crash rather than the panel, and saying so beats "undefined is not an object".
    const body = (await response.json().catch(() => undefined)) as T | undefined;

    return { status: response.status, ok: response.ok, body };
}

const post = <T = unknown>(path: string, body?: unknown): Promise<Answer<T>> =>
    call<T>(path, { method: "POST", body: body === undefined ? undefined : JSON.stringify(body) });

export const api = {
    mode: () => call<{ code: boolean; password: boolean }>("api/auth/mode"),
    challenge: () => post<{ id: string; nonce: string }>("api/auth/challenge"),
    verify: (challengeId: string, proof: string) => post("api/auth/verify", { challengeId, proof }),
    password: (password: string) => post("api/auth/password", { password }),

    state: () => call<SetupState>("api/state"),
    step: (answer: unknown) => post<SetupState>("api/setup/step", answer),
    interrogate: () => post<SetupState>("api/setup/interrogate"),
    apply: () => post<SetupState>("api/setup/apply"),
    setPanelPassword: (password: string) => post("api/panel/password", { password }),

    overview: () => call<PanelOverview>("api/panel/overview"),
    logs: (service: string) => call<LogAnswer>(`api/panel/services/${encodeURIComponent(service)}/logs`),
    control: (service: string, action: string) =>
        post(`api/panel/services/${encodeURIComponent(service)}/${encodeURIComponent(action)}`),
    backup: () => post("api/panel/backup"),
    plan: (version: string) => call<UpgradePlan>(`api/panel/upgrade/plan?version=${encodeURIComponent(version)}`),
    upgrade: (version: string, confirm: boolean) => post("api/panel/upgrade", confirm ? { version, confirm } : { version }),
};

/**
 * The code is never sent.
 *
 * The server issues a nonce, this proves knowledge of the code against it, and a session is issued
 * against the proof — so a channel that turns out not to have been private did not carry the credential
 * to this machine.
 *
 * `createHmac("sha256", code).update(nonce)` on the other side takes the nonce as the *string* it is,
 * not as the bytes it encodes, so this signs the same: the ASCII of the hex, not the hex decoded.
 */
export async function proofFor(code: string, nonce: string): Promise<string> {
    const encoder = new TextEncoder();

    const key = await crypto.subtle.importKey("raw", encoder.encode(code), { name: "HMAC", hash: "SHA-256" }, false, [
        "sign",
    ]);

    const signature = await crypto.subtle.sign("HMAC", key, encoder.encode(nonce));

    return [...new Uint8Array(signature)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

/* ------------------------------------------------------------------------------------------------
 * What the server says.
 *
 * Declared here rather than imported from `src/`: this is a browser bundle and those modules pull in
 * node's filesystem and the docker socket. The shapes are the API's, and the API is the seam — a type
 * shared across it would compile the server into the page.
 * ---------------------------------------------------------------------------------------------- */

export type Stage =
    | "awaiting-configuration"
    | "ready"
    | "applying"
    | "invalid"
    | "configured"
    | "starting"
    | "running"
    | "degraded"
    | "blocked"
    | "unavailable";

export interface RoleSummary {
    readonly id: string;
    readonly kind: "silo" | "client";
    readonly grains: number;
    readonly features: number;
    readonly description: string;
}

export interface ServiceStatus {
    readonly service: string;
    readonly state: string;
    readonly health?: string;
    readonly exitCode?: number;
}

export interface Rejection {
    readonly field: string;
    readonly problem: string;
}

export interface SetupState {
    readonly stage: Stage;
    readonly answers: Record<string, unknown> & {
        domain?: string;
        serverVersion?: string;
        roles?: readonly string[];
        storage?: { kind: string; endpoint?: string; bucket?: string; region?: string };
        traffic?: { kind: string; voiceHost?: string };
        voice?: boolean;
    };
    readonly missing: readonly string[];
    readonly policy: { required: readonly string[]; optional: readonly string[]; refused: readonly string[] };
    readonly credentials: readonly string[];
    readonly image?: {
        reference: string;
        version?: { value: string };
        pairing?: { ok: boolean; detail?: string };
        roles: readonly RoleSummary[];
    };
    readonly warnings: readonly string[];
    readonly restarted: boolean;
    readonly note?: string;
    readonly written?: readonly { path: string; mode: number }[];
    readonly validation?: readonly { role: string; ok: boolean; output: string }[];
    readonly panel?: { url: string; note: string };
    readonly progress?: readonly string[];
    readonly services?: readonly ServiceStatus[];
    readonly problem?: string;
    readonly retired?: boolean;
    readonly panelPassword?: boolean;
}

export type CertificateReport =
    | { host: string; purpose: string; verdict: "not-applicable" | "unreadable"; why: string }
    | {
          host: string;
          purpose: string;
          verdict: "expired" | "not-yet-valid" | "expiring" | "valid";
          certificate: { issuer?: { commonName?: string } };
          renewal: string;
          days: number;
          coverage: { covers: true; by: string } | { covers: false; why: string };
      };

export interface AppliedVersion {
    readonly version: string;
    readonly at: string;
    readonly outcome: string;
}

export interface PanelOverview {
    readonly domain?: string;
    readonly services: readonly ServiceStatus[];
    readonly controllable: readonly string[];
    readonly certificates: readonly CertificateReport[];
    readonly backups: readonly { name: string; bytes: number }[];
    readonly version: { current?: AppliedVersion; previous?: AppliedVersion };
}

export interface UpgradePlan {
    readonly direction?: string;
    readonly crossing?: string;
    readonly backupFirst?: boolean;
    readonly warnings?: readonly string[];
    readonly images?: readonly { repository?: string; name?: string; from?: string; to?: string }[];
    readonly to?: { value?: string };
    readonly judgement?: { ok: boolean; standing?: "settled" | "unproven"; problem?: string };
}

export interface LogLine {
    readonly stream: "stdout" | "stderr";
    readonly text: string;
}

export interface LogAnswer {
    readonly lines?: readonly LogLine[];
    readonly truncated?: boolean;
    readonly problem?: string;
}
