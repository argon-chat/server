import { afterEach, describe, expect, test } from "bun:test";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
    ENVIRONMENT,
    environmentContract,
    localFiles,
    permissionComplaint,
    planFromEnvironment,
    resolveStartup,
    type Environment,
    type InstallerFiles,
    type Plan,
    type Planned,
    type Refusal,
    type Startup,
} from "./config";

const CODE_FILE = "/run/argon/bootstrap.code";
const CONFIG_DIR = "/etc/argon";
const CERTIFICATE_FILE = "/run/argon/tls/fullchain.pem";
const KEY_FILE = "/run/argon/tls/privkey.pem";

const CERTIFICATE_PEM = "-----BEGIN CERTIFICATE-----\nnot a real certificate\n-----END CERTIFICATE-----\n";
const KEY_PEM = "-----BEGIN PRIVATE KEY-----\nnot a real key\n-----END PRIVATE KEY-----\n";

interface FakeEntry {
    /** Absent means the path exists but cannot be read — a directory, or a mode this process is not. */
    readonly text?: string;
    readonly mode?: number;
    readonly directory?: boolean;
}

/**
 * A disk, without one.
 *
 * Every refusal in `config.ts` is about a file being absent, empty, unreadable or the wrong shape, and
 * arranging four of those on a real filesystem — one of them a permission this test process may not be
 * able to set — is how a suite ends up skipped on somebody's machine.
 */
function fakeFiles(entries: Readonly<Record<string, FakeEntry>>): InstallerFiles {
    return {
        readText: async (path) => {
            const entry = entries[path];

            if (entry?.text === undefined) throw new Error(`ENOENT: no such file, open '${path}'`);

            return entry.text;
        },

        describe: async (path) => {
            const entry = entries[path];

            if (entry === undefined) throw new Error(`ENOENT: no such file, stat '${path}'`);

            return { isDirectory: entry.directory === true, mode: entry.mode };
        },
    };
}

/** What a correct install looks like coming out of the script, before a test spoils one part of it. */
function environment(overrides: Environment = {}): Environment {
    return {
        [ENVIRONMENT.codeFile]: CODE_FILE,
        [ENVIRONMENT.configDirectory]: CONFIG_DIR,
        ...overrides,
    };
}

function installed(overrides: Readonly<Record<string, FakeEntry>> = {}): InstallerFiles {
    return fakeFiles({
        [CODE_FILE]: { text: "quiet-harbour-42\n", mode: 0o100600 },
        [CONFIG_DIR]: { directory: true, mode: 0o40700 },
        ...overrides,
    });
}

/** Narrows, and puts the refusal's own sentence in the failure when a test expected one and did not get it. */
function refusalOf(result: { readonly ok: true } | Refusal): Refusal {
    if (result.ok) throw new Error("expected a refusal; it resolved to something startable");

    return result;
}

function startupOf(result: { readonly ok: true; readonly startup: Startup } | Refusal): Startup {
    if (!result.ok) throw new Error(`expected a startup; it refused: ${result.problem}`);

    return result.startup;
}

function planOf(result: Planned): Plan {
    if (!result.ok) throw new Error(`expected a plan; it refused: ${result.problem}`);

    return result.plan;
}

describe("the bootstrap code the install script left", () => {
    test("a complete install starts, with the code trimmed of the newline the shell wrote", async () => {
        const startup = startupOf(await resolveStartup(environment(), installed()));

        expect(startup.config.code).toBe("quiet-harbour-42");
        expect(startup.configDirectory).toBe(CONFIG_DIR);
    });

    /**
     * A missing code is a broken install, not an open one.
     *
     * The property is that there is no path through this process that starts without a credential. Lose
     * it and the setup UI — which holds the docker socket by the end of §10 — answers to whoever reaches
     * the port first, and it does so silently, because nothing about a running container says which of
     * its files failed to appear.
     */
    test("a bootstrap code file that is not there refuses, and names the file it wanted", async () => {
        const refusal = refusalOf(await resolveStartup(environment(), fakeFiles({ [CONFIG_DIR]: { directory: true } })));

        expect(refusal.reason).toBe("code-file-unreadable");
        expect(refusal.problem).toContain(CODE_FILE);
        expect(refusal.problem).toContain(ENVIRONMENT.codeFile);
    });

    /** Same property as above: an empty code would be a code every guess matches. */
    test("a bootstrap code file with nothing but whitespace in it refuses", async () => {
        const refusal = refusalOf(
            await resolveStartup(environment(), installed({ [CODE_FILE]: { text: "  \n\t\n" } })),
        );

        expect(refusal.reason).toBe("code-empty");
        expect(refusal.problem).toContain(CODE_FILE);
    });

    test("not naming the code file at all refuses before anything is opened", () => {
        const refusal = refusalOf(planFromEnvironment(environment({ [ENVIRONMENT.codeFile]: undefined })));

        expect(refusal.reason).toBe("code-file-not-named");
        expect(refusal.problem).toContain(ENVIRONMENT.codeFile);
    });
});

describe("TLS", () => {
    const withCertificate = (overrides: Environment = {}) =>
        environment({
            [ENVIRONMENT.certificate]: CERTIFICATE_FILE,
            [ENVIRONMENT.certificateKey]: KEY_FILE,
            ...overrides,
        });

    const withPems = (overrides: Readonly<Record<string, FakeEntry>> = {}) =>
        installed({
            [CERTIFICATE_FILE]: { text: CERTIFICATE_PEM },
            [KEY_FILE]: { text: KEY_PEM },
            ...overrides,
        });

    test("a configured certificate reaches the server as PEM text", async () => {
        const startup = startupOf(await resolveStartup(withCertificate(), withPems()));

        expect(startup.config.tls).toEqual({ cert: CERTIFICATE_PEM, key: KEY_PEM });
        expect(startup.warnings).toEqual([]);
    });

    /**
     * The property: no silent fall back from configured-but-broken TLS to plain HTTP.
     *
     * This is the one failure in the file that hides. A refusal is a container that will not start and
     * an operator who reads why; the alternative is an instance that answers, looks installed, and
     * carries the wizard's answers — and later the panel's session — across the network in the clear
     * because a path in an env var no longer resolves. Whoever simplifies this into a `catch` that
     * warns and continues will have made exactly that trade without noticing.
     */
    test("a certificate that was named and cannot be read refuses rather than dropping to HTTP", async () => {
        const refusal = refusalOf(
            await resolveStartup(withCertificate(), withPems({ [CERTIFICATE_FILE]: {} })),
        );

        expect(refusal.reason).toBe("tls-unreadable");
        expect(refusal.problem).toContain(CERTIFICATE_FILE);
    });

    /** The key is half of the same property; a certificate without one serves nothing. */
    test("a key that was named and cannot be read refuses too", async () => {
        const refusal = refusalOf(await resolveStartup(withCertificate(), withPems({ [KEY_FILE]: {} })));

        expect(refusal.reason).toBe("tls-unreadable");
        expect(refusal.problem).toContain(KEY_FILE);
    });

    test("a certificate named without its key refuses", () => {
        const refusal = refusalOf(
            planFromEnvironment(environment({ [ENVIRONMENT.certificate]: CERTIFICATE_FILE })),
        );

        expect(refusal.reason).toBe("tls-half-configured");
        expect(refusal.problem).toContain(ENVIRONMENT.certificateKey);
    });

    test("a key named without its certificate refuses", () => {
        const refusal = refusalOf(planFromEnvironment(environment({ [ENVIRONMENT.certificateKey]: KEY_FILE })));

        expect(refusal.reason).toBe("tls-half-configured");
        expect(refusal.problem).toContain(ENVIRONMENT.certificate);
    });

    test("something that is not PEM is refused here rather than at the listener", async () => {
        const refusal = refusalOf(
            await resolveStartup(withCertificate(), withPems({ [CERTIFICATE_FILE]: { text: " DER " } })),
        );

        expect(refusal.reason).toBe("tls-not-pem");
    });

    /**
     * §5 has shapes that genuinely serve plain HTTP — a Cloudflare tunnel carries the TLS itself, and a
     * local install has no public address to get a certificate for. So this is allowed. What it is not
     * is quiet: the warning is the only thing standing between "deliberate" and "nobody noticed".
     *
     * What is asserted is that it says the listener is plaintext and names the variable that would
     * change it — not the sentence around them. The sentence has already moved once: it used to warn
     * about answers crossing a network in the clear, which stopped being the ordinary case when Traefik
     * started coming up *before* setup. Pinning the prose would have made that correction a test
     * failure, which is how warnings end up frozen at whatever they said when they were written.
     */
    test("no certificate at all is allowed, and says so rather than going quiet", async () => {
        const startup = startupOf(await resolveStartup(environment(), installed()));

        expect(startup.config.tls).toBeUndefined();
        expect(startup.warnings.some((warning) => warning.includes("plain HTTP"))).toBe(true);
        expect(startup.warnings.some((warning) => warning.includes(ENVIRONMENT.certificate))).toBe(true);
    });

    /**
     * Compose writes `FOO=` for a variable the host never defined, so a tunnel install arrives with the
     * TLS variables present and empty. Read as "a certificate was named" that becomes a refusal on the
     * one path §5 says may run without one — the install works, then stops working, over a change in
     * how the script was invoked.
     */
    test("TLS variables that are present but empty are the same as unset", async () => {
        const startup = startupOf(
            await resolveStartup(
                environment({ [ENVIRONMENT.certificate]: "", [ENVIRONMENT.certificateKey]: "  " }),
                installed(),
            ),
        );

        expect(startup.config.tls).toBeUndefined();
    });
});

describe("the mode on the code file", () => {
    test("a file only its owner can read is not complained about", () => {
        expect(permissionComplaint(CODE_FILE, 0o100600)).toBeUndefined();
    });

    test("a file the group can read is complained about", () => {
        expect(permissionComplaint(CODE_FILE, 0o100640)).toContain("its group");
    });

    test("a file everyone can read is complained about, and the complaint says what the mode is", () => {
        const complaint = permissionComplaint(CODE_FILE, 0o100644);

        expect(complaint).toContain("everyone else");
        expect(complaint).toContain("644");
    });

    /** Windows has no answer to this question, and a guess would complain about every developer run. */
    test("a platform that cannot report a mode is not guessed at", () => {
        expect(permissionComplaint(CODE_FILE, undefined)).toBeUndefined();
    });

    /**
     * A complaint, not a refusal. The operator is the only person who can find out what widened the
     * file, and they cannot do it from an instance that refused to start over it.
     */
    test("a widened code file still starts, loudly", async () => {
        const startup = startupOf(
            await resolveStartup(environment(), installed({ [CODE_FILE]: { text: "quiet-harbour-42", mode: 0o100644 } })),
        );

        expect(startup.config.code).toBe("quiet-harbour-42");
        expect(startup.warnings.some((warning) => warning.includes("644"))).toBe(true);
    });
});

describe("the address to listen on", () => {
    test("defaults to what the server would have chosen anyway", () => {
        const plan = planOf(planFromEnvironment(environment()));

        expect(plan.host).toBe("0.0.0.0");
        expect(plan.port).toBe(8443);
    });

    test("a port the script named is used as given", () => {
        expect(planOf(planFromEnvironment(environment({ [ENVIRONMENT.port]: "9443" }))).port).toBe(9443);
    });

    test("a port that is not a number refuses", () => {
        expect(refusalOf(planFromEnvironment(environment({ [ENVIRONMENT.port]: "8443/tcp" }))).reason).toBe(
            "port-invalid",
        );
    });

    /**
     * Zero is the invalid value that would otherwise work: the kernel picks a port and the process
     * serves happily on one nobody was told about, while the operator's terminal is holding a URL from
     * the install script naming a different one.
     */
    test("port 0 refuses, because the URL was printed before this process started", () => {
        expect(refusalOf(planFromEnvironment(environment({ [ENVIRONMENT.port]: "0" }))).reason).toBe("port-invalid");
    });

    test("a port outside the range refuses", () => {
        expect(refusalOf(planFromEnvironment(environment({ [ENVIRONMENT.port]: "70000" }))).reason).toBe(
            "port-invalid",
        );
    });
});

describe("where the configuration goes", () => {
    /**
     * Checked at start rather than at the end of the wizard. The operator answers a dozen questions
     * before anything is written, and a missing mount discovered then costs all of them.
     */
    test("a configuration directory that is not there refuses before the operator answers anything", async () => {
        const refusal = refusalOf(
            await resolveStartup(environment(), fakeFiles({ [CODE_FILE]: { text: "quiet-harbour-42" } })),
        );

        expect(refusal.reason).toBe("config-directory-missing");
        expect(refusal.problem).toContain(CONFIG_DIR);
    });

    test("a file where the directory should be refuses", async () => {
        const refusal = refusalOf(
            await resolveStartup(environment(), installed({ [CONFIG_DIR]: { text: "not a directory" } })),
        );

        expect(refusal.reason).toBe("config-directory-missing");
    });

    test("not naming it at all refuses", () => {
        expect(
            refusalOf(planFromEnvironment(environment({ [ENVIRONMENT.configDirectory]: undefined }))).reason,
        ).toBe("config-directory-not-named");
    });
});

/**
 * The contract is documented in one place and read in another; this is the cheap check that the two
 * have not drifted. A variable this process reads and never mentions is one the operator debugging a
 * refusal has no way to find.
 */
test("the printed contract names every variable this process reads", () => {
    const contract = environmentContract();

    for (const variable of Object.values(ENVIRONMENT)) expect(contract).toContain(variable);
});

describe("on a real filesystem", () => {
    let directory: string | undefined;

    afterEach(async () => {
        if (directory !== undefined) await rm(directory, { recursive: true, force: true });

        directory = undefined;
    });

    async function install(files: Readonly<Record<string, string>>): Promise<string> {
        directory = await mkdtemp(join(tmpdir(), "argon-bootstrapper-"));

        for (const [name, contents] of Object.entries(files)) await writeFile(join(directory, name), contents);

        return directory;
    }

    test("reads the files the install script actually wrote", async () => {
        const at = await install({ "bootstrap.code": "quiet-harbour-42\n", "cert.pem": CERTIFICATE_PEM, "key.pem": KEY_PEM });

        const startup = startupOf(
            await resolveStartup(
                {
                    [ENVIRONMENT.codeFile]: join(at, "bootstrap.code"),
                    [ENVIRONMENT.configDirectory]: at,
                    [ENVIRONMENT.certificate]: join(at, "cert.pem"),
                    [ENVIRONMENT.certificateKey]: join(at, "key.pem"),
                },
                localFiles,
            ),
        );

        expect(startup.config.code).toBe("quiet-harbour-42");
        expect(startup.config.tls?.cert).toBe(CERTIFICATE_PEM);
    });

    /**
     * The same refusal as the fake-disk test above, run through the real adapter.
     *
     * Worth both: the fake proves the decision, and this proves that `localFiles` fails the way the
     * decision expects it to. An adapter that returned "" for a missing file instead of rejecting would
     * pass every test up there and serve the setup UI over plain HTTP down here.
     */
    test("a certificate path pointing at nothing refuses rather than starting without TLS", async () => {
        const at = await install({ "bootstrap.code": "quiet-harbour-42\n" });

        const refusal = refusalOf(
            await resolveStartup(
                {
                    [ENVIRONMENT.codeFile]: join(at, "bootstrap.code"),
                    [ENVIRONMENT.configDirectory]: at,
                    [ENVIRONMENT.certificate]: join(at, "cert.pem"),
                    [ENVIRONMENT.certificateKey]: join(at, "key.pem"),
                },
                localFiles,
            ),
        );

        expect(refusal.reason).toBe("tls-unreadable");
        expect(refusal.problem).toContain("cert.pem");
    });

    test("a bootstrap code file that was never written refuses", async () => {
        const at = await install({});

        const refusal = refusalOf(
            await resolveStartup(
                { [ENVIRONMENT.codeFile]: join(at, "bootstrap.code"), [ENVIRONMENT.configDirectory]: at },
                localFiles,
            ),
        );

        expect(refusal.reason).toBe("code-file-unreadable");
    });
});
