import { environmentContract, localFiles, resolveStartup } from "./config";
import { readCredential, writeCredential } from "./credential";
import { bootstrapFiles, parseBootstrapArguments } from "./emit";
import { panelFor } from "./panel/facade";
import { createServer } from "./server";
import { localStore } from "./setup";

/**
 * What the container runs.
 *
 * Deliberately the only file here that touches the world: it reads the environment and the disk, starts
 * the server, and waits for a signal. Every decision it makes was made in `config.ts`, where it can be
 * tested without any of that. If something in here grows an `if` that is about Argon rather than about
 * starting and stopping, it belongs over there.
 */

/**
 * How long in-flight work gets after a signal before it is dropped.
 *
 * Under docker's default the container has ten seconds between SIGTERM and SIGKILL, so this has to be
 * comfortably inside it: overrun the window and the process is killed anyway, but killed in the middle
 * of whatever it was doing rather than having chosen to give up. The one case that matters is a stop
 * that lands while the generator is part-way through writing conf.d/ and the secrets file — half a
 * configuration on disk is worse than none, and worse still with a secrets file that was not finished.
 */
const SHUTDOWN_GRACE_MS = 8_000;

/**
 * Log lines, plain and timestamped.
 *
 * Container logs, so no colour and no structure: whatever reads these is `docker logs` or a paste in a
 * bug report. Warnings and errors go to stderr so that a compose log with two streams still separates
 * them. Nothing here ever takes the bootstrap code as an argument, and the config object holding it is
 * never logged as a whole — a credential in a log file outlives every other precaution in this project.
 */
const log = {
    info: (message: string): void => console.log(`${new Date().toISOString()}  info   ${message}`),
    warn: (message: string): void => console.warn(`${new Date().toISOString()}  warn   ${message}`),
    error: (message: string): void => console.error(`${new Date().toISOString()}  error  ${message}`),
};

/**
 * Starts, serves, and resolves with the exit code once the server has stopped.
 *
 * Returning the code rather than calling `process.exit` keeps the one interesting decision — whether the
 * shutdown was clean — visible to a caller instead of buried in a handler.
 */
export async function main(environment: NodeJS.ProcessEnv = process.env): Promise<number> {
    const resolution = await resolveStartup(environment, localFiles);

    if (!resolution.ok) {
        log.error(`refusing to start: ${resolution.problem}`);
        log.error(`the install script sets these:\n${environmentContract()}`);

        return 1;
    }

    const { config, configDirectory, warnings } = resolution.startup;

    for (const warning of warnings) log.warn(warning);

    let server: ReturnType<typeof createServer>;

    try {
        server = createServer({
            ...config,

            // The panel's own password lives beside the configuration this install writes, because that
            // is the directory whose lifetime matches it: it has to survive a container restart and an
            // upgrade, and it has to be gone when the operator removes the install.
            credentials: {
                read: () => readCredential(configDirectory),
                write: (password) => writeCredential(configDirectory, password),
            },

            // The panel half. Over the same directory: what it reports on — the services, the
            // certificate, the backups, the history — is this install and no other.
            panel: panelFor(configDirectory),
        });
    } catch (cause) {
        // Two things fail here: the address is already in use, or the TLS stack will not take the
        // certificate pair that `config.ts` was only able to check the shape of. Both are the operator's
        // to fix and neither is worth a stack trace — but the cause's own message stays in, because
        // "the container exited" with nothing after it has cost somebody an afternoon before.
        log.error(
            `could not start the listener on ${config.hostname}:${config.port}: ${cause instanceof Error ? cause.message : String(cause)}`,
        );

        return 1;
    }

    log.info(`setup UI listening on ${server.server!.url.href}`);
    log.info(`configuration will be written under ${configDirectory}`);

    return await untilStopped(server);
}

/**
 * Waits for a signal, then stops the server without cutting anything off mid-response.
 *
 * `stop()` stops accepting connections and lets the ones with a request in flight finish; `stop(true)`
 * cuts them. The first is what a signal means and the second is what the deadline means, and a second
 * signal is an operator saying they are not waiting — all three end with the process gone, and only the
 * first ends with every response delivered.
 */
async function untilStopped(server: ReturnType<typeof createServer>): Promise<number> {
    return await new Promise<number>((resolve) => {
        let stopping = false;
        let settled = false;

        /**
         * The first path home wins, and the other says nothing.
         *
         * Two of these can be in flight at once — a second signal while the graceful stop is still
         * waiting — and without the guard the impatient one reports the requests it dropped and then the
         * graceful one, finishing a moment later, reports that everything closed cleanly. Two
         * contradictory lines about one shutdown, with the wrong one last.
         */
        const finish = (code: number): void => {
            if (settled) return;

            settled = true;

            resolve(code);
        };

        const gracefully = async (): Promise<void> => {
            let deadline: ReturnType<typeof setTimeout> | undefined;

            const expired = new Promise<"expired">((expire) => {
                deadline = setTimeout(() => expire("expired"), SHUTDOWN_GRACE_MS);
            });

            const outcome = await Promise.race([server.stop().then(() => "closed" as const), expired]);

            // Cleared either way: a timer still pending holds the loop open for its full duration, which
            // would turn a clean two-second shutdown into an eight-second one for no visible reason.
            clearTimeout(deadline);

            if (settled) return;

            if (outcome === "closed") {
                log.info("stopped; every connection closed");
                finish(0);

                return;
            }

            log.warn(`still ${server.server!.pendingRequests} request(s) in flight after ${SHUTDOWN_GRACE_MS}ms; dropping them`);

            await server.stop(true);

            // Non-zero because something was cut off. `docker stop` does not care, and the operator
            // reading these logs after an upgrade that went wrong does.
            finish(1);
        };

        const shutdown = (signal: string): void => {
            if (stopping) {
                log.warn(`${signal} again while stopping; dropping ${server.server!.pendingRequests} in-flight request(s)`);
                void server.stop(true).then(() => finish(1));

                return;
            }

            stopping = true;

            log.info(
                `${signal}; letting ${server.server!.pendingRequests} in-flight request(s) finish, up to ${SHUTDOWN_GRACE_MS}ms`,
            );

            void gracefully();
        };

        // SIGINT as well as SIGTERM: the same image is run in the foreground during development, and a
        // Ctrl-C that skipped this path would be the one shutdown nobody tests before shipping.
        //
        // The name is closed over rather than taken from the handler's argument, which is what the
        // runtime passes and what anything re-emitting the event by hand does not — a log line that
        // reads "undefined; letting 0 requests finish" is exactly as useless as it sounds.
        for (const signal of ["SIGTERM", "SIGINT"] as const) process.on(signal, () => shutdown(signal));
    });
}

/**
 * `emit-bootstrap`: write the front door's compose project and exit.
 *
 * Run by the install script before anything is serving, with the install root bind-mounted. See
 * `emit.ts` for why the script asks the image instead of writing these files itself.
 *
 * Two directories, and they are not the same one: what the daemon resolves bind mounts against (the
 * host path, passed as `--root`) and where this process can write (the mount point inside this
 * container). Confusing them is the classic version of this bug — a compose file full of paths that
 * exist only inside a container that has already exited.
 */
async function emit(argv: readonly string[]): Promise<number> {
    const parsed = parseBootstrapArguments(argv);

    if (!parsed.ok) {
        log.error(`refusing to emit: ${parsed.problem}`);

        return 1;
    }

    const directory = process.env["ARGON_BOOTSTRAP_CONFIG_DIR"] ?? "/argon";

    let files: readonly { path: string }[];

    try {
        const written = bootstrapFiles(parsed.phase);

        await localStore(directory).write(directory, written);

        files = written;
    } catch (cause) {
        // The shapes that terminate TLS here refuse to be built without a certificate, and a domain
        // that is not a hostname refuses too. Both are the operator's answer being wrong rather than
        // anything having gone wrong, so the message is the whole report.
        log.error(`refusing to emit: ${cause instanceof Error ? cause.message : String(cause)}`);

        return 1;
    }

    for (const file of files) log.info(`wrote ${file.path}`);

    return 0;
}

// Only when this file is what was run. It costs nothing and it means a test may import `main` — or a
// future entry point may wrap it — without a server appearing as a side effect of the import.
if (import.meta.main)
    process.exit(process.argv[2] === "emit-bootstrap" ? await emit(process.argv.slice(3)) : await main());
