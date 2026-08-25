import {
    COMPOSE_PROJECT_LABEL,
    COMPOSE_SERVICE_LABEL,
    type EngineCommand,
    type EngineRequest,
    type EngineStream,
} from "../docker";
import { COMPOSE_PROJECT, PANEL_SERVICE } from "../compose";

/**
 * The running instance as a control surface: what each service is doing, and what it said while doing it.
 *
 * This is the half of §9 that happens after an install — start, stop, restart, and the log an operator
 * reads when one of those did not do what they expected. It talks to the daemon directly rather than
 * shelling out to `docker compose logs`, for the reason `docker.ts` gives about `ps`: the CLI's output is
 * whatever the operator's distribution shipped, and this needs a schema.
 *
 * ## Services, never container names
 *
 * Everything here resolves a compose *service* to container ids through the labels compose stamps, the
 * same way `projectStatus` does. Nothing here ever builds a container name. `<project>-<service>-1` is a
 * convention that `container_name:` in the compose document silently overrides, and the failure that
 * produces is the worst kind available: a stop that succeeds against the wrong container, or one that
 * reports "no such container" for a service plainly running on the operator's screen.
 *
 * ## The panel cannot switch itself off
 *
 * The panel is a service in this project, so it is in every listing this makes, and stopping it is the
 * operator turning off the thing they are holding — with no way back except ssh, which is exactly what an
 * operator who installed a panel was trying not to need. So the refusal is a *type*: see
 * {@link ControllableService}. It covers restart and start as well as stop, because neither is better
 * than the third. A restart kills the process mid-request, so the operator never learns whether it
 * worked; a start is unreachable, because a stopped panel cannot serve the request that would start it.
 * One rule, in one place, for the whole service.
 *
 * Reading the panel's *logs* is allowed, and that is the point of scoping the refusal to lifecycle: the
 * log of the thing that just refused an operation is the first thing anyone will want.
 *
 * ## What leaves this module
 *
 * Log text is another process's output, and the roles print about configuration they have just read — so
 * a connection string, an object-storage key or the SFU's API secret can be in there. This module knows
 * no secrets and could not decide what to strip if it wanted to, so {@link LogPorts.redact} is required
 * rather than optional: a caller has to say what it wants removed, and the awkwardness of passing an
 * identity function is the moment somebody notices they never chose.
 *
 * Redaction runs on assembled *lines*, not on frames or chunks. A secret can straddle a frame boundary —
 * docker cuts frames at a byte count, not at anything meaningful — and a blind substring replacement over
 * half a secret matches nothing, which is a redactor that reports success and leaks.
 *
 * {@link LifecyclePorts} carries the same redactor, and for a while it did not. The argument for leaving
 * it off was that a lifecycle failure is only the daemon's own sentence about container state — "No such
 * container", "port is already allocated" — and that the daemon has never seen this instance's
 * configuration. The second half of that is false: compose hands the daemon this instance's Postgres
 * password and the SFU's keys as container *environment*, so the daemon holds every secret here in the
 * container config it is being asked about. A review pointed out that both strings come out of the same
 * `complaintFrom` in `docker.ts`, and this module was calling one of them sensitive and the byte-identical
 * other one not. One field is a cheap price for not having to be right about which of the daemon's
 * sentences can quote a container's environment back.
 *
 * ## Which instance, decided once
 *
 * Nothing here takes an {@link Instance} per call. {@link containersOf} binds one and returns the
 * functions closed over it, because both of the things this module protects are read off that value: the
 * panel refusal is `instance.panel`, and the label filter that keeps this to *our* compose project is
 * `instance.project`. As a field on the request it was a request input in everything but name — a handler
 * filling one from a JSON body takes `service` and `instance` out of the same object, and
 * `{project: "argon", panel: ""}` unprotects the panel while `{project: "someone-elses-stack"}` turns this
 * into a log reader for every compose project on the box, whose secrets this instance's redactor has never
 * heard of and will not remove. The brand on {@link ControllableService} proves the check ran; only
 * binding proves what it ran against. It costs the caller one construction at startup.
 */

/* ------------------------------------------------------------------------------------------------
 * Ports.
 * ---------------------------------------------------------------------------------------------- */

/**
 * What to take out of text that came from a container, before anyone reads it.
 *
 * A function and not a list of secrets, because this module must never hold one. `setup.ts` already has
 * the mint and the operator's own credentials, and already has `redact` to apply them; what belongs here
 * is the seam, not a second copy of the material.
 */
export type Redactor = (text: string) => string;

/** Enough of the daemon to start and stop things: one read to resolve the service, one POST per container. */
export interface LifecyclePorts {
    readonly request: EngineRequest;
    readonly command: EngineCommand;

    /** Applied to the daemon's complaints, for the reason the header gives. Required, like {@link LogPorts.redact}. */
    readonly redact: Redactor;
}

/** Enough of it to read a log: the same read, the byte stream, and the decision about what not to show. */
export interface LogPorts {
    readonly request: EngineRequest;
    readonly stream: EngineStream;
    readonly redact: Redactor;
}

/**
 * Which compose project this acts on, and which service inside it is the panel itself.
 *
 * `panel` is a field rather than a constant read from `compose.ts` inside the refusal, so the thing being
 * protected is named where the surface is bound rather than being invisible to everyone who uses it — and
 * so that a test can move it, which is the only way to tell a refusal that reads this field from one that
 * reached for the constant instead.
 *
 * Named at the *binding*, and not at the call: see {@link Containers} for why the difference is the whole
 * protection.
 */
export interface Instance {
    readonly project: string;
    readonly panel: string;
}

/** This installer's own instance. Both names come from `compose.ts`, which is where they are decided. */
export const ARGON_INSTANCE: Instance = { project: COMPOSE_PROJECT, panel: PANEL_SERVICE };

/**
 * Everything this module does, against one instance that the caller of these functions cannot change.
 *
 * The methods take their ports rather than closing over them, which is the one thing deliberately left
 * per-call: ports are the daemon, and a test that wants a daemon which refuses the listing supplies a
 * different one per case. The instance is not like that — it is the subject of the refusal, and a subject
 * a caller supplies is a subject an attacker supplies one level up.
 */
export interface Containers {
    /** {@link controllable}, against the bound instance. What the UI asks before drawing a stop button. */
    controllable(service: string): ControllableService | undefined;

    control(request: ControlRequest, ports: LifecyclePorts): Promise<ControlOutcome>;

    readLogs(request: LogRequest, ports: LogPorts): Promise<LogOutcome>;
}

export function containersOf(instance: Instance): Containers {
    return {
        controllable: (service) => controllable(instance, service),
        control: (request, ports) => control(instance, request, ports),
        readLogs: (request, ports) => readLogs(instance, request, ports),
    };
}

/**
 * The one a route handler is given.
 *
 * Bound here, at module scope, rather than by whoever wires the panel's routes — so that there is no
 * construction anywhere near a request to attach a request-derived instance to.
 */
export const ARGON_CONTAINERS: Containers = containersOf(ARGON_INSTANCE);

/* ------------------------------------------------------------------------------------------------
 * The refusal, as a type.
 * ---------------------------------------------------------------------------------------------- */

declare const checked: unique symbol;

/**
 * A service name that has been held against the panel's own and is not it.
 *
 * A branded string, and the brand does real work rather than decorating. Nothing below can be told to act
 * on a plain `string`: the only way to obtain one of these is {@link controllable}, which is the one
 * function in this module that knows which service is the panel. A `kill` or a `pause` added here next
 * year inherits the refusal by having to accept this type, where a comment saying "remember to exclude
 * the panel" inherits nothing.
 *
 * It reaches past this module too, which is the half that matters more. A route handler holds a service
 * name that came out of an HTTP request; it cannot hand that name to anything here without the refusal
 * happening somewhere, so the check cannot be skipped one level up either — which is where checks
 * normally get skipped.
 */
export type ControllableService = string & { readonly [checked]: true };

/**
 * The service, if it is one a lifecycle action may name — and nothing when it is the panel.
 *
 * Exported because the UI needs the same answer this does: a stop button drawn beside the panel's own row
 * is a button whose only outcome is an error, and the honest thing is not to draw it.
 *
 * A brand obtained here with some *other* instance buys nothing: {@link control} derives its own from the
 * instance it was bound to and never trusts one it was handed.
 */
export function controllable(instance: Instance, service: string): ControllableService | undefined {
    return service === instance.panel ? undefined : (service as ControllableService);
}

/* ------------------------------------------------------------------------------------------------
 * Lifecycle.
 * ---------------------------------------------------------------------------------------------- */

export type Lifecycle = "start" | "stop" | "restart";

/**
 * How long the daemon waits for a container to leave on its own before it is killed.
 *
 * Docker's default is ten seconds and compose's is the same, and ten is short for what runs here. An
 * Argon role is an Orleans silo: on `SIGTERM` it deactivates its grains and tells the cluster it is
 * going, and a silo killed before it finishes that is one the rest of the cluster keeps routing to until
 * the membership timeout expires — so a ten-second stop buys ten seconds and pays for it with a minute of
 * requests failing against grains that are not there. Thirty is long enough for the handover and short
 * enough that an operator watching a button does not conclude it has hung.
 */
export const GRACE_SECONDS = 30;

/**
 * The path each action POSTs to — a table, and not a verb interpolated into one.
 *
 * `start` takes no timeout because there is nothing to wait for: it returns once the container is running,
 * and docker ignores a `t` on it rather than rejecting it, which is the sort of thing that makes a reader
 * believe the parameter does something.
 *
 * The table is the shape it is because two reviewers arrived at the same hole from different ends.
 * {@link Lifecycle} is a compile-time union and nothing of it survives into the running program, so
 * `/containers/{id}/{action}` was a path built out of a value only TypeScript constrained — and `action`
 * is the field that decides what happens to the container. A handler filling a request from an HTTP body
 * has a `string` and needs the union, and the path of least resistance there is a cast. What that bought:
 * `"kill"` reached `/containers/<id>/kill`, discarding the grace period the paragraph above argues for;
 * `"stop?t=0&"` reached the same endpoint with a zero the daemon reads first; and
 * `"../../containers/prune"` reached the daemon as `POST /containers/prune`, because `fetch` parses its
 * argument as a WHATWG URL and dot-segments are removed there, before the request line is written. That
 * last one leaves the project entirely — every stopped container on the host, in every compose project —
 * and it walks straight around the {@link ControllableService} brand the rest of this file is built on.
 *
 * A table cannot express any of them. Nothing outside it is reachable, so there is no verb to sanitise
 * and no encoding to get right.
 */
const COMMAND_PATH: Readonly<Record<Lifecycle, (id: string) => string>> = {
    start: (id) => `/containers/${id}/start`,
    stop: (id) => `/containers/${id}/stop?t=${GRACE_SECONDS}`,
    restart: (id) => `/containers/${id}/restart?t=${GRACE_SECONDS}`,
};

/**
 * The action, if it is one — the runtime half of {@link Lifecycle}.
 *
 * Exported for the caller the cast above describes: a handler holding a string off a query or a body has
 * somewhere honest to take it, and gets `undefined` rather than a container endpoint of its own choosing.
 *
 * `Object.hasOwn` rather than a plain lookup, because a plain lookup on an object literal answers for
 * `"constructor"` and `"toString"` as well, and an inherited function is exactly the kind of hit that
 * turns a guard into a way through.
 */
export function asLifecycle(action: string): Lifecycle | undefined {
    return Object.hasOwn(COMMAND_PATH, action) ? (action as Lifecycle) : undefined;
}

export type ControlOutcome =
    | {
          readonly ok: true;
          readonly action: Lifecycle;
          readonly service: string;

          /** How many containers were acted on. One, unless somebody scaled the service. */
          readonly containers: number;
      }
    /** The service is the panel. See {@link ControllableService}. */
    | { readonly ok: false; readonly reason: "protected"; readonly problem: string }
    /** Nothing in this project carries that service label — a stale button, or a service never created. */
    | { readonly ok: false; readonly reason: "unknown-service"; readonly problem: string }
    /** The action was not one of the three. See {@link COMMAND_PATH}; nothing was asked of the daemon. */
    | { readonly ok: false; readonly reason: "unknown-action"; readonly problem: string }
    /** The daemon refused, in its own words. */
    | { readonly ok: false; readonly reason: "failed"; readonly problem: string };

export interface ControlRequest {
    readonly service: string;
    readonly action: Lifecycle;
}

/**
 * Starts, stops or restarts one compose service.
 *
 * Every container carrying the service label is acted on rather than only the first. Compose can scale a
 * service, this installer never does, and the gap between those two facts is where a bug lives: acting on
 * one of two containers leaves the operator looking at a service that is half stopped and a button that
 * reported success.
 *
 * A failure part-way through is reported as a failure, and the containers already dealt with are left
 * dealt with. There is no rollback worth writing — restarting the ones that stopped would be a second
 * operation the operator did not ask for, against a daemon that has just said it is unhappy.
 */
async function control(instance: Instance, request: ControlRequest, ports: LifecyclePorts): Promise<ControlOutcome> {
    const allowed = controllable(instance, request.service);

    // Before anything is resolved, so that a daemon which happens to be down cannot turn a refusal into
    // something that reads like a retryable failure — and so that no listing is even made for a name
    // this will never act on.
    if (allowed === undefined)
        return {
            ok: false,
            reason: "protected",
            problem: `'${request.service}' is this panel. Switching it off from inside itself would leave no way back into this machine except ssh, so it is not offered here.`,
        };

    // Likewise before anything is resolved. The declared type of `request.action` is not evidence at
    // runtime — see {@link COMMAND_PATH} for what a cast used to reach — so this is the check that makes
    // it evidence, and `act` below is only ever handed what came out of it.
    const action = asLifecycle(request.action);

    return action === undefined
        ? {
              ok: false,
              reason: "unknown-action",
              problem: `'${String(request.action)}' is not something this can do to a container. There are three: start, stop and restart.`,
          }
        : act(instance, allowed, action, ports);
}

/**
 * The only thing here that POSTs to the daemon — and it will not accept a plain string.
 *
 * That signature *is* the refusal, rather than a check written inside it. {@link controllable} is the
 * one way to obtain a {@link ControllableService}, so a `kill` or a `pause` written below next year
 * cannot reach a container without having gone through it first. Split out of {@link control} for
 * exactly that: while the two were one function the protection lived in an early return, which is a
 * thing a later edit can move, reorder or return past.
 */
async function act(
    instance: Instance,
    service: ControllableService,
    action: Lifecycle,
    ports: LifecyclePorts,
): Promise<ControlOutcome> {
    let containers: readonly Placement[];

    try {
        containers = await containersFor(instance, service, ports.request);
    } catch (cause) {
        return { ok: false, reason: "failed", problem: ports.redact(reasonOf(cause)) };
    }

    if (containers.length === 0)
        return {
            ok: false,
            reason: "unknown-service",
            problem: `no container in project '${instance.project}' is the service '${service}'.`,
        };

    for (const container of containers)
        try {
            // Encoded for the reason `containersFor` encodes its filter: the id is the daemon's own
            // string, and the only thing standing between a listing that answers with `../../x` and a
            // POST to `/x` is this call. Ordinary ids are hex and come back unchanged.
            await ports.command(COMMAND_PATH[action](encodeURIComponent(container.id)));
        } catch (cause) {
            return { ok: false, reason: "failed", problem: ports.redact(reasonOf(cause)) };
        }

    return { ok: true, action, service, containers: containers.length };
}

/* ------------------------------------------------------------------------------------------------
 * Logs: the framing, first.
 * ---------------------------------------------------------------------------------------------- */

export type LogStream = "stdout" | "stderr";

export interface LogLine {
    readonly stream: LogStream;
    readonly text: string;
}

/**
 * A frame header is eight bytes: one of stream type, three of padding, four of big-endian length.
 *
 * This is the trap the whole of {@link logReader} exists for. A container without a TTY has its two
 * streams multiplexed down one connection, and docker separates them by prefixing every chunk with that
 * header. Handed to a browser as it arrives, a log reads as text with a scattering of control bytes and a
 * stray length in it every few kilobytes — which looks like a corrupt log rather than like a framing that
 * was never undone, so the operator goes looking for a fault in their instance.
 */
const HEADER_BYTES = 8;

/**
 * The longest run of text this will hold before cutting it and calling it a line.
 *
 * A log line has no length limit — a stack trace serialised onto one line, or a base64 blob, is a real
 * thing a role prints — and a buffer that grows until a newline arrives is one an unlucky log can grow
 * without bound inside the process holding the docker socket. Sixty-four kilobytes is far past any line
 * worth reading and far short of anything that hurts.
 *
 * The cost, stated rather than hidden: a secret straddling the cut is redacted in neither half. That
 * needs a single line longer than this *and* a secret at exactly the wrong offset in it, and the
 * alternative is an out-of-memory in the process that owns the machine.
 */
const LONGEST_LINE = 65_536;

const EMPTY = new Uint8Array(0);

const BOTH: readonly LogStream[] = ["stdout", "stderr"];

/**
 * An incremental demultiplexer: bytes in, whole lines out.
 *
 * Incremental because the boundaries are not ours to choose. A header can arrive with one byte in this
 * chunk and seven in the next, and a payload can be cut anywhere; the only correct reader is one that
 * holds what it cannot yet use and says nothing about it. That is also why it is a separate exported
 * thing rather than a loop inside {@link readLogs} — the failure modes are all about boundaries, and a
 * test can only put a boundary somewhere if it can drive the reader directly.
 *
 * There is one {@link TextDecoder} per stream and they are kept across pushes, decoding in streaming
 * mode. Docker cuts frames at a byte count with no idea what a character is, so a multi-byte UTF-8
 * sequence is regularly split across two frames of the same stream; decoding each frame on its own turns
 * that character into replacement marks, and an operator reading a log full of them where their
 * instance's name should be will report it as a bug in Argon.
 */
export interface LogReader {
    /** Whole lines completed by this chunk, in the order the container produced them. */
    push(chunk: Uint8Array): readonly LogLine[];

    /** What is left when the stream ends: a last line with no newline after it, and any partial frame. */
    end(): readonly LogLine[];
}

export function logReader(redact: Redactor): LogReader {
    const decoders: Record<LogStream, TextDecoder> = {
        stdout: new TextDecoder("utf-8"),
        stderr: new TextDecoder("utf-8"),
    };

    /** Text seen since the last newline, per stream. Lines are what leave here; frames are not. */
    const held: Record<LogStream, string> = { stdout: "", stderr: "" };

    let pending: Uint8Array = EMPTY;

    /**
     * Whether this stream is framed at all, decided once from the first eight bytes.
     *
     * A container started with a TTY gets one stream and no headers, and docker's content type has not
     * been a reliable way to tell the two apart across API versions. The bytes are: a header opens with a
     * stream type of 0, 1 or 2 followed by three zero bytes, and no log begins with that. Left undecided
     * until eight bytes exist, because deciding from four would misread a raw log opening with a null.
     */
    let framed: boolean | undefined;

    /** Which stream last had text left over with no newline after it. Read by {@link LogReader.end}. */
    let lastHeld: LogStream | undefined;

    /**
     * One line out of one run of text.
     *
     * `terminated` is a parameter rather than a thing worked out from the text because of what the strip
     * below is *for*. It removes the carriage return of a CRLF, which is invisible in HTML and very
     * visible the moment anybody copies a line out of the panel — but that is only what a `\r` means when
     * a newline came directly after it. On a piece produced by the {@link LONGEST_LINE} cut the `\r` is
     * ordinary content that happened to land on the boundary, and stripping it there deleted a byte out
     * of the middle of a line: a review reconstructed 65,545 characters from 65,546 pushed in. Any
     * container that prints `\r` progress output on a long enough line hits it.
     */
    const complete = (stream: LogStream, text: string, terminated: boolean): LogLine => ({
        stream,
        text: redact(terminated && text.endsWith("\r") ? text.slice(0, -1) : text),
    });

    /**
     * One line's worth of text, in pieces no longer than {@link LONGEST_LINE}.
     *
     * The cut applies to a line that ended at a newline and not only to a trailing remainder, which is
     * the other half of the same review. The remainder-only version let a single 1,000,005-character
     * line through whole, and {@link readLogs} then dropped it to stay inside its own bound and answered
     * `{ok: true, lines: []}` — a read that lost everything, reported as a healthy read of nothing.
     */
    const emit = (stream: LogStream, text: string, terminated: boolean, out: LogLine[]): void => {
        let rest = text;

        while (rest.length > LONGEST_LINE) {
            out.push(complete(stream, rest.slice(0, LONGEST_LINE), false));
            rest = rest.slice(LONGEST_LINE);
        }

        out.push(complete(stream, rest, terminated));
    };

    const absorb = (stream: LogStream, text: string, out: LogLine[]): void => {
        if (text.length === 0) return;

        let rest = held[stream] + text;

        for (;;) {
            const at = rest.indexOf("\n");

            if (at < 0) break;

            emit(stream, rest.slice(0, at), true, out);
            rest = rest.slice(at + 1);
        }

        while (rest.length > LONGEST_LINE) {
            out.push(complete(stream, rest.slice(0, LONGEST_LINE), false));
            rest = rest.slice(LONGEST_LINE);
        }

        held[stream] = rest;

        if (rest.length > 0) lastHeld = stream;
    };

    return {
        push(chunk) {
            const out: LogLine[] = [];

            if (chunk.length > 0) pending = pending.length === 0 ? chunk : joined(pending, chunk);

            if (framed === undefined) {
                if (pending.length < HEADER_BYTES) return out;

                framed = looksFramed(pending);
            }

            if (!framed) {
                absorb("stdout", decoders.stdout.decode(pending, { stream: true }), out);
                pending = EMPTY;

                return out;
            }

            while (pending.length >= HEADER_BYTES) {
                const view = new DataView(pending.buffer, pending.byteOffset, pending.byteLength);
                const length = view.getUint32(4, false);

                // Announced but not all here. Hold everything, header included: the header is how the
                // next push knows which stream the rest of the payload belongs to.
                if (pending.length - HEADER_BYTES < length) break;

                const stream = streamOf(pending[0]);

                absorb(
                    stream,
                    decoders[stream].decode(pending.subarray(HEADER_BYTES, HEADER_BYTES + length), { stream: true }),
                    out,
                );

                pending = pending.subarray(HEADER_BYTES + length);
            }

            return out;
        },

        end() {
            const out: LogLine[] = [];

            // Fewer than eight bytes ever arrived, so no header was ever possible and whatever is there
            // is text. An empty log lands here too and produces nothing, which is the right answer.
            if (framed === undefined) framed = false;

            if (!framed) {
                if (pending.length > 0) absorb("stdout", decoders.stdout.decode(pending, { stream: true }), out);
            } else if (pending.length > HEADER_BYTES) {
                // A stream cut inside a frame. The payload that did arrive is real output and is shown; a
                // leftover of eight bytes or fewer is header and is dropped, because rendering four bytes
                // of a length as text is the binary garbage this reader exists to remove.
                const stream = streamOf(pending[0]);

                absorb(stream, decoders[stream].decode(pending.subarray(HEADER_BYTES), { stream: true }), out);
            }

            pending = EMPTY;

            // {@link LogReader.push} promises the order the container produced them in, and a fixed
            // stdout-then-stderr flush here does not keep that promise: a role that writes `connecting to
            // db` on stderr and then `web ready` on stdout, with the response cut before either got a
            // newline, came back inverted. `lastHeld` is the only ordering information there is by this
            // point — which stream most recently had text left over — and flushing that one last is
            // enough to put the two right, which is the whole of what can be wrong here.
            const order: readonly LogStream[] = lastHeld === "stdout" ? ["stderr", "stdout"] : BOTH;

            for (const stream of order) {
                // Flushed without `stream: true`, which is what turns a dangling partial character into a
                // replacement mark rather than silently dropping the last bytes of a truncated log.
                absorb(stream, decoders[stream].decode(), out);

                const rest = held[stream];

                if (rest.length > 0) {
                    // Nothing terminated this one, so a trailing carriage return is content and stays.
                    emit(stream, rest, false, out);
                    held[stream] = "";
                }
            }

            return out;
        },
    };
}

/** Stream type 0 is stdin, which never appears in a log; anything that is not stderr is treated as output. */
function streamOf(type: number | undefined): LogStream {
    return type === 2 ? "stderr" : "stdout";
}

function looksFramed(bytes: Uint8Array): boolean {
    const type = bytes[0];

    return type !== undefined && type <= 2 && bytes[1] === 0 && bytes[2] === 0 && bytes[3] === 0;
}

function joined(left: Uint8Array, right: Uint8Array): Uint8Array {
    const out = new Uint8Array(left.length + right.length);

    out.set(left, 0);
    out.set(right, left.length);

    return out;
}

/* ------------------------------------------------------------------------------------------------
 * Logs: reading one service's.
 * ---------------------------------------------------------------------------------------------- */

/** What an operator gets when they open a log without asking for a size. Roughly two screens of scrollback. */
export const DEFAULT_TAIL = 200;

/**
 * The most lines this will ask docker for, whatever was requested.
 *
 * `tail=all` is a real option and it is the one that must not be reachable from a query string: a role up
 * for a month has a log measured in gigabytes, and the panel would read all of it into the memory of the
 * process that holds the docker socket before deciding it was too much.
 */
export const LONGEST_TAIL = 5_000;

/** What is kept after demultiplexing, whatever docker sent. Both bounds hold; whichever bites first wins. */
export const MOST_LINES = 2_000;
export const MOST_CHARACTERS = 1_000_000;

export interface LogRequest {
    readonly service: string;

    /** Lines from the end. Clamped to {@link LONGEST_TAIL}; absent means {@link DEFAULT_TAIL}. */
    readonly tail?: number;
}

export type LogOutcome =
    | {
          readonly ok: true;
          readonly service: string;
          readonly lines: readonly LogLine[];

          /** Older lines were dropped to stay inside the bounds. The end of the log is always what is kept. */
          readonly truncated: boolean;
      }
    | { readonly ok: false; readonly reason: "unknown-service"; readonly problem: string }
    | { readonly ok: false; readonly reason: "unavailable"; readonly problem: string };

/**
 * The tail of one compose service's log, demultiplexed, split into lines and redacted.
 *
 * The newest container is read when a service has more than one. That is not only about scale: a listing
 * that includes stopped containers can hold the corpse of a container that was replaced, and reading the
 * dead one's log shows the operator output that stops exactly where their problem begins — with no
 * indication that they are looking at the wrong container.
 *
 * When the bounds bite, the *oldest* lines go. Docker already returned the tail, and the reason anyone
 * opens a log is to see what happened last; dropping from the other end would return the beginning of the
 * tail of the log, which is a window on nothing in particular.
 */
async function readLogs(instance: Instance, request: LogRequest, ports: LogPorts): Promise<LogOutcome> {
    let containers: readonly Placement[];

    try {
        containers = await containersFor(instance, request.service, ports.request);
    } catch (cause) {
        return { ok: false, reason: "unavailable", problem: ports.redact(reasonOf(cause)) };
    }

    const newest = containers[0];

    if (newest === undefined)
        return {
            ok: false,
            reason: "unknown-service",
            problem: `no container in project '${instance.project}' is the service '${request.service}'.`,
        };

    // An infinity is what a query string saying `all` becomes once something calls `Number` on it, and it
    // means "as much as you will give me" — so it clamps rather than falling back, because falling back
    // would answer a request for everything with the default and look like the bound was not applied.
    // Only a value that is not a number at all has nothing to clamp, and a `NaN` here would otherwise
    // travel into the path as the literal `tail=NaN` and be rejected by the daemon rather than by this.
    //
    // The guard sits after the truncation, and it did not always. `Number.isNaN` is true of the NaN value
    // alone, while the thing that *produces* NaN here is `Math.trunc` running on something that was never
    // a number — a route handler reading `?tail=abc` off a query string without calling `Number` first,
    // which is the caller this clamp is written for. Checked first, the guard missed exactly that and let
    // `tail=NaN` onto the path. (A reviewer suggested `Number.isFinite` instead, which also folds in the
    // infinity — and would answer `tail=all` with the default rather than the clamp, which is the
    // behaviour the paragraph above argues against.)
    const asked = request.tail ?? DEFAULT_TAIL;
    const wanted = Math.trunc(asked);
    const tail = Number.isNaN(wanted) ? DEFAULT_TAIL : Math.min(Math.max(wanted, 1), LONGEST_TAIL);

    const reader = logReader(ports.redact);
    const lines: LogLine[] = [];

    let characters = 0;
    let truncated = false;

    const keep = (produced: readonly LogLine[]): void => {
        for (const line of produced) {
            lines.push(line);
            characters += line.text.length + 1;
        }

        // A floor of one line. Emptying the array to satisfy a bound answers a log that had output with
        // `{ok: true, lines: []}`, which the caller cannot tell from a container that printed nothing —
        // and the operator opened the log precisely because something happened. It cannot cost the bound
        // anything: {@link LONGEST_LINE} is well under {@link MOST_CHARACTERS}, so the one line this
        // refuses to drop always fits inside it. The `undefined` check below is unreachable with the
        // floor in place and stays because `shift` is typed to allow it.
        while (lines.length > 1 && (lines.length > MOST_LINES || characters > MOST_CHARACTERS)) {
            const dropped = lines.shift();

            if (dropped === undefined) break;

            characters -= dropped.text.length + 1;
            truncated = true;
        }
    };

    try {
        // The stream is drained rather than broken out of when the bounds bite, which is a decision and
        // not an oversight. Docker returns the *tail*, and the newest lines are therefore the last ones
        // to arrive; stopping at a byte ceiling would keep the beginning of the tail and throw away the
        // end — the window on nothing in particular that the doc comment above rejects. What draining
        // costs is decode time, not memory: what is held at any moment is one frame, at most
        // {@link LONGEST_LINE} of unterminated text per stream, and a line list already bounded twice
        // over, so a long log is slow here and not fatal here. A reviewer read `docker.ts` as promising
        // the opposite; that promise has been corrected there rather than here.
        for await (const chunk of ports.stream(
            `/containers/${encodeURIComponent(newest.id)}/logs?stdout=1&stderr=1&tail=${tail}`,
        ))
            keep(reader.push(chunk));

        keep(reader.end());
    } catch (cause) {
        return { ok: false, reason: "unavailable", problem: ports.redact(reasonOf(cause)) };
    }

    return { ok: true, service: request.service, lines, truncated };
}

/* ------------------------------------------------------------------------------------------------
 * Resolution.
 * ---------------------------------------------------------------------------------------------- */

interface Placement {
    readonly id: string;

    /** Unix seconds, as the listing reports it. Zero when the daemon did not say, which sorts oldest. */
    readonly created: number;
}

/**
 * Every container compose created for one service of one project, newest first.
 *
 * Both labels go into the daemon's own filter rather than being matched here, which is one round trip
 * instead of a listing plus a scan — and, more usefully, means the *project* half of the match is a thing
 * a test can read off the requested path. Without it this would act on a service of the same name
 * belonging to somebody else's compose project on the same box.
 *
 * `all=true` for a reason `projectStatus` does not have: a stopped container is not in the default
 * listing, and a stopped container is precisely what `start` is for.
 */
async function containersFor(instance: Instance, service: string, request: EngineRequest): Promise<Placement[]> {
    const filters = JSON.stringify({
        label: [`${COMPOSE_PROJECT_LABEL}=${instance.project}`, `${COMPOSE_SERVICE_LABEL}=${service}`],
    });

    const rows = await request(`/containers/json?all=true&filters=${encodeURIComponent(filters)}`);

    if (!Array.isArray(rows)) return [];

    const found: Placement[] = [];

    for (const row of rows as readonly { readonly Id?: unknown; readonly Created?: unknown }[]) {
        const id = typeof row?.Id === "string" ? row.Id : "";

        if (id.length === 0) continue;

        found.push({ id, created: typeof row?.Created === "number" ? row.Created : 0 });
    }

    return found.sort((left, right) => right.created - left.created);
}

/**
 * Local rather than shared with `setup.ts`, which keeps its own: importing that module for one line would
 * pull the whole installer into the panel's log reader.
 */
function reasonOf(cause: unknown): string {
    return cause instanceof Error ? cause.message : String(cause);
}
