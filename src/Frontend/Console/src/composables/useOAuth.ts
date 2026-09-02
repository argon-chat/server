import delay from "@/lib/delay";
import { logger } from "@argon/core";

const OAUTH_URL = "https://aegis.argon.gl/";
const TOKEN_URL = "https://aegis.argon.gl/connect/token";
const USERINFO_URL = "https://aegis.argon.gl/connect/userinfo";

/** What the console has always asked for, and cannot work without. */
const BASE_SCOPES = ["identity", "offline_access"];

/**
 * The scope that makes `/connect/userinfo` return `avatarUrl`.
 *
 * Kept apart from the base list because the allow-list lives with the client registration, in the
 * database, and Aegis rejects the whole authorization when a client asks for a scope it was not
 * granted. Asking unconditionally would therefore turn a registration that has not been updated
 * yet into a console nobody can sign into, so a refusal is remembered and the next attempt goes
 * without it: the avatar falls back to initials, the sign-in still works.
 */
const AVATAR_SCOPE = "user.read";
const AVATAR_SCOPE_REFUSED = "avatar_scope_refused";

function requestedScopes() {
    return localStorage.getItem(AVATAR_SCOPE_REFUSED) === "1"
        ? BASE_SCOPES
        : [...BASE_SCOPES, AVATAR_SCOPE];
}

const CLIENT_ID = "F474304DE91F44F7E66C9085503C47CD";
const REDIRECT_URI = import.meta.env.DEV ? "https://localhost:5005/callback" : "https://console.argon.gl/callback";

export async function generatePKCE() {
    const randomBytes = new Uint8Array(32)
    crypto.getRandomValues(randomBytes)

    const codeVerifier = btoa(String.fromCharCode(...randomBytes))
        .replace(/\+/g, "-")
        .replace(/\//g, "_")
        .replace(/=/g, "")

    const encoder = new TextEncoder()
    const data = encoder.encode(codeVerifier)

    const digest = await crypto.subtle.digest("SHA-256", data)
    const hashArray = Array.from(new Uint8Array(digest))
    const base64 = btoa(String.fromCharCode(...hashArray))
        .replace(/\+/g, "-")
        .replace(/\//g, "_")
        .replace(/=/g, "")

    const codeChallenge = base64

    return { codeVerifier, codeChallenge }
}

export async function ensureAuthenticated() {
    const url = new URL(window.location.href);
    const code = url.searchParams.get("code");

    // Aegis refuses the entire authorization over one disallowed scope, so this is what a client
    // registration that has not been given the avatar scope looks like from here: no code, an
    // error, and nothing to proceed with until the optional scope is dropped.
    if (url.searchParams.get("error") === "invalid_scope") {
        logger.warn("Aegis refused a requested scope; retrying without the avatar scope");
        localStorage.setItem(AVATAR_SCOPE_REFUSED, "1");

        url.searchParams.delete("error");
        url.searchParams.delete("error_description");
        window.history.replaceState({}, "", url.toString());

        await redirectToOAuth();
        return;
    }

    const token = localStorage.getItem("access_token");

    if (code) {
        logger.warn("Exchanging code for token...");
        await exchangeCodeForToken(code);

        url.searchParams.delete("code");
        window.history.replaceState({}, "", url.toString());

        startTokenAutoRefresh();
        return;
    }

    if (!token) {
        logger.warn("No access token, redirecting to OAuth...");
        await redirectToOAuth();
        return;
    }

    if (isTokenExpired()) {
        logger.warn("Access token expired, refreshing...");
        const ok = await refreshAccessToken();
        if (ok) return;

        await redirectToOAuth();
        return;
    }

    startTokenAutoRefresh();
}
async function redirectToOAuth() {
    const { codeVerifier, codeChallenge } = await generatePKCE()

    localStorage.setItem("pkce_verifier", codeVerifier)

    const params = new URLSearchParams({
        client_id: CLIENT_ID,
        redirect_uri: REDIRECT_URI,
        response_type: "code",
        scope: requestedScopes().join(" "),
        code_challenge: codeChallenge,
        code_challenge_method: "S256",
    })

    window.location.href = `${OAUTH_URL}?${params.toString()}`

    await delay(5000);
}
export async function exchangeCodeForToken(code: string) {
    const codeVerifier = localStorage.getItem("pkce_verifier");
    if (!codeVerifier)
        throw new Error("PKCE verifier missing");

    const form = new URLSearchParams({
        grant_type: "authorization_code",
        code,
        redirect_uri: REDIRECT_URI,
        client_id: CLIENT_ID,
        code_verifier: codeVerifier,
    });

    const res = await fetch(TOKEN_URL, {
        method: "POST",
        body: form,
        headers: { "Content-Type": "application/x-www-form-urlencoded" },
    });
    const data = await res.json();

    if (!data.access_token)
        throw new Error("OAuth error: no access_token returned");

    saveTokens(data);

    localStorage.removeItem("pkce_verifier");
}

function saveTokens(data: any) {
    const now = Math.floor(Date.now() / 1000);

    localStorage.setItem("access_token", data.access_token);
    localStorage.setItem("refresh_token", data.refresh_token ?? "");
    localStorage.setItem("access_token_exp", String(now + (data.expires_in ?? 3600)));
}

export function isTokenExpired(): boolean {
    const exp = Number(localStorage.getItem("access_token_exp"));
    if (!exp) return true;

    const now = Math.floor(Date.now() / 1000);
    return now >= exp - 15;
}

export function startTokenAutoRefresh() {
    setInterval(async () => {
        if (isTokenExpired()) {
            await refreshAccessToken();
        }
    }, 100_000);
}
export async function refreshAccessToken(): Promise<boolean> {
    const refreshToken = localStorage.getItem("refresh_token");
    if (!refreshToken) {
        console.warn("No refresh_token");
        return false;
    }

    const form = new URLSearchParams({
        grant_type: "refresh_token",
        refresh_token: refreshToken,
        client_id: CLIENT_ID,
    });

    try {
        const res = await fetch(TOKEN_URL, {
            method: "POST",
            body: form,
            headers: { "Content-Type": "application/x-www-form-urlencoded" },
        });
        const data = await res.json();

        if (!data.access_token) {
            return false;
        }

        saveTokens(data);
        return true;
    } catch (err) {
        console.error("Ошибка обновления токена", err);
        return false;
    }
}
/**
 * The signed-in user as Aegis describes them.
 *
 * Read from `/connect/userinfo` rather than from the token, because the token carries the file id
 * of an avatar and an id is only useful to something that already knows how this deployment
 * addresses files. `avatarUrl` is the address Aegis is configured to publish — proxied through the
 * API, cached hard, and carrying the token that keeps the endpoint from being a scraping surface —
 * so it is the one thing here that stays correct when file storage moves.
 *
 * Absent when the user has no avatar, and null when the scope was not granted or userinfo could
 * not be reached; all of them mean the same to a caller, which is to fall back to initials.
 */
export type AegisUserInfo = {
    sub: string;
    preferred_username?: string;
    displayName?: string;
    avatarUrl?: string;
};

export async function fetchUserInfo(): Promise<AegisUserInfo | null> {
    const token = localStorage.getItem("access_token");

    if (!token) return null;

    try {
        const res = await fetch(USERINFO_URL, { headers: { Authorization: `Bearer ${token}` } });

        if (!res.ok) {
            logger.warn(`userinfo returned ${res.status}`);
            return null;
        }

        return await res.json() as AegisUserInfo;
    } catch (err) {
        logger.error(err);
        return null;
    }
}
