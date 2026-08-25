import { afterEach, describe, expect, test } from "bun:test";
import { proofFor } from "./auth";
import { createServer } from "./server";

const CODE = "quiet-harbour-42";

let server: ReturnType<typeof createServer> | undefined;

/** Port 0 so the kernel picks one: these run beside everything else in the suite. */
function start(): string {
    server = createServer({ code: CODE, hostname: "127.0.0.1", port: 0 });
    return `http://127.0.0.1:${server.server!.port}`;
}

afterEach(() => {
    server?.stop();
    server = undefined;
});

async function signIn(base: string, code = CODE): Promise<Response> {
    const challenge = (await (await fetch(`${base}/api/auth/challenge`, { method: "POST" })).json()) as {
        id: string;
        nonce: string;
    };

    return fetch(`${base}/api/auth/verify`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ challengeId: challenge.id, proof: proofFor(code, challenge.nonce) }),
    });
}

describe("the setup server", () => {
    test("health answers without a session, because the installer polls it before there is one", async () => {
        const base = start();

        const response = await fetch(`${base}/api/health`);

        expect(response.status).toBe(200);
        expect(await response.text()).toBe("ok");
    });

    test("state is closed to callers without a session", async () => {
        const base = start();

        expect((await fetch(`${base}/api/state`)).status).toBe(401);
    });

    test("the right code opens it", async () => {
        const base = start();

        const signedIn = await signIn(base);
        const cookie = signedIn.headers.get("set-cookie");

        expect(signedIn.status).toBe(200);
        expect(cookie).toBeTruthy();

        const state = await fetch(`${base}/api/state`, { headers: { cookie: cookie! } });

        expect(state.status).toBe(200);

        // What the state says depends on the setup machine, which this server was not given one of —
        // `setup.test.ts` is where a wired one is driven through its stages. What belongs to this test is
        // that a session opens the route at all, and that the bootstrap code is still live.
        expect(await state.json()).toMatchObject({ retired: false });
    });

    test("the wrong code does not", async () => {
        const base = start();

        const response = await signIn(base, "loud-harbour-42");

        expect(response.status).toBe(401);
        expect(response.headers.get("set-cookie")).toBeNull();
    });

    /**
     * The cookie is the thing an injected script would go looking for, and `HttpOnly` is what stops it
     * finding it. Asserted rather than assumed because it is one attribute in a string, and a string is
     * easy to rewrite without noticing what came off it.
     */
    test("the session cookie is not readable from the page", async () => {
        const base = start();

        const cookie = (await signIn(base)).headers.get("set-cookie") ?? "";

        expect(cookie).toContain("HttpOnly");
        expect(cookie).toContain("SameSite=Strict");
    });

    /**
     * `Secure` only when there is TLS to be secure over. Set unconditionally, a browser talking to a
     * plain-HTTP local install drops the cookie and the operator cannot sign in, with nothing on screen
     * to say why.
     */
    test("Secure is left off when there is no TLS to be secure over", async () => {
        const base = start();

        expect((await signIn(base)).headers.get("set-cookie") ?? "").not.toContain("Secure");
    });

    test("a body that is not what the route expects is a 400, not a 500", async () => {
        const base = start();

        const response = await fetch(`${base}/api/auth/verify`, {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: "{ this is not json",
        });

        expect(response.status).toBe(400);
    });

    /**
     * One answer for every rejection. "Your challenge expired" and "your proof was wrong" are different
     * facts, and telling them apart is worth something to somebody guessing and nothing to an operator,
     * who retries either way.
     */
    test("rejections do not say which kind they were", async () => {
        const base = start();

        const unknown = await fetch(`${base}/api/auth/verify`, {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({ challengeId: "no-such-challenge", proof: "00" }),
        });

        const wrong = await signIn(base, "loud-harbour-42");

        expect(unknown.status).toBe(401);
        expect(await unknown.json()).toEqual({ error: "rejected" });
        expect(await wrong.json()).toEqual({ error: "rejected" });
    });

    test("guessing gets rate limited, and says for how long", async () => {
        const base = start();

        for (let attempt = 0; attempt < 5; attempt++) await signIn(base, `guess-${attempt}`);

        const locked = await signIn(base);

        expect(locked.status).toBe(429);
        expect(Number(locked.headers.get("retry-after"))).toBeGreaterThan(0);
    });

    test("an unknown route is a 404 rather than anything more interesting", async () => {
        const base = start();

        expect((await fetch(`${base}/../etc/passwd`)).status).toBe(404);
    });

});

/**
 * Well-formed JSON of the wrong shape is refused before the handler.
 *
 * This is the case the framework was brought in for. It used to be a pair of hand-written `typeof`
 * checks, which was fine for two fields on one route and is where a missed one hides once the wizard
 * has a dozen routes carrying whatever the operator typed. The schema sits beside the route and a body
 * that does not match never reaches the code.
 */
describe("bodies that are not the shape the route declared", () => {
    const send = (base: string, body: unknown) =>
        fetch(`${base}/api/auth/verify`, {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify(body),
        });

    test("a field of the wrong type is a 400", async () => {
        const base = start();

        expect((await send(base, { challengeId: 123, proof: "aa" })).status).toBe(400);
    });

    test("a missing field is a 400", async () => {
        const base = start();

        expect((await send(base, { proof: "aa" })).status).toBe(400);
    });

    /**
     * An empty string is not a credential, and letting one through would spend a challenge for nothing —
     * the verify path consumes it whether or not the proof was right.
     */
    test("an empty field is a 400", async () => {
        const base = start();

        expect((await send(base, { challengeId: "", proof: "" })).status).toBe(400);
    });
});
