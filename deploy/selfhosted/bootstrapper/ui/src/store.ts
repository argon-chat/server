import { reactive } from "vue";
import { api, proofFor, type LogAnswer, type PanelOverview, type SetupState, type UpgradePlan } from "./api";

/**
 * Everything the page holds, and it is deliberately almost nothing.
 *
 * ## The one architectural rule
 *
 * There is no client-side wizard. Every screen is a function of what `GET /api/state` returned, and the
 * only things kept here are the session the browser holds, what is currently typed, and which request
 * is in flight. Close the tab in the middle of an install, come back an hour later from another
 * machine, and the same screen is there — because it was never here in the first place.
 *
 * `draft` is the exception that proves it: a field the server rejected has to keep what the operator
 * wrote rather than snapping back to the last accepted answer. It is what is being typed, not what is
 * known.
 */

export type Busy = undefined | "signin" | "step" | "interrogate" | "password" | "apply" | "plan" | "upgrade" | "backup" | "logs" | `service:${string}`;

export interface Draft {
    code?: string;
    domain?: string;
    serverVersion?: string;
    roles?: string[];
    storageKind?: string;
    endpoint?: string;
    bucket?: string;
    region?: string;
    accessKey?: string;
    secretKey?: string;
    trafficKind?: string;
    voiceHost?: string;
    voice?: boolean;
    panelPassword?: string;
    signInPassword?: string;
    upgradeTo?: string;
    upgradeConfirmed?: boolean;
}

export const store = reactive({
    authed: false,

    /** Which sign-in doors the server says are open. Undefined until asked. */
    mode: undefined as { code: boolean; password: boolean } | undefined,

    state: undefined as SetupState | undefined,

    /**
     * What the panel shows once the instance is up.
     *
     * Separate from `state` because it comes from a separate route, and that route costs real work — a
     * TLS handshake, a directory walk, the daemon. Folding it into the state the wizard polls every two
     * seconds during an install would make the install pay for all of it.
     */
    overview: undefined as PanelOverview | undefined,

    draft: {} as Draft,
    rejections: {} as Record<string, string | undefined>,

    busy: undefined as Busy,
    signInError: undefined as string | undefined,
    lockedUntil: 0,

    logs: undefined as ({ service: string } & LogAnswer) | undefined,
    plan: undefined as UpgradePlan | undefined,
    planProblem: undefined as string | undefined,
    serviceProblem: undefined as string | undefined,
    backupProblem: undefined as string | undefined,
});

let polling: ReturnType<typeof setInterval> | undefined;

export function stopPolling(): void {
    if (polling !== undefined) clearInterval(polling);
    polling = undefined;
}

/**
 * While an apply runs, the request that started it is still open — for minutes.
 *
 * This polls beside it so the log moves and a second tab sees the same thing, and because a browser
 * that gives up waiting takes the response with it but not the install.
 */
export function poll(): void {
    stopPolling();
    polling = setInterval(() => void refresh(), 2000);
}

export async function refresh(): Promise<void> {
    const response = await api.state();

    if (response.status === 401) {
        store.authed = false;
        stopPolling();

        // Which door to offer. Asked here rather than once at startup because the answer changes during
        // the session this page is holding: the install retires the code, and a tab left open across
        // that has to show the password field on its next visit rather than a code that is now dead.
        const mode = await api.mode();

        if (mode.ok) store.mode = mode.body;

        return;
    }

    if (!response.ok) return;

    store.authed = true;
    store.state = response.body;

    // Fetched only once the instance is up, and only when not already held: this is the expensive half
    // and the wizard has no use for it.
    if (response.body?.stage === "running" && store.overview === undefined && store.busy === undefined)
        await refreshOverview();
}

export async function refreshOverview(): Promise<void> {
    const response = await api.overview();

    if (response.ok) store.overview = response.body;
}

/** One step of the wizard, taken whole or not at all. */
export async function submit(answer: unknown): Promise<void> {
    store.busy = "step";

    const response = await api.step(answer);

    store.busy = undefined;

    if (response.status === 400 && Array.isArray((response.body as { rejections?: unknown })?.rejections)) {
        const rejections = (response.body as unknown as { rejections: { field: string; problem: string }[] }).rejections;

        store.rejections = Object.fromEntries(rejections.map((r) => [r.field, r.problem]));

        return;
    }

    if (response.ok) {
        store.rejections = {};
        store.state = response.body;
    }
}

/**
 * Signing in with the code.
 *
 * The lockout countdown is a courtesy rather than the rule: the server counts, and thirty seconds is
 * what it applies. Without it the field is simply dead with nothing on screen saying why.
 */
export async function signInWithCode(code: string): Promise<void> {
    store.busy = "signin";
    store.signInError = undefined;

    try {
        const challenge = await api.challenge();

        if (challenge.status === 410) {
            store.signInError = "Setup on this machine is already finished, so this code no longer opens anything.";

            return;
        }

        if (!challenge.ok || challenge.body === undefined) {
            store.signInError = "Could not start a sign-in. The panel answered, but not with a challenge.";

            return;
        }

        const verified = await api.verify(challenge.body.id, await proofFor(code, challenge.body.nonce));

        if (verified.ok) {
            store.draft.code = undefined;
            await refresh();

            return;
        }

        if (verified.status === 429) {
            store.lockedUntil = Date.now() + 30_000;
            store.signInError = "Too many wrong codes. The panel has stopped answering for a moment.";
        } else if (verified.status === 410) {
            store.signInError = "That attempt sat too long and expired. Try the code again.";
        } else {
            store.signInError = "That code was not accepted.";
        }
    } catch {
        store.signInError = "Could not reach the panel. It may still be starting.";
    } finally {
        store.busy = undefined;
    }
}

/** Signing in with the password, which is every visit after the install finished. */
export async function signInWithPassword(password: string): Promise<void> {
    store.busy = "signin";
    store.signInError = undefined;

    try {
        const response = await api.password(password);

        if (response.ok) {
            store.draft.signInPassword = undefined;
            await refresh();

            return;
        }

        if (response.status === 429) {
            store.lockedUntil = Date.now() + 30_000;
            store.signInError = "Too many wrong answers. The panel has stopped answering for a moment.";
        } else {
            store.signInError = "That password was not accepted.";
        }
    } catch {
        store.signInError = "Could not reach the panel.";
    } finally {
        store.busy = undefined;
    }
}
