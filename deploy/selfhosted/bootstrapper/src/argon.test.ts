import { describe, expect, test } from "bun:test";
import {
    ArgonCliError,
    dockerCommandFor,
    assertDetailMatchesSummary,
    checkPairing,
    explainRole,
    interrogate,
    parseExplain,
    parseImageTag,
    parseRoles,
    parseVersionNumbers,
    resolveVersion,
    sectionsOf,
    validateConfig,
    type CommandResult,
    type ServerImage,
    type ServerVersion,
} from "./argon";

/**
 * The fixtures are the shapes `ArgonClusterCli` actually prints, abridged. Alignment is significant to
 * a reader and not to the parser, and it is kept anyway: a fixture that has been tidied stops being
 * evidence about the thing it stands in for.
 */

const REFERENCE = "ghcr.io/argon-chat/server:0.4.1";

const ROLES = `role           kind    grains  features  description
account        client       0        15  developer account console — accounts, dev teams and their apps
admin          client       0        11  operator console
aegis          client       0        18  sign-in, sessions, device attestation
botapi         client       0         9  bot gateway
commerce       silo         4        12  entitlements and payments
core           silo        26        26  space, channel, identity, session, bot runtime, dev teams
dev            silo        42        42  every role in one process; development only
entrypoint     client       0        32  Ion protocol, SignalR hub, auth, webhooks
jobs           silo         6        14  deletions, exports, mail, expired-row sweep
media          silo         3        13  avatars and attachments
moderation     silo         2         8  ONNX image moderation
voice          silo         2        10  LiveKit tokens and room lifecycle

12 role(s), 42 grain class(es) discovered in 2 assembly(ies)

topology distributed [entrypoint, botapi, admin, account, aegis, core, voice, media, moderation, commerce, jobs]
topology single-instance [dev]
`;

/**
 * The same table as a container actually hands it over: Serilog's JSON formatter writing onto the same
 * stdout as the human output. The last event is the awkward one — a rendered message that is itself
 * shaped like a role row.
 */
const ROLES_WITH_LOGS = `{"Timestamp":"2026-08-21T03:54:29.1180000+00:00","Level":"Warning","MessageTemplate":"Assembly {Name} was not found","RenderedMessage":"Assembly Argon.Moderation was not found","Properties":{"Name":"Argon.Moderation"}}
role           kind    grains  features  description
account        client       0        15  developer account console — accounts, dev teams and their apps
{"Timestamp":"2026-08-21T03:54:29.2210000+00:00","Level":"Information","RenderedMessage":"Discovery finished"}
core           silo        26        26  space, channel, identity, session, bot runtime, dev teams
{"Timestamp":"2026-08-21T03:54:29.3010000+00:00","Level":"Warning","RenderedMessage":"ghost          silo         9         9  not a role at all"}
entrypoint     client       0        32  Ion protocol, SignalR hub, auth, webhooks

3 role(s), 42 grain class(es) discovered in 2 assembly(ies)

topology single-instance [dev]
`;

const EXPLAIN_ENTRYPOINT = `role 'entrypoint' — Orleans client
  Ion protocol, SignalR hub, auth, webhooks

hosts 0 grain(s):

calls 3 grain interface(s):
  IUserSessionGrain                  x9    remote
  ISpaceGrain                        x4    remote
  IFusionGrain                       x1    remote

features, in configure order:
  geoip  [GeoIp]
  http-client
  kestrel  [Kestrel:Argon]
  database  [Database, Database:Regions]
  argon-authorization  [auth, auth:passwordHashing, auth:anonymousRateLimits, auth:deviceMatching, attestation:android]
  ion
  websockets  [WebSockets]

reads 10 configuration section(s); each may also come from conf.d/<feature>.json
`;

/**
 * A silo, which is the fixture that matters most: `hosts N grain(s):` prints lines of exactly the same
 * shape as the feature list, badges in brackets and all.
 */
const EXPLAIN_CORE = `role 'core' — Orleans silo
  space, channel, identity, session, bot runtime, dev teams
  gateway: True, reminders: True

hosts 3 grain(s):
  ChannelGrain  [storage:channel]
  SpaceGrain  [remindable, storage:space]
  UserChatGrain  [stateless-worker, storage:default]

calls 1 grain interface(s):
  IUserGrain                         x7    local

features, in configure order:
  database  [Database, Database:Regions]
  orleans  [Orleans:Cluster]
  jobs

reads 3 configuration section(s); each may also come from conf.d/<feature>.json
`;

const EXPLAIN_WITHOUT_FEATURES = `role 'ghost' — Orleans silo
  a role that enables nothing

hosts 0 grain(s):

calls 0 grain interface(s):
`;

function ok(stdout: string): CommandResult {
    return { stdout, stderr: "", exitCode: 0 };
}

const LABELLED: ServerVersion = { value: "0.4.1.1763-development+e2ed453", source: "image-label", reference: REFERENCE };

/** An image that answers from fixtures. No container, no docker, no network. */
function imageAnswering(answers: Record<string, CommandResult>, version: ServerVersion = LABELLED): ServerImage {
    return {
        reference: REFERENCE,
        run: async (args) =>
            answers[args.join(" ")] ?? { stdout: "", stderr: `no fixture for '${args.join(" ")}'`, exitCode: 64 },
        version: async () => version,
    };
}

/** The failure a call produced, so a test can say which kind it was and not only that there was one. */
function failureFrom(work: () => unknown): ArgonCliError {
    try {
        work();
    } catch (error) {
        if (error instanceof ArgonCliError) return error;
        throw error;
    }

    throw new Error("expected this to fail, and it answered");
}

async function failureOf(work: Promise<unknown>): Promise<ArgonCliError> {
    try {
        await work;
    } catch (error) {
        if (error instanceof ArgonCliError) return error;
        throw error;
    }

    throw new Error("expected this to fail, and it answered");
}

describe("reading --roles", () => {
    test("every role in the table comes back, with its shape and its description", () => {
        const capabilities = parseRoles(ROLES, "0.4.1");

        expect(capabilities.roles).toHaveLength(12);
        expect(capabilities.roles[0]).toEqual({
            id: "account",
            kind: "client",
            grains: 0,
            features: 15,
            description: "developer account console — accounts, dev teams and their apps",
        });
        expect(capabilities.roles.find((role) => role.id === "core")).toEqual({
            id: "core",
            kind: "silo",
            grains: 26,
            features: 26,
            description: "space, channel, identity, session, bot runtime, dev teams",
        });
    });

    test("topologies come back with their roles split out", () => {
        const capabilities = parseRoles(ROLES, "0.4.1");

        expect(capabilities.topologies).toHaveLength(2);
        expect(capabilities.topologies[0]?.name).toBe("distributed");
        expect(capabilities.topologies[0]?.roles).toContain("moderation");
        expect(capabilities.topologies[1]).toEqual({ name: "single-instance", roles: ["dev"] });
    });

    /**
     * The version is a parameter because `--roles` does not print one — see the note on VersionSource
     * for why there is no `--version` to ask. This pins that it is passed through untouched rather
     * than quietly derived from something in the text.
     */
    test("the version is the caller's, because the command does not print one", () => {
        expect(parseRoles(ROLES, "0.4.1.1763-development+e2ed453").version).toBe("0.4.1.1763-development+e2ed453");
    });

    /**
     * Log events share stdout with the table, and one of them here renders a message shaped exactly
     * like a role row. Without the noise filter it parses as a role called `{"Timestamp":..."ghost`,
     * and the wizard offers the operator a role that does not exist.
     */
    test("a log line is not a role, however much it looks like one", () => {
        const capabilities = parseRoles(ROLES_WITH_LOGS, "0.4.1");

        expect(capabilities.roles.map((role) => role.id)).toEqual(["account", "core", "entrypoint"]);
    });

    /**
     * Silently returning nothing here produces a wizard with no roles to offer, which gets reported as
     * a UI bug and looked for in the wrong half of the system.
     */
    test("output with no table at all is a failure, not an empty list", () => {
        const failure = failureFrom(() => parseRoles("nothing here\nnor here\n", "0.4.1"));

        expect(failure.kind).toBe("unreadable-output");
    });

    /**
     * The command counts itself, so a short read is detectable. It matters because the roles that go
     * missing are silently absent from the install rather than visibly broken in it.
     */
    test("a table shorter than the count it declares is a failure", () => {
        const truncated = `role           kind    grains  features  description
core           silo        26        26  spaces and channels
entrypoint     client       0        32  the API

12 role(s), 42 grain class(es) discovered in 2 assembly(ies)
`;

        expect(failureFrom(() => parseRoles(truncated, "0.4.1")).message).toContain("12 role(s) but 2");
    });

    test("a role with an empty description keeps the rest of its columns", () => {
        const capabilities = parseRoles(
            `core           silo        26        26  \n\n1 role(s), 26 grain class(es) discovered in 1 assembly(ies)\n`,
            "0.4.1",
        );

        expect(capabilities.roles[0]).toEqual({
            id: "core",
            kind: "silo",
            grains: 26,
            features: 26,
            description: "",
        });
    });
});

describe("reading --explain", () => {
    test("features come back in configure order, with the sections each one reads", () => {
        const detail = parseExplain(EXPLAIN_ENTRYPOINT);

        expect(detail.id).toBe("entrypoint");
        expect(detail.features.map((feature) => feature.name)).toEqual([
            "geoip",
            "http-client",
            "kestrel",
            "database",
            "argon-authorization",
            "ion",
            "websockets",
        ]);
    });

    test("a feature may read nothing, one section, or several", () => {
        const byName = new Map(parseExplain(EXPLAIN_ENTRYPOINT).features.map((f) => [f.name, f.sections]));

        expect(byName.get("http-client")).toEqual([]);
        expect(byName.get("kestrel")).toEqual(["Kestrel:Argon"]);
        expect(byName.get("database")).toEqual(["Database", "Database:Regions"]);
        expect(byName.get("argon-authorization")).toEqual([
            "auth",
            "auth:passwordHashing",
            "auth:anonymousRateLimits",
            "auth:deviceMatching",
            "attestation:android",
        ]);
    });

    /**
     * The property this pins: only the block after the features header is read as features.
     *
     * `hosts N grain(s):` prints `  UserChatGrain  [stateless-worker, storage:default]`, which is the
     * same shape as `  websockets  [WebSockets]`. A line-wise parser reports the grain as a feature and
     * `storage:default` as a configuration section, and the generator then writes a `conf.d` full of
     * sections no feature declared — which the server refuses as C3 at best, and ignores at worst.
     */
    test("hosted grains are not features, and their badges are not configuration sections", () => {
        const detail = parseExplain(EXPLAIN_CORE);

        expect(detail.features.map((feature) => feature.name)).toEqual(["database", "orleans", "jobs"]);
        expect(sectionsOf([detail])).toEqual(["Database", "Database:Regions", "Orleans:Cluster"]);
        expect(sectionsOf([detail])).not.toContain("storage:default");
    });

    test("a log line inside the feature block is stepped over rather than ending it", () => {
        const noisy = `role 'core' — Orleans silo

features, in configure order:
  database  [Database, Database:Regions]
{"Timestamp":"2026-08-21T03:54:29.9910000+00:00","Level":"Warning","RenderedMessage":"slow reflection pass"}
  orleans  [Orleans:Cluster]
  jobs

reads 3 configuration section(s); each may also come from conf.d/<feature>.json
`;

        expect(parseExplain(noisy).features.map((feature) => feature.name)).toEqual(["database", "orleans", "jobs"]);
    });

    /**
     * The command prints its own section total, which is the only way to tell a feature block that
     * ended from one that was cut short. Configuration generated from a short block is missing a
     * section the server will then refuse to start without, discovered several minutes later.
     */
    test("a feature block that does not add up to the declared total is a failure", () => {
        const cut = `role 'entrypoint' — Orleans client

features, in configure order:
  geoip  [GeoIp]

reads 10 configuration section(s); each may also come from conf.d/<feature>.json
`;

        expect(failureFrom(() => parseExplain(cut)).message).toContain("10 configuration section(s) but 1");
    });

    test("sections listed without the total that confirms them are a failure", () => {
        const unconfirmed = `role 'entrypoint' — Orleans client

features, in configure order:
  geoip  [GeoIp]
`;

        expect(failureFrom(() => parseExplain(unconfirmed)).kind).toBe("unreadable-output");
    });

    test("a role that enables no features is empty rather than broken", () => {
        expect(parseExplain(EXPLAIN_WITHOUT_FEATURES)).toEqual({ id: "ghost", features: [] });
    });

    test("output that is not an explain at all is a failure", () => {
        expect(failureFrom(() => parseExplain("Unhandled exception. System.IO.FileNotFoundException\n")).kind).toBe(
            "unreadable-output",
        );
    });
});

describe("what the two commands say about each other", () => {
    /**
     * Both numbers come from the same field of the same descriptor, so they cannot legitimately
     * differ. When they do, one of the two was misread — and the one about to be turned into files is
     * the detail.
     */
    test("a feature count that disagrees with the feature list is a failure", () => {
        const summary = parseRoles(ROLES, "0.4.1").roles.find((role) => role.id === "core");
        const detail = parseExplain(EXPLAIN_CORE);

        expect(summary?.features).toBe(26);
        expect(failureFrom(() => assertDetailMatchesSummary(summary!, detail)).message).toContain("26 feature(s)");
    });

    test("a detail for a different role than the summary is a failure", () => {
        const summary = parseRoles(ROLES, "0.4.1").roles.find((role) => role.id === "voice");

        expect(failureFrom(() => assertDetailMatchesSummary(summary!, parseExplain(EXPLAIN_CORE))).kind).toBe(
            "unreadable-output",
        );
    });

    test("the sections a set of roles reads are collected once each, in order", () => {
        expect(sectionsOf([parseExplain(EXPLAIN_ENTRYPOINT), parseExplain(EXPLAIN_CORE)])).toEqual([
            "Database",
            "Database:Regions",
            "GeoIp",
            "Kestrel:Argon",
            "Orleans:Cluster",
            "WebSockets",
            "attestation:android",
            "auth",
            "auth:anonymousRateLimits",
            "auth:deviceMatching",
            "auth:passwordHashing",
        ]);
    });
});

describe("which server this is", () => {
    test("the label wins, because the build wrote it and the operator wrote the tag", () => {
        expect(resolveVersion("ghcr.io/argon-chat/server:0.4.1", "0.4.1.1763-development+e2ed453")).toEqual({
            value: "0.4.1.1763-development+e2ed453",
            source: "image-label",
            reference: "ghcr.io/argon-chat/server:0.4.1",
        });
    });

    test("the tag is taken when the image carries no label", () => {
        expect(resolveVersion("ghcr.io/argon-chat/server:0.4.1", undefined).source).toBe("image-tag");
        expect(resolveVersion("ghcr.io/argon-chat/server:0.4.1", "  ").value).toBe("0.4.1");
    });

    /** Docker prints this for a template that resolved to nothing. It is an absence, not a version. */
    test("a label the build never filled in is an absence", () => {
        expect(resolveVersion("ghcr.io/argon-chat/server:0.4.1", "<no value>").source).toBe("image-tag");
    });

    test("a tag that names a stream rather than a release identifies nothing", () => {
        expect(resolveVersion("ghcr.io/argon-chat/server:latest", undefined).source).toBe("unknown");
        expect(resolveVersion("ghcr.io/argon-chat/server", undefined).source).toBe("unknown");
    });

    /** A digest pins exactly one image and says nothing at all about which version is inside it. */
    test("a digest is not a version", () => {
        const digest = "ghcr.io/argon-chat/server@sha256:0123456789abcdef";

        expect(parseImageTag(digest)).toBeUndefined();
        expect(resolveVersion(digest, undefined).source).toBe("unknown");
    });

    test("a registry port is not a tag", () => {
        expect(parseImageTag("localhost:5000/argon/server")).toBeUndefined();
        expect(parseImageTag("localhost:5000/argon/server:0.4.1")).toBe("0.4.1");
    });

    /** GitVersion gives the server four components and a suffix. Only the first three are ordering. */
    test("GitVersion's fourth component and suffix do not confuse the numbers", () => {
        expect(parseVersionNumbers("0.4.1.1763-development+e2ed453")).toEqual([0, 4, 1]);
        expect(parseVersionNumbers("v0.4")).toEqual([0, 4, 0]);
        expect(parseVersionNumbers("not-a-version")).toBeUndefined();
    });
});

describe("refusing a pairing", () => {
    const at = (value: string, source: ServerVersion["source"] = "image-label"): ServerVersion => ({
        value,
        source,
        reference: REFERENCE,
    });

    test("a server inside the range is accepted", () => {
        expect(checkPairing(at("0.4.1.1763-development+e2ed453")).ok).toBe(true);
    });

    test("a server older than the range is refused, and says which range", () => {
        const pairing = checkPairing(at("0.3.9"));

        expect(pairing.ok).toBe(false);
        expect(pairing.ok === false && pairing.reason).toBe("too-old");
        expect(pairing.ok === false && pairing.detail).toContain("0.4.0");
    });

    test("a server newer than the range is refused, and says to update this side", () => {
        const pairing = checkPairing(at("0.5.0"));

        expect(pairing.ok === false && pairing.reason).toBe("too-new");
        expect(pairing.ok === false && pairing.detail).toContain("Update the bootstrapper");
    });

    /**
     * Semver sorts `0.5.0-rc1` below `0.5.0`; this deliberately does not. The question is which output
     * format the binary prints, and an rc of 0.5 prints 0.5's — so treating it as 0.4-compatible would
     * wave through exactly the build most likely to have changed the shape these parsers depend on.
     */
    test("a prerelease of a version we do not understand is still one we do not understand", () => {
        expect(checkPairing(at("0.5.0-rc1")).ok).toBe(false);
    });

    /**
     * An unreadable version is refused rather than assumed good: `:latest` may hold anything, and the
     * failure it produces if it holds the wrong thing is a misparse, which does not announce itself.
     * The verdict is data, so the wizard can still let an operator past it knowingly.
     */
    test("a version that cannot be read is refused rather than assumed", () => {
        const pairing = checkPairing(at("ghcr.io/argon-chat/server:latest", "unknown"));

        expect(pairing.ok === false && pairing.reason).toBe("unreadable");
    });
});

describe("interrogating an image", () => {
    test("the roles and the version come back together", async () => {
        const image = imageAnswering({ "--roles": ok(ROLES) });

        const { capabilities, version, pairing } = await interrogate(image);

        expect(capabilities.roles).toHaveLength(12);
        expect(capabilities.version).toBe("0.4.1.1763-development+e2ed453");
        expect(version.source).toBe("image-label");
        expect(pairing.ok).toBe(true);
    });

    /**
     * A pairing this side does not understand is reported and not thrown, because refusing is a policy
     * and this is a fact. An operator running a nightly should be able to go on past a warning; the
     * wizard is what decides, and it cannot decide about an exception it never sees.
     */
    test("a pairing it does not understand is reported rather than thrown", async () => {
        const image = imageAnswering({ "--roles": ok(ROLES) }, { value: "0.9.0", source: "image-tag", reference: REFERENCE });

        const { pairing } = await interrogate(image);

        expect(pairing.ok === false && pairing.reason).toBe("too-new");
    });

    test("a command that fails is an error carrying the server's own words", async () => {
        const image = imageAnswering({ "--roles": { stdout: "", stderr: "boom", exitCode: 1 } });

        const failure = await failureOf(interrogate(image));

        expect(failure.kind).toBe("command-failed");
        expect(failure.output).toBe("boom");
    });

    /**
     * Docker failing to run something is not the server saying no. Exit 125 is docker's own code for
     * "I could not start this", and reporting it as a command failure sends the operator looking at
     * their configuration for a problem that is in their daemon.
     */
    test("docker failing to run is a different failure from the command failing", async () => {
        const image = imageAnswering({ "--roles": { stdout: "", stderr: "docker: not found", exitCode: 125 } });

        expect((await failureOf(interrogate(image))).kind).toBe("runner-failed");
    });

    test("explaining a role checks the answer against the role that was asked for", async () => {
        const image = imageAnswering({ "--explain core": ok(EXPLAIN_CORE) });

        expect((await explainRole(image, "core")).features).toHaveLength(3);
        expect((await failureOf(explainRole(image, "voice"))).kind).toBe("command-failed");
    });

    test("explaining a role cross-checks the count --roles gave for it", async () => {
        const image = imageAnswering({ "--roles": ok(ROLES), "--explain core": ok(EXPLAIN_CORE) });
        const { capabilities } = await interrogate(image);
        const summary = capabilities.roles.find((role) => role.id === "core");

        expect((await failureOf(explainRole(image, "core", summary))).kind).toBe("unreadable-output");
    });
});

describe("validating a configuration", () => {
    const REPORT = `role 'core' — 3 configuration section(s) across 26 feature(s)
  => 0 error(s), 1 warning(s)
`;

    /**
     * The verdict is the exit code and nothing else. The command also prints `=> N error(s)` per role,
     * and reading the answer out of that text would be a second implementation of an aggregation the
     * process already did — one that starts disagreeing the first time another role joins the run.
     */
    test("exit zero is the whole verdict, warnings in the text or not", async () => {
        const image = imageAnswering({ "--validate-config --role core": ok(REPORT) });

        const outcome = await validateConfig(image, "core");

        expect(outcome.ok).toBe(true);
        expect(outcome.output).toContain("1 warning(s)");
    });

    test("a non-zero exit is an outcome, and keeps the text for the operator", async () => {
        const image = imageAnswering({
            "--validate-config": { stdout: "  [E] C3 unknown section 'Kestrel'", stderr: "", exitCode: 1 },
        });

        const outcome = await validateConfig(image);

        expect(outcome).toEqual({ ok: false, exitCode: 1, output: "  [E] C3 unknown section 'Kestrel'" });
    });

    /**
     * The one case that must not be reported as a failed validation: an operator told their
     * configuration is invalid will go and edit files, and the problem is that docker could not run.
     */
    test("docker failing to run is not a failed validation", async () => {
        const image = imageAnswering({
            "--validate-config": { stdout: "", stderr: "docker: permission denied", exitCode: 125 },
        });

        expect((await failureOf(validateConfig(image))).kind).toBe("runner-failed");
    });
});

/**
 * The unscoped document travels with the directory, or validation lies.
 *
 * Mounting `conf.d` alone leaves every generated secret invisible to the container, and a required
 * setting with no value is an Error rather than a warning — so a good configuration comes back invalid,
 * confidently. The env var and the mount are asserted together because either one alone is its own false
 * red: the server reports a named-but-absent file as an Error too.
 */
describe("what the container is allowed to see", () => {
    test("the secrets document is mounted and named together", () => {
        const cmd = dockerCommandFor("argon/server:1.2.3", {
            configDir: "/install/conf.d",
            secretsFile: "/install/secrets.json",
        });

        const line = cmd.join(" ");

        expect(line).toContain("/install/secrets.json:");
        expect(line).toContain("ARGON_CONFIG_FILE=");

        // Read-only, for the same reason conf.d is: a validator that could rewrite what it is checking
        // is not a validator.
        expect(line).toMatch(/\/install\/secrets\.json:[^\s]+:ro/);
    });

    test("naming no secrets document mounts nothing and sets nothing", () => {
        const line = dockerCommandFor("argon/server:1.2.3", { configDir: "/install/conf.d" }).join(" ");

        expect(line).not.toContain("ARGON_CONFIG_FILE");
    });
});
