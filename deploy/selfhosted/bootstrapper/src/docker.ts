import type { ServiceStatus } from "./model";

/**
 * What docker is actually doing, read from the daemon rather than from a CLI's printed output.
 *
 * The panel already holds the docker socket — §10 calls that root-equivalent and accepts it — so the
 * Engine API is right there, returning JSON with a schema. What this replaces is
 * `docker compose ps --format json`, whose output changed shape partway through compose v2 (one object
 * per line, then a single array) and which an installer does not get to pin, because it is whatever the
 * operator's distribution shipped. The parser that read both is gone with it.
 *
 * `compose up` and `compose pull` stay on the CLI, and that is not an inconsistency. They are the two
 * calls that genuinely need compose's own semantics — reconciling a file against reality — and they are
 * the two whose output is streamed to the operator verbatim rather than interpreted. Reading is what
 * moved; deciding what to run did not.
 *
 * Nothing here knows about Argon. It knows about compose's labels and docker's container states.
 */

/** Compose stamps these on every container it creates. They are the only reason this can work. */
export const COMPOSE_PROJECT_LABEL = "com.docker.compose.project";
export const COMPOSE_SERVICE_LABEL = "com.docker.compose.service";

/** Where the daemon listens inside the panel's container, as `compose.ts` mounts it. */
export const DOCKER_SOCKET = "/var/run/docker.sock";

/**
 * One GET against the Engine API, returning parsed JSON.
 *
 * A port, so that everything below it is testable without a daemon — and so that the *path* is
 * observable, which matters more than it looks: the label filter is what keeps this from reporting on
 * some other compose project that happens to be on the same machine.
 */
export type EngineRequest = (path: string) => Promise<unknown>;

/**
 * One POST against the Engine API, for the calls that change something rather than report it.
 *
 * Its own port rather than a `method` argument on {@link EngineRequest}, because the two differ in more
 * than the verb. This one succeeds with no body at all, and its *failures* are the part worth carrying:
 * "No such container" is a sentence the panel puts in front of an operator, where a bare `404` sends
 * them to the docker documentation to find out what the daemon meant.
 */
export type EngineCommand = (path: string) => Promise<void>;

/**
 * One GET whose body is bytes rather than JSON, handed over in the chunks it arrived in.
 *
 * Chunks and not one buffer, because the endpoint behind this is `/logs` and its stream is framed: an
 * 8-byte header can land with one byte in this chunk and seven in the next. A port that concatenated
 * first would hide the only case a demultiplexer gets wrong, and hide it specifically from the tests.
 * It is also the shape `follow=1` needs, which is where this goes next.
 */
export type EngineStream = (path: string) => AsyncIterable<Uint8Array>;

/** `/containers/json` rows, reduced to the two fields anything here reads. */
interface ContainerRow {
    readonly Id?: unknown;
    readonly Labels?: unknown;
}

/** `/containers/{id}/json`, likewise. */
interface Inspection {
    readonly State?: {
        readonly Status?: unknown;
        readonly ExitCode?: unknown;
        readonly Health?: { readonly Status?: unknown } | null;
    };
    readonly Config?: { readonly Labels?: unknown };
}

/**
 * Every container compose created for one project, and what state each is in.
 *
 * Two round trips per container rather than one for the lot, and that is deliberate. The list endpoint
 * carries health only inside a human sentence — `"Up 2 minutes (healthy)"` — and reading it back out of
 * there would be the same string-parsing this exists to remove, in a new place. The inspection carries
 * `State.Health.Status` as a field. Over a unix socket, for the ten-odd containers of one instance, the
 * extra calls are not measurable; a health check misread as healthy is an instance reported ready that
 * is not.
 *
 * A container compose created but that carries no service label is skipped rather than guessed at from
 * its name. Names are `<project>-<service>-<n>` by convention and by convention only — `container_name`
 * overrides them — so a name is a hint and the label is the answer.
 */
export async function projectStatus(project: string, request: EngineRequest): Promise<ServiceStatus[]> {
    // `all` because one container is *supposed* to have exited: the bundled store's init job runs once
    // and stops with a zero. Without it the readiness wait sits there for its full five minutes waiting
    // for something that finished in nine seconds.
    const filters = JSON.stringify({ label: [`${COMPOSE_PROJECT_LABEL}=${project}`] });
    const rows = await request(`/containers/json?all=true&filters=${encodeURIComponent(filters)}`);

    if (!Array.isArray(rows)) return [];

    const statuses: ServiceStatus[] = [];

    for (const row of rows as ContainerRow[]) {
        const id = typeof row?.Id === "string" ? row.Id : undefined;

        if (id === undefined) continue;

        const inspected = await request(`/containers/${id}/json`);
        const status = statusOf(inspected as Inspection, labelled(row?.Labels, COMPOSE_SERVICE_LABEL));

        if (status !== undefined) statuses.push(status);
    }

    return statuses;
}

/**
 * One inspection, as the readiness rules want it.
 *
 * Exported because it is the whole of the translation, and because every field in it has a way of being
 * absent that is not an error: a container with no healthcheck has no `Health` at all, and one that has
 * never run has an `ExitCode` of zero that means nothing. Reporting that zero as "exited cleanly" would
 * make a container stuck in `created` read as a job that finished.
 */
export function statusOf(inspection: Inspection, service: string | undefined): ServiceStatus | undefined {
    const named = service ?? labelled(inspection?.Config?.Labels, COMPOSE_SERVICE_LABEL);

    if (named === undefined) return undefined;

    const state = typeof inspection?.State?.Status === "string" ? inspection.State.Status : "unknown";
    const health = typeof inspection?.State?.Health?.Status === "string" ? inspection.State.Health.Status : undefined;
    const code = inspection?.State?.ExitCode;

    return {
        service: named,
        state,

        // Absent rather than empty when there is no healthcheck. The readiness rule treats an empty
        // string and a missing value the same way, and one of the two is a lie about a container that
        // was never asked how it feels.
        ...(health === undefined ? {} : { health }),

        // Only when it means something. Docker reports `ExitCode: 0` on a container that has not run
        // yet, and `unreadiness` reads a zero as "finished, and cleanly".
        ...(state === "exited" && typeof code === "number" ? { exitCode: code } : {}),
    };
}

function labelled(labels: unknown, name: string): string | undefined {
    if (typeof labels !== "object" || labels === null) return undefined;

    const value = (labels as Record<string, unknown>)[name];

    return typeof value === "string" && value.length > 0 ? value : undefined;
}

/**
 * The real transport: HTTP over the docker socket.
 *
 * Unversioned paths, so the daemon answers with whatever API version it supports rather than being
 * pinned to one this was written against — the fields read above have been stable for the life of the
 * API, and pinning would mean an installer that stops working on a newer docker for no gain.
 *
 * A non-2xx is thrown rather than returned. Every caller of this is inside `setup.ts`'s status call,
 * which already treats "docker would not say" as "nothing is running yet" — the distinction that
 * matters there is between an answer and no answer, and an exception is the honest shape of no answer.
 */
export function dockerEngine(socket: string = DOCKER_SOCKET): EngineRequest {
    return async (path) => {
        const response = await fetch(`http://docker${path}`, { unix: socket });

        if (!response.ok) throw new Error(`docker answered ${response.status} for ${path}`);

        return await response.json();
    };
}

/**
 * The real {@link EngineCommand}: a POST with nothing in it.
 *
 * **A 304 is a success.** Docker answers `Not Modified` when a container is told to start and is
 * already running, or to stop and is already stopped. Both of those are the state the operator asked
 * for, and reporting them as failures would put a red banner in the panel for "the thing you wanted is
 * already true" — which then teaches the operator to ignore red banners.
 *
 * The daemon's own sentence is carried into the error rather than only its status, for the reason
 * {@link EngineCommand} gives. It is read out of the JSON body docker sends with every error, and falls
 * back to the raw text, because an error path that can itself throw is an error path that hides the
 * error it was called about.
 */
export function dockerCommand(socket: string = DOCKER_SOCKET): EngineCommand {
    return async (path) => {
        const response = await fetch(`http://docker${path}`, { method: "POST", unix: socket });

        if (!response.ok && response.status !== 304) throw new Error(await complaintFrom(response, path));

        // Drained even though it is empty. An unread body holds the connection open against a socket
        // the panel makes one of these calls on per button press for the life of the instance.
        await response.text().catch(() => "");
    };
}

/**
 * The real {@link EngineStream}.
 *
 * A reader taken by hand rather than `for await` over the body, because a consumer of this is allowed to
 * stop early, and a `break` that leaves the response body unfinished leaves the connection to the daemon
 * open. The `finally` is what closes it, and it runs on a `break` because breaking a `for await` calls
 * `return()` on the generator.
 *
 * This used to claim that `panel/containers.ts` breaks out when its byte budget is spent. It does not,
 * and a reviewer was right to check: it drains the response and bounds what it *keeps*, because docker
 * returns the tail of a log and the newest lines are the last to arrive, so stopping early keeps the
 * wrong end. Corrected here rather than made true there. The `finally` stays either way — `follow=1` is
 * where this goes next, and that consumer has no other way to stop.
 */
export function dockerStream(socket: string = DOCKER_SOCKET): EngineStream {
    return async function* (path) {
        const response = await fetch(`http://docker${path}`, { unix: socket });

        if (!response.ok) throw new Error(await complaintFrom(response, path));

        const body = response.body;

        if (body === null) return;

        const reader = body.getReader();

        try {
            for (;;) {
                const { done, value } = await reader.read();

                if (done) return;
                if (value !== undefined) yield value;
            }
        } finally {
            reader.releaseLock();
            await body.cancel().catch(() => {});
        }
    };
}

/** The daemon's `{"message": "..."}`, or whatever it sent instead, appended to the status. */
async function complaintFrom(response: Response, path: string): Promise<string> {
    const said = await response.text().catch(() => "");

    let detail = said.trim();

    try {
        const parsed: unknown = JSON.parse(said);
        const message = (parsed as { readonly message?: unknown })?.message;

        if (typeof message === "string" && message.length > 0) detail = message;
    } catch {
        // Not JSON. `detail` is already the raw text, which is the best available answer.
    }

    return detail.length === 0
        ? `docker answered ${response.status} for ${path}`
        : `docker answered ${response.status} for ${path}: ${detail}`;
}
