import { describe, expect, test } from "bun:test";
import { COMPOSE_PROJECT_LABEL, COMPOSE_SERVICE_LABEL, projectStatus, statusOf, type EngineRequest } from "./docker";
import { unreadiness } from "./setup";

/* ------------------------------------------------------------------------------------------------
 * Reading the daemon.
 *
 * This replaced a parser for `docker compose ps --format json`, so the bar it has to clear is not "it
 * returns something" — it is that every state the readiness rules distinguish still arrives
 * distinguishable. The failures worth catching are the ones that read as ready: a healthcheck that has
 * not passed, and a container that has never run reporting the exit code zero it is born with.
 * ---------------------------------------------------------------------------------------------- */

const PROJECT = "argon";

interface Container {
    readonly id: string;
    readonly service?: string;
    readonly state?: string;
    readonly exitCode?: number;
    readonly health?: string;
}

/** A daemon, as far as this module can tell. Records what it was asked, because the filter is the point. */
function daemon(containers: readonly Container[]): { request: EngineRequest; paths: string[] } {
    const paths: string[] = [];

    const request: EngineRequest = async (path) => {
        paths.push(path);

        if (path.startsWith("/containers/json"))
            return containers.map((container) => ({
                Id: container.id,
                Labels: {
                    [COMPOSE_PROJECT_LABEL]: PROJECT,
                    ...(container.service === undefined ? {} : { [COMPOSE_SERVICE_LABEL]: container.service }),
                },
            }));

        const found = containers.find((container) => path === `/containers/${container.id}/json`);

        if (found === undefined) throw new Error(`nothing asked for ${path}`);

        return {
            State: {
                Status: found.state ?? "running",
                ExitCode: found.exitCode ?? 0,
                ...(found.health === undefined ? {} : { Health: { Status: found.health } }),
            },
        };
    };

    return { request, paths };
}

describe("what the daemon is asked", () => {
    /**
     * The filter is what keeps this from reporting on somebody else's compose project.
     *
     * A machine can have others on it, and without the label this would list every container on the
     * box — then match them to expected service names and answer "ready" from a container that is
     * nothing to do with Argon.
     */
    test("containers are listed by the project label", async () => {
        const { request, paths } = daemon([{ id: "a", service: "argon-core" }]);

        await projectStatus(PROJECT, request);

        const listing = paths[0]!;

        expect(listing).toContain("/containers/json");
        expect(decodeURIComponent(listing)).toContain(`${COMPOSE_PROJECT_LABEL}=${PROJECT}`);
    });

    /**
     * One container is *supposed* to have exited: the bundled store's init job runs once and stops.
     *
     * Without `all`, docker lists only running containers, that job is invisible, and the readiness
     * wait sits there for its whole five minutes waiting for something that finished in nine seconds —
     * then reports it as the service that never came up.
     */
    test("stopped containers are listed too", async () => {
        const { request, paths } = daemon([{ id: "a", service: "argon-storage-init", state: "exited" }]);

        await projectStatus(PROJECT, request);

        expect(paths[0]).toContain("all=true");
    });
});

describe("what an inspection means", () => {
    test("a running service with a passing healthcheck is ready", () => {
        const status = statusOf({ State: { Status: "running", Health: { Status: "healthy" } } }, "argon-postgres");

        expect(status).toEqual({ service: "argon-postgres", state: "running", health: "healthy" });
        expect(unreadiness("argon-postgres", status)).toBeUndefined();
    });

    /**
     * The distinction the old parser had to make from a human sentence, and the one that decides
     * whether an instance is reported ready before its database will take a connection.
     */
    test("a healthcheck that has not passed is not ready", () => {
        const status = statusOf({ State: { Status: "running", Health: { Status: "starting" } } }, "argon-postgres");

        expect(status?.health).toBe("starting");
        expect(unreadiness("argon-postgres", status)).toContain("starting");
    });

    /**
     * No healthcheck is not the same as a healthcheck with nothing to say.
     *
     * Most services declare none, and for those "running" is the whole answer. Reporting an empty
     * string instead of nothing would be a claim about a container that was never asked.
     */
    test("a service without a healthcheck reports no health at all", () => {
        const status = statusOf({ State: { Status: "running" } }, "argon-core");

        expect(status).not.toHaveProperty("health");
        expect(unreadiness("argon-core", status)).toBeUndefined();
    });

    /**
     * Docker reports `ExitCode: 0` on a container that has never run.
     *
     * Carried through unconditionally, that zero reaches a readiness rule which reads "exited with a
     * zero" as a job that finished cleanly — so a container stuck in `created`, which is a real failure
     * with a real cause, would be reported as one that had done its work.
     */
    test("a container that has never run does not report an exit code", () => {
        const status = statusOf({ State: { Status: "created", ExitCode: 0 } }, "argon-storage-init");

        expect(status).not.toHaveProperty("exitCode");
        expect(unreadiness("argon-storage-init", status)).toContain("created");
    });

    test("a job that exited cleanly is ready, and one that did not is named with its code", () => {
        const clean = statusOf({ State: { Status: "exited", ExitCode: 0 } }, "argon-storage-init");
        const failed = statusOf({ State: { Status: "exited", ExitCode: 137 } }, "argon-storage-init");

        expect(clean?.exitCode).toBe(0);
        expect(unreadiness("argon-storage-init", clean)).toBeUndefined();
        expect(unreadiness("argon-storage-init", failed)).toContain("137");
    });

    /**
     * Names are `<project>-<service>-<n>` by convention only — `container_name` overrides them — so the
     * label is the answer and a container without one is skipped rather than guessed at.
     */
    test("a container with no service label is not reported", () => {
        expect(statusOf({ State: { Status: "running" } }, undefined)).toBeUndefined();
    });

    test("the label is read off the inspection when the listing did not carry it", () => {
        const status = statusOf(
            { State: { Status: "running" }, Config: { Labels: { [COMPOSE_SERVICE_LABEL]: "argon-edge" } } },
            undefined,
        );

        expect(status?.service).toBe("argon-edge");
    });

    /**
     * A daemon that answers something this has never seen must not take the installer down with it.
     * "unknown" is not ready, which is the safe direction: it keeps waiting and then names the service.
     */
    test("an answer with no state at all is unknown rather than a crash", () => {
        const status = statusOf({}, "argon-core");

        expect(status?.state).toBe("unknown");
        expect(unreadiness("argon-core", status)).toContain("unknown");
    });
});

describe("a whole project", () => {
    test("every labelled container comes back, and the unlabelled one does not", async () => {
        const { request } = daemon([
            { id: "a", service: "argon-core" },
            { id: "b", service: "argon-postgres", health: "healthy" },
            { id: "c", service: "argon-storage-init", state: "exited", exitCode: 0 },
            { id: "d" },
        ]);

        const statuses = await projectStatus(PROJECT, request);

        expect(statuses.map((status) => status.service)).toEqual([
            "argon-core",
            "argon-postgres",
            "argon-storage-init",
        ]);
    });

    /** Nothing created yet is an empty list, not a failure: it is the state every install starts in. */
    test("a project with no containers is empty rather than an error", async () => {
        expect(await projectStatus(PROJECT, daemon([]).request)).toEqual([]);
    });
});
