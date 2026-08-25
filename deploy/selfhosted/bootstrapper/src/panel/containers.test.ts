import { describe, expect, test } from "bun:test";
import {
    ARGON_CONTAINERS,
    ARGON_INSTANCE,
    asLifecycle,
    containersOf,
    controllable,
    DEFAULT_TAIL,
    GRACE_SECONDS,
    logReader,
    LONGEST_TAIL,
    MOST_CHARACTERS,
    MOST_LINES,
    type ControlRequest,
    type Instance,
    type Lifecycle,
    type LogLine,
    type LogReader,
    type LogRequest,
    type Redactor,
} from "./containers";
import { COMPOSE_PROJECT_LABEL, COMPOSE_SERVICE_LABEL, type EngineCommand, type EngineRequest, type EngineStream } from "../docker";
import { COMPOSE_PROJECT, PANEL_SERVICE } from "../compose";

/* ------------------------------------------------------------------------------------------------
 * The panel's control surface.
 *
 * Two things here can be wrong in a way that looks like working software, and they are what most of
 * this file is about. The first is the framing: a log handed over undemultiplexed still *renders*, as
 * text with binary in it, and the operator concludes their instance is broken rather than the panel.
 * The second is the refusal: a stop button that works on the panel works exactly once, and the machine
 * it worked on is no longer reachable from a browser.
 *
 * The frames below are built to docker's wire format rather than from the module's own constants, on
 * purpose. A test that shares the reader's idea of a header agrees with the reader about a header that
 * is wrong.
 * ---------------------------------------------------------------------------------------------- */

/**
 * Deliberately neither of the real names.
 *
 * A fixture equal to `ARGON_INSTANCE` cannot tell a module that reads the instance it was given from one
 * that reached into `compose.ts` for the constant instead. A review rewrote the refusal to compare
 * against `PANEL_SERVICE` and the label filter to use `COMPOSE_PROJECT`, and the whole suite stayed
 * green — which is a suite that would not notice the panel of *this* install going unprotected. The real
 * wiring is pinned once, in the test written for it, and nowhere else.
 */
const HERE: Instance = { project: "other-project", panel: "other-panel" };

/** The bound surface. There is no way to call any of this with an instance chosen per request. */
const here = containersOf(HERE);

/**
 * The cast a route handler reaches for: it holds a `string` off a body or a query and needs the union.
 *
 * Written out because it is the whole threat model for {@link asLifecycle}. `Lifecycle` is gone by the
 * time the program runs, so this is not a hypothetical — it is the shortest thing that compiles.
 */
const forged = (action: string): Lifecycle => action as Lifecycle;

const utf8 = new TextEncoder();

/** Docker's frame: one byte of stream type, three of padding, four of big-endian length, then payload. */
function frame(stream: 0 | 1 | 2, body: string | Uint8Array): Uint8Array {
    const payload = typeof body === "string" ? utf8.encode(body) : body;
    const out = new Uint8Array(8 + payload.length);

    out[0] = stream;
    new DataView(out.buffer).setUint32(4, payload.length, false);
    out.set(payload, 8);

    return out;
}

function bytes(...parts: readonly Uint8Array[]): Uint8Array {
    const out = new Uint8Array(parts.reduce((total, part) => total + part.length, 0));

    let at = 0;

    for (const part of parts) {
        out.set(part, at);
        at += part.length;
    }

    return out;
}

/** The same bytes, delivered in pieces of a chosen size — which is where a demultiplexer goes wrong. */
function inPieces(whole: Uint8Array, size: number): Uint8Array[] {
    const pieces: Uint8Array[] = [];

    for (let at = 0; at < whole.length; at += size) pieces.push(whole.subarray(at, Math.min(at + size, whole.length)));

    return pieces;
}

function readAll(reader: LogReader, chunks: readonly Uint8Array[]): LogLine[] {
    const lines: LogLine[] = [];

    for (const chunk of chunks) lines.push(...reader.push(chunk));

    lines.push(...reader.end());

    return lines;
}

/** A redactor that removes nothing, said out loud so a test about framing is not also a test about secrets. */
const nothingHidden: Redactor = (text) => text;

function refusal<T extends { readonly ok: boolean }>(outcome: T): Extract<T, { readonly ok: false }> {
    if (outcome.ok) throw new Error(`expected a refusal, got ${JSON.stringify(outcome)}`);

    return outcome as Extract<T, { readonly ok: false }>;
}

function success<T extends { readonly ok: boolean }>(outcome: T): Extract<T, { readonly ok: true }> {
    if (!outcome.ok) throw new Error(`expected success, got ${JSON.stringify(outcome)}`);

    return outcome as Extract<T, { readonly ok: true }>;
}

interface Daemon {
    /** Every path that was *read*: the container listing, and the log endpoint. */
    readonly paths: string[];

    /** Every path that was POSTed to. Empty is the assertion that matters for the panel's own service. */
    readonly issued: string[];

    readonly ports: {
        readonly request: EngineRequest;
        readonly command: EngineCommand;
        readonly stream: EngineStream;
        readonly redact: Redactor;
    };
}

function daemon(setup: {
    /**
     * Whatever the daemon answers the listing with — `unknown`, not a row type.
     *
     * It was a shaped array, and that made three guards in `containersFor` unreachable from here: a
     * response that is not a list at all, a row with no `Id`, and a row with no `Created`. `EngineRequest`
     * returns `Promise<unknown>` precisely because the daemon's shape is not ours to assume, and a fixture
     * that can only express well-formed rows tests the assumption rather than the guard.
     */
    readonly rows?: unknown;
    readonly chunks?: readonly Uint8Array[];
    readonly refuseList?: string;
    readonly refuseStream?: string;
    readonly refuseCommand?: (issuedSoFar: number) => string | undefined;
    readonly redact?: Redactor;
}): Daemon {
    const paths: string[] = [];
    const issued: string[] = [];

    const request: EngineRequest = async (path) => {
        paths.push(path);

        if (setup.refuseList !== undefined) throw new Error(setup.refuseList);

        return setup.rows ?? [];
    };

    const command: EngineCommand = async (path) => {
        const complaint = setup.refuseCommand?.(issued.length);

        issued.push(path);

        if (complaint !== undefined) throw new Error(complaint);
    };

    const stream: EngineStream = (path) => {
        paths.push(path);

        return (async function* () {
            if (setup.refuseStream !== undefined) throw new Error(setup.refuseStream);

            for (const chunk of setup.chunks ?? []) yield chunk;
        })();
    };

    return { paths, issued, ports: { request, command, stream, redact: setup.redact ?? nothingHidden } };
}

/* ------------------------------------------------------------------------------------------------
 * Framing.
 * ---------------------------------------------------------------------------------------------- */

describe("undoing docker's multiplexing", () => {
    /**
     * The whole point. Without the demultiplexer these three lines arrive as one run of text with eight
     * bytes of header wedged between them, every stream reads as stdout, and the header's length field
     * lands in the middle of a sentence as two or three unprintable characters.
     */
    test("stdout and stderr come back apart, in the order they were written", () => {
        const lines = readAll(logReader(nothingHidden), [
            frame(1, "argon-core listening on 5001\n"),
            frame(2, "npgsql: no such host\n"),
            frame(1, "retrying in 2s\n"),
        ]);

        expect(lines).toEqual([
            { stream: "stdout", text: "argon-core listening on 5001" },
            { stream: "stderr", text: "npgsql: no such host" },
            { stream: "stdout", text: "retrying in 2s" },
        ]);
    });

    /**
     * The trap named in the brief: a header can be cut anywhere, and so can a payload.
     *
     * Every cut point rather than a chosen one, because the interesting offsets are exactly the ones
     * nobody thinks to pick — a chunk ending after the type byte, after the padding, one byte short of
     * the length field, one byte into a payload. A reader that consumes a header before checking it has
     * all eight bytes passes a test cut at 8 and fails at 3.
     */
    test("the same lines come out however the bytes are cut up", () => {
        const whole = bytes(frame(1, "alpha\n"), frame(2, "beta\n"), frame(1, "gamma\n"));

        const expected: LogLine[] = [
            { stream: "stdout", text: "alpha" },
            { stream: "stderr", text: "beta" },
            { stream: "stdout", text: "gamma" },
        ];

        for (let cut = 1; cut < whole.length; cut++)
            expect(readAll(logReader(nothingHidden), [whole.subarray(0, cut), whole.subarray(cut)])).toEqual(expected);

        for (const size of [1, 2, 3, 5, 7, 8, 9, 13])
            expect(readAll(logReader(nothingHidden), inPieces(whole, size))).toEqual(expected);
    });

    /**
     * Docker cuts frames at a byte count and knows nothing about characters, so a three-byte UTF-8
     * sequence is regularly split across two frames. Decoding each frame on its own turns it into two
     * replacement marks, and a log full of those where the instance's name should be gets reported as a
     * bug in Argon rather than in the thing that displayed it.
     */
    test("a character split across two frames survives as one character", () => {
        const dash = utf8.encode("—");

        expect(dash.length).toBe(3);

        const lines = readAll(logReader(nothingHidden), [
            frame(1, bytes(utf8.encode("argon "), dash.subarray(0, 1))),
            frame(1, bytes(dash.subarray(1), utf8.encode(" ready\n"))),
        ]);

        expect(lines).toEqual([{ stream: "stdout", text: "argon — ready" }]);
    });

    /**
     * And the two streams need their own decoder, not one shared.
     *
     * A stderr frame arriving between the halves of a stdout character is ordinary — the roles write to
     * both — and a single decoder would hand stdout's dangling bytes to stderr's text, producing a
     * replacement mark on one line and a corrupted character on the other.
     */
    test("a stderr frame between the halves does not corrupt either", () => {
        const dash = utf8.encode("—");

        const lines = readAll(logReader(nothingHidden), [
            frame(1, bytes(utf8.encode("core "), dash.subarray(0, 2))),
            frame(2, "warn: slow query\n"),
            frame(1, bytes(dash.subarray(2), utf8.encode(" ready\n"))),
        ]);

        expect(lines).toEqual([
            { stream: "stderr", text: "warn: slow query" },
            { stream: "stdout", text: "core — ready" },
        ]);
    });

    /** A container with a TTY gets one stream and no headers at all. Demultiplexing it eats real text. */
    test("a TTY stream has no frames and is passed through", () => {
        const lines = readAll(logReader(nothingHidden), [utf8.encode("[INFO] starting\n[INFO] ready\n")]);

        expect(lines).toEqual([
            { stream: "stdout", text: "[INFO] starting" },
            { stream: "stdout", text: "[INFO] ready" },
        ]);
    });

    /**
     * The three padding bytes are half of what tells a header from text, and the cheap half to skip.
     *
     * A stream type is only ever 0, 1 or 2, so a raw log that happens to open with a control byte passes
     * the first check on its own. What saves it is that the next three bytes are not zeroes — because if
     * a reader believes it has a header, it reads four bytes of ordinary text as a big-endian length,
     * concludes the frame is a gigabyte long, and waits for the rest of it until the stream ends. What
     * the operator gets then is the log with its first eight characters bitten off.
     */
    test("a raw log opening with a control byte is not mistaken for a frame", () => {
        const lines = readAll(logReader(nothingHidden), [bytes(new Uint8Array([1]), utf8.encode("hello world\n"))]);

        // The control byte stays. Raw output is passed through as written, and a rule about which bytes
        // a container is allowed to print would lose real output to a cosmetic preference.
        expect(lines).toEqual([{ stream: "stdout", text: "\u0001hello world" }]);
    });

    /**
     * A raw log too short to contain even one header must still arrive.
     *
     * The reader cannot decide framed-or-not until eight bytes exist, so a two-byte log is the case where
     * it never decides — and "never decided" must resolve to text rather than to silence.
     */
    test("a raw log shorter than one header is still text", () => {
        expect(readAll(logReader(nothingHidden), [utf8.encode("hi")])).toEqual([{ stream: "stdout", text: "hi" }]);
    });

    test("an empty log produces nothing rather than an empty line", () => {
        expect(readAll(logReader(nothingHidden), [])).toEqual([]);
        expect(readAll(logReader(nothingHidden), [new Uint8Array(0)])).toEqual([]);
    });

    /** The last thing a crashing role printed usually has no newline after it. It is the line that matters. */
    test("a final line with no newline is not swallowed", () => {
        const lines = readAll(logReader(nothingHidden), [frame(2, "Unhandled exception. System.Exception: boom")]);

        expect(lines).toEqual([{ stream: "stderr", text: "Unhandled exception. System.Exception: boom" }]);
    });

    /** Invisible in HTML, very visible the moment somebody copies a line out of the panel. */
    test("the carriage return of a CRLF does not travel with the line", () => {
        expect(readAll(logReader(nothingHidden), [frame(1, "starting\r\ndone\r\n")])).toEqual([
            { stream: "stdout", text: "starting" },
            { stream: "stdout", text: "done" },
        ]);
    });

    /**
     * A response cut inside a frame still holds real output, and it is the newest output there is.
     */
    test("a stream cut mid-frame still shows the payload that arrived", () => {
        const whole = bytes(frame(1, "the last thing it said\n"));

        expect(readAll(logReader(nothingHidden), [whole.subarray(0, whole.length - 5)])).toEqual([
            { stream: "stdout", text: "the last thing it " },
        ]);
    });

    /**
     * A leftover of eight bytes or fewer is header, not text.
     *
     * Emitting it renders a stream type and a length as characters — which is precisely the binary
     * garbage the demultiplexer exists to remove, reintroduced at the very end of every truncated log.
     */
    test("a leftover shorter than a header is dropped rather than rendered", () => {
        const lines = readAll(logReader(nothingHidden), [
            bytes(frame(1, "done\n"), new Uint8Array([1, 0, 0, 0, 0, 0, 0])),
        ]);

        expect(lines).toEqual([{ stream: "stdout", text: "done" }]);
    });

    /**
     * A line with no end to it must not grow without bound inside the process that holds the docker
     * socket. Cut into pieces, every character still arrives and in order — the bound costs line
     * boundaries, not content.
     */
    test("an endless line is cut into bounded pieces rather than buffered forever", () => {
        const enormous = "A".repeat(200_000);

        const lines = readAll(logReader(nothingHidden), [utf8.encode(enormous)]);

        expect(lines.length).toBe(4);
        expect(Math.max(...lines.map((line) => line.text.length))).toBe(65_536);
        expect(lines.map((line) => line.text).join("")).toBe(enormous);
    });

    /**
     * The same cut, whether or not a newline ended the line.
     *
     * The bound used to apply only to a trailing remainder — text with no newline after it yet — so a
     * line that *did* end at a newline came out at whatever length it happened to be. A review pushed one
     * 1,000,005-character line through `readLogs` and got `{ok: true, lines: []}`: emitted whole, then
     * dropped entire by the character bound. A read that lost everything, reported as a healthy read of
     * nothing.
     */
    test("a line that ended at a newline is cut to the same bound as one that did not", () => {
        const enormous = "A".repeat(200_000);

        const lines = readAll(logReader(nothingHidden), [utf8.encode(`${enormous}\n`)]);

        expect(lines.length).toBe(4);
        expect(Math.max(...lines.map((line) => line.text.length))).toBe(65_536);
        expect(lines.map((line) => line.text).join("")).toBe(enormous);
    });

    /**
     * The cut is lossless, and a carriage return is a character like any other when it lands on it.
     *
     * The strip exists for the `\r` of a CRLF — a `\r` with a newline directly after it. A piece produced
     * by the length cut has no newline after it, so that byte is ordinary content: stripping it there
     * deletes one character out of the middle of a line, and the pieces stop reconstructing what the
     * container printed. Any role that writes `\r` progress output on a long enough line reaches it.
     */
    test("a carriage return on the cut boundary is content and stays", () => {
        const payload = `${"A".repeat(65_535)}\r${"B".repeat(10)}`;

        const lines = readAll(logReader(nothingHidden), [utf8.encode(payload)]);

        expect(lines.map((line) => line.text).join("")).toBe(payload);
    });

    /**
     * `push` promises the order the container produced them in, and `end` has to keep the same promise.
     *
     * Both streams can be holding an unterminated tail when a response is cut — a role that wrote
     * `connecting to db` on stderr and then `web ready` on stdout, with neither newline arriving — and a
     * fixed stdout-then-stderr flush shows the operator those two the wrong way round.
     */
    test("the unterminated tails of both streams come back in the order they were written", () => {
        expect(readAll(logReader(nothingHidden), [frame(2, "connecting to db"), frame(1, "web ready")])).toEqual([
            { stream: "stderr", text: "connecting to db" },
            { stream: "stdout", text: "web ready" },
        ]);
    });

    /**
     * A log cut in the middle of a character ends with bytes that are not one.
     *
     * The decoders are flushed at the end without `stream: true`, which turns those bytes into a single
     * replacement mark. Dropping them silently would take the last characters of a truncated log with
     * them, and the end of a truncated log is the part somebody is reading it for.
     */
    test("bytes of a character that never finished are shown rather than dropped", () => {
        const dash = utf8.encode("—");

        const lines = readAll(logReader(nothingHidden), [frame(1, bytes(utf8.encode("argon "), dash.subarray(0, 2)))]);

        expect(lines).toEqual([{ stream: "stdout", text: "argon \uFFFD" }]);
    });
});

/* ------------------------------------------------------------------------------------------------
 * Redaction.
 * ---------------------------------------------------------------------------------------------- */

describe("what the operator is not shown", () => {
    /**
     * The reason redaction runs on assembled lines and not on frames.
     *
     * Docker cuts a frame at a byte count, so a connection string printed at boot can have its password
     * split down the middle. A redactor is a blind substring replacement; run on each frame it matches
     * neither half, reports that it redacted the output, and prints the secret in two pieces that any
     * reader can put back together.
     */
    test("a secret split across two frames is still removed", () => {
        const secret = "S3cr3t-Passw0rd-Value";
        const redact: Redactor = (text) => text.split(secret).join("<redacted>");

        const lines = readAll(logReader(redact), [
            frame(1, `Host=argon-postgres;Password=${secret.slice(0, 9)}`),
            frame(1, `${secret.slice(9)};Pooling=true\n`),
        ]);

        expect(lines).toEqual([{ stream: "stdout", text: "Host=argon-postgres;Password=<redacted>;Pooling=true" }]);
        expect(lines[0]?.text).not.toContain(secret.slice(0, 9));
    });

    test("both streams go through the redactor", () => {
        const redact: Redactor = (text) => text.split("keyid").join("<redacted>");

        const lines = readAll(logReader(redact), [frame(1, "using keyid\n"), frame(2, "bad keyid\n")]);

        expect(lines.map((line) => line.text)).toEqual(["using <redacted>", "bad <redacted>"]);
    });

    /** Including the last line, which is the one a reader is most likely to forget: it leaves through `end`. */
    test("the unterminated last line is redacted too", () => {
        const redact: Redactor = (text) => text.split("hunter2hunter2").join("<redacted>");

        expect(readAll(logReader(redact), [frame(1, "password is hunter2hunter2")])).toEqual([
            { stream: "stdout", text: "password is <redacted>" },
        ]);
    });

    /**
     * Everything above drives the reader directly, which left the wiring untested.
     *
     * `readLogs` builds its own reader, and every fixture in this file handed it a redactor that removes
     * nothing — so replacing `logReader(ports.redact)` with an identity function kept all thirty-eight
     * tests green while the panel served unredacted container output.
     */
    test("the lines readLogs returns went through the caller's redactor", async () => {
        const secret = "S3cr3t-Passw0rd-Value";
        const redact: Redactor = (text) => text.split(secret).join("<redacted>");

        const fake = daemon({
            rows: [{ Id: "one", Created: 1 }],
            chunks: [frame(1, `Host=argon-postgres;Password=${secret};Pooling=true\n`), frame(2, `npgsql: ${secret}`)],
            redact,
        });

        const outcome = success(await here.readLogs({ service: "argon-core" }, fake.ports));

        expect(outcome.lines.map((line) => line.text)).toEqual([
            "Host=argon-postgres;Password=<redacted>;Pooling=true",
            "npgsql: <redacted>",
        ]);
    });

    /**
     * And the failure paths, which are the ones that get forgotten. What comes back from a refused
     * listing or a dead stream is `complaintFrom` in `docker.ts`, which appends the daemon's whole
     * response body to the status — so it is exactly as capable of quoting something as the log is.
     */
    test("a daemon complaint about a log is redacted before it is returned", async () => {
        const secret = "hunter2-hunter2";
        const redact: Redactor = (text) => text.split(secret).join("<redacted>");

        for (const fake of [
            daemon({ refuseList: `docker answered 500: Password=${secret}`, redact }),
            daemon({
                rows: [{ Id: "one", Created: 1 }],
                refuseStream: `docker answered 500: Password=${secret}`,
                redact,
            }),
        ]) {
            const outcome = refusal(await here.readLogs({ service: "argon-core" }, fake.ports));

            expect(outcome.reason).toBe("unavailable");
            expect(outcome.problem).not.toContain(secret);
            expect(outcome.problem).toContain("<redacted>");
        }
    });

    /**
     * `control` used to take no redactor at all, and there was no seam to add one: `LifecyclePorts` had
     * two fields and neither was a redactor. The argument was that a lifecycle failure is only the
     * daemon's own sentence about container state, and that the daemon has never seen this instance's
     * configuration — but compose hands it the Postgres password and the SFU's keys as container
     * environment, and the string is byte-identical to the one `readLogs` treats as sensitive, produced
     * by the same function.
     */
    test("a daemon complaint about a lifecycle call is redacted the same way", async () => {
        const secret = "APIabc123.SUPERSECRET";
        const redact: Redactor = (text) => text.split(secret).join("<redacted>");

        const refused = daemon({
            rows: [{ Id: "one", Created: 1 }],
            refuseCommand: () => `docker answered 500 for /containers/one/start: LIVEKIT_KEYS=${secret}`,
            redact,
        });

        const failed = refusal(await here.control({ service: "argon-core", action: "start" }, refused.ports));

        expect(failed.reason).toBe("failed");
        expect(failed.problem).not.toContain(secret);
        expect(failed.problem).toContain("<redacted>");

        const unreachable = daemon({ refuseList: `connect ENOENT: ${secret}`, redact });
        const listing = refusal(await here.control({ service: "argon-core", action: "stop" }, unreachable.ports));

        expect(listing.reason).toBe("failed");
        expect(listing.problem).not.toContain(secret);
    });
});

/* ------------------------------------------------------------------------------------------------
 * Resolution.
 * ---------------------------------------------------------------------------------------------- */

describe("finding a service's container", () => {
    /**
     * Both halves of the match go to the daemon.
     *
     * Without the project label this acts on a service of the same name in somebody else's compose
     * project on the same box — and `argon-postgres` is exactly the sort of name two projects share.
     */
    test("the listing is filtered by project and by service, and includes stopped containers", async () => {
        const fake = daemon({ rows: [{ Id: "abc", Created: 10 }] });

        await here.readLogs({ service: "argon-core" }, fake.ports);

        const listing = decodeURIComponent(fake.paths[0] ?? "");

        expect(listing).toContain(`${COMPOSE_PROJECT_LABEL}=other-project`);
        expect(listing).toContain(`${COMPOSE_SERVICE_LABEL}=argon-core`);
        expect(fake.paths[0]).toContain("all=true");
    });

    /**
     * `all=true` is what makes `start` reachable: a stopped container is not in the default listing, so
     * without it the one action that exists to fix a stopped service reports that it does not exist.
     */
    test("a stopped container can still be started", async () => {
        const fake = daemon({ rows: [{ Id: "stopped", Created: 1 }] });

        const outcome = success(await here.control({ service: "argon-core", action: "start" }, fake.ports));

        expect(fake.paths[0]).toContain("all=true");
        expect(outcome.containers).toBe(1);
        expect(fake.issued).toEqual(["/containers/stopped/start"]);
    });

    /**
     * A listing that includes stopped containers can hold the corpse of one that was replaced. Reading
     * its log shows output that stops exactly where the operator's problem begins, with nothing on
     * screen to say they are looking at the wrong container.
     */
    test("the newest container is the one read", async () => {
        const fake = daemon({ rows: [{ Id: "replaced", Created: 100 }, { Id: "current", Created: 500 }] });

        await here.readLogs({ service: "argon-core" }, fake.ports);

        expect(fake.paths[1]).toContain("/containers/current/logs");
        expect(fake.paths[1]).not.toContain("replaced");
    });

    test("nothing carrying that service label is a named refusal, and reads no log", async () => {
        const fake = daemon({ rows: [] });

        const outcome = refusal(await here.readLogs({ service: "argon-typo" }, fake.ports));

        expect(outcome.reason).toBe("unknown-service");
        expect(outcome.problem).toContain("argon-typo");
        expect(fake.paths).toHaveLength(1);
    });

    test("a daemon that will not answer is reported as unavailable rather than as an empty log", async () => {
        const fake = daemon({ refuseList: "connect ENOENT /var/run/docker.sock" });

        const outcome = refusal(await here.readLogs({ service: "argon-core" }, fake.ports));

        expect(outcome.reason).toBe("unavailable");
        expect(outcome.problem).toContain("docker.sock");
    });

    /**
     * `EngineRequest` answers `unknown`, and these three are why.
     *
     * A daemon that answers a listing with an object rather than an array, a row with no `Id`, and a row
     * with no `Created` are all things the type system has nothing to say about — and while the fixture
     * could only express well-formed rows, all three guards could be deleted without a test noticing.
     */
    test("a listing that is not a list is an empty listing rather than a crash", async () => {
        const fake = daemon({ rows: { message: "you are not allowed to do that" } });

        expect(refusal(await here.readLogs({ service: "argon-core" }, fake.ports)).reason).toBe("unknown-service");
    });

    test("a row with no id is skipped rather than acted on", async () => {
        const fake = daemon({ rows: [{ Created: 500 }, { Id: "real", Created: 1 }] });

        const outcome = success(await here.control({ service: "argon-core", action: "stop" }, fake.ports));

        // And specifically not `/containers//stop`, which is a path the daemon answers *something* for
        // and this would report as a stop that worked.
        expect(fake.issued).toEqual([`/containers/real/stop?t=${GRACE_SECONDS}`]);
        expect(outcome.containers).toBe(1);
    });

    test("a row with no creation time sorts oldest rather than newest", async () => {
        const fake = daemon({ rows: [{ Id: "undated" }, { Id: "dated", Created: 5 }] });

        await here.readLogs({ service: "argon-core" }, fake.ports);

        expect(fake.paths[1]).toContain("/containers/dated/logs");
    });
});

/* ------------------------------------------------------------------------------------------------
 * The refusal.
 * ---------------------------------------------------------------------------------------------- */

describe("the panel cannot switch itself off", () => {
    const actions: readonly Lifecycle[] = ["start", "stop", "restart"];

    /**
     * The failure this prevents has no undo inside the product: the operator's browser is talking to the
     * container they just told to stop, and the way back is ssh — which is the thing installing a panel
     * was meant to avoid needing.
     */
    test("no lifecycle action reaches the panel's own container", async () => {
        for (const action of actions) {
            const fake = daemon({ rows: [{ Id: "panel", Created: 1 }] });

            const outcome = refusal(await here.control({ service: HERE.panel, action }, fake.ports));

            expect(outcome.reason).toBe("protected");
            expect(outcome.problem).toContain(HERE.panel);
            expect(fake.issued).toEqual([]);
        }
    });

    /** Refused before anything is resolved, so a daemon that is down cannot turn the refusal into a retry. */
    test("the refusal happens before the daemon is asked anything", async () => {
        const fake = daemon({ refuseList: "the daemon is not answering" });

        expect(refusal(await here.control({ service: HERE.panel, action: "stop" }, fake.ports)).reason).toBe(
            "protected",
        );

        expect(fake.paths).toEqual([]);
    });

    /** The protection is scoped to lifecycle: the log of the thing that just refused is what anyone wants next. */
    test("the panel's own log is readable", async () => {
        const fake = daemon({ rows: [{ Id: "panel", Created: 1 }], chunks: [frame(1, "panel listening\n")] });

        const outcome = success(await here.readLogs({ service: HERE.panel }, fake.ports));

        expect(outcome.lines).toEqual([{ stream: "stdout", text: "panel listening" }]);
    });

    test("controllable answers for the UI the same way control does", () => {
        // Widened deliberately. A `ControllableService` will not compare against a bare string, which is
        // the brand refusing to be forged — the same refusal a route handler runs into, one level up.
        const allowed: string | undefined = controllable(HERE, "argon-core");

        expect(controllable(HERE, HERE.panel)).toBeUndefined();
        expect(allowed).toBe("argon-core");
    });

    /**
     * The protected name is compose's, not a second copy of it.
     *
     * A rename of the panel's service in `compose.ts` that left a literal behind here would unprotect
     * the panel silently — the buttons would appear and work, once.
     */
    test("the protected service is the one compose actually runs the panel as", () => {
        // The first line compares a value against the expression that defines it, so on its own it only
        // catches somebody hand-editing that one line to a literal. What does the work is the pair below:
        // they go through `ARGON_CONTAINERS`, which is the instance production is bound to and the only
        // one a route handler ever gets — every other test in this file deliberately uses another.
        expect(ARGON_INSTANCE).toEqual({ project: COMPOSE_PROJECT, panel: PANEL_SERVICE });
        expect(PANEL_SERVICE.length).toBeGreaterThan(0);

        const allowed: string | undefined = ARGON_CONTAINERS.controllable("argon-core");

        expect(ARGON_CONTAINERS.controllable(PANEL_SERVICE)).toBeUndefined();
        expect(allowed).toBe("argon-core");
    });

    /**
     * The refusal and the project scope are both read off the instance, so an instance that arrives with
     * the request is a refusal that arrives with the request: `{project: "argon", panel: ""}` unprotects
     * the panel, and `{project: "someone-elses-stack"}` reads another project's boot logs through a
     * redactor that has never heard of that project's secrets. The brand on `ControllableService` proves
     * the check ran, never what it ran against — so the fix is that there is nothing to hand in.
     *
     * The compile error is the assertion. `bun test` does not typecheck, but `tsc --noEmit` does, and it
     * fails on a `@ts-expect-error` that stops being an error.
     */
    test("a request cannot carry an instance of its own", () => {
        // @ts-expect-error — `instance` is not a field of ControlRequest.
        const control: ControlRequest = { service: "argon-core", action: "stop", instance: HERE };

        // @ts-expect-error — nor of LogRequest.
        const logs: LogRequest = { service: "argon-core", instance: HERE };

        expect([control.service, logs.service]).toEqual(["argon-core", "argon-core"]);
    });
});

/* ------------------------------------------------------------------------------------------------
 * Lifecycle.
 * ---------------------------------------------------------------------------------------------- */

describe("starting, stopping and restarting a service", () => {
    test("each action posts to its own endpoint", async () => {
        for (const [action, path] of [
            ["start", "/containers/one/start"],
            ["stop", `/containers/one/stop?t=${GRACE_SECONDS}`],
            ["restart", `/containers/one/restart?t=${GRACE_SECONDS}`],
        ] as const) {
            const fake = daemon({ rows: [{ Id: "one", Created: 1 }] });

            const outcome = success(await here.control({ service: "argon-core", action }, fake.ports));

            expect(fake.issued).toEqual([path]);
            expect(outcome.action).toBe(action);
            expect(outcome.service).toBe("argon-core");
        }
    });

    /**
     * Ten seconds is docker's default and it is short for an Orleans silo, which spends its `SIGTERM`
     * handing grains over. A silo killed part-way through that is one the rest of the cluster keeps
     * routing to until the membership timeout expires.
     */
    test("stop and restart give the container time to leave on its own", async () => {
        const fake = daemon({ rows: [{ Id: "one", Created: 1 }] });

        await here.control({ service: "argon-core", action: "stop" }, fake.ports);

        expect(GRACE_SECONDS).toBeGreaterThan(10);
        expect(fake.issued[0]).toContain(`t=${GRACE_SECONDS}`);
    });

    /**
     * Acting on one of two containers leaves the operator with a service that is half stopped and a
     * button that said it worked.
     */
    test("every container carrying the label is acted on", async () => {
        const fake = daemon({ rows: [{ Id: "one", Created: 2 }, { Id: "two", Created: 1 }] });

        const outcome = success(await here.control({ service: "argon-core", action: "stop" }, fake.ports));

        expect(outcome.containers).toBe(2);
        expect(fake.issued).toEqual([`/containers/one/stop?t=${GRACE_SECONDS}`, `/containers/two/stop?t=${GRACE_SECONDS}`]);
    });

    /** The daemon's own sentence is the only thing that says what went wrong, so it is what comes back. */
    test("a daemon that refuses is reported in its own words", async () => {
        const fake = daemon({
            rows: [{ Id: "one", Created: 1 }],
            refuseCommand: () => "docker answered 409 for /containers/one/start: port is already allocated",
        });

        const outcome = refusal(await here.control({ service: "argon-core", action: "start" }, fake.ports));

        expect(outcome.reason).toBe("failed");
        expect(outcome.problem).toContain("port is already allocated");
    });

    test("a failure on the second container does not report success for the first", async () => {
        const fake = daemon({
            rows: [{ Id: "one", Created: 2 }, { Id: "two", Created: 1 }],
            refuseCommand: (issued) => (issued === 1 ? "No such container: two" : undefined),
        });

        const outcome = refusal(await here.control({ service: "argon-core", action: "stop" }, fake.ports));

        expect(outcome.reason).toBe("failed");
        expect(outcome.problem).toContain("No such container");
        expect(fake.issued).toHaveLength(2);
    });

    test("a service nothing was created for is not a failure of the daemon", async () => {
        const fake = daemon({ rows: [] });

        const outcome = refusal(await here.control({ service: "argon-voice", action: "stop" }, fake.ports));

        expect(outcome.reason).toBe("unknown-service");
        expect(outcome.problem).toContain("argon-voice");
        expect(fake.issued).toEqual([]);
    });

    /**
     * The action is the field that decides what happens to the container, and it was the one field here
     * with no runtime gate: a compile-time union, interpolated into the path.
     *
     * Every string below was reachable with the cast `forged` performs. `"kill"` discards the grace
     * period this module argues for at length and SIGKILLs a silo mid-handover; `"stop?t=0&"` is the same
     * endpoint with a zero the daemon reads first; `"../../containers/prune"` arrives at the daemon as
     * `POST /containers/prune`, because `fetch` parses its argument as a WHATWG URL and dot segments are
     * removed there — which is every stopped container on the host, in every compose project on it.
     * `"constructor"` is here because a lookup table that answers for its own prototype is not a gate.
     */
    test("an action that is not one of the three never reaches the daemon", async () => {
        for (const action of [
            "kill",
            "pause",
            "stop?t=0&",
            "../../containers/prune",
            "../../containers/panel/stop",
            "constructor",
            "toString",
            "",
        ]) {
            const fake = daemon({ rows: [{ Id: "one", Created: 1 }] });

            const outcome = refusal(await here.control({ service: "argon-core", action: forged(action) }, fake.ports));

            expect(outcome.reason).toBe("unknown-action");
            expect(fake.issued).toEqual([]);

            // Refused before the listing, like the panel refusal: the daemon is not asked anything at all.
            expect(fake.paths).toEqual([]);
        }
    });

    /** The gate, on its own — and exported so a handler has somewhere honest to take a string. */
    test("asLifecycle is where a string off a request becomes an action", () => {
        expect(asLifecycle("stop")).toBe("stop");
        expect(asLifecycle("start")).toBe("start");
        expect(asLifecycle("restart")).toBe("restart");
        expect(asLifecycle("kill")).toBeUndefined();
        expect(asLifecycle("constructor")).toBeUndefined();
    });

    /**
     * The other half of the same path, and this one is not ours: the id comes out of the daemon's
     * listing. A row answering with `../../containers/x` would POST to `/containers/x` for the same
     * reason — around the label filter and around the panel refusal both.
     */
    test("a container id cannot become a path of its own", async () => {
        const fake = daemon({ rows: [{ Id: "../../containers/other/stop", Created: 1 }] });

        success(await here.control({ service: "argon-core", action: "stop" }, fake.ports));

        expect(new URL(`http://docker${fake.issued[0]}`).pathname).toBe(
            "/containers/..%2F..%2Fcontainers%2Fother%2Fstop/stop",
        );
    });

    /**
     * The listing can fail too, and it is the failure an operator meets first: pressing Stop while the
     * socket is unreachable. Every `refuseList` fixture was paired with `readLogs` or with the panel's
     * own service — which refuses before any listing is made — so this catch was never entered, and
     * replacing it with `throw cause` left the suite green and the button answering with a 500 that says
     * nothing.
     */
    test("a listing that fails is a named failure rather than a throw", async () => {
        const fake = daemon({ refuseList: "connect ENOENT /var/run/docker.sock" });

        const outcome = refusal(await here.control({ service: "argon-core", action: "stop" }, fake.ports));

        expect(outcome.reason).toBe("failed");
        expect(outcome.problem).toContain("docker.sock");
        expect(fake.issued).toEqual([]);
    });
});

/* ------------------------------------------------------------------------------------------------
 * Bounds.
 * ---------------------------------------------------------------------------------------------- */

describe("how much of a log can be asked for", () => {
    async function tailAsked(request: { readonly tail?: number }): Promise<string> {
        const fake = daemon({ rows: [{ Id: "one", Created: 1 }] });

        await here.readLogs({ service: "argon-core", ...request }, fake.ports);

        return fake.paths[1] ?? "";
    }

    test("the log endpoint asks for both streams and a bounded tail", async () => {
        const path = await tailAsked({});

        expect(path).toBe(`/containers/one/logs?stdout=1&stderr=1&tail=${DEFAULT_TAIL}`);
    });

    /**
     * `tail=all` is a real docker option and it is the one that must not be reachable from a query
     * string: a role up for a month has a log measured in gigabytes, and the whole of it would be read
     * into the memory of the process that holds the docker socket before anything decided it was too much.
     */
    test("an unbounded request is clamped rather than honoured", async () => {
        expect(await tailAsked({ tail: 10_000_000 })).toContain(`tail=${LONGEST_TAIL}`);
        expect(await tailAsked({ tail: Number.POSITIVE_INFINITY })).toContain(`tail=${LONGEST_TAIL}`);
        expect(await tailAsked({ tail: Number.NaN })).toContain(`tail=${DEFAULT_TAIL}`);
        expect(await tailAsked({ tail: -20 })).toContain("tail=1");
    });

    /**
     * The end of the log is what anyone opened it for, so the bound drops the beginning. Trimming from
     * the other end would return the start of the tail of the log — a window on nothing in particular.
     */
    test("too many lines keeps the newest and says it truncated", async () => {
        const written = Array.from({ length: MOST_LINES + 100 }, (_, index) => `line ${index}`);

        const fake = daemon({ rows: [{ Id: "one", Created: 1 }], chunks: [frame(1, `${written.join("\n")}\n`)] });

        const outcome = success(await here.readLogs({ service: "argon-core" }, fake.ports));

        expect(outcome.truncated).toBe(true);
        expect(outcome.lines).toHaveLength(MOST_LINES);
        expect(outcome.lines[0]?.text).toBe("line 100");
        expect(outcome.lines[outcome.lines.length - 1]?.text).toBe(`line ${MOST_LINES + 99}`);
    });

    /**
     * A handful of enormous lines is under the line bound and still far past what a browser should be
     * sent.
     *
     * Each of these is longer than `LONGEST_LINE`, so each arrives as two pieces — the reader applies the
     * same cut to a line that ended at a newline as to one that did not, which is what stops a single
     * enormous line from being dropped whole. So the assertion is about characters and about *which* end
     * survived, not about a count.
     */
    test("too many characters is bounded even when the line count is not", async () => {
        const written = Array.from({ length: 12 }, (_, index) => `${index}`.padEnd(100_000, "."));

        const fake = daemon({ rows: [{ Id: "one", Created: 1 }], chunks: [frame(1, `${written.join("\n")}\n`)] });

        const outcome = success(await here.readLogs({ service: "argon-core" }, fake.ports));
        const kept = outcome.lines.map((line) => line.text).join("");

        expect(outcome.truncated).toBe(true);
        expect(kept.length).toBeLessThanOrEqual(MOST_CHARACTERS);

        // The newest line is there in full and the oldest is gone: the bound drops the front.
        expect(kept.endsWith(`${written[11]}`)).toBe(true);
        expect(kept).not.toContain(`${written[0]}`);
    });

    test("a log that fits is not reported as truncated", async () => {
        const fake = daemon({ rows: [{ Id: "one", Created: 1 }], chunks: [frame(1, "one\ntwo\n")] });

        const outcome = success(await here.readLogs({ service: "argon-core" }, fake.ports));

        expect(outcome.truncated).toBe(false);
        expect(outcome.lines).toHaveLength(2);
    });

    test("a stream that dies part-way is a refusal rather than a short log presented as whole", async () => {
        const fake = daemon({ rows: [{ Id: "one", Created: 1 }], refuseStream: "unexpected EOF from the daemon" });

        const outcome = refusal(await here.readLogs({ service: "argon-core" }, fake.ports));

        expect(outcome.reason).toBe("unavailable");
        expect(outcome.problem).toContain("unexpected EOF");
    });

    /**
     * An upper pin, in one place.
     *
     * Every test above sizes its fixture from the constant it is checking, which is the one thing a bound
     * must not be measured against: `LONGEST_TAIL = 10_000_000` and `MOST_LINES = 200_000` both keep this
     * file green while restoring exactly the gigabyte-log condition the comments beside them argue
     * against. This does not pin the taste — it makes growing one a thing somebody decides here, rather
     * than a number that drifted with nothing red.
     */
    test("the bounds are the numbers the comments argue for", () => {
        expect(LONGEST_TAIL).toBe(5_000);
        expect(MOST_LINES).toBe(2_000);
        expect(MOST_CHARACTERS).toBe(1_000_000);
        expect(DEFAULT_TAIL).toBe(200);
    });

    /**
     * The value the fallback exists for: a handler that read `?tail=abc` off a query string without
     * calling `Number` on it. `Number.isNaN` is true of the NaN value alone, so a guard sitting before
     * the truncation never saw the NaN that `Math.trunc` was about to produce — and `tail=NaN` went onto
     * the path, to be rejected by the daemon and shown to the operator as docker being unavailable.
     */
    test("a tail that was never a number falls back rather than travelling into the path", async () => {
        const path = await tailAsked({ tail: "abc" as unknown as number });

        expect(path).toContain(`tail=${DEFAULT_TAIL}`);
        expect(path).not.toContain("NaN");
    });

    /** One line longer than every bound is still a log with something in it. */
    test("a single line past the character bound does not empty the result", async () => {
        const fake = daemon({
            rows: [{ Id: "one", Created: 1 }],
            chunks: [frame(1, `${"Z".repeat(MOST_CHARACTERS + 5)}\n`)],
        });

        const outcome = success(await here.readLogs({ service: "argon-core" }, fake.ports));

        expect(outcome.lines.length).toBeGreaterThan(0);
        expect(outcome.truncated).toBe(true);
        expect(Math.max(...outcome.lines.map((line) => line.text.length))).toBeLessThanOrEqual(65_536);
    });

    /**
     * `reader.end()` is what flushes the last line of a log that did not end in a newline — which is what
     * a crashing role leaves behind. Every other fixture here ends in one, so deleting that call left the
     * suite green while the panel dropped the line the operator opened the log for.
     */
    test("a log whose last line has no newline still shows that line", async () => {
        const fake = daemon({
            rows: [{ Id: "one", Created: 1 }],
            chunks: [frame(1, "starting\n"), frame(2, "Unhandled exception. System.Exception: boom")],
        });

        const outcome = success(await here.readLogs({ service: "argon-core" }, fake.ports));

        expect(outcome.lines).toEqual([
            { stream: "stdout", text: "starting" },
            { stream: "stderr", text: "Unhandled exception. System.Exception: boom" },
        ]);
    });
});
