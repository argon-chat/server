import { Elysia, t } from "elysia";
import type { BunFile } from "bun";
import { BootstrapAuth } from "./auth";
import { refuseCredential } from "./credential";
import type { Panel } from "./panel/facade";
import { setupFromEnvironment, type Setup, type SetupState } from "./setup";

/**
 * The setup server.
 *
 * Elysia rather than raw `Bun.serve`, and the reason is the wizard. Three auth endpoints are comfortable
 * to hand-roll; the setup step is a union of storage answers beside a union of traffic shapes, every
 * field of it typed by the operator, and hand-written `typeof` checks across that is where a missed one
 * hides. Here the shape is declared beside the route and a body that does not match never reaches the
 * handler.
 *
 * The routes are thin on purpose. Deciding anything is `setup.ts`, which knows nothing about HTTP, and
 * these handlers do one thing each: map an outcome onto a status code. A rule that lives here instead is
 * a rule that cannot be tested without a socket.
 *
 * Everything security-shaped still lives in `auth.ts`, which knows nothing about HTTP — that is what let
 * the transport be swapped without touching it, and it is worth keeping true.
 *
 * TLS is not configured here. The install script establishes it before this process starts — by one of
 * the three paths in the design — and hands the certificate in. A server that could start without one
 * would eventually be started without one.
 *
 * ## What this file does not do any more
 *
 * It used to own a handover: this process held the host's `:443`, and the apply released it so that
 * Traefik could bind it, then stopped the container once the last response had drained. Traefik is now
 * started *before* setup and this process has never held a public port, so all of that is gone — and
 * with it the rule about how the install script had to publish this container. The panel keeps serving
 * after a successful apply, which is what §10 wanted of it all along.
 */

const SESSION_COOKIE = "argon_setup";

/**
 * Reading and setting the password that outlives setup. See `credential.ts` for what is stored.
 *
 * `write` returns the hash it stored so the caller can start accepting it without going back to the
 * disk — and so that "written" and "in effect" cannot come apart.
 */
export interface PanelCredentials {
    read(): Promise<string | undefined>;
    write(password: string): Promise<string>;
}

/**
 * Where the built page lives inside the image, resolved from this file rather than from the working
 * directory — `docker run … emit-bootstrap` and the panel start the same image from different places.
 *
 * `ui/dist`, not `ui`: the page is a Vue application and what ships is what Vite emitted. The build
 * names its output `index.html`, `app.js` and `app.css` with no content hash, which is what lets the
 * routes below stay an enumeration — see `ui/vite.config.ts` for why that matters here.
 */
const UI = `${import.meta.dir}/../ui/dist`;

/** One file, by name, from one directory. Both halves are constants; see the routes that use it. */
function file(directory: string, name: string): BunFile {
    return Bun.file(`${directory}/${name}`);
}

export interface ServerConfig {
    /** The bootstrap code, already read from disk by the caller. */
    readonly code: string;

    readonly hostname?: string;
    readonly port?: number;

    /** PEM certificate and key. Absent only in tests, which do not go over a wire. */
    readonly tls?: { readonly cert: string; readonly key: string };

    /**
     * The setup in progress. Given by the caller so a test can hand in one with fake ports, and falling
     * back to the environment so the routes are not dark until `main.ts` grows the line that builds one —
     * see {@link setupFromEnvironment}.
     */
    readonly setup?: Setup;

    /**
     * Where the panel's own password is kept, and how it is set.
     *
     * A port because the alternative is this module knowing the install root, hashing, and the mode a
     * credential file has to be written with — three things `credential.ts` already owns. Absent in a
     * server with nowhere to write, which is a server that cannot finish an install either.
     */
    readonly credentials?: PanelCredentials;

    /**
     * The four panel modules, wired. See `panel/facade.ts`.
     *
     * Optional for the same reason {@link setup} is: a test that only drives the wizard has no use for
     * a docker socket, and the panel routes answer 503 without one rather than being absent — an
     * operator who reaches a dead route deserves the same sentence as one who reaches a dead wizard.
     */
    readonly panel?: Panel;
}

/**
 * What the setup routes answer when there is no setup machine.
 *
 * A stage of its own rather than an empty wizard, because the two are indistinguishable to an operator
 * and only one of them is something they can do anything about. The mutating routes refuse outright; the
 * state route still answers, because a UI that cannot read a state cannot show the reason either.
 */
const UNAVAILABLE = {
    error: "setup-unavailable",
    problem:
        "the setup machine was not wired into this server: no configuration directory was named, so there is nowhere to write conf.d and the secrets file. That is a broken install rather than an answer that was wrong.",
} as const;

const UNAVAILABLE_STATE: SetupState = {
    stage: "unavailable",
    answers: {},
    missing: [],

    // Empty rather than the real lists: this state exists because there is no setup machine, so there
    // is nothing to choose roles for and a page offering the choice would be offering it into a void.
    policy: { required: [], optional: [], refused: [] },
    credentials: [],
    warnings: [],
    restarted: false,
    problem: UNAVAILABLE.problem,
};

/**
 * The bounds on the wizard's body.
 *
 * Declared rather than checked, which is what the framework is here for, and bounded rather than merely
 * typed: every string below ends up in a file on the operator's disk, and a hostile body is the one thing
 * that reaches this process before there is an account to blame it on. The lengths are what the values
 * genuinely are — a hostname is 253 bytes, a bucket name is 63 — so nothing legitimate is refused.
 */
const HOST = t.String({ minLength: 1, maxLength: 253 });

const STEP = t.Object({
    domain: t.Optional(HOST),
    serverVersion: t.Optional(t.String({ minLength: 1, maxLength: 200 })),
    roles: t.Optional(t.Array(t.String({ minLength: 1, maxLength: 64 }), { maxItems: 32 })),
    storage: t.Optional(
        t.Union([
            t.Object({ kind: t.Literal("local") }),
            t.Object({
                kind: t.Literal("s3"),
                endpoint: t.String({ minLength: 1, maxLength: 253 }),
                bucket: t.String({ minLength: 1, maxLength: 63 }),
                region: t.Optional(t.String({ maxLength: 32 })),

                // The two fields in this whole API that carry a secret. They arrive here because they are
                // one screen to the operator, and `Setup.submit` takes them straight back out of the
                // answer — nothing downstream of it can echo what it never held.
                accessKey: t.Optional(t.String({ minLength: 1, maxLength: 512 })),
                secretKey: t.Optional(t.String({ minLength: 1, maxLength: 512 })),
            }),
        ]),
    ),
    traffic: t.Optional(
        t.Union([
            t.Object({ kind: t.Literal("own-certificate") }),
            t.Object({ kind: t.Literal("cloudflare-proxied"), voiceHost: t.Optional(HOST) }),
            t.Object({ kind: t.Literal("cloudflare-tunnel") }),
            t.Object({ kind: t.Literal("lets-encrypt") }),
        ]),
    ),
    voice: t.Optional(t.Boolean()),
});

export function createServer(config: ServerConfig) {
    const auth = new BootstrapAuth(config.code);
    const secure = config.tls !== undefined;
    const setup = config.setup ?? setupFromEnvironment();
    const credentials = config.credentials;
    const panel = config.panel;

    // Whatever password was set on an earlier run. Read once, here, rather than per attempt: a panel
    // that hits the disk on every sign-in gives an attacker a way to make it do work for free.
    void credentials?.read().then((hash) => auth.adoptPassword(hash));

    /**
     * One session cookie, issued the same way whichever door was opened.
     *
     * SameSite=Strict because nothing links here and nothing should be able to make a browser act on
     * this from elsewhere. HttpOnly because the token is what an injected script would go looking for.
     * Secure only when there is TLS to be secure over: setting it on a plain-HTTP local install makes
     * the browser drop the cookie and the operator cannot sign in, with nothing on screen to say why.
     */
    const issue = (cookie: Record<string, { set(options: Record<string, unknown>): void } | undefined>, token: string): void => {
        cookie[SESSION_COOKIE]?.set({ value: token, path: "/", httpOnly: true, sameSite: "strict", secure });
    };

    const app = new Elysia()
        /**
         * Registered before the routes, and that is not style.
         *
         * Elysia binds lifecycle hooks in declaration order: an `onError` added after a route does not
         * apply to it. Put this at the bottom of the chain and a body that fails its schema comes back
         * as the framework's own 422 with a validation dump in it, while this handler sits there looking
         * correct. It was written that way first, and the test for a wrong-shaped body is what caught it.
         */
        .onError(({ code, status }) => {
            // A body that does not match its schema is the operator's mistake or a stale page, not a
            // fault, so it answers 400 rather than 422 — and never echoes what was wrong with it, since
            // the only body this route takes is a credential.
            // Two different codes for one operator-visible fact. VALIDATION is a body that parsed and
            // was the wrong shape; PARSE is a body that never parsed at all. Mapping only the first
            // leaves broken JSON answering 500 — the server calling a caller's mistake its own fault,
            // which is what it did until a test sent it a truncated document.
            if (code === "VALIDATION" || code === "PARSE") return status(400, { error: "malformed" });
            if (code === "NOT_FOUND") return status(404, { error: "not-found" });

            return status(500, { error: "internal" });
        })

        /**
         * Liveness, unauthenticated on purpose: the install script polls it to know when to tell the
         * operator to open a browser, and it has no session at that point. It says nothing about the
         * instance beyond "this process is answering".
         */
        .get("/api/health", () => "ok")

        /**
         * The page itself, and the two files it pulls in.
         *
         * Unauthenticated, and that is not an oversight: markup, a stylesheet and a script are not
         * secrets, and the code is checked by the API the page then calls. Gating the shell would mean
         * a sign-in form that cannot render until you have signed in.
         *
         * Enumerated rather than served from a directory. Three files are three routes, and a route per
         * file cannot be talked into reading `../../etc/passwd` — this container holds the docker
         * socket, so the cost of being wrong here is the whole machine. The build is configured to emit
         * exactly these three names, with no content hash, so that this can stay an enumeration.
         */
        .get("/", () => file(UI, "index.html"))
        .get("/app.js", () => file(UI, "app.js"))
        .get("/app.css", () => file(UI, "app.css"))

        .post("/api/auth/challenge", ({ status }) => {
            if (auth.retired) return status(410, { error: "setup-complete" });

            const { id, nonce } = auth.challenge();

            return { id, nonce };
        })

        .post(
            "/api/auth/verify",
            ({ body, cookie, set, status }) => {
                const result = auth.verify(body.challengeId, body.proof);

                if (result.ok) {
                    issue(cookie, result.session.token);

                    return { ok: true };
                }

                if (result.reason === "locked") {
                    set.headers["retry-after"] = String(Math.ceil((result.retryAfterMs ?? 0) / 1000));
                    return status(429, { error: result.reason });
                }

                if (result.reason === "spent") return status(410, { error: result.reason });

                // Every other failure answers the same way. Telling a caller that their challenge
                // expired rather than that their proof was wrong is a small oracle, and there is nothing
                // a legitimate operator does with the distinction that retrying does not also do.
                return status(401, { error: "rejected" });
            },
            {
                // Declared rather than checked in the handler. A body that is not this shape is refused
                // before any of the code above runs, which is the whole reason for the framework.
                body: t.Object({
                    challengeId: t.String({ minLength: 1 }),
                    proof: t.String({ minLength: 1 }),
                }),
            },
        )

        /**
         * Which doors are open, asked before there is a session to ask with.
         *
         * The page has to draw either the code field or the password field, and it cannot read the
         * state to find out — that is the thing behind the door. Both are false only in the moment
         * between the code being retired and a password existing, which {@link BootstrapAuth.retire}
         * refuses to create.
         */
        .get("/api/auth/mode", () => ({ code: !auth.retired, password: auth.hasPassword }))

        /**
         * The door that outlives setup.
         *
         * Sent rather than proved, unlike the code. A challenge-response over a password would need the
         * server to hold something reversible to check the proof against, which is exactly what storing
         * a hash avoids — so this one relies on the TLS the edge terminates, which is the same thing
         * every other password on the internet relies on.
         */
        .post(
            "/api/auth/password",
            async ({ body, cookie, set, status }) => {
                const result = await auth.verifyPassword(body.password);

                if (result.ok) {
                    issue(cookie, result.session.token);

                    return { ok: true };
                }

                if (result.reason === "locked") {
                    set.headers["retry-after"] = String(Math.ceil((result.retryAfterMs ?? 0) / 1000));
                    return status(429, { error: result.reason });
                }

                return status(401, { error: "rejected" });
            },
            { body: t.Object({ password: t.String({ minLength: 1 }) }) },
        )

        /** Everything past setup's front door goes through here. */
        .guard(
            {
                beforeHandle: ({ cookie, status }) =>
                    auth.holds(cookie[SESSION_COOKIE]?.value as string | undefined)
                        ? undefined
                        : status(401, { error: "unauthenticated" }),
            },
            (app) =>
                app
                    /**
                     * The whole wizard in one document, and deliberately cheap: it starts no container
                     * and touches no disk it has not already touched. Everything slow below is a POST for
                     * that reason — a UI that polls its state must not be polling something that starts a
                     * container per poll.
                     *
                     * `retired` comes from the auth side rather than the setup side. They are different
                     * facts: one is whether the bootstrap code still opens the door, the other is how far
                     * the install has got.
                     */
                    .get("/api/state", async () => ({
                        ...(setup === undefined ? UNAVAILABLE_STATE : await setup.state()),
                        retired: auth.retired,

                        // Not folded into `credentials`, which is the setup machine's list of what it
                        // holds for Argon. This one is the panel's own, and the page gates the install
                        // button on it — there is no finishing an install that locks you out.
                        panelPassword: auth.hasPassword,
                    }))

                    /**
                     * One step of the wizard, taken whole or not at all.
                     *
                     * 400 rather than 422 for a refused answer, matching what `onError` does with a body
                     * of the wrong shape: to the operator both are "that is not something I can use", and
                     * the rejections say which field and why.
                     */
                    .post(
                        "/api/setup/step",
                        async ({ body, status }) => {
                            if (setup === undefined) return status(503, UNAVAILABLE);

                            const submission = await setup.submit(body);

                            if (!submission.ok)
                                return status(400, { error: "rejected", rejections: submission.rejections });

                            return { ...submission.state, retired: auth.retired };
                        },
                        { body: STEP },
                    )

                    /**
                     * Sets the password that will still work tomorrow.
                     *
                     * Authenticated, because the only person who should be able to set it is whoever
                     * already got in with the code — and after retirement, whoever already knows the
                     * password. That makes this the change-password route as well, which is why it does
                     * not ask for the old one: holding a session already required it.
                     */
                    .post(
                        "/api/panel/password",
                        async ({ body, status }) => {
                            if (credentials === undefined) return status(503, UNAVAILABLE);

                            const refusal = refuseCredential(body.password, config.code);

                            if (refusal !== undefined) return status(400, { error: "rejected", problem: refusal });

                            auth.adoptPassword(await credentials.write(body.password));

                            return { ok: true };
                        },
                        { body: t.Object({ password: t.String({ minLength: 1 }) }) },
                    )

                    /**
                     * Asks the image what it offers. A POST because it starts a container: a minute of
                     * work, once, and the machine holds the answer so a second tab joins the first rather
                     * than starting a second container beside it.
                     */
                    .post("/api/setup/interrogate", async ({ status }) => {
                        if (setup === undefined) return status(503, UNAVAILABLE);

                        const outcome = await setup.interrogate();

                        if (outcome.ok) return outcome;

                        // Two failures that read alike and are not: one is a question the operator has
                        // not answered yet, the other is docker. Sending both back as one leaves them
                        // looking at their daemon for a missing answer.
                        if (outcome.reason === "no-version")
                            return status(409, { error: outcome.reason, problem: outcome.problem });

                        return status(503, { error: "image-unavailable", problem: outcome.problem });
                    })

                    /**
                     * Generate, validate, write, hand the port over, start, wait.
                     *
                     * The longest route in the process by a wide margin, and the only one whose reply may
                     * arrive on a listener that has already closed — which is why nothing here retries
                     * and why every answer is complete on its own. Nothing returns a file's contents:
                     * `written` is paths and modes, because the one file this produces that a route could
                     * leak is the secrets document, and `output` is `docker compose`'s own words with
                     * every known secret taken out of them by `setup.ts`.
                     */
                    .post("/api/setup/apply", async ({ status }) => {
                        if (setup === undefined) return status(503, UNAVAILABLE);

                        // Refused before anything happens rather than discovered after. A successful
                        // install retires the bootstrap code — see below — and retiring it with no
                        // password set would leave a panel holding the docker socket that nobody can
                        // ever sign into again.
                        if (!auth.hasPassword)
                            return status(409, {
                                error: "not-startable",
                                problem:
                                    "no panel password has been set. Finishing the install retires the bootstrap code, so without one there would be no way back into this panel — and it is the panel that starts, stops and upgrades the instance from here on.",
                            });

                        // Read before applying, because the record below needs to say which version was
                        // installed and the state after an apply is about what happened rather than
                        // about what was asked for.
                        const installing = (await setup.state()).answers.serverVersion;

                        const outcome = await setup.apply();

                        /**
                         * The first line of the history, written here because nothing else can write it.
                         *
                         * The panel refuses to upgrade an install it has no record of — it cannot tell a
                         * fresh machine from one that has been running Argon since before this file
                         * existed, and guessing wrong means a downgrade onto a database a migration has
                         * already moved. But the upgrade route is the only other thing that records, and
                         * it records *after* that refusal.
                         *
                         * So without this line the two lock: the empty history refuses the upgrade, and
                         * the upgrade is the only thing that would have filled it. No instance installed
                         * by this wizard could ever be upgraded through this panel. A green test suite
                         * said nothing, because neither route had a test.
                         */
                        if (outcome.ok && panel !== undefined && installing !== undefined)
                            await panel.record(setup, installing, outcome).catch(() => undefined);

                        // The code's whole life is this one install. It is printed in a terminal and
                        // left in a file in the install root, and §4 is right that leaving it valid
                        // afterwards replaces one problem with a worse one. Sessions already issued
                        // survive, so the operator watching this is not thrown out at the moment it
                        // becomes the panel.
                        if (outcome.ok) auth.retire();

                        if (outcome.ok)
                            return {
                                ok: true,
                                written: outcome.written,
                                services: outcome.services,
                                panel: outcome.panel,
                            };

                        if (outcome.reason === "incomplete")
                            return status(400, { error: outcome.reason, rejections: outcome.rejections });

                        // The server read what we generated and said no. Its own words come back with it,
                        // redacted — they are the only thing that says which section it did not like.
                        if (outcome.reason === "invalid")
                            return status(422, { error: outcome.reason, reports: outcome.reports });

                        if (outcome.reason === "blocked")
                            return status(409, { error: outcome.reason, problem: outcome.problem });

                        // Configuration that cannot become a project, or a process that cannot let go of
                        // the port the front door wants. 409 with `blocked`, because to the operator both
                        // are "this cannot proceed as it stands" rather than "something is broken".
                        if (outcome.reason === "not-startable")
                            return status(409, { error: outcome.reason, problem: outcome.problem });

                        if (outcome.reason === "image")
                            return status(503, { error: "image-unavailable", problem: outcome.problem });

                        // The two that carry `running`, and the field is the point: the status code says
                        // the install did not finish and only the body says whether there are containers
                        // on the machine now. A UI that shows one and not the other tells the operator to
                        // retry into a live stack.
                        if (outcome.reason === "start-failed")
                            return status(500, {
                                error: outcome.reason,
                                problem: outcome.problem,
                                output: outcome.output,
                                running: outcome.running,
                                services: outcome.services,
                                panel: outcome.panel,
                            });

                        // 504 rather than 500: nothing here failed, something upstream did not answer in
                        // time, and the body names which service it was still waiting on.
                        if (outcome.reason === "not-ready")
                            return status(504, {
                                error: outcome.reason,
                                problem: outcome.problem,
                                running: outcome.running,
                                services: outcome.services,
                                panel: outcome.panel,
                            });

                        return status(500, { error: outcome.reason, problem: outcome.problem });
                    })

                    /* ------------------------------------------------------------------------------
                     * The panel — what these routes are for the rest of the instance's life.
                     *
                     * Behind the same guard as setup, which is the honest arrangement: the credential
                     * that opens the wizard is retired the moment the install succeeds, and what opens
                     * these afterwards is the password set before it ran. Same session, same cookie,
                     * different half of the container's life.
                     * ---------------------------------------------------------------------------- */

                    /**
                     * Everything the panel draws, in one call.
                     *
                     * Separate from `/api/state` because it costs real work — a TLS handshake to read
                     * the certificate, a directory walk for the backups, the daemon for the services —
                     * and `/api/state` is polled every two seconds for the length of an install.
                     *
                     * Nothing here throws for a part that is unavailable. An edge that is down, a
                     * daemon that will not answer, a history that was never written: each is reported
                     * as its own absence, because the page has to render while the instance is broken.
                     * That is precisely when somebody opens it.
                     */
                    .get("/api/panel/overview", async ({ status }) => {
                        if (setup === undefined || panel === undefined) return status(503, UNAVAILABLE);

                        return await panel.overview(setup);
                    })

                    /**
                     * One service's log.
                     *
                     * Redacted through the setup's own bundle: the roles print about configuration they
                     * have just read, so a connection string or the SFU secret can be in there. The
                     * module does the redacting; what it is handed is a closure — see `Setup.redactor`.
                     */
                    .get("/api/panel/services/:service/logs", async ({ params, query, status }) => {
                        if (setup === undefined || panel === undefined) return status(503, UNAVAILABLE);

                        const outcome = await panel.logs(setup, params.service, query.tail);

                        return outcome.ok ? outcome : status(outcome.reason === "unknown-service" ? 404 : 502, outcome);
                    })

                    /**
                     * Start, stop or restart one service.
                     *
                     * The verb is checked by the module before anything is resolved — see
                     * `asLifecycle` in panel/containers.ts. A verb out of an HTTP path is exactly the
                     * untrusted string that check exists for, and this route is where it arrives.
                     *
                     * Stopping waits for the container to leave before it is killed, so this can take
                     * the better part of a minute to answer. That is the daemon's timing, not ours.
                     */
                    .post("/api/panel/services/:service/:action", async ({ params, status }) => {
                        if (setup === undefined || panel === undefined) return status(503, UNAVAILABLE);

                        const outcome = await panel.control(setup, params.service, params.action);

                        if (outcome.ok) return outcome;

                        // `protected` is the panel refusing to switch itself off, which is a rule and
                        // not a failure — 409, not 502. The page does not draw that button, so reaching
                        // this means something other than the page asked.
                        const code =
                            outcome.reason === "protected"
                                ? 409
                                : outcome.reason === "unknown-service" || outcome.reason === "unknown-action"
                                  ? 404
                                  : 502;

                        return status(code, outcome);
                    })

                    /** Takes a backup now. Minutes on a large instance; the page keeps the button busy. */
                    .post("/api/panel/backup", async ({ status }) => {
                        if (setup === undefined || panel === undefined) return status(503, UNAVAILABLE);

                        const outcome = await panel.backup();

                        return outcome.ok ? outcome : status(502, outcome);
                    })

                    /**
                     * What moving to a version would do, before it is done.
                     *
                     * Read-only, and deliberately a separate call from the one that does it. The
                     * refusals it can return — a downgrade across a release line, a change the panel
                     * cannot reason about because the running version came from a moving tag — are
                     * things an operator should read rather than discover.
                     */
                    .get("/api/panel/upgrade/plan", async ({ query, status }) => {
                        if (setup === undefined || panel === undefined) return status(503, UNAVAILABLE);

                        const version = (query.version ?? "").trim();

                        if (version.length === 0) return status(400, { error: "rejected", problem: "no version was named." });

                        return await panel.plan(setup, version);
                    })

                    /**
                     * Moves the instance to another version.
                     *
                     * Nothing new happens here: the version is an answer, and changing an answer and
                     * applying is what an install already is. What this adds is the record — an install
                     * with no history cannot be rolled back, and cannot say what changed between "it
                     * worked" and "it does not".
                     */
                    .post(
                        "/api/panel/upgrade",
                        async ({ body, status }) => {
                            if (setup === undefined || panel === undefined) return status(503, UNAVAILABLE);

                            const verdict = await panel.judgeUpgrade(setup, body.version);

                            /**
                             * Two kinds of no, and only one of them can be argued with.
                             *
                             * `settled` is a change that cannot work whatever anybody says — a
                             * downgrade across a release line onto a database a migration has moved.
                             * `unproven` is the panel saying it cannot see far enough: the running
                             * version came from a moving tag, or nothing wrote down what is installed.
                             * The operator can see further than the panel can, and `confirm` is them
                             * saying so.
                             *
                             * Collapsing the two — which this route did — locked every `latest`-pinned
                             * install out of even re-pulling its own tag, which is the one operation
                             * that kind of install exists to do.
                             */
                            if (!verdict.ok && (verdict.standing === "settled" || body.confirm !== true))
                                return status(409, {
                                    error: "refused",
                                    standing: verdict.standing,
                                    problem: verdict.problem,
                                });

                            const submitted = await setup.submit({ serverVersion: body.version });

                            if (!submitted.ok) return status(400, { error: "rejected", rejections: submitted.rejections });

                            const outcome = await setup.apply();

                            await panel.record(setup, body.version, outcome);

                            return outcome.ok ? outcome : status(500, outcome);
                        },
                        {
                            body: t.Object({
                                version: t.String({ minLength: 1, maxLength: 200 }),

                                // Only ever consulted for an `unproven` verdict. A settled refusal
                                // ignores it, so a caller cannot confirm its way past a change that
                                // cannot work.
                                confirm: t.Optional(t.Boolean()),
                            }),
                        },
                    ),
        )

        .listen({
            hostname: config.hostname ?? "0.0.0.0",
            port: config.port ?? 8443,
            tls: config.tls,
        });

    return app;
}
