import { readFile, stat } from "node:fs/promises";
import type { ServerConfig } from "./server";

/**
 * What the install script left behind, and whether it is enough to start.
 *
 * The script knows about the machine and about traffic; it knows nothing about Argon's configuration
 * (design §2). What it hands over is four facts — where the bootstrap code is, where TLS material is if
 * the path it took produced any, where Argon's configuration should be written, and what to listen on —
 * and it hands them over through the environment, because that is the one channel a `docker run` already
 * has and the one an operator can read back off their own command line afterwards.
 *
 * Everything decidable lives in this file as a pure function, so the refusals below can be tested without
 * a filesystem, a container or a certificate authority. The two things that genuinely need a disk —
 * reading a file, asking what mode it is — go through {@link InstallerFiles}, which the tests replace.
 * `main.ts` is the shell that supplies the real one and starts the server.
 *
 * ## The contract with the install script
 *
 * This table and the {@link ENVIRONMENT} constant below it are the whole contract. If you change one,
 * change both, and change the install script — nothing else in this process knows these names.
 *
 * | Variable                     | Holds                                                       | Required      |
 * |------------------------------|-------------------------------------------------------------|---------------|
 * | `ARGON_BOOTSTRAP_CODE_FILE`  | the file the script wrote the bootstrap code into, mode 600 | yes           |
 * | `ARGON_BOOTSTRAP_CONFIG_DIR` | the directory that will hold `conf.d/` and the secrets file | yes           |
 * | `ARGON_BOOTSTRAP_TLS_CERT`   | PEM certificate, when the path taken produced one           | with the key  |
 * | `ARGON_BOOTSTRAP_TLS_KEY`    | PEM private key, when the path taken produced one           | with the cert |
 * | `ARGON_BOOTSTRAP_VOICE_CERT` | PEM certificate for the media subdomain                     | with its key  |
 * | `ARGON_BOOTSTRAP_VOICE_KEY`  | PEM private key for the media subdomain                     | with its cert |
 * | `ARGON_BOOTSTRAP_HOST`       | address to listen on (default `0.0.0.0`)                    | no            |
 * | `ARGON_BOOTSTRAP_PORT`       | port to listen on (default `8443`)                          | no            |
 *
 * Both TLS variables or neither, and both readable or nothing starts. TLS is optional in the shape of
 * this code because §5 has a path — a tunnel, or a local install with no public address — that genuinely
 * serves plain HTTP. It is not optional in the sense that a broken certificate may be stepped over: an
 * instance that quietly answers on HTTP because its certificate file moved is the failure an operator
 * finds out about last, and from somebody else.
 */

/** The names, in one place, because they are a contract with a shell script and not a detail of this one. */
export const ENVIRONMENT = {
    codeFile: "ARGON_BOOTSTRAP_CODE_FILE",
    configDirectory: "ARGON_BOOTSTRAP_CONFIG_DIR",
    certificate: "ARGON_BOOTSTRAP_TLS_CERT",
    certificateKey: "ARGON_BOOTSTRAP_TLS_KEY",

    /**
     * A second pair, for the media subdomain, and it exists because of §5's Cloudflare shape.
     *
     * When voice is published directly rather than through the proxy, that subdomain is grey-clouded and
     * Cloudflare is not in its path at all — so it needs a certificate of its own, from a different
     * issuer than the origin one, expiring on a different day. Two live certificates on one machine is
     * the shape the design already warns about; this is where the second one comes in.
     *
     * Unset everywhere else. An instance that is not Cloudflare-proxied, or one whose voice rides the
     * main hostname, has nothing to put here.
     */
    voiceCertificate: "ARGON_BOOTSTRAP_VOICE_CERT",
    voiceCertificateKey: "ARGON_BOOTSTRAP_VOICE_KEY",
    host: "ARGON_BOOTSTRAP_HOST",
    port: "ARGON_BOOTSTRAP_PORT",
} as const;

/** What `createServer` would have defaulted to anyway; stated here so the startup log can name them. */
const DEFAULT_HOST = "0.0.0.0";
const DEFAULT_PORT = 8443;

/** Enough of a check to turn an opaque listener failure into a sentence. Not a validation of the chain. */
const PEM_MARKER = "-----BEGIN ";

export type Environment = Readonly<Record<string, string | undefined>>;

/**
 * What this process needs off the disk, narrowed to the two questions it actually asks.
 *
 * A port rather than `node:fs` directly, because every refusal below is about a file being missing,
 * empty or unreadable, and a test that has to arrange those on a real filesystem to prove them is a test
 * that gets quietly deleted the first time it is flaky on somebody's machine.
 */
export interface InstallerFiles {
    /** Rejects when the path cannot be read, for any reason. The reason is reported, not interpreted. */
    readText(path: string): Promise<string>;

    /** Rejects when the path does not exist. */
    describe(path: string): Promise<PathFacts>;
}

export interface PathFacts {
    readonly isDirectory: boolean;

    /**
     * POSIX mode bits, or `undefined` where the platform does not model them.
     *
     * Undefined is not "nothing is restricted" — it is "this platform cannot answer", and the caller
     * then says nothing rather than saying something wrong.
     */
    readonly mode: number | undefined;
}

export type RefusalReason =
    | "code-file-not-named"
    | "code-file-unreadable"
    | "code-empty"
    | "config-directory-not-named"
    | "config-directory-missing"
    | "tls-half-configured"
    | "tls-unreadable"
    | "tls-not-pem"
    | "port-invalid";

/** Why this process will not start, in a form a test can assert on and an operator can act on. */
export interface Refusal {
    readonly ok: false;
    readonly reason: RefusalReason;
    readonly problem: string;
}

/** Where TLS stands, decided from what was *named* rather than from what happens to be on disk. */
export type TlsIntent =
    | { readonly kind: "none" }
    | { readonly kind: "required"; readonly certificatePath: string; readonly keyPath: string };

/** The environment, understood. No file has been opened at this point. */
export interface Plan {
    readonly codeFile: string;
    readonly configDirectory: string;
    readonly host: string;
    readonly port: number;
    readonly tls: TlsIntent;
}

export type Planned = { readonly ok: true; readonly plan: Plan } | Refusal;

/** Everything `main.ts` needs to start, and everything it should say out loud first. */
export interface Startup {
    readonly config: ServerConfig;

    /** Where `conf.d/` and the secrets file go. Not part of `ServerConfig` yet — see the report. */
    readonly configDirectory: string;

    /** Things that are true and unwelcome. Collected rather than logged, so this stays pure. */
    readonly warnings: readonly string[];
}

export type Resolution = { readonly ok: true; readonly startup: Startup } | Refusal;

function refuse(reason: RefusalReason, problem: string): Refusal {
    return { ok: false, reason, problem };
}

function reasonOf(cause: unknown): string {
    return cause instanceof Error ? cause.message : String(cause);
}

/**
 * A variable's value, with unset and set-to-nothing treated the same.
 *
 * Compose puts `FOO=` into a container's environment for a variable the host did not define, so an empty
 * string is what "no certificate" looks like coming out of a tunnel install. Reading that as "a
 * certificate was named" would refuse to start on exactly the path §5 says may run without one.
 */
function named(environment: Environment, variable: string): string | undefined {
    const value = environment[variable]?.trim();

    return value === undefined || value.length === 0 ? undefined : value;
}

/**
 * Reads the environment into a plan, or refuses.
 *
 * Pure, and the first refusal wins. These variables are written by one script in one pass, so they are
 * wrong together far more often than they are wrong one at a time, and a list of one problem reads worse
 * than a sentence about it.
 */
export function planFromEnvironment(environment: Environment): Planned {
    const codeFile = named(environment, ENVIRONMENT.codeFile);

    if (codeFile === undefined)
        return refuse(
            "code-file-not-named",
            `${ENVIRONMENT.codeFile} is not set. The install script sets it to the file it wrote the bootstrap code into; without it there is nothing telling the operator apart from anybody else who can reach this port.`,
        );

    const configDirectory = named(environment, ENVIRONMENT.configDirectory);

    if (configDirectory === undefined)
        return refuse(
            "config-directory-not-named",
            `${ENVIRONMENT.configDirectory} is not set. It is the directory conf.d/ and the secrets file are written into, and finding out it is missing after the operator has answered the whole wizard is the expensive way to find out.`,
        );

    const certificatePath = named(environment, ENVIRONMENT.certificate);
    const keyPath = named(environment, ENVIRONMENT.certificateKey);

    if (certificatePath !== undefined && keyPath === undefined)
        return refuse(
            "tls-half-configured",
            `${ENVIRONMENT.certificate} names ${certificatePath} but ${ENVIRONMENT.certificateKey} is not set. A certificate is half a pair; starting on plain HTTP with one sitting unused on disk would look like it worked.`,
        );

    if (keyPath !== undefined && certificatePath === undefined)
        return refuse(
            "tls-half-configured",
            `${ENVIRONMENT.certificateKey} names ${keyPath} but ${ENVIRONMENT.certificate} is not set. A key on its own serves nothing.`,
        );

    const rawPort = named(environment, ENVIRONMENT.port);
    let port = DEFAULT_PORT;

    if (rawPort !== undefined) {
        port = Number(rawPort);

        // 0 is refused along with the nonsense, and it is the interesting one: it is the invalid value
        // that would *work*, by letting the kernel choose — while the install script has already printed
        // the operator a URL naming the port it was told to expect.
        if (!Number.isInteger(port) || port < 1 || port > 65535)
            return refuse(
                "port-invalid",
                `${ENVIRONMENT.port} is "${rawPort}", which is not a port to listen on. It has to be a whole number between 1 and 65535, and it has to be the one the install script printed.`,
            );
    }

    return {
        ok: true,
        plan: {
            codeFile,
            configDirectory,
            host: named(environment, ENVIRONMENT.host) ?? DEFAULT_HOST,
            port,
            tls:
                certificatePath !== undefined && keyPath !== undefined
                    ? { kind: "required", certificatePath, keyPath }
                    : { kind: "none" },
        },
    };
}

/**
 * Complains about a bootstrap code file anybody on the box can read.
 *
 * A complaint and not a refusal. The script wrote the file 0600, so a wider mode means something on this
 * machine widened it — and the operator is the only person who can find out what, which they cannot do
 * from an instance that refused to start. The code also stops mattering the moment setup produces a real
 * account. What is not acceptable is passing over it in silence.
 */
export function permissionComplaint(path: string, mode: number | undefined): string | undefined {
    if (mode === undefined) return undefined;

    const group = (mode & 0o040) !== 0;
    const world = (mode & 0o004) !== 0;

    if (!group && !world) return undefined;

    const who = group && world ? "its group and everyone else" : world ? "everyone else" : "its group";
    const bits = (mode & 0o777).toString(8).padStart(3, "0");

    return `the bootstrap code file ${path} is readable by ${who} (mode ${bits}). The installer writes it 0600; something widened it, and until setup finishes that file is the credential to this machine.`;
}

/** Reads one PEM file, refusing rather than degrading when it is not there or is not a PEM. */
async function readPem(
    files: InstallerFiles,
    variable: string,
    path: string,
): Promise<{ readonly ok: true; readonly pem: string } | Refusal> {
    let pem: string;

    try {
        pem = await files.readText(path);
    } catch (cause) {
        return refuse(
            "tls-unreadable",
            `${variable} names ${path}, which could not be read (${reasonOf(cause)}). A certificate that was configured and is not there is a refusal, not a reason to serve the setup UI in the clear.`,
        );
    }

    if (!pem.includes(PEM_MARKER))
        return refuse(
            "tls-not-pem",
            `${variable} names ${path}, which contains no PEM block. Both this process and Traefik's file provider want PEM; a DER file or a directory here fails at the listener with an error nobody can read.`,
        );

    return { ok: true, pem };
}

/**
 * Turns the environment into something startable, or into the one sentence explaining why not.
 *
 * The order of the checks is the order of the consequences: the credential first, then whether the
 * channel is the one the operator was told it would be, then whether there is anywhere to write the
 * answers. Every one is a refusal rather than a default, because each default this could pick — no code,
 * no TLS, no output directory — produces a worse instance than the one that did not start.
 */
export async function resolveStartup(environment: Environment, files: InstallerFiles): Promise<Resolution> {
    const planned = planFromEnvironment(environment);

    if (!planned.ok) return planned;

    const { plan } = planned;
    const warnings: string[] = [];

    let code: string;

    try {
        code = await files.readText(plan.codeFile);
    } catch (cause) {
        return refuse(
            "code-file-unreadable",
            `the bootstrap code was expected in ${plan.codeFile} (${ENVIRONMENT.codeFile}) and could not be read (${reasonOf(cause)}). That is a broken install rather than a reason to let anybody in.`,
        );
    }

    if (code.trim().length === 0)
        return refuse(
            "code-empty",
            `the bootstrap code file ${plan.codeFile} is empty. An empty code is not a code: it would let the first caller through.`,
        );

    // A stat that fails on a file that just read is odd enough that there is nothing useful to say about
    // it, so it costs the complaint and not the start. Never the other way round: this is the softest
    // check in the file and it must not be the one that keeps an instance down.
    try {
        const complaint = permissionComplaint(plan.codeFile, (await files.describe(plan.codeFile)).mode);

        if (complaint !== undefined) warnings.push(complaint);
    } catch {
        // Nothing. It read a moment ago; whatever this was, it is not something the operator can act on.
    }

    let tls: ServerConfig["tls"];

    if (plan.tls.kind === "required") {
        const certificate = await readPem(files, ENVIRONMENT.certificate, plan.tls.certificatePath);

        if (!certificate.ok) return certificate;

        const key = await readPem(files, ENVIRONMENT.certificateKey, plan.tls.keyPath);

        if (!key.ok) return key;

        tls = { cert: certificate.pem, key: key.pem };
    } else {
        warnings.push(
            `no certificate was configured (${ENVIRONMENT.certificate} is unset), so this process serves plain HTTP. That is the ordinary shape rather than a problem: the install script starts Traefik in front of this before setup begins, and it holds the certificate — TLS ends one hop earlier, on the compose network. What it would mean is an unencrypted channel only if this port were reachable directly, which nothing publishes.`,
        );
    }

    try {
        const facts = await files.describe(plan.configDirectory);

        if (!facts.isDirectory)
            return refuse(
                "config-directory-missing",
                `${ENVIRONMENT.configDirectory} names ${plan.configDirectory}, which is not a directory. conf.d/ and the secrets file are written into it.`,
            );
    } catch (cause) {
        return refuse(
            "config-directory-missing",
            `${ENVIRONMENT.configDirectory} names ${plan.configDirectory}, which could not be looked at (${reasonOf(cause)}). It is where conf.d/ and the secrets file go, and saying so now is cheaper than saying it after the operator has answered every question.`,
        );
    }

    return {
        ok: true,
        startup: {
            // Trimmed here as well as inside BootstrapAuth, because `printf '%s\n' "$code" > file` leaves
            // a newline and the operator types what they saw in their terminal, not what the shell added.
            config: { code: code.trim(), hostname: plan.host, port: plan.port, tls },
            configDirectory: plan.configDirectory,
            warnings,
        },
    };
}

/** The contract, rendered. Printed on a refusal, where an operator is already reading the logs. */
export function environmentContract(): string {
    const rows: readonly (readonly [string, string])[] = [
        [ENVIRONMENT.codeFile, "the file holding the bootstrap code, mode 600 (required)"],
        [ENVIRONMENT.configDirectory, "where conf.d/ and the secrets file are written (required)"],
        [ENVIRONMENT.certificate, "PEM certificate (with the key, or neither)"],
        [ENVIRONMENT.certificateKey, "PEM private key (with the certificate, or neither)"],
        [ENVIRONMENT.voiceCertificate, "PEM certificate for the media subdomain (with its key, or neither)"],
        [ENVIRONMENT.voiceCertificateKey, "PEM private key for the media subdomain (with its certificate)"],
        [ENVIRONMENT.host, `address to listen on (default ${DEFAULT_HOST})`],
        [ENVIRONMENT.port, `port to listen on (default ${DEFAULT_PORT})`],
    ];

    const width = Math.max(...rows.map(([variable]) => variable.length));

    return rows.map(([variable, meaning]) => `  ${variable.padEnd(width)}  ${meaning}`).join("\n");
}

/**
 * The real disk.
 *
 * It lives beside the port rather than in `main.ts` so that the one test that does want a real file —
 * proving this adapter and the decisions above agree about what "unreadable" means — does not have to
 * import the module whose job is to start a server.
 */
export const localFiles: InstallerFiles = {
    readText: (path) => readFile(path, "utf8"),

    describe: async (path) => {
        const facts = await stat(path);

        return {
            isDirectory: facts.isDirectory(),

            // Windows models a read-only bit and nothing about group or world, and reports 0o666 for an
            // ordinary file. Answering with that would print a permission complaint on every developer
            // run about a file nobody widened. The container is Linux; this branch is for the laptop.
            mode: process.platform === "win32" ? undefined : facts.mode,
        };
    },
};
