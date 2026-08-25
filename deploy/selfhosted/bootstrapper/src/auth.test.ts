import { describe, expect, test } from "bun:test";
import { BootstrapAuth, proofFor } from "./auth";

const CODE = "quiet-harbour-42";

/** Long enough for the panel's own rule, and recognisably not the code. */
const PASSWORD = "operator-panel-password";

describe("the bootstrap code", () => {
    test("a correct proof gets a session", () => {
        const auth = new BootstrapAuth(CODE);
        const challenge = auth.challenge();

        const result = auth.verify(challenge.id, proofFor(CODE, challenge.nonce));

        expect(result.ok).toBe(true);
        expect(result.ok && auth.holds(result.session.token)).toBe(true);
    });

    test("a wrong code does not", () => {
        const auth = new BootstrapAuth(CODE);
        const challenge = auth.challenge();

        const result = auth.verify(challenge.id, proofFor("loud-harbour-42", challenge.nonce));

        expect(result).toEqual({ ok: false, reason: "mismatch" });
    });

    /**
     * The nonce is what makes a proof worth nothing twice. Without this, anything that saw one request
     * could replay it forever — which is the whole reason the code is not simply posted.
     */
    test("a proof cannot be replayed", () => {
        const auth = new BootstrapAuth(CODE);
        const challenge = auth.challenge();
        const proof = proofFor(CODE, challenge.nonce);

        expect(auth.verify(challenge.id, proof).ok).toBe(true);
        expect(auth.verify(challenge.id, proof)).toEqual({ ok: false, reason: "unknown-challenge" });
    });

    /**
     * A challenge is spent by being answered, right or wrong.
     *
     * Left alive after a failure it becomes an oracle: one nonce, unlimited guesses, and the lockout
     * below never fires because each guess can use a fresh challenge id anyway. Consuming it means every
     * guess costs a round trip and a new nonce.
     */
    test("a failed attempt spends the challenge too", () => {
        const auth = new BootstrapAuth(CODE);
        const challenge = auth.challenge();

        auth.verify(challenge.id, proofFor("wrong", challenge.nonce));

        const second = auth.verify(challenge.id, proofFor(CODE, challenge.nonce));

        expect(second).toEqual({ ok: false, reason: "unknown-challenge" });
    });

    test("a challenge does not outlive its window", () => {
        const auth = new BootstrapAuth(CODE);
        const start = 1_000_000;
        const challenge = auth.challenge(start);

        const result = auth.verify(challenge.id, proofFor(CODE, challenge.nonce), start + 3 * 60 * 1000);

        expect(result).toEqual({ ok: false, reason: "expired" });
    });

    test("guessing is locked out after a handful of tries", () => {
        const auth = new BootstrapAuth(CODE);
        const now = 5_000_000;

        for (let attempt = 0; attempt < 5; attempt++) {
            const challenge = auth.challenge(now);
            auth.verify(challenge.id, proofFor(`guess-${attempt}`, challenge.nonce), now);
        }

        const challenge = auth.challenge(now);
        const locked = auth.verify(challenge.id, proofFor(CODE, challenge.nonce), now);

        expect(locked.ok).toBe(false);
        expect(locked.ok === false && locked.reason).toBe("locked");

        // And it lets go by itself, so somebody who can reach the port cannot keep the operator out.
        const later = now + 31 * 1000;
        const fresh = auth.challenge(later);

        expect(auth.verify(fresh.id, proofFor(CODE, fresh.nonce), later).ok).toBe(true);
    });

    /**
     * Setup ends the code's life. An installer that leaves a permanently valid bootstrap credential on a
     * public box has replaced one problem with a worse one.
     */
    test("retiring stops new sessions and keeps existing ones", async () => {
        const auth = new BootstrapAuth(CODE);
        const challenge = auth.challenge();
        const result = auth.verify(challenge.id, proofFor(CODE, challenge.nonce));

        expect(result.ok).toBe(true);

        auth.adoptPassword(await Bun.password.hash(PASSWORD, { algorithm: "argon2id" }));
        auth.retire();

        const after = auth.challenge();

        expect(auth.verify(after.id, proofFor(CODE, after.nonce))).toEqual({ ok: false, reason: "spent" });
        expect(result.ok && auth.holds(result.session.token)).toBe(true);
    });

    /**
     * The refusal that keeps the operator from locking themselves out of their own machine.
     *
     * Retiring the code is right — it is printed in a terminal and left in a file. Retiring it with
     * nothing behind it removes the only way into a panel that holds the docker socket, and nothing
     * short of editing files on the host would undo that. So the class refuses rather than trusting
     * every future caller to check first.
     */
    test("the code cannot be retired while it is the only way in", () => {
        const auth = new BootstrapAuth(CODE);

        expect(() => auth.retire()).toThrow(/no way in/);
        expect(auth.retired).toBe(false);

        // Still open, which is the point: the refusal left the panel reachable.
        const challenge = auth.challenge();

        expect(auth.verify(challenge.id, proofFor(CODE, challenge.nonce)).ok).toBe(true);
    });

    describe("the password", () => {
        async function withPassword(): Promise<BootstrapAuth> {
            const auth = new BootstrapAuth(CODE);

            auth.adoptPassword(await Bun.password.hash(PASSWORD, { algorithm: "argon2id" }));

            return auth;
        }

        test("opens the panel, and a wrong one does not", async () => {
            const auth = await withPassword();

            expect((await auth.verifyPassword(PASSWORD)).ok).toBe(true);
            expect((await auth.verifyPassword("not-the-password")).ok).toBe(false);
        });

        /** It is the credential that outlives setup, so it has to keep working once the code is gone. */
        test("still opens the panel after the code is retired", async () => {
            const auth = await withPassword();

            auth.retire();

            expect(auth.retired).toBe(true);
            expect((await auth.verifyPassword(PASSWORD)).ok).toBe(true);
        });

        test("is refused when none was ever set", async () => {
            const auth = new BootstrapAuth(CODE);

            expect(await auth.verifyPassword(PASSWORD)).toEqual({ ok: false, reason: "unknown-challenge" });
        });

        /**
         * One door's worth of patience, not two.
         *
         * The lockout is shared with the code deliberately: an attacker who could spend five attempts on
         * each in turn would have twice the budget for the same five seconds of work.
         */
        test("shares the lockout with the code", async () => {
            const auth = await withPassword();

            for (let attempt = 0; attempt < 5; attempt++) await auth.verifyPassword("wrong");

            const challenge = auth.challenge();
            const blocked = auth.verify(challenge.id, proofFor(CODE, challenge.nonce));

            expect(blocked.ok).toBe(false);
            expect(blocked.ok === false && blocked.reason).toBe("locked");
        });
    });

    test("a proof that is not hex is refused rather than thrown at", () => {
        const auth = new BootstrapAuth(CODE);
        const challenge = auth.challenge();

        expect(auth.verify(challenge.id, "not hex at all")).toEqual({ ok: false, reason: "mismatch" });
    });

    test("an empty code is a broken install, not a permissive one", () => {
        expect(() => new BootstrapAuth("   ")).toThrow();
    });
});
