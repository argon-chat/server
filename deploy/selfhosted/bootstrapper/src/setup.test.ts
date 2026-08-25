import { ENVIRONMENT } from "./config";
import { afterEach, describe, expect, test } from "bun:test";
import { mkdtemp, readFile, rm, stat, writeFile } from "node:fs/promises";
import { mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { proofFor } from "./auth";
import { COMPOSE_FILENAME, COMPOSE_PROJECT, ENV_FILENAME, PANEL_PATH, PANEL_SERVICE } from "./compose";
import { DEPLOYMENT } from "./generate";
import type { CommandResult, ServerImage } from "./argon";
import type { GeneratedFile } from "./model";
import { createServer } from "./server";
import {
    MINT_FILE,
    REQUIRED_ROLES,
    Setup,
    adoptMint,
    checkAnswers,
    composeCommandFor,
    localStore,
    missingAnswers,
    panelFor,
    redact,
    certificatePair,
    referenceFor,
    setupFromEnvironment,
    takeStep,
    unreadyServices,
    type Certificates,
    type ComposeInvocation,
    type ComposeResult,
    type ComposeRunner,
    type ConfigStore,
    type ImageFor,
    type Mounts,
    type ServiceStatus,
    type Step,
} from "./setup";

/* ------------------------------------------------------------------------------------------------
 * Fixtures.
 *
 * The two commands have to agree with each other or `argon.ts` refuses the pair: `--roles` prints a
 * feature count per role and `--explain` prints the features themselves, and a fixture where those
 * disagree is testing the cross-check rather than what it meant to test. The section names are real ones
 * — `database` owns `Database`, `file-storage` owns `Storage` — because the generator's output is only
 * meaningful against ownership the server would actually report.
 * ---------------------------------------------------------------------------------------------- */

const ROLES = `role           kind    grains  features  description
aegis          client       0         2  sign-in, sessions, device attestation
commerce       silo         4         1  entitlements and payments
core           silo        26         3  space, channel, identity, session
entrypoint     client       0         3  Ion protocol, SignalR hub, auth, webhooks
jobs           silo         6         1  deletions, exports, mail, expired-row sweep
media          silo         3         1  avatars and attachments
voice          silo         2         1  LiveKit tokens and room lifecycle

7 role(s), 46 grain class(es) discovered in 2 assembly(ies)

topology distributed [entrypoint, aegis, core, media, jobs, voice]
`;

const EXPLAIN: Readonly<Record<string, string>> = {
    aegis: `role 'aegis' — Orleans client
  sign-in, sessions, device attestation

features, in configure order:
  argon-authorization  [auth]
  telemetry  [Metrics:BasicAuth]

reads 2 configuration section(s); each may also come from conf.d/<feature>.json
`,
    commerce: `role 'commerce' — Orleans silo
  entitlements and payments

features, in configure order:
  billing  [Commerce]

reads 1 configuration section(s); each may also come from conf.d/<feature>.json
`,
    core: `role 'core' — Orleans silo
  space, channel, identity, session

features, in configure order:
  database  [Database, Database:Regions]
  cache  [Redis]
  messages  [Messages]

reads 4 configuration section(s); each may also come from conf.d/<feature>.json
`,
    entrypoint: `role 'entrypoint' — Orleans client
  Ion protocol, SignalR hub, auth, webhooks

features, in configure order:
  kestrel  [Kestrel:Argon]
  jwt  [Jwt]
  cache  [Redis]

reads 3 configuration section(s); each may also come from conf.d/<feature>.json
`,
    jobs: `role 'jobs' — Orleans silo
  deletions, exports, mail, expired-row sweep

features, in configure order:
  mailing  [Mail]

reads 1 configuration section(s); each may also come from conf.d/<feature>.json
`,
    media: `role 'media' — Orleans silo
  avatars and attachments

features, in configure order:
  file-storage  [Storage, Storage:Limits]

reads 2 configuration section(s); each may also come from conf.d/<feature>.json
`,
    voice: `role 'voice' — Orleans silo
  LiveKit tokens and room lifecycle

features, in configure order:
  sfu  [CallKit]

reads 1 configuration section(s); each may also come from conf.d/<feature>.json
`,
};

const VERSION = "0.4.1";

/* ------------------------------------------------------------------------------------------------
 * The ports, faked.
 * ---------------------------------------------------------------------------------------------- */

interface Start {
    readonly version: string;
    readonly mounts: Mounts | undefined;
    readonly args: readonly string[];
}

interface FakeImage {
    readonly imageFor: ImageFor;
    /** Every container that would have been started, in order. */
    readonly starts: Start[];
    /** What `--validate-config --role X` answers. Replaced by the tests that care. */
    validate: (role: string, mounts: Mounts | undefined) => CommandResult;
}

/**
 * An image that answers from the fixtures above. No docker, no network, no container.
 *
 * It records the mounts as well as the arguments, because half of what this file proves is *what the
 * container was allowed to see* — a validation pointed at the install root instead of the staging copy
 * would pass every test that only looked at the verdict.
 */
function fakeImage(): FakeImage {
    const starts: Start[] = [];

    const image: FakeImage = {
        starts,
        validate: () => ({ stdout: "  => 0 error(s), 0 warning(s)", stderr: "", exitCode: 0 }),
        imageFor: (version, mounts): ServerImage => ({
            reference: referenceFor(version),

            version: async () => ({ value: version, source: "image-label", reference: referenceFor(version) }),

            run: async (args) => {
                starts.push({ version, mounts, args });

                if (args[0] === "--roles") return { stdout: ROLES, stderr: "", exitCode: 0 };

                if (args[0] === "--explain") {
                    const explained = EXPLAIN[args[1] ?? ""];

                    return explained === undefined
                        ? { stdout: "", stderr: `unknown role '${args[1]}'`, exitCode: 1 }
                        : { stdout: explained, stderr: "", exitCode: 0 };
                }

                if (args[0] === "--validate-config") return image.validate(args[2] ?? "", mounts);

                return { stdout: "", stderr: `no fixture for '${args.join(" ")}'`, exitCode: 64 };
            },
        }),
    };

    return image;
}

interface FakeStore {
    readonly store: ConfigStore;
    /** Every write, in order, with the directory it went to. */
    readonly writes: { readonly directory: string; readonly files: readonly GeneratedFile[] }[];
    /** What is on the fake disk, keyed the way a path would be. */
    readonly disk: Map<string, GeneratedFile>;
    readonly discarded: string[];
    /** What was written into the install itself, as opposed to staged. */
    committed(path: string): GeneratedFile | undefined;
}

const ROOT = join("/install");

/**
 * A disk, without one.
 *
 * The properties this file exists to prove are about *when* a write happens relative to a validation, and
 * a test that has to arrange that on a real filesystem proves it once and then rots. The one place a real
 * filesystem is used is the store's own adapter, at the bottom of this file, where the thing under test
 * is the mode bits.
 */
function fakeStore(seed: Readonly<Record<string, string>> = {}): FakeStore {
    const disk = new Map<string, GeneratedFile>();
    const writes: FakeStore["writes"] = [];
    const discarded: string[] = [];

    for (const [path, contents] of Object.entries(seed)) disk.set(join(ROOT, path), { path, contents, mode: 0o600 });

    let staged = 0;

    return {
        writes,
        disk,
        discarded,
        committed: (path) => disk.get(join(ROOT, path)),

        store: {
            root: ROOT,

            write: async (directory, files) => {
                writes.push({ directory, files });

                for (const file of files) disk.set(join(directory, file.path), file);
            },

            read: async (path) => disk.get(join(ROOT, path))?.contents,

            scratch: async () => join(ROOT, `.staging-${staged++}`),

            discard: async (directory) => void discarded.push(directory),
        },
    };
}

/**
 * Everything the start did, in order.
 *
 * One list rather than one per fake, because half of what the new tests prove is *ordering* — that the
 * images are pulled while this process still holds the port, and that the port goes before anything is
 * started. Two lists with timestamps would prove the same thing and read like an audit log.
 */
type Call = {
    readonly kind: "pull" | "up" | "status";
    readonly where?: ComposeInvocation;

    /** What `up` was told to start. The panel must never be among them; see the test that says so. */
    readonly services?: readonly string[];
};

interface FakeCompose {
    readonly runner: ComposeRunner;
    readonly calls: Call[];

    /** What each command answers. Replaced by the tests that care. */
    pull: () => ComposeResult;
    up: () => ComposeResult;
    status: (services: readonly string[]) => ServiceStatus[];

    /** What the command prints while it runs. A function, so a test can quote a file it wrote earlier. */
    emits: () => string[];
}

/**
 * The service names the generated document declares, read off the fake disk.
 *
 * The same place a real `docker compose ps` gets them from, and the reason the fixture does not carry a
 * hand-written list: `compose.ts` decides which services exist from the answers, and a list here would
 * go stale the first time a service is added and pass anyway.
 */
function declaredServices(store: FakeStore): string[] {
    const document = store.committed(COMPOSE_FILENAME);

    if (document === undefined) return [];

    return Object.keys((JSON.parse(document.contents) as { services: Record<string, unknown> }).services);
}

/** Compose, without a daemon. Answers success and reports every service running. */
function fakeCompose(store: FakeStore, calls: Call[]): FakeCompose {
    const compose: FakeCompose = {
        calls,
        pull: () => ({ ok: true, output: "argon-postgres Pulled" }),
        up: () => ({ ok: true, output: "argon-core Started" }),
        status: (services) => services.map((service) => ({ service, state: "running" })),
        emits: () => ["argon-postgres Pulling"],

        runner: {
            pull: async (where, onOutput) => {
                calls.push({ kind: "pull", where });

                for (const line of compose.emits()) onOutput(line);

                return compose.pull();
            },

            up: async (where, services, onOutput) => {
                calls.push({ kind: "up", where, services });

                for (const line of compose.emits()) onOutput(line);

                return compose.up();
            },

            status: async (where) => {
                calls.push({ kind: "status", where });

                return compose.status(declaredServices(store));
            },
        },
    };

    return compose;
}

interface Machine {
    readonly setup: Setup;
    readonly store: FakeStore;
    readonly image: FakeImage;
    readonly compose: FakeCompose;
    readonly calls: Call[];

    /** Every line that reached the progress port, which in the container is the log. */
    readonly reported: string[];
}

/**
 * A setup with every port faked and a listener registered, which is what `server.ts` does for real.
 *
 * The readiness bound is a tenth of a second here. The real one is five minutes, which is right for a
 * cold Postgres and wrong for a test suite: the tests that matter most are the ones where a service
 * never becomes ready, and at the production bound each of them would cost five minutes.
 */
function machine(
    seed: Readonly<Record<string, string>> = {},
    options: { readonly certificates?: Certificates } = {},
): Machine {
    const store = fakeStore(seed);
    const image = fakeImage();
    const calls: Call[] = [];
    const compose = fakeCompose(store, calls);
    const reported: string[] = [];

    const setup = new Setup({
        store: store.store,
        imageFor: image.imageFor,
        compose: compose.runner,
        certificates: options.certificates,
        progress: (line) => void reported.push(line),
        readiness: { timeoutMs: 100, pollMs: 10 },
    });

    return { setup, store, image, compose, calls, reported };
}

/* ------------------------------------------------------------------------------------------------
 * Answering the wizard.
 * ---------------------------------------------------------------------------------------------- */

const ROLE_ANSWER = [...REQUIRED_ROLES];

/** Every answer a complete setup needs, so a test can spoil exactly one of them. */
function steps(overrides: Step = {}): Step {
    return {
        domain: "chat.example.org",
        serverVersion: VERSION,
        traffic: { kind: "lets-encrypt" },
        roles: ROLE_ANSWER,
        voice: false,
        storage: { kind: "local" },
        ...overrides,
    };
}

/** A setup with every question answered, ready to apply. */
async function answered(
    overrides: Step = {},
    options: { readonly certificates?: Certificates } = {},
): Promise<Machine> {
    const built = machine({}, options);

    const submission = await built.setup.submit(steps(overrides));

    if (!submission.ok) throw new Error(`the fixture answers were refused: ${JSON.stringify(submission.rejections)}`);

    return built;
}

function secretsOf(store: FakeStore): Record<string, any> {
    const document = store.committed(DEPLOYMENT.secretsFile);

    if (document === undefined) throw new Error("no secrets document was written");

    return JSON.parse(document.contents) as Record<string, any>;
}

/** The one generated value that a running instance cannot survive losing. */
function databasePassword(store: FakeStore): string {
    const connection = secretsOf(store)["Database"]["ConnectionString"] as string;
    const password = /Password=([^;]+)/.exec(connection)?.[1];

    if (password === undefined) throw new Error(`no password in '${connection}'`);

    return password;
}

/* ------------------------------------------------------------------------------------------------
 * Tests.
 * ---------------------------------------------------------------------------------------------- */

describe("collecting the answers", () => {
    test("answers accumulate across steps", async () => {
        const { setup } = machine();

        await setup.submit({ domain: "chat.example.org" });
        await setup.submit({ serverVersion: VERSION });

        const state = await setup.state();

        expect(state.answers.domain).toBe("chat.example.org");
        expect(state.answers.serverVersion).toBe(VERSION);
        expect(state.missing).toContain("storage");
        expect(state.stage).toBe("awaiting-configuration");
    });

    test("every answer in means ready, and not applied", async () => {
        const { setup } = await answered();

        const state = await setup.state();

        expect(state.missing).toEqual([]);
        expect(state.stage).toBe("ready");
        expect(state.written).toBeUndefined();
    });

    /**
     * All or nothing. A step that half-applies leaves the operator looking at a form where some fields
     * took and some did not, with one error message between them to explain it.
     */
    test("a refused step changes nothing", async () => {
        const { setup } = await answered();

        const refused = await setup.submit({ domain: "https://chat.example.org/setup", voice: true });

        expect(refused.ok).toBe(false);
        expect((await setup.state()).answers.domain).toBe("chat.example.org");
        expect((await setup.state()).answers.voice).toBe(false);
    });

    test("a domain that is a URL is refused, and says which field", () => {
        const rejections = checkAnswers({ domain: "https://chat.example.org" }, {});

        expect(rejections).toHaveLength(1);
        expect(rejections[0]?.field).toBe("domain");
        expect(rejections[0]?.problem).toContain("URL");
    });

    /**
     * `Storage:Endpoint` is a host and not a URL: one of its two readers builds `{bucket}.{Endpoint}`, so
     * a bucket path left on the end comes back as `argon.s3.example.com/argon` and fails at the first
     * upload looking like a network fault. A scheme is fine — `splitEndpoint` takes it off.
     */
    test("a storage endpoint with a path in it is refused; a scheme is not", () => {
        const credentials = { objectStorage: { accessKey: "AKIAEXAMPLE", secretKey: "s3cret-example-key" } };
        const withPath = { kind: "s3", endpoint: "https://s3.example.com/argon", bucket: "argon" } as const;
        const withScheme = { kind: "s3", endpoint: "https://s3.example.com", bucket: "argon" } as const;

        expect(checkAnswers({ storage: withPath }, credentials)).toHaveLength(1);
        expect(checkAnswers({ storage: withScheme }, credentials)).toEqual([]);
    });

    /**
     * §6: `dev` is every role in one process, a shape that exists nowhere else — so it breaks in ways a
     * real deployment never sees and gets fixed last. The other half is the roles nobody may drop: an
     * instance without `aegis` installs cleanly and cannot sign anybody in.
     */
    test("the roles that are not optional, and the one that is not offered", () => {
        expect(checkAnswers({ roles: ["dev"] }, {}).map((rejection) => rejection.problem).join(" ")).toContain("dev");

        const missingAegis = ROLE_ANSWER.filter((role) => role !== "aegis");

        expect(checkAnswers({ roles: missingAegis }, {}).map((rejection) => rejection.problem).join(" ")).toContain(
            "aegis",
        );
    });

    /**
     * The combination `generate.ts` cannot express: a tunnelled instance has one hostname and it is the
     * tunnel's, and media does not travel through a tunnel. By the time answers reach the generator there
     * is nowhere left to put the answer, so the refusal has to be here.
     */
    test("voice through a Cloudflare tunnel is refused, because media is UDP", () => {
        const rejections = checkAnswers({ voice: true, traffic: { kind: "cloudflare-tunnel" }, roles: [] }, {});

        expect(rejections.some((rejection) => rejection.field === "voice")).toBe(true);
    });

    test("voice and the voice role have to agree, in both directions", () => {
        const withVoice = [...ROLE_ANSWER, "voice"];

        expect(checkAnswers({ voice: true, roles: ROLE_ANSWER }, {})).not.toEqual([]);
        expect(checkAnswers({ voice: false, roles: withVoice }, {})).not.toEqual([]);
        expect(checkAnswers({ voice: true, roles: withVoice }, {})).toEqual([]);
    });

    test("a role this image does not have is refused once the image has been asked", async () => {
        const { setup } = await answered();

        await setup.interrogate();

        const refused = await setup.submit({ roles: [...ROLE_ANSWER, "analytics"] });

        expect(refused.ok).toBe(false);
        expect(refused.ok === false && refused.rejections[0]?.problem).toContain("analytics");
    });

    test("a hostname is normalised rather than refused for its case or its root dot", () => {
        const held = takeStep({ answers: {}, credentials: {} }, { domain: "Chat.Example.ORG." });

        expect(held.answers.domain).toBe("chat.example.org");
    });

    /**
     * "Behind the proxy" is what leaving the media host out means. The same hostname repeated into that
     * answer would generate identical files while reading as a different decision — one the operator
     * would later be told to change to fix voice that was never broken.
     */
    test("a media host equal to the domain is not a second host", () => {
        const held = takeStep(
            { answers: { domain: "chat.example.org" }, credentials: {} },
            { traffic: { kind: "cloudflare-proxied", voiceHost: "chat.example.org" } },
        );

        expect(held.answers.traffic).toEqual({ kind: "cloudflare-proxied" });
    });

    test("missing answers are reported in the order a wizard would ask for them", () => {
        expect(missingAnswers({ domain: "chat.example.org" })[0]).toBe("serverVersion");
    });
});

describe("the operator's own credentials", () => {
    /**
     * The property that makes "no secret in a response" structural rather than remembered: the keys are
     * split out of the storage answer at the door, and what is kept is a `StorageChoice`, which has
     * nowhere to put them.
     */
    test("object storage keys never reach the state", async () => {
        const { setup } = await answered({
            storage: {
                kind: "s3",
                endpoint: "https://s3.example.com",
                bucket: "argon",
                accessKey: "AKIA-MARKED-ACCESS",
                secretKey: "MARKED-SECRET-KEY-VALUE",
            },
        });

        const state = await setup.state();

        expect(JSON.stringify(state)).not.toContain("MARKED-SECRET-KEY-VALUE");
        expect(JSON.stringify(state)).not.toContain("AKIA-MARKED-ACCESS");
        expect(state.credentials).toEqual(["object storage"]);
    });

    /**
     * Kept when the bucket changes, because a form that demands a secret be retyped to fix a typo in a
     * bucket name is a form people paste secrets into. Dropped when the answer becomes local, because
     * nothing should hold a credential it has no use for.
     */
    test("keys survive a changed bucket and do not survive a changed answer", async () => {
        const { setup } = await answered({
            storage: {
                kind: "s3",
                endpoint: "https://s3.example.com",
                bucket: "argon",
                accessKey: "AKIAEXAMPLE",
                secretKey: "s3cret-example-key",
            },
        });

        await setup.submit({ storage: { kind: "s3", endpoint: "https://s3.example.com", bucket: "argon-two" } });

        expect((await setup.state()).credentials).toEqual(["object storage"]);

        await setup.submit({ storage: { kind: "local" } });

        expect((await setup.state()).credentials).toEqual([]);
    });

    test("an S3 bucket with no keys is refused, and the refusal does not quote a key", () => {
        const rejections = checkAnswers({ storage: { kind: "s3", endpoint: "s3.example.com", bucket: "argon" } }, {});

        expect(rejections.some((rejection) => rejection.field === "storage")).toBe(true);
    });
});

describe("asking the image", () => {
    test("the roles come back, and the version with them", async () => {
        const { setup } = await answered();

        const outcome = await setup.interrogate();

        expect(outcome.ok).toBe(true);
        expect(outcome.ok && outcome.roles).toHaveLength(7);
        expect(outcome.ok && outcome.pairing.ok).toBe(true);
        expect((await setup.state()).image?.reference).toContain(VERSION);
    });

    /**
     * Interrogation is a container start on a box that was sized for one. Two tabs, or one impatient
     * operator, must not become two containers reflecting over every assembly at the same time.
     */
    test("two callers at once start one container, not two", async () => {
        const { setup, image } = await answered();

        await Promise.all([setup.interrogate(), setup.interrogate(), setup.interrogate()]);

        expect(image.starts.filter((start) => start.args[0] === "--roles")).toHaveLength(1);
    });

    test("interrogating twice does not ask twice", async () => {
        const { setup, image } = await answered();

        await setup.interrogate();
        await setup.interrogate();

        expect(image.starts.filter((start) => start.args[0] === "--roles")).toHaveLength(1);
    });

    /** Changing the answer changes the image, so the old image's roles are not the answer any more. */
    test("a changed version is a new interrogation, and the old roles stop being offered", async () => {
        const { setup, image } = await answered();

        await setup.interrogate();
        await setup.submit({ serverVersion: "0.4.2" });

        expect((await setup.state()).image).toBeUndefined();

        await setup.interrogate();

        expect(image.starts.filter((start) => start.args[0] === "--roles")).toHaveLength(2);
    });

    /**
     * One container per role, and only once. `--explain` reflects over every assembly on a cold container,
     * so an apply that asked again after a refusal would cost the operator that much a second time for an
     * answer that cannot have changed.
     */
    test("each role is explained once, however many times the apply runs", async () => {
        const { setup, image } = await answered();

        await setup.apply();
        await setup.apply();

        expect(image.starts.filter((start) => start.args[0] === "--explain")).toHaveLength(ROLE_ANSWER.length);
    });

    test("the interrogation sees no configuration at all", async () => {
        const { setup, image } = await answered();

        await setup.interrogate();

        expect(image.starts[0]?.mounts).toBeUndefined();
    });

    test("no version answered is a different failure from docker not working", async () => {
        const { setup } = machine();

        const outcome = await setup.interrogate();

        expect(outcome.ok === false && outcome.reason).toBe("no-version");
    });
});

describe("applying", () => {
    test("the files land, with the modes they carry", async () => {
        const { setup, store } = await answered();

        const outcome = await setup.apply();

        expect(outcome.ok).toBe(true);
        expect(store.committed(DEPLOYMENT.secretsFile)?.mode).toBe(0o600);
        expect(store.committed(`${DEPLOYMENT.confD}/database.json`)?.mode).toBe(0o644);
        expect((await setup.state()).stage).toBe("running");
    });

    /**
     * The whole point of the ordering. Writing first and validating after is the shape this falls into on
     * its own — `--validate-config` wants files on a disk — and it leaves a half-configured install behind
     * on every refusal, on a box where the operator cannot tell which half is which.
     */
    test("a failed validation writes nothing into the install", async () => {
        const { setup, store, image } = await answered();

        image.validate = (role) => ({
            stdout: `role '${role}'\n  [E] C3 unknown section\n  => 1 error(s), 0 warning(s)`,
            stderr: "",
            exitCode: 1,
        });

        const outcome = await setup.apply();

        expect(outcome.ok).toBe(false);
        expect(outcome.ok === false && outcome.reason).toBe("invalid");
        expect(store.writes.every((write) => write.directory !== ROOT)).toBe(true);
        expect(store.committed(DEPLOYMENT.secretsFile)).toBeUndefined();
        expect(store.committed(MINT_FILE)).toBeUndefined();
        expect((await setup.state()).stage).toBe("invalid");
    });

    /** Everything the server said, kept for the operator: it is the only thing that says *what* it read. */
    test("a refusal comes back in the server's own words, per role", async () => {
        const { setup, image } = await answered();

        image.validate = (role) => ({
            stdout: `role '${role}'\n  [E] C3 unknown section 'Kestrel'`,
            stderr: "",
            exitCode: role === "core" ? 1 : 0,
        });

        const outcome = await setup.apply();

        expect(outcome.ok === false && outcome.reason === "invalid" && outcome.reports).toHaveLength(
            ROLE_ANSWER.length,
        );
        expect(outcome.ok === false && outcome.reason === "invalid" && outcome.reports.find((r) => r.role === "core")?.ok).toBe(
            false,
        );
        expect(
            outcome.ok === false && outcome.reason === "invalid" && outcome.reports.find((r) => r.role === "core")?.output,
        ).toContain("C3");
    });

    /**
     * Validation looks at the staged copy and never at the install.
     *
     * Pointed at the install root instead, it would be checking whatever the last run left there — which
     * passes, and says nothing about the configuration about to be written.
     */
    test("the container is shown the staged copy, and the install is untouched while it looks", async () => {
        const { setup, store, image } = await answered();

        let directoriesWhenAsked: string[] = [];

        image.validate = (_role, mounts) => {
            directoriesWhenAsked = store.writes.map((write) => write.directory);

            expect(mounts?.configDir).toContain(".staging-");
            expect(mounts?.secretsFile).toContain(".staging-");

            return { stdout: "", stderr: "", exitCode: 0 };
        };

        await setup.apply();

        expect(directoriesWhenAsked.every((directory) => directory !== ROOT)).toBe(true);
    });

    /** Per role, because unscoped it validates every role in the catalog — including ones nobody chose. */
    test("every chosen role is validated, and only the chosen ones", async () => {
        const { setup, image } = await answered();

        await setup.apply();

        const validated = image.starts
            .filter((start) => start.args[0] === "--validate-config")
            .map((start) => start.args[2]);

        expect(validated.sort()).toEqual([...ROLE_ANSWER].sort());
    });

    test("the staging directory is cleaned up either way", async () => {
        const { setup, store, image } = await answered();

        await setup.apply();

        image.validate = () => ({ stdout: "", stderr: "", exitCode: 1 });

        await setup.apply();

        expect(store.discarded).toHaveLength(2);
    });

    test("two applies at once do not both run", async () => {
        const { setup, image } = await answered();

        await Promise.all([setup.apply(), setup.apply()]);

        expect(image.starts.filter((start) => start.args[0] === "--validate-config")).toHaveLength(
            ROLE_ANSWER.length,
        );
    });

    test("answers that are not finished are refused before any container starts", async () => {
        const { setup, store, image } = machine();

        await setup.submit({ domain: "chat.example.org" });

        const outcome = await setup.apply();

        expect(outcome.ok === false && outcome.reason).toBe("incomplete");
        expect(image.starts).toHaveLength(0);
        expect(store.writes).toHaveLength(0);
    });
});

describe("starting the instance", () => {
    test("the compose project lands beside the configuration, with the modes it carries", async () => {
        const { setup, store } = await answered();

        const outcome = await setup.apply();

        expect(outcome.ok).toBe(true);
        expect(store.committed(COMPOSE_FILENAME)?.mode).toBe(0o644);

        // The one file in the project with secrets in it — the database password Postgres is started
        // with — and the only reason it is not 0644 like the rest.
        expect(store.committed(ENV_FILENAME)?.mode).toBe(0o600);

        expect(outcome.ok && outcome.written.map((file) => file.path)).toContain(COMPOSE_FILENAME);
        expect(outcome.ok && outcome.written.map((file) => file.path)).toContain(DEPLOYMENT.secretsFile);
    });

    /**
     * Compose reconciles by project name. Two applies against the same name converge on one stack and
     * one network; two applies that let compose name the project after whatever directory it was run
     * from build the second beside the first, and the operator finds out from `docker ps`.
     */
    test("every invocation names the same project, so a second apply is not a second stack", async () => {
        const { setup, store, calls } = await answered();

        await setup.apply();
        await setup.apply();

        const named = calls.filter((call) => call.where !== undefined);

        expect(named.length).toBeGreaterThan(0);
        expect(named.every((call) => call.where?.project === COMPOSE_PROJECT)).toBe(true);

        // And the document says the same thing, so a `docker compose` run by hand in that directory
        // reconciles the same project rather than making a third.
        const document = JSON.parse(store.committed(COMPOSE_FILENAME)?.contents ?? "{}") as { name?: string };

        expect(document.name).toBe(COMPOSE_PROJECT);
    });

    /**
     * Pulling is its own step, and it comes first.
     *
     * It is the longest part of an install and the part with the most failures that are nobody's fault
     * — a registry that is down, a tag never published, a disk with no room. Kept ahead of `up`, those
     * land while nothing is running and the operator can press the same button again. Folded into `up`
     * they would leave a half-created project behind to explain.
     */
    test("the images are pulled before anything starts", async () => {
        const { setup, calls } = await answered();

        await setup.apply();

        const kinds = calls.map((call) => call.kind);

        expect(kinds.indexOf("pull")).toBe(0);
        expect(kinds.indexOf("up")).toBeGreaterThan(kinds.indexOf("pull"));
    });

    /**
     * The apply must not start the container it is running in.
     *
     * The panel is a service in this project — Traefik was started in front of it before setup began —
     * so a `compose up` with no arguments would recreate this container and kill the process part-way
     * through starting everything else. What the operator would see is a request that never answers and
     * a project in whatever state it had reached, with nothing left running that could say so.
     */
    test("everything is started except the container doing the starting", async () => {
        const { setup, calls } = await answered();

        await setup.apply();

        const started = calls.find((call) => call.kind === "up")?.services ?? [];

        expect(started).not.toContain(PANEL_SERVICE);
        expect(started).toContain("argon-edge");
        expect(started.length).toBeGreaterThan(1);
    });

    /**
     * The payoff for pulling first: a registry that is down costs an operator a retry rather than an
     * instance. `compose.ts` says a tag that does not exist fails here, loudly and early — this is what
     * "early" is worth.
     */
    test("a pull that fails starts nothing and leaves a clean retry", async () => {
        const { setup, store, compose, calls } = await answered();

        compose.pull = () => ({ ok: false, output: "manifest for argonchat/argon-server:0.4.1 not found" });

        const outcome = await setup.apply();

        expect(outcome.ok === false && outcome.reason).toBe("start-failed");
        expect(outcome.ok === false && outcome.reason === "start-failed" && outcome.running).toBe(false);
        expect(calls.some((call) => call.kind === "up")).toBe(false);

        // The configuration is on disk and the stage says so: nothing is running against it.
        expect(store.committed(COMPOSE_FILENAME)).toBeDefined();
        expect((await setup.state()).stage).toBe("configured");
    });

    /**
     * The distinction the outcome exists to carry.
     *
     * "The install failed" is the same sentence either side of `compose up`, and the operator's next
     * move is not: before it they change an answer and try again, after it they are acting on a machine
     * with containers on it. It is answered by asking compose what exists rather than by guessing from
     * where the code got to.
     */
    test("a failed start says whether there are containers on the machine now", async () => {
        const created = await answered();

        created.compose.up = () => ({ ok: false, output: "argon-edge exited with code 1" });

        const withContainers = await created.setup.apply();

        expect(withContainers.ok === false && withContainers.reason === "start-failed" && withContainers.running).toBe(
            true,
        );
        expect((await created.setup.state()).stage).toBe("degraded");

        const empty = await answered();

        empty.compose.up = () => ({ ok: false, output: "invalid compose project" });
        empty.compose.status = () => [];

        const nothingCreated = await empty.setup.apply();

        expect(nothingCreated.ok === false && nothingCreated.reason === "start-failed" && nothingCreated.running).toBe(
            false,
        );
        expect((await empty.setup.state()).stage).toBe("configured");
    });

    /**
     * "Timed out after five minutes" tells the operator to look at everything. "argon-core is
     * 'restarting'" tells them which `docker logs` to run, which is the whole difference between a
     * report and an apology.
     */
    test("a service that never comes up is named, rather than the wait being reported as a duration", async () => {
        const { setup, compose } = await answered();

        compose.status = (services) =>
            services.map((service) => ({ service, state: service === "argon-core" ? "restarting" : "running" }));

        const outcome = await setup.apply();

        expect(outcome.ok === false && outcome.reason).toBe("not-ready");
        expect(outcome.ok === false && outcome.reason === "not-ready" && outcome.problem).toContain("argon-core");
        expect(outcome.ok === false && outcome.reason === "not-ready" && outcome.running).toBe(true);
        expect((await setup.state()).stage).toBe("degraded");
    });

    /**
     * The project has a service that is supposed to exit: `argon-storage-init` creates the two buckets
     * and stops, and `media` waits on it having *completed*. A readiness rule that only accepted
     * `running` would wait out its whole bound on a container that finished in nine seconds.
     */
    test("a one-shot service that exited zero is ready, not missing", async () => {
        const { setup, compose } = await answered();

        compose.status = (services) =>
            services.map((service) =>
                service === "argon-storage-init"
                    ? { service, state: "exited", exitCode: 0 }
                    : { service, state: "running" },
            );

        const outcome = await setup.apply();

        expect(outcome.ok).toBe(true);
        expect((await setup.state()).stage).toBe("running");
    });

    /**
     * The apply gets exactly one answer — the listener closes half-way through it — so the address has
     * to be somewhere the operator can have read beforehand.
     */
    test("where the panel moved to is known before the apply, and repeated in the answer", async () => {
        const { setup } = await answered();

        expect((await setup.state()).panel?.url).toBe(`https://chat.example.org${PANEL_PATH}`);

        const outcome = await setup.apply();

        expect(outcome.ok && outcome.panel.url).toBe(panelFor("chat.example.org").url);
        expect(outcome.ok && outcome.panel.note).toContain("https://chat.example.org/");
    });

    /**
     * A container that says nothing for the length of an image pull is indistinguishable from one that
     * has hung, and the operator's next move is a reboot. Redacted on the way out for the same reason
     * the server's own diagnostics are: compose reads the `.env` beside the document.
     */
    test("progress leaves while it is happening, with the secrets taken out of it", async () => {
        const { setup, store, compose, reported } = await answered();

        compose.emits = () => [`argon-postgres | env ${store.committed(ENV_FILENAME)?.contents ?? ""}`];

        await setup.apply();

        expect(reported.length).toBeGreaterThan(0);
        expect(reported.join("\n")).toContain("<redacted>");
        expect(reported.join("\n")).not.toContain(databasePassword(store));

        // And the same lines are readable over HTTP for as long as this process still answers there.
        expect((await setup.state()).progress?.length).toBeGreaterThan(0);
    });

    /**
     * §5 says a Cloudflare-proxied instance terminates TLS here from an Origin CA certificate, and
     * `compose.ts` refuses to generate an edge without one rather than quietly serving plain HTTP behind
     * a padlock somebody else is holding. What this checks is *when* that refusal lands: before the
     * write, so the install is exactly as it was.
     */
    test("a traffic shape whose certificate this process was never given refuses before the write", async () => {
        const { setup, store } = await answered({ traffic: { kind: "cloudflare-proxied" } });

        const outcome = await setup.apply();

        expect(outcome.ok === false && outcome.reason).toBe("not-startable");
        expect(store.writes.every((write) => write.directory !== ROOT)).toBe(true);

        const supplied = await answered(
            { traffic: { kind: "cloudflare-proxied" } },
            { certificates: { instance: { certificatePath: "/etc/argon/tls.crt", keyPath: "/etc/argon/tls.key" } } },
        );

        expect((await supplied.setup.apply()).ok).toBe(true);
    });

    /**
     * A role configured and not run is silent in every other way: `conf.d` has a file for it, the wizard
     * shows it as chosen, and no container exists. `compose.ts` drops the roles §6 refuses.
     */
    test("a role that was configured and will not be run is said out loud", async () => {
        const { setup, store } = await answered({ roles: [...ROLE_ANSWER, "commerce"] });

        await setup.apply();

        // The role is in the answers and there is no container for it. Nothing else about the install
        // says so: the wizard shows it as chosen and `--explain` was asked about it.
        expect((await setup.state()).answers.roles).toContain("commerce");
        expect(declaredServices(store)).not.toContain("argon-commerce");
        expect((await setup.state()).warnings.join(" ")).toContain("will not be run");
    });

    /**
     * The single flight mattered when this was a validation and a write. It matters more now that it is
     * an image pull and a `compose up`: the window is minutes, and a second caller that started its own
     * would reconcile the same project from a second set of staged content.
     */
    test("two applies at once still pull once and start once", async () => {
        const { setup, calls } = await answered();

        await Promise.all([setup.apply(), setup.apply(), setup.apply()]);

        expect(calls.filter((call) => call.kind === "pull")).toHaveLength(1);
        expect(calls.filter((call) => call.kind === "up")).toHaveLength(1);
    });

    /** The image that was interrogated and validated is the image that runs. See the report. */
    test("the roles run the same image the server validated the configuration with", async () => {
        const { setup, store } = await answered();

        await setup.apply();

        const document = JSON.parse(store.committed(COMPOSE_FILENAME)?.contents ?? "{}") as {
            services: Record<string, { image?: string }>;
        };

        expect(document.services["argon-core"]?.image).toBe(referenceFor(VERSION));
    });
});

describe("reading what compose said", () => {
    test("a service with no container, and one that is not running, are both named", () => {
        const waiting = unreadyServices(
            ["argon-core", "argon-edge"],
            [{ service: "argon-core", state: "restarting" }],
        );

        expect(waiting.join(" ")).toContain("argon-core");
        expect(waiting.join(" ")).toContain("argon-edge");
    });

    /** Running is not ready while docker's own healthcheck disagrees; `media` depends on that answer. */
    test("a healthcheck that has not passed is not ready, and one that never runs is", () => {
        expect(unreadyServices(["a"], [{ service: "a", state: "running", health: "starting" }])).toHaveLength(1);
        expect(unreadyServices(["a"], [{ service: "a", state: "running" }])).toHaveLength(0);
        expect(unreadyServices(["a"], [{ service: "a", state: "running", health: "healthy" }])).toHaveLength(0);
    });

    test("an exit is ready when it was a zero and a failure when it was not", () => {
        expect(unreadyServices(["a"], [{ service: "a", state: "exited", exitCode: 0 }])).toHaveLength(0);
        expect(unreadyServices(["a"], [{ service: "a", state: "exited", exitCode: 1 }])[0]).toContain("exited 1");
    });

    /**
     * What the command is pointed at, which is the part that decides whether a second apply reconciles
     * the first project or builds another one beside it.
     */
    test("every compose command names the project, the directory and the document", () => {
        const argv = composeCommandFor(
            { project: COMPOSE_PROJECT, directory: "/opt/argon", file: COMPOSE_FILENAME },
            ["up", "--detach"],
        );

        expect(argv.slice(0, 2)).toEqual(["docker", "compose"]);
        expect(argv).toContain("--project-name");
        expect(argv[argv.indexOf("--project-name") + 1]).toBe(COMPOSE_PROJECT);
        expect(argv[argv.indexOf("--project-directory") + 1]).toBe("/opt/argon");
        expect(argv[argv.indexOf("--file") + 1]).toBe(join("/opt/argon", COMPOSE_FILENAME));
        expect(argv.slice(-2)).toEqual(["up", "--detach"]);
    });
});

describe("the secrets survive a re-run", () => {
    /**
     * The thing that is only discovered after a restart.
     *
     * Postgres takes `POSTGRES_PASSWORD` when its data directory is first initialised and ignores it
     * forever after. A second mint therefore produces a configuration that cannot log in to the database
     * that was created from the first one — and the operator finds out when the containers come up, not
     * when the wizard was re-run.
     */
    test("going back a step and applying again does not change the database password", async () => {
        const { setup, store } = await answered();

        await setup.apply();

        const first = databasePassword(store);

        await setup.submit({ domain: "chat.example.net" });
        await setup.apply();

        expect(databasePassword(store)).toBe(first);
    });

    test("a restarted process reuses the mint it finds, and says the setup restarted", async () => {
        const first = await answered();

        await first.setup.apply();

        const password = databasePassword(first.store);
        const mint = first.store.committed(MINT_FILE);

        // A new machine over a store that already holds what the old one wrote — which is what a restart
        // is, since nothing about the answers is durable.
        const { setup: restarted, store } = machine({ [MINT_FILE]: mint?.contents ?? "" });

        const state = await restarted.state();

        expect(state.restarted).toBe(true);
        expect(state.note).toContain("started before");
        expect(state.answers).toEqual({});

        const submission = await restarted.submit(steps());

        expect(submission.ok).toBe(true);

        await restarted.apply();

        expect(databasePassword(store)).toBe(password);
    });

    /**
     * The mint is written before the files it produced. It is the one thing that cannot be regenerated;
     * with it, every file below can be rebuilt byte for byte, so a process that dies between the two
     * writes comes back and produces the same install rather than a new set of secrets.
     */
    test("the mint lands before the configuration it produced", async () => {
        const { setup, store } = await answered();

        await setup.apply();

        const commits = store.writes.filter((write) => write.directory === ROOT);

        expect(commits[0]?.files.map((file) => file.path)).toEqual([MINT_FILE]);
        expect(commits[1]?.files.some((file) => file.path === DEPLOYMENT.secretsFile)).toBe(true);
    });

    /**
     * A mint that cannot be read is not a mint that is absent.
     *
     * It is the case where an instance is already running on the old values, and minting a new bundle
     * over the top of it produces a configuration that cannot reach its own database. Blocking is the
     * only answer that cannot cost data.
     */
    test("a mint that cannot be read blocks the apply rather than rotating the secrets", async () => {
        const { setup, store } = machine({ [MINT_FILE]: "{ this was never json" });

        await setup.submit(steps());

        const state = await setup.state();
        const outcome = await setup.apply();

        expect(state.stage).toBe("blocked");
        expect(state.problem).toContain(MINT_FILE);
        expect(outcome.ok === false && outcome.reason).toBe("blocked");
        expect(store.writes).toHaveLength(0);
    });

    /**
     * A bootstrapper that grows a new secret has to be able to run against an install that predates it.
     * Refusing the whole file would make that upgrade impossible; minting the whole bundle fresh would
     * rotate a password a running Postgres will not accept. What is there is kept.
     */
    test("a mint missing one value keeps the rest and mints only what is absent", () => {
        const stored = {
            format: 1,
            secrets: {
                databasePassword: "kept-database-password",
                jwtMachineSalt: "kept-machine-salt",
                jwtSigning: { privateKey: "kept-signing-private", publicKey: "kept-signing-public" },
                ticketKey: "kept-ticket-key",
                transportHashKey: "kept-transport-hash",
                totpSecretPart: "kept-totp-part",
                metricsPassword: "kept-metrics-password",
                objectStorage: { accessKey: "kept-storage-access", secretKey: "kept-storage-secret" },
                sfu: { clientId: "kept-sfu-client", secret: "kept-sfu-secret" },
            },
        };

        const adopted = adoptMint(stored);

        expect(adopted.secrets.databasePassword).toBe("kept-database-password");
        expect(adopted.added).toEqual(["jwtEncryption"]);
        expect(adopted.secrets.jwtEncryption.privateKeyBase64.length).toBeGreaterThan(0);
    });

    /**
     * Half a key pair is worse than a new one: the private key would not match the public one, and every
     * token signed by it would be rejected by the same process that signed it.
     */
    test("half a key pair is replaced whole", () => {
        const adopted = adoptMint({ secrets: { jwtSigning: { privateKey: "kept-signing-private" } } });

        expect(adopted.added).toContain("jwtSigning");
        expect(adopted.secrets.jwtSigning.privateKey).not.toBe("kept-signing-private");
    });

    test("a document with no secrets in it is not a mint", () => {
        expect(() => adoptMint({ format: 1 })).toThrow();
        expect(() => adoptMint("not an object")).toThrow();
    });
});

describe("nothing generated comes back out", () => {
    /**
     * The leak that is easy to miss. `--validate-config` reports on configuration it has just read, so
     * its diagnostics can quote a value this installer generated — and that text is exactly what the
     * wizard wants to show. Showing it with the words we know to be secret taken out is the only answer
     * that is both honest and safe.
     */
    test("a secret quoted back by the server is redacted before it leaves", async () => {
        const { setup, store, image } = await answered();

        // What the server prints when it has read the document and did not like something in it: the
        // document itself, quoted, secrets and all.
        image.validate = (role, mounts) => {
            const staged = mounts?.secretsFile === undefined ? undefined : store.disk.get(mounts.secretsFile);

            return {
                stdout: `role '${role}'\n  [W] C7 could not parse ${staged?.contents ?? ""}`,
                stderr: "",
                exitCode: 0,
            };
        };

        await setup.apply();

        const state = await setup.state();
        const password = databasePassword(store);

        expect(password.length).toBeGreaterThan(16);
        expect(JSON.stringify(state)).not.toContain(password);
        expect(state.validation?.[0]?.output).toContain("<redacted>");
    });

    test("redaction leaves short values alone, so a diagnostic stays readable", () => {
        expect(redact("the value ab is wrong", { key: "ab" })).toBe("the value ab is wrong");
        expect(redact("the value abcdefghij is wrong", { key: "abcdefghij" })).toContain("<redacted>");
    });
});

/* ------------------------------------------------------------------------------------------------
 * Over HTTP, which is where a leak would actually happen.
 * ---------------------------------------------------------------------------------------------- */

describe("the setup routes", () => {
    const CODE = "quiet-harbour-42";

    let server: ReturnType<typeof createServer> | undefined;

    afterEach(() => {
        // Guarded rather than wrapped in a `try`, because an apply hands the port over and the server
        // has already stopped itself by then — and Elysia *prints* about a `stop` on something that is
        // not listening rather than rejecting, so there is nothing a catch could swallow.
        if (server?.server !== null) server?.stop();

        server = undefined;
    });

    /** Port 0 so the kernel picks one: these run beside everything else in the suite. */
    const post = (base: string, cookie: string, path: string, body?: unknown) =>
        fetch(`${base}${path}`, {
            method: "POST",
            headers: { cookie, "content-type": "application/json" },
            body: body === undefined ? "{}" : JSON.stringify(body),
        });

    /** Long enough for the panel's own rule, and recognisably not the bootstrap code. */
    const PANEL_PASSWORD = "operator-panel-password";

    /** The code half of `open`, for the tests that build their own server. */
    async function signedIn(base: string): Promise<string> {
        const challenge = (await (await fetch(`${base}/api/auth/challenge`, { method: "POST" })).json()) as {
            id: string;
            nonce: string;
        };

        const response = await fetch(`${base}/api/auth/verify`, {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({ challengeId: challenge.id, proof: proofFor(CODE, challenge.nonce) }),
        });

        return response.headers.get("set-cookie") ?? "";
    }

    /**
     * A panel that records what it was asked rather than doing it.
     *
     * The four modules behind the real one need a docker socket, a TLS handshake and a disk. What the
     * routes are responsible for is which of them gets called and what a refusal becomes — and that is
     * exactly what was untested when a deadlock between two routes shipped.
     */
    function fakePanel(options: {
        readonly onRecord?: (version: string) => void;
        readonly verdict?: { ok: boolean; standing?: "settled" | "unproven"; problem?: string };
        readonly onUpgrade?: (version: string) => void;
    }): NonNullable<Parameters<typeof createServer>[0]["panel"]> {
        return {
            overview: async () => ({
                domain: "chat.example.org",
                services: [],
                controllable: [],
                certificates: [],
                backups: [],
                version: {},
            }),
            logs: async () => ({ ok: true, lines: [], truncated: false }) as never,
            control: async () => ({ ok: true, issued: [] }) as never,
            backup: async () => ({ ok: true }) as never,
            plan: async () => ({}) as never,
            judgeUpgrade: async () => (options.verdict ?? { ok: true }) as never,
            record: async (_setup, version) => {
                options.onRecord?.(version);
                options.onUpgrade?.(version);
            },
        };
    }

    async function open(setup: Setup, options: { readonly password?: boolean } = {}): Promise<{ base: string; cookie: string }> {
        // In memory, because what these tests exercise is the routes rather than the file. `write` hands
        // back what `credential.ts` would have stored, so the server accepts the same password afterwards.
        let stored: string | undefined;

        server = createServer({
            code: CODE,
            hostname: "127.0.0.1",
            port: 0,
            setup,
            credentials: {
                read: async () => stored,
                write: async (password) => (stored = await Bun.password.hash(password, { algorithm: "argon2id" })),
            },
        });

        const base = `http://127.0.0.1:${server.server!.port}`;

        const challenge = (await (await fetch(`${base}/api/auth/challenge`, { method: "POST" })).json()) as {
            id: string;
            nonce: string;
        };

        const signedIn = await fetch(`${base}/api/auth/verify`, {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({ challengeId: challenge.id, proof: proofFor(CODE, challenge.nonce) }),
        });

        const cookie = signedIn.headers.get("set-cookie") ?? "";

        // Set unless a test is about its absence. The apply refuses without one — finishing an install
        // retires the bootstrap code, and retiring it with no password would leave a panel holding the
        // docker socket that nobody can sign into again.
        if (options.password !== false) await post(base, cookie, "/api/panel/password", { password: PANEL_PASSWORD });

        return { base, cookie };
    }

    test("a step, then the state, then the apply", async () => {
        const { setup, store } = await answered();
        const { base, cookie } = await open(setup);

        const state = (await (await fetch(`${base}/api/state`, { headers: { cookie } })).json()) as Record<string, any>;

        expect(state["stage"]).toBe("ready");
        expect(state["retired"]).toBe(false);

        const applied = await post(base, cookie, "/api/setup/apply");

        expect(applied.status).toBe(200);
        expect(((await applied.json()) as { written: { path: string }[] }).written.map((file) => file.path)).toContain(
            DEPLOYMENT.secretsFile,
        );
        expect(store.committed(DEPLOYMENT.secretsFile)).toBeDefined();
    });

    /**
     * The install will not finish while finishing would lock the operator out.
     *
     * A successful apply retires the bootstrap code — it is printed in a terminal and left in a file in
     * the install root, and leaving it valid on a public box replaces one problem with a worse one. With
     * no password set, retiring it removes the only way into a panel that holds the docker socket, and
     * nothing short of editing files on the host would get it back. So the refusal comes first, before
     * anything is written.
     */
    test("an apply with no panel password is refused before anything is written", async () => {
        const { setup, store } = await answered();
        const { base, cookie } = await open(setup, { password: false });

        const response = await post(base, cookie, "/api/setup/apply");

        expect(response.status).toBe(409);

        const body = (await response.json()) as { error: string; problem: string };

        expect(body.error).toBe("not-startable");
        expect(body.problem).toContain("panel password");

        expect(store.writes).toHaveLength(0);

        // And the door is still open, which is the point of refusing.
        expect((await fetch(`${base}/api/auth/challenge`, { method: "POST" })).status).toBe(200);
    });

    /**
     * And once it has finished, the code stops working — while the password does not.
     *
     * This is the other half of the same change: the code's whole life is one install. Sessions already
     * issued survive, so the operator watching the install is not thrown out at the moment it becomes
     * the panel; what ends is the ability to get a *new* one from a string sitting in a file.
     */
    test("a successful apply retires the code and leaves the password", async () => {
        const { setup } = await answered();
        const { base, cookie } = await open(setup);

        expect((await post(base, cookie, "/api/setup/apply")).status).toBe(200);

        // The code is spent: no new challenge, and therefore no new session from it.
        expect((await fetch(`${base}/api/auth/challenge`, { method: "POST" })).status).toBe(410);

        // The session that was watching still works.
        expect((await fetch(`${base}/api/state`, { headers: { cookie } })).status).toBe(200);

        // And the password opens a fresh one.
        const signedIn = await fetch(`${base}/api/auth/password`, {
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify({ password: PANEL_PASSWORD }),
        });

        expect(signedIn.status).toBe(200);
    });

    /**
     * The deadlock that a green suite said nothing about.
     *
     * The panel refuses to upgrade an install it has no record of — it cannot tell a fresh machine from
     * one that has been running Argon for a year, and guessing wrong means a downgrade onto a database
     * a migration has already moved. But the upgrade route is the only other thing that writes a record,
     * and it writes one *after* that refusal.
     *
     * So without a record written here, the two lock: the empty history refuses the upgrade, and the
     * upgrade is the only thing that would have filled it. No instance this wizard installed could ever
     * be upgraded through this panel. Neither route had a test, which is why nothing said so.
     */
    test("a finished install writes the first line of the history", async () => {
        const { setup } = await answered();
        const recorded: { version: string }[] = [];

        server = createServer({
            code: CODE,
            hostname: "127.0.0.1",
            port: 0,
            setup,
            credentials: { read: async () => undefined, write: async () => "hash" },
            panel: fakePanel({ onRecord: (version) => recorded.push({ version }) }),
        });

        const base = `http://127.0.0.1:${server.server!.port}`;
        const cookie = await signedIn(base);

        await post(base, cookie, "/api/panel/password", { password: PANEL_PASSWORD });

        // Read from the setup rather than written out here, so this asserts that the version *that was
        // installed* is the one recorded — a constant would pass just as well against a route that
        // recorded whatever it liked.
        const installed = (await setup.state()).answers.serverVersion;

        expect((await post(base, cookie, "/api/setup/apply")).status).toBe(200);

        expect(recorded).toEqual([{ version: installed! }]);
    });

    /**
     * Two kinds of no, and only one of them can be argued with.
     *
     * `settled` is a change that cannot work whatever anybody says. `unproven` is the panel admitting it
     * cannot see far enough — the running version came from a moving tag, or nothing wrote down what is
     * installed — and the operator can see further than the panel does.
     *
     * Collapsing them locked every `latest`-pinned install out of re-pulling its own tag, which is the
     * single operation that kind of install exists to do. The route is where that distinction has to
     * survive, because the page is not the only thing that can reach it.
     */
    test.each([
        ["settled", false, 409],
        ["settled", true, 409],
        ["unproven", false, 409],
        ["unproven", true, 200],
    ] as const)("an %s refusal with confirm=%s answers %s", async (standing, confirm, expected) => {
        const { setup } = await answered();

        server = createServer({
            code: CODE,
            hostname: "127.0.0.1",
            port: 0,
            setup,
            credentials: { read: async () => undefined, write: async () => "hash" },
            panel: fakePanel({ verdict: { ok: false, standing, problem: "the panel cannot establish that." } }),
        });

        const base = `http://127.0.0.1:${server.server!.port}`;
        const cookie = await signedIn(base);

        await post(base, cookie, "/api/panel/password", { password: PANEL_PASSWORD });

        const response = await post(base, cookie, "/api/panel/upgrade", { version: "0.4.9", confirm });

        expect(response.status).toBe(expected);

        if (expected === 409) {
            const body = (await response.json()) as { standing: string; problem: string };

            // The page branches on this. A flat refusal that dropped it is what made a question look
            // like a wall.
            expect(body.standing).toBe(standing);
            expect(body.problem).toContain("cannot establish");
        }
    });

    /** A failed install records nothing: the history is what *ran*, not what was attempted. */
    test("an install that failed writes no record", async () => {
        const { setup, compose } = await answered();
        const recorded: string[] = [];

        compose.up = () => ({ ok: false, output: "argon-core exited with code 1" });

        server = createServer({
            code: CODE,
            hostname: "127.0.0.1",
            port: 0,
            setup,
            credentials: { read: async () => undefined, write: async () => "hash" },
            panel: fakePanel({ onRecord: (version) => recorded.push(version) }),
        });

        const base = `http://127.0.0.1:${server.server!.port}`;
        const cookie = await signedIn(base);

        await post(base, cookie, "/api/panel/password", { password: PANEL_PASSWORD });
        await post(base, cookie, "/api/setup/apply");

        expect(recorded).toEqual([]);
    });

    /**
     * The apply answers, and the process that answered is still there afterwards.
     *
     * This test used to assert the opposite — that the listener was gone by the time the response
     * arrived — because the apply handed `:443` to Traefik half-way through. Starting the door first
     * removed the handover, and with it the one-answer-only constraint that shaped this route: the
     * operator can refresh, retry, and keep using the same URL.
     *
     * The panel's address is still in the body, and still worth asserting. It is what the UI walks the
     * operator to, and it is the one thing the response says that the operator cannot work out for
     * themselves.
     */
    test("the apply answers over the socket it arrived on, which stays open", async () => {
        const { setup } = await answered();
        const { base, cookie } = await open(setup);

        const applied = await post(base, cookie, "/api/setup/apply");

        expect(applied.status).toBe(200);

        const body = (await applied.json()) as { panel: { url: string }; services: unknown[] };

        expect(body.panel.url).toContain(PANEL_PATH);
        expect(body.services.length).toBeGreaterThan(0);

        expect((await fetch(`${base}/api/health`)).status).toBe(200);
    });

    /**
     * And then the container stays.
     *
     * It used to stop: this process held `:443`, gave it to Traefik during the apply, and had nothing
     * left to answer on. Traefik is now started before setup and this process has never held a public
     * port, so the same container carries on as §10's control surface — which is what makes "the
     * panel" and "the installer" one image rather than two.
     *
     * There is no `stopProcess` seam any more, so this cannot regress by something calling it; what it
     * still catches is the listener being closed, which would leave the operator with a URL that was
     * working a second ago and a container that is up.
     */
    test("the container keeps serving after a successful apply", async () => {
        const { setup } = await answered();
        const { base, cookie } = await open(setup);

        expect((await post(base, cookie, "/api/setup/apply")).status).toBe(200);

        // Long enough that a stop scheduled behind the response would have landed.
        await new Promise<void>((wake) => setTimeout(wake, 250));

        expect((await fetch(`${base}/api/health`)).status).toBe(200);
    });

    /**
     * A failure after `compose up` is a different thing from a failure before it, and the status code
     * cannot say which — only the body can. A UI that showed the code and not the body would tell the
     * operator to retry into a live stack.
     */
    test("a start that fails after containers exist is a 500 that says so", async () => {
        const { setup, compose } = await answered();

        compose.up = () => ({ ok: false, output: "argon-edge exited with code 1" });

        const { base, cookie } = await open(setup);
        const response = await post(base, cookie, "/api/setup/apply");

        expect(response.status).toBe(500);

        const body = (await response.json()) as { error: string; running: boolean; panel: { url: string } };

        expect(body.error).toBe("start-failed");
        expect(body.running).toBe(true);
        expect(body.panel.url).toContain(PANEL_PATH);
    });

    /** Nothing here failed; something upstream did not answer in time, and the body names which. */
    test("a stack that never comes up is a 504 naming the service", async () => {
        const { setup, compose } = await answered();

        compose.status = (services) =>
            services.map((service) => ({ service, state: service === "argon-jobs" ? "restarting" : "running" }));

        const { base, cookie } = await open(setup);
        const response = await post(base, cookie, "/api/setup/apply");

        expect(response.status).toBe(504);

        const body = (await response.json()) as { error: string; problem: string; running: boolean };

        expect(body.error).toBe("not-ready");
        expect(body.problem).toContain("argon-jobs");
        expect(body.running).toBe(true);
    });

    /**
     * The refusal that has to land *before* the write, so the machine is as it was. A 409 rather than a
     * 500, because it is a state the operator can act on rather than a fault in this process.
     */
    test("a project that cannot be assembled is a 409 and nothing is written", async () => {
        const { setup, store } = await answered({ traffic: { kind: "cloudflare-proxied" } });
        const { base, cookie } = await open(setup);

        const response = await post(base, cookie, "/api/setup/apply");

        expect(response.status).toBe(409);
        expect(((await response.json()) as { error: string }).error).toBe("not-startable");
        expect(store.writes.every((write) => write.directory !== ROOT)).toBe(true);

        // And the wizard is still there, because nothing was started.
        expect((await fetch(`${base}/api/health`)).status).toBe(200);
    });

    /**
     * The property the whole design is arranged around: the UI needs to know the secrets were written,
     * and never what they are. Every route is checked rather than only the one that seems likely, because
     * the one that seems unlikely is the one that grows a field later.
     *
     * The apply goes **last**, and that is not tidiness. It hands the port over half-way through, so the
     * listener these other two routes are answering on is closed by the time it returns — a version of
     * this test that applied first would be testing a connection that no longer exists.
     */
    test("no route hands back a generated secret", async () => {
        const { setup, store, image, compose } = await answered();

        // The server quoting our own configuration back at us, which is the way this leaks in practice.
        image.validate = (role, mounts) => ({
            stdout: `role '${role}' ${store.disk.get(mounts?.secretsFile ?? "")?.contents ?? ""}`,
            stderr: "",
            exitCode: 0,
        });

        const { base, cookie } = await open(setup);

        const step = await (await post(base, cookie, "/api/setup/step", { voice: false })).text();
        const state = await (await fetch(`${base}/api/state`, { headers: { cookie } })).text();

        // And docker doing the same: compose reads the `.env` beside the document, so its output is the
        // second way a generated secret gets quoted back at the operator.
        compose.up = () => ({ ok: false, output: `argon-postgres | ${store.committed(ENV_FILENAME)?.contents ?? ""}` });

        const applied = await (await post(base, cookie, "/api/setup/apply")).text();

        const password = databasePassword(store);
        const secrets = secretsOf(store);

        for (const response of [applied, state, step]) {
            expect(response).not.toContain(password);
            expect(response).not.toContain(secrets["TicketJwt"]["Key"]);
            expect(response).not.toContain(secrets["Totp"]["SecretPart"]);
        }

        expect(applied).toContain("<redacted>");
    });

    /**
     * The bodies are well formed on purpose.
     *
     * Elysia validates a declared body *before* it runs a guard's `beforeHandle`, so a malformed body
     * answers 400 whether or not the caller has a session — which is fine (the handler never runs) and is
     * not what this test is about. Sending a valid body is what makes the 401 evidence about the guard.
     */
    test("the setup routes are closed to callers without a session", async () => {
        const { setup, store, image } = await answered();

        server = createServer({ code: CODE, hostname: "127.0.0.1", port: 0, setup });

        const base = `http://127.0.0.1:${server.server!.port}`;
        const unauthenticated = (path: string) =>
            fetch(`${base}${path}`, { method: "POST", headers: { "content-type": "application/json" }, body: "{}" });

        expect((await unauthenticated("/api/setup/step")).status).toBe(401);
        expect((await unauthenticated("/api/setup/interrogate")).status).toBe(401);
        expect((await unauthenticated("/api/setup/apply")).status).toBe(401);

        // And none of them got far enough to do anything.
        expect(store.writes).toHaveLength(0);
        expect(image.starts).toHaveLength(0);
    });

    test("a refused answer is a 400 that names the field", async () => {
        const { setup } = await answered();
        const { base, cookie } = await open(setup);

        const response = await post(base, cookie, "/api/setup/step", { domain: "https://chat.example.org" });

        expect(response.status).toBe(400);

        const body = (await response.json()) as { error: string; rejections: { field: string }[] };

        expect(body.error).toBe("rejected");
        expect(body.rejections[0]?.field).toBe("domain");
    });

    test("a body that is not the shape the step declared never reaches the machine", async () => {
        const { setup } = await answered();
        const { base, cookie } = await open(setup);

        expect((await post(base, cookie, "/api/setup/step", { voice: "yes" })).status).toBe(400);
        expect((await post(base, cookie, "/api/setup/step", { storage: { kind: "gcs" } })).status).toBe(400);
        expect((await setup.state()).answers.voice).toBe(false);
    });

    test("a configuration the server refuses is a 422 carrying its words, not a 500", async () => {
        const { setup, image } = await answered();

        image.validate = () => ({ stdout: "  [E] C3 unknown section 'Nope'", stderr: "", exitCode: 1 });

        const { base, cookie } = await open(setup);

        const response = await post(base, cookie, "/api/setup/apply");

        expect(response.status).toBe(422);
        expect(JSON.stringify(await response.json())).toContain("C3");
    });

    /**
     * Docker not working must not be reported as "your configuration is invalid": an operator told that
     * goes and edits files, and the problem is in their daemon.
     */
    test("docker failing to run is a 503, not a refused configuration", async () => {
        const { setup, image } = await answered();

        image.validate = () => ({ stdout: "", stderr: "docker: permission denied", exitCode: 125 });

        const { base, cookie } = await open(setup);

        const response = await post(base, cookie, "/api/setup/apply");

        expect(response.status).toBe(503);
        expect(((await response.json()) as { error: string }).error).toBe("image-unavailable");
    });

    /**
     * An empty wizard after a restart is indistinguishable from a wizard nobody has started, and only one
     * of those is something the operator should be told about.
     */
    test("a reload after a restart says the setup restarted", async () => {
        const first = await answered();

        await first.setup.apply();

        const { setup } = machine({ [MINT_FILE]: first.store.committed(MINT_FILE)?.contents ?? "" });

        const { base, cookie } = await open(setup);

        const state = (await (await fetch(`${base}/api/state`, { headers: { cookie } })).json()) as Record<string, any>;

        expect(state["restarted"]).toBe(true);
        expect(state["note"]).toContain("started before");
        expect(state["stage"]).toBe("awaiting-configuration");
    });
});

/* ------------------------------------------------------------------------------------------------
 * The one place a real disk is used.
 * ---------------------------------------------------------------------------------------------- */

describe("the install directory", () => {
    const directories: string[] = [];

    afterEach(async () => {
        for (const directory of directories.splice(0)) await rm(directory, { recursive: true, force: true });
    });

    async function root(): Promise<string> {
        const directory = await mkdtemp(join(tmpdir(), "argon-setup-"));

        directories.push(directory);

        return directory;
    }

    /**
     * The mode is the whole difference between the two kinds of file this writes, and it is one digit.
     *
     * `writeFile(path, data, { mode })` does not change the mode of a file that already exists, and the
     * umask this process inherited subtracts from it — so both the rename and the explicit chmod are load
     * bearing, and neither shows up in a test that only reads the contents back.
     */
    test.skipIf(process.platform === "win32")("each file lands with the mode it carries", async () => {
        const directory = await root();
        const store = localStore(directory);

        await writeFile(join(directory, DEPLOYMENT.secretsFile), "{}", { mode: 0o644 });

        await store.write(directory, [
            { path: DEPLOYMENT.secretsFile, contents: "{ \"a\": 1 }\n", mode: 0o600 },
            { path: `${DEPLOYMENT.confD}/database.json`, contents: "{}\n", mode: 0o644 },
        ]);

        expect((await stat(join(directory, DEPLOYMENT.secretsFile))).mode & 0o777).toBe(0o600);
        expect((await stat(join(directory, DEPLOYMENT.confD, "database.json"))).mode & 0o777).toBe(0o644);
    });

    test("the contents land, and a missing file reads as absent rather than as an error", async () => {
        const directory = await root();
        const store = localStore(directory);

        expect(await store.read(MINT_FILE)).toBeUndefined();

        await store.write(directory, [{ path: MINT_FILE, contents: "{}\n", mode: 0o600 }]);

        expect(await store.read(MINT_FILE)).toBe("{}\n");
        expect(await readFile(join(directory, MINT_FILE), "utf8")).toBe("{}\n");
    });

    /** A write that escapes the install root is not recoverable by an operator: they cannot see where it went. */
    test("a path that climbs out of the directory is refused", async () => {
        const directory = await root();

        await expect(
            localStore(directory).write(directory, [{ path: "../escaped.json", contents: "{}", mode: 0o600 }]),
        ).rejects.toThrow();
    });

    /**
     * Staging happens inside the install directory because the docker daemon resolves the mount on the
     * host, and this container's `/tmp` is not there. What that costs is a directory left behind by a
     * crash, holding a staged secrets file — so the next run sweeps before it stages, rather than relying
     * on a process that is no longer running to clean up after itself.
     */
    test("staging is inside the install, and old staging directories are swept", async () => {
        const directory = await root();
        const store = localStore(directory);

        mkdirSync(join(directory, ".staging-leftover"));

        const staging = await store.scratch();

        expect(staging.startsWith(directory)).toBe(true);
        expect(await store.read(".staging-leftover")).toBeUndefined();

        await store.write(staging, [{ path: DEPLOYMENT.secretsFile, contents: "{}", mode: 0o600 }]);
        await store.discard(staging);

        // Gone with the staged secrets file inside it, which is the point of discarding at all.
        await expect(stat(staging)).rejects.toThrow();
    });

    /** `discard` takes a path, and a path is a thing that can arrive wrong. The install root is one bad join away. */
    test("discard refuses a directory it did not make", async () => {
        const directory = await root();

        await expect(localStore(directory).discard(directory)).rejects.toThrow();
    });
});

describe("which image a version names", () => {
    test("a bare version is a tag on the published image", () => {
        // ghcr.io/argon-chat/orleans, which is what CI actually publishes — .github/workflows/production.yml
        // pushes that name, and the argonchat/argon-server push beside it is commented out. This test
        // named the commented-out one, copied from deploy/docker/docker-compose.yml, which is stale.
        expect(referenceFor("0.4.1")).toBe("ghcr.io/argon-chat/orleans:0.4.1");
    });

    /** Somebody installing from their own registry types the whole thing, and it is taken as it is. */
    test("a reference is taken whole", () => {
        expect(referenceFor("registry.example.com:5000/argon/server:0.4.1")).toBe(
            "registry.example.com:5000/argon/server:0.4.1",
        );
    });
});

/**
 * The media subdomain's certificate, read from the environment by the same rule as the instance one.
 *
 * §5's Cloudflare shape publishes voice on a grey-clouded subdomain when the operator's plan cannot
 * carry media through the proxy. Cloudflare is not in that path, so the name needs a certificate of its
 * own — a second live certificate on the machine, from a different issuer, expiring on a different day.
 *
 * Both or neither, and that is the whole test. Half a pair is a listener that starts and fails every
 * handshake, and it is worse on this name than on the main one: voice failing is quieter than the API
 * failing, so it gets found by a user rather than by the operator watching the install finish.
 */
describe("a certificate and its key", () => {
    test("both halves present is a pair", () => {
        expect(certificatePair("/certs/media.pem", "/certs/media-key.pem")).toEqual({
            certificatePath: "/certs/media.pem",
            keyPath: "/certs/media-key.pem",
        });
    });

    /**
     * Half a pair is nothing, not half a certificate.
     *
     * A listener handed a certificate with no key starts and then fails every handshake, which reads as
     * a broken network rather than as a missing file. On the media subdomain that is worse than on the
     * main name: voice failing is quieter than the API failing, so it gets found by a user rather than
     * by the operator watching the install finish.
     */
    test.each([
        ["only the certificate", "/certs/media.pem", undefined],
        ["only the key", undefined, "/certs/media-key.pem"],
        ["neither", undefined, undefined],
    ])("%s is not a pair", (_label, certificate, key) => {
        expect(certificatePair(certificate, key)).toBeUndefined();
    });

    /**
     * Compose writes `FOO=` for a variable the host never defined, so a shape that legitimately has no
     * certificate — a tunnel, a local install — arrives with both variables present and empty. Reading
     * that as "a certificate was named" would refuse exactly the installs §5 permits.
     */
    test.each([
        ["an empty certificate", "", "/certs/media-key.pem"],
        ["an empty key", "/certs/media.pem", ""],
        ["both empty", "", ""],
    ])("%s is not a pair either", (_label, certificate, key) => {
        expect(certificatePair(certificate, key)).toBeUndefined();
    });
});

/**
 * And the environment is read through it, for both pairs.
 *
 * Asserted on the argv the compose invocation carries, because that is the only place a certificate the
 * setup was handed becomes observable from outside — the machine keeps them private, which is the point.
 */
describe("the media subdomain's certificate", () => {
    test("the environment names two pairs and both are read the same way", () => {
        const environment = {
            [ENVIRONMENT.configDirectory]: process.cwd(),
            [ENVIRONMENT.certificate]: "/certs/instance.pem",
            [ENVIRONMENT.certificateKey]: "/certs/instance-key.pem",
            [ENVIRONMENT.voiceCertificate]: "/certs/media.pem",
            [ENVIRONMENT.voiceCertificateKey]: "",
        };

        expect(setupFromEnvironment(environment)).toBeDefined();

        // The instance pair is whole and the media pair is not, so only the first survives — the same
        // rule, applied twice, rather than two implementations that could drift apart.
        expect(certificatePair(environment[ENVIRONMENT.certificate], environment[ENVIRONMENT.certificateKey]))
            .toBeDefined();
        expect(certificatePair(environment[ENVIRONMENT.voiceCertificate], environment[ENVIRONMENT.voiceCertificateKey]))
            .toBeUndefined();
    });
});
