import { describe, expect, test } from "bun:test";
import { DOCKER_SOCKET } from "./docker";
import type { Answers, RoleSummary } from "./model";
import { DEPLOYMENT, type MintedSecrets } from "./generate";
import {
    BOOTSTRAPPER_IMAGE_REPOSITORY,
    BOOTSTRAP_CODE_FILE,
    COMPOSE_PROJECT,
    EDGE_DYNAMIC_CONFIG,
    EDGE_STATIC_CONFIG,
    ENVIRONMENT_VARIABLES,
    ENV_FILENAME,
    OPTIONAL_ROLES,
    PANEL_PATH,
    REFUSED_ROLES,
    REQUIRED_ROLES,
    SERVER_IMAGE_REPOSITORY,
    STORAGE_IDENTITIES,
    assertHostname,
    bootstrapProject,
    composeProject,
    rolesFor,
    serverImageFor,
    type ComposeOptions,
    type ComposeProject,
} from "./compose";

/* ------------------------------------------------------------------------------------------------
 * Fixtures.
 *
 * The document is JSON — see the header of `compose.ts` for why — so every assertion below reads it
 * back with `JSON.parse` and asserts on structure. That is the whole point of the format choice: a
 * test that looked for `"8443"` in a string would pass on a document that published it from the wrong
 * service, which is precisely the mistake worth catching.
 * ---------------------------------------------------------------------------------------------- */

/** Recognisable, so a leak test asserts on the value rather than on entropy. Mirrors generate.test.ts. */
function markedSecrets(): MintedSecrets {
    return {
        databasePassword: "MARKED-database-password",
        jwtMachineSalt: "MARKED-machine-salt",
        jwtSigning: { privateKey: "MARKED-signing-private", publicKey: "MARKED-signing-public" },
        jwtEncryption: {
            privateKeyBase64: "MARKED-encryption-private",
            publicKeyBase64: "MARKED-encryption-public",
        },
        ticketKey: "MARKED-ticket-key",
        transportHashKey: "MARKED-transport-hash",
        totpSecretPart: "MARKED-totp-part",
        metricsPassword: "MARKED-metrics-password",
        objectStorage: { accessKey: "MARKED-storage-access", secretKey: "MARKED-storage-secret" },
        sfu: { clientId: "MARKED-sfu-client", secret: "MARKED-sfu-secret" },
    };
}

/** Every string in a minted bundle, however deeply nested. */
function everyValue(source: object): string[] {
    return Object.values(source).flatMap((value) =>
        typeof value === "string" ? [value] : typeof value === "object" && value !== null ? everyValue(value) : [],
    );
}

/**
 * The default shape is `own-certificate`, not `lets-encrypt`.
 *
 * Deliberate: it is the shape in which material arrives on disk, which is what most of the assertions
 * below are about. `lets-encrypt` now *refuses* `options.tls` — Traefik obtains its own — so a fixture
 * carrying both would be a contradiction, and every certificate test would be testing the refusal.
 */
function answers(overrides: Partial<Answers> = {}): Answers {
    return {
        domain: "chat.example.org",
        serverVersion: "0.4.2",
        roles: [],
        storage: { kind: "local" },
        traffic: { kind: "own-certificate" },
        voice: false,
        ...overrides,
    };
}

const TLS = { certificatePath: "/etc/argon/tls.crt", keyPath: "/etc/argon/tls.key" };
const VOICE_TLS = { certificatePath: "/etc/argon/media.crt", keyPath: "/etc/argon/media.key" };

const OPTIONS: ComposeOptions = { installRoot: "/opt/argon", tls: TLS };

function project(overrides: Partial<Answers> = {}, options: ComposeOptions = OPTIONS): ComposeProject {
    return composeProject(answers(overrides), markedSecrets(), options);
}

interface ComposeService {
    readonly image?: string;
    readonly command?: readonly string[];
    readonly entrypoint?: readonly string[];
    readonly restart?: string;
    readonly environment?: Readonly<Record<string, string>>;
    readonly volumes?: readonly string[];
    readonly ports?: readonly string[];
    readonly networks?: readonly string[];
    readonly depends_on?: Readonly<Record<string, { readonly condition: string }>>;
}

interface ComposeDocument {
    readonly name: string;
    readonly services: Readonly<Record<string, ComposeService>>;
    readonly networks: Readonly<Record<string, unknown>>;
    readonly volumes: Readonly<Record<string, unknown>>;
}

function read(built: ComposeProject): ComposeDocument {
    return JSON.parse(built.document) as ComposeDocument;
}

/* ------------------------------------------------------------------------------------------------
 * Reading the edge back.
 *
 * Traefik's two files are JSON in a `.yml`, for the reason the compose document is JSON: YAML is a
 * superset, Traefik parses them with a YAML parser, and a test can therefore ask whether a *router*
 * exists on the right entry point rather than whether a word appears in a string. The routing this
 * module has to get right is structural — one certificate resolver, one catch-all, a bucket route
 * scoped to two verbs — and a substring assertion cannot tell those from a comment.
 * ---------------------------------------------------------------------------------------------- */

interface TraefikRouter {
    readonly rule: string;
    readonly priority?: number;
    readonly entryPoints: readonly string[];
    readonly service: string;
    readonly middlewares?: readonly string[];
    readonly tls?: { readonly certResolver?: string };
}

interface TraefikDynamic {
    readonly http: {
        readonly routers: Readonly<Record<string, TraefikRouter>>;
        readonly middlewares: Readonly<
        Record<
            string,
            {
                readonly stripPrefix?: { prefixes: string[] };
                readonly redirectRegex?: { regex: string; replacement: string; permanent?: boolean };
            }
        >
    >;
        readonly services: Readonly<Record<string, { readonly loadBalancer: { servers: { url: string }[] } }>>;
    };
    readonly tls?: {
        readonly stores?: { default?: { defaultCertificate?: { certFile: string; keyFile: string } } };
        readonly certificates?: readonly { certFile: string; keyFile: string }[];
    };
}

interface TraefikStatic {
    readonly entryPoints: Readonly<Record<string, { address: string }>>;
    readonly providers: { file: { filename: string; watch: boolean } };
    readonly certificatesResolvers?: Readonly<Record<string, { acme: Record<string, unknown> }>>;
    readonly api?: unknown;
}

function dynamic(built: ComposeProject): TraefikDynamic {
    return JSON.parse(fileNamed(built, EDGE_DYNAMIC_CONFIG).contents) as TraefikDynamic;
}

function statically(built: ComposeProject): TraefikStatic {
    return JSON.parse(fileNamed(built, EDGE_STATIC_CONFIG).contents) as TraefikStatic;
}

/** The backend URL a named router forwards to, resolved through the service it names. */
function backendOf(built: ComposeProject, router: string): string {
    const document = dynamic(built);
    const found = document.http.routers[router];

    if (found === undefined)
        throw new Error(`no router '${router}' among ${Object.keys(document.http.routers).join(", ")}`);

    return document.http.services[found.service]?.loadBalancer.servers[0]?.url ?? "";
}

function service(built: ComposeProject, name: string): ComposeService {
    const found = read(built).services[name];

    if (found === undefined)
        throw new Error(`no service '${name}' among ${Object.keys(read(built).services).join(", ")}`);

    return found;
}

/** The service a role runs in. Kept here rather than exported, so the naming is pinned by the tests. */
const roleService = (role: string) => `argon-${role}`;

function fileNamed(built: ComposeProject, path: string): { contents: string; mode: number } {
    const found = built.files.find((file) => file.path === path);

    if (found === undefined) throw new Error(`no file '${path}' among ${built.files.map((f) => f.path).join(", ")}`);

    return found;
}

describe("which roles run", () => {
    test("every required role runs, whatever the operator answered about roles", () => {
        const built = project({ roles: [] });

        for (const role of REQUIRED_ROLES) expect(built.roles).toContain(role);
    });

    test("an optional role runs only when the operator asked for it", () => {
        const without = project({ roles: [] });
        const with_ = project({ roles: ["botapi", "admin", "account"] });

        for (const role of ["botapi", "admin", "account"]) {
            expect(without.roles).not.toContain(role);
            expect(with_.roles).toContain(role);
        }

        expect(Object.keys(read(without).services)).not.toContain(roleService("botapi"));
        expect(Object.keys(read(with_).services)).toContain(roleService("botapi"));
    });

    /**
     * `voice` is its own question, and `generate.ts` gates `CallKit` on the same flag.
     *
     * Reading it from the role list instead would let the two disagree, and the disagreement is silent
     * in both directions: a voice role with no SFU configuration, or an SFU nothing talks to.
     */
    test("the voice role follows the voice answer and not the role list", () => {
        expect(project({ voice: true, roles: [] }).roles).toContain("voice");
        expect(project({ voice: false, roles: ["voice"] }).roles).not.toContain("voice");
    });

    /**
     * §6 puts three roles out of reach: two have no meaning on one operator's box, and `dev` is the
     * single-process role that exists in no other deployment and so breaks in ways production never
     * sees. This is here because the wizard is the thing that would start offering them by accident.
     */
    test("commerce, moderation and the dev role never run, even when asked for", () => {
        const built = project({ roles: [...REFUSED_ROLES] });

        for (const role of REFUSED_ROLES) {
            expect(built.roles).not.toContain(role);
            expect(Object.keys(read(built).services)).not.toContain(roleService(role));
        }
    });

    test("a version becomes a tag on the published image, and a reference is left alone", () => {
        expect(serverImageFor("0.4.2")).toBe(`${SERVER_IMAGE_REPOSITORY}:0.4.2`);
        expect(serverImageFor("ghcr.io/argon-chat/orleans@sha256:abc")).toBe(
            "ghcr.io/argon-chat/orleans@sha256:abc",
        );
        expect(service(project(), roleService("core")).image).toBe(`${SERVER_IMAGE_REPOSITORY}:0.4.2`);
    });

    test("the caller is handed the same role list the document runs", () => {
        const built = project({ roles: ["admin"], voice: true });

        for (const role of built.roles) expect(Object.keys(read(built).services)).toContain(roleService(role));

        expect(built.roles.length).toBe(REQUIRED_ROLES.length + 2);
        expect(rolesFor(answers({ roles: ["admin"], voice: true }))).toEqual([...built.roles]);
    });
});

describe("what faces the world", () => {
    /**
     * The property this file exists to keep.
     *
     * A silo's ports are its Orleans silo and gateway endpoints. Publishing one puts a cluster gateway
     * on the internet, where anything that can reach it can address a grain — no authentication sits in
     * front of that. The roles find each other through the membership table in Redis, on the compose
     * network, so none of them ever needs a host port.
     */
    test("no silo publishes anything", () => {
        const built = project({ roles: [...OPTIONAL_ROLES], voice: true });
        const silos = ["core", "media", "jobs", "voice"];

        for (const role of silos) expect(service(built, roleService(role)).ports).toBeUndefined();
    });

    /**
     * And the rule that produces it is "publish for clients", never "skip silos" — so a role nothing
     * has heard of publishes nothing. Here the image is made to disagree with this module's own table:
     * `--roles` calls `admin` a silo, and that has to win, because the direction of the mistake is the
     * difference between an unreachable console and an exposed gateway.
     */
    test("what the image says a role is beats what this module thinks it is", () => {
        const known: readonly RoleSummary[] = [
            { id: "admin", kind: "silo", grains: 0, features: 0, description: "" },
        ];

        const built = composeProject(answers({ roles: ["admin"] }), markedSecrets(), {
            ...OPTIONS,
            knownRoles: known,
        });

        expect(service(built, roleService("admin")).ports).toBeUndefined();
    });

    /**
     * The same rule reaches the edge, because a route is a claim about what is behind it.
     *
     * `aegis` gets a Traefik entry point and a host port only if the image says it is a client. Told it
     * is a silo, there is no listener to route to — so there is no router, no entry point and no
     * published port, rather than a door onto a 502.
     */
    test("a role the image calls a silo gets no route and no port at the edge either", () => {
        const known: readonly RoleSummary[] = [
            { id: "aegis", kind: "silo", grains: 0, features: 0, description: "" },
        ];

        const built = composeProject(answers(), markedSecrets(), { ...OPTIONS, knownRoles: known });

        expect(Object.keys(dynamic(built).http.routers)).not.toContain("identity");
        expect(Object.keys(statically(built).entryPoints)).not.toContain("identity");
        expect(built.published.some((port) => port.hostPort === 8443)).toBe(false);
    });

    /**
     * The consequence of Traefik owning TLS, stated as a property.
     *
     * `aegis` and `botapi` used to publish their own Kestrel listeners on 8443 and 8444. Kestrel now
     * serves plain HTTP, so publishing one would be a sign-in page and a bot API on the internet in the
     * clear. Both host ports survive — same URL for the operator — but they belong to the edge, which
     * is the only process on the machine with a key.
     */
    test("sign-in and the bot API are reached through the edge, not from their own containers", () => {
        const built = project({ roles: ["botapi"] });

        expect(service(built, roleService("aegis")).ports).toBeUndefined();
        expect(service(built, roleService("botapi")).ports).toBeUndefined();

        for (const port of [8443, 8444]) {
            const published = built.published.filter((candidate) => candidate.hostPort === port);

            expect(published).toHaveLength(1);
            expect(published[0]?.service).toBe("argon-edge");
            expect(published[0]?.address).toBe("0.0.0.0");
        }

        expect(backendOf(built, "identity")).toBe("http://argon-aegis:8080");
        expect(backendOf(built, "bots")).toBe("http://argon-botapi:8080");
    });

    /** And the bot API's entry point exists only when the role does; nothing listens for it otherwise. */
    test("the bot API's port is published only when the bot API runs", () => {
        expect(project({ roles: [] }).published.some((port) => port.hostPort === 8444)).toBe(false);
        expect(project({ roles: ["botapi"] }).published.some((port) => port.hostPort === 8444)).toBe(true);
    });

    /** Every published port carries the sentence the installer prints before the operator opens a firewall. */
    test("every published port is published by exactly one service and says why", () => {
        const built = project({ roles: ["botapi", "admin", "account"], voice: true });

        for (const port of built.published) {
            expect(port.why.length).toBeGreaterThan(20);
            expect(Object.keys(read(built).services)).toContain(port.service);
        }

        const declared = Object.values(read(built).services).flatMap((s) => s.ports ?? []);

        expect(declared.length).toBe(built.published.length);
    });

    test("only one port carries the product, and it is the front door", () => {
        const built = project({ roles: ["botapi", "admin", "account"], voice: true });
        const world = built.published.filter((port) => port.address === "0.0.0.0" && port.protocol === "tcp");

        expect(world.filter((port) => port.hostPort === 443)).toHaveLength(1);
        expect(world.find((port) => port.hostPort === 443)?.service).toBe("argon-edge");
        expect(service(built, roleService("entrypoint")).ports).toBeUndefined();
    });

    /**
     * The consoles answer on listeners of their own — 8920 and 8930, which the server image EXPOSEs —
     * and those listeners have no TLS: `Kestrel:Argon` configures the other port. §10 calls the operator
     * console the highest-value target on the machine, so it does not go on a public interface in the
     * clear.
     */
    test("the operator and developer consoles are reachable only on the loopback", () => {
        const built = project({ roles: ["admin", "account"] });

        for (const [role, port] of [
            ["admin", 8920],
            ["account", 8930],
        ] as const) {
            expect(service(built, roleService(role)).ports).toEqual([`127.0.0.1:${port}:${port}`]);
        }
    });

    test("voice publishes media and nothing else, and only when voice is on", () => {
        const off = project({ voice: false });

        expect(off.published.some((port) => port.service === DEPLOYMENT.hosts.sfu)).toBe(false);
        expect(Object.keys(read(off).services)).not.toContain(DEPLOYMENT.hosts.sfu);

        const on = project({ voice: true });
        const sfu = on.published.filter((port) => port.service === DEPLOYMENT.hosts.sfu);

        expect(sfu.map((port) => `${port.hostPort}/${port.protocol}`).sort()).toEqual(["7881/tcp", "7882/udp"]);

        // Signalling is reached through the front door, so it needs no host port and must not get one.
        expect(sfu.some((port) => port.containerPort === DEPLOYMENT.ports.sfu)).toBe(false);
    });

    /**
     * Nothing an operator can type reaches a routing rule unchecked.
     *
     * A Traefik rule is an expression, not data: the value lands inside `` Host(`…`) ``, where a
     * backtick closes the literal and `||` opens another clause. The dynamic file being JSON does not
     * help — `JSON.stringify` escapes for the file, and Traefik parses what comes out of it.
     */
    test("a domain that is not a hostname is refused rather than written into a Traefik rule", () => {
        expect(() => assertHostname("chat.example.org", "domain")).not.toThrow();
        expect(() => project({ domain: "chat.example.org`) || Host(`evil.example.com" })).toThrow(
            /not a hostname/,
        );
        expect(() =>
            project({ traffic: { kind: "cloudflare-proxied", voiceHost: "media example org" }, voice: true }),
        ).toThrow(/not a hostname/);
    });
});

describe("what a role is given", () => {
    /**
     * Both halves, always. `conf.d` alone is what `argon.ts` warns about when it interrogates an image:
     * every generated secret is deliberately kept out of the per-feature files, so a container that can
     * see only `conf.d` is a container missing every required value it has.
     */
    test("every role mounts conf.d and the secrets document, and names both in its environment", () => {
        const built = project({ roles: [...OPTIONAL_ROLES], voice: true });

        for (const role of built.roles) {
            const definition = service(built, roleService(role));

            expect(definition.volumes).toContain(`/opt/argon/${DEPLOYMENT.confD}:/conf.d:ro`);
            expect(definition.volumes).toContain(`/opt/argon/${DEPLOYMENT.secretsFile}:/argon.secrets.json:ro`);
            expect(definition.environment?.["ARGON_CONFIG_DIR"]).toBe("/conf.d");
            expect(definition.environment?.["ARGON_CONFIG_FILE"]).toBe("/argon.secrets.json");
            expect(definition.command).toEqual(["--role", role]);
        }
    });

    /** Read-only: an upgrade that rewrote its own configuration would be undiagnosable afterwards. */
    test("nothing a role is configured with is writable by it", () => {
        for (const mount of service(project(), roleService("core")).volumes ?? [])
            expect(mount.endsWith(":ro")).toBe(true);
    });

    /**
     * The private key exists in exactly one container.
     *
     * This is the substantive half of the move to Traefik. Nothing but the edge terminates TLS, so
     * nothing but the edge has a certificate to open — and a role that is compromised cannot hand out
     * the instance's identity. Putting a mount back on a role means either two terminations for one
     * connection or a private key in six containers, and `generate.ts` has to change with it.
     */
    test("no role is given a certificate, because no role terminates TLS", () => {
        const built = project({ roles: [...OPTIONAL_ROLES, "botapi"], voice: true });

        for (const role of built.roles)
            for (const mount of service(built, roleService(role)).volumes ?? [])
                expect(mount).not.toContain(DEPLOYMENT.tls.certificate);

        const edge = service(built, "argon-edge").volumes ?? [];

        expect(edge).toContain(`${TLS.certificatePath}:${DEPLOYMENT.tls.certificate}:ro`);
        expect(edge).toContain(`${TLS.keyPath}:${DEPLOYMENT.tls.key}:ro`);
    });

    test("roles wait for a healthy database and a healthy cache", () => {
        const definition = service(project(), roleService("core"));

        expect(definition.depends_on?.[DEPLOYMENT.hosts.postgres]?.condition).toBe("service_healthy");
        expect(definition.depends_on?.[DEPLOYMENT.hosts.redis]?.condition).toBe("service_healthy");
        expect(definition.depends_on?.[DEPLOYMENT.hosts.nats]).toBeDefined();
    });

    /**
     * A role that has died five times in a row is failing on its configuration. Stopping means the panel
     * can report it and `docker logs` ends on the failure rather than on the four-hundredth retry of it;
     * the infrastructure keeps `unless-stopped` because a database has to come back after a reboot.
     */
    test("a crash-looping role gives up, and the infrastructure does not", () => {
        const built = project();

        for (const role of built.roles) expect(service(built, roleService(role)).restart).toBe("on-failure:5");

        for (const name of [DEPLOYMENT.hosts.postgres, DEPLOYMENT.hosts.redis, DEPLOYMENT.hosts.nats])
            expect(service(built, name).restart).toBe("unless-stopped");
    });
});

describe("the infrastructure", () => {
    /**
     * The contract with `generate.ts`. Those hostnames are what the generated configuration dials, and
     * a service named anything else is an instance that starts and cannot talk to itself — a failure
     * that reads as a network fault rather than as a rename.
     */
    test("the service names are the hostnames the generated configuration points at", () => {
        const built = project({ voice: true });
        const names = Object.keys(read(built).services);

        for (const host of Object.values(DEPLOYMENT.hosts)) expect(names).toContain(host);
    });

    /**
     * Redis here is not only a cache: `Redis:OrleansStorage` is grain persistence and `Redis:Orleans` is
     * cluster membership. Losing the volume is losing grain state, so the append-only log is on — and
     * the five profiles use logical databases 0, 1, 2, 3 and 10, so the count is spelled out rather than
     * inherited from whatever the image ships.
     */
    test("the cache persists, because Orleans grain state lives in it", () => {
        const command = service(project(), DEPLOYMENT.hosts.redis).command ?? [];

        expect(command).toContain("--appendonly");
        expect(command[command.indexOf("--appendonly") + 1]).toBe("yes");
        expect(Number(command[command.indexOf("--databases") + 1])).toBeGreaterThan(10);
        expect(service(project(), DEPLOYMENT.hosts.redis).volumes).toContain("argon-cache-data:/data");
    });

    test("the bundled object store runs only when the operator did not bring their own", () => {
        const bundled = project();
        const theirs = project({ storage: { kind: "s3", endpoint: "https://s3.example.com", bucket: "theirs" } });

        expect(Object.keys(read(bundled).services)).toContain(DEPLOYMENT.hosts.storage);
        expect(Object.keys(read(theirs).services)).not.toContain(DEPLOYMENT.hosts.storage);
        expect(theirs.files.some((file) => file.path === STORAGE_IDENTITIES)).toBe(false);

        // Their bucket is theirs to make; ours is not, and SeaweedFS does not create one on first write.
        expect(Object.keys(read(bundled).services)).toContain("argon-storage-init");
        expect(service(bundled, roleService("media")).depends_on?.["argon-storage-init"]?.condition).toBe(
            "service_completed_successfully",
        );
    });

    /**
     * The store is never published; the front door forwards reads of the content bucket and nothing
     * else. That is what keeps the export bucket — whole-account GDPR archives — off the internet even
     * though the store itself answers an unsigned request.
     *
     * The route is narrow in three ways at once and each one matters: the bucket is named, the trailing
     * slash keeps `/s3/argon-exports/…` from matching it as a prefix, and the verbs are the two that
     * read. Widen any of them and the archives are public or the bucket is writable by anybody.
     */
    test("the object store is reachable exactly where the generated configuration says it is", () => {
        const built = project();
        const router = dynamic(built).http.routers["storage"];

        expect(service(built, DEPLOYMENT.hosts.storage).ports).toBeUndefined();
        expect(router?.rule).toContain(`PathPrefix(\`${DEPLOYMENT.storagePath}/${DEPLOYMENT.buckets.content}/\`)`);
        expect(router?.rule).toContain("Method(`GET`)");
        expect(router?.rule).toContain("Method(`HEAD`)");
        expect(router?.rule).not.toContain("POST");
        expect(backendOf(built, "storage")).toBe(
            `http://${DEPLOYMENT.hosts.storage}:${DEPLOYMENT.ports.storage}`,
        );

        // The published prefix comes off and the bucket stays on: the store serves `{bucket}/{key}`, and
        // `generate.ts` wrote that bucket into `Cdn.Default.PathPrefix` for the redirect to carry.
        const strip = dynamic(built).http.middlewares[router?.middlewares?.[0] ?? ""];

        expect(strip?.stripPrefix?.prefixes).toEqual([DEPLOYMENT.storagePath]);
        expect(fileNamed(built, EDGE_DYNAMIC_CONFIG).contents).not.toContain(DEPLOYMENT.buckets.exports);
    });

    /**
     * The bucket check depends on the buckets, not on the shell having run.
     *
     * `weed shell` prints `error: …` to stderr and exits 0, so a loop guarded on its exit status
     * reported success whether or not anything was created — and the first anybody heard of it was an
     * upload failing much later with `NoSuchBucket`. So both bucket names have to come back from
     * `s3.bucket.list` before this container is allowed to exit 0.
     */
    test("storage-init only succeeds once both buckets are listed", () => {
        const command = service(project(), "argon-storage-init").command?.[0] ?? "";

        expect(command).toContain("s3.bucket.list");

        for (const bucket of [DEPLOYMENT.buckets.content, DEPLOYMENT.buckets.exports])
            expect(command).toContain(`== "${bucket}"`);

        // Whole fields, not substrings: `argon` is a prefix of `argon-exports`, so a grep for the first
        // would be satisfied by the second and a half-made store would report success again.
        expect(command).not.toMatch(/grep/);

        // And it can fail. The old loop could not, which is how a wedged install looked like a healthy
        // one; a bounded one exits non-zero and `media` never starts, which is a verdict.
        expect(command).toContain("exit 1");
    });

    /**
     * Every `$` in that script is doubled, and this is the trap.
     *
     * Compose interpolates variables in the document before the container sees it, so a single `$i`
     * arrives at the shell as an empty string: the awk program then compares nothing to the bucket
     * names, never matches, and the check fails on a store that was fine. `$$` is compose's escape.
     */
    test("nothing in the storage-init script is eaten by compose interpolation", () => {
        const command = service(project(), "argon-storage-init").command?.[0] ?? "";

        expect(command).toMatch(/\$\$i/);
        expect(command.replace(/\$\$/g, "")).not.toContain("$");
    });

    test("every service is on the declared network and every volume it names is declared", () => {
        const document = read(project({ roles: [...OPTIONAL_ROLES], voice: true }));
        const declared = Object.keys(document.volumes);

        for (const definition of Object.values(document.services)) {
            expect(definition.networks).toEqual(["argon"]);

            for (const mount of definition.volumes ?? []) {
                const source = mount.split(":")[0] ?? "";

                // A bind mount is an absolute host path; anything else is a named volume and has to be
                // declared, or compose creates an anonymous one and the data is lost on the next `down`.
                if (!source.startsWith("/")) expect(declared).toContain(source);
            }
        }

        expect(document.name).toBe(COMPOSE_PROJECT);
        expect(Object.keys(document.networks)).toEqual(["argon"]);
    });
});

describe("secrets", () => {
    /**
     * The property: the compose file is a file people paste into issues.
     *
     * Everything that needs a credential gets it by `${…}` interpolation out of the `.env` beside it,
     * which is 0o600. A value that leaked in here would be in every bug report about a start-up failure.
     */
    test("no minted secret appears anywhere in the document", () => {
        const built = project({ roles: [...OPTIONAL_ROLES], voice: true });

        for (const value of everyValue(markedSecrets())) expect(built.document).not.toContain(value);
    });

    /** And the values are in fact somewhere, so the test above cannot pass by emitting nothing. */
    test("the credentials the infrastructure needs are in the .env, at 0600", () => {
        const built = project({ voice: true });
        const env = fileNamed(built, ENV_FILENAME);
        const secrets = markedSecrets();

        expect(env.mode).toBe(0o600);
        expect(env.contents).toContain(`${ENVIRONMENT_VARIABLES.databasePassword}=${secrets.databasePassword}`);
        expect(env.contents).toContain(
            `${ENVIRONMENT_VARIABLES.storageAccessKey}=${secrets.objectStorage.accessKey}`,
        );
        expect(env.contents).toContain(`${ENVIRONMENT_VARIABLES.sfuKeys}=${secrets.sfu.clientId}: ${secrets.sfu.secret}`);

        // Named in the document, so a variable that stops being written stops the project rather than
        // starting one service with an empty password.
        expect(built.document).toContain(`\${${ENVIRONMENT_VARIABLES.databasePassword}:?`);
        expect(built.document).toContain(`\${${ENVIRONMENT_VARIABLES.sfuKeys}:?`);
    });

    /** Everything with a secret in it is 0600; everything else is not, and that is one digit apart. */
    test("only the files with credentials in them are 0600", () => {
        const built = project({ voice: true });

        for (const file of built.files) {
            const carries = everyValue(markedSecrets()).some((value) => file.contents.includes(value));

            expect(file.mode).toBe(carries ? 0o600 : 0o644);
        }
    });

    /**
     * The bundled store's identities. Anonymous requests may read the content bucket and nothing else,
     * because a browser following a 302 to an avatar carries no credentials — and because the export
     * bucket must never be one guessed key away from public.
     */
    test("the bundled store lets an unsigned request read avatars and nothing more", () => {
        const identities = JSON.parse(fileNamed(project(), STORAGE_IDENTITIES).contents) as {
            identities: { name: string; actions: string[] }[];
        };

        const anonymous = identities.identities.find((identity) => identity.name === "anonymous");

        expect(anonymous?.actions).toEqual([`Read:${DEPLOYMENT.buckets.content}`]);
    });
});

describe("the edge", () => {
    /**
     * The whole reason for the move, in one assertion.
     *
     * Under Caddy the last hop was HTTPS to Kestrel with verification switched off — two terminations
     * for one connection, and a disabled check that needed a paragraph of justification. Traefik owns
     * TLS, so Kestrel is plain HTTP on a bridge network between two containers this file started, and
     * there is nothing to skip.
     */
    test("the hop to entrypoint is plain HTTP, with no certificate and nothing skipped", () => {
        const built = project();

        expect(backendOf(built, "api")).toBe(`http://argon-entrypoint:${DEPLOYMENT.ports.plaintext}`);

        const document = fileNamed(built, EDGE_DYNAMIC_CONFIG).contents;

        expect(document).not.toContain("insecureSkipVerify");
        expect(document).not.toContain(`https://argon-entrypoint`);
    });

    /** Everything unclaimed is the API's, and it must lose to every route that claims something. */
    test("the catch-all is the API, and every other router outranks it", () => {
        const routers = dynamic(project({ voice: true })).http.routers;
        const api = routers["api"];

        expect(api?.rule).toBe("Host(`chat.example.org`)");
        expect(api?.service).toBe("entrypoint");

        for (const [name, router] of Object.entries(routers)) {
            if (name === "api" || router.entryPoints[0] !== "public") continue;

            expect(router.priority ?? 0).toBeGreaterThan(api?.priority ?? 0);
        }
    });

    /**
     * Traefik is configured from files, never from container labels.
     *
     * The docker provider would need the socket inside the container that faces the internet, and §10
     * already calls that socket root-equivalent. There is no discovery to do here anyway — this module
     * generates every service and every route in one pass.
     */
    test("the edge holds no docker socket and reads its routing from a file", () => {
        const built = project();

        for (const mount of service(built, "argon-edge").volumes ?? [])
            expect(mount).not.toContain("docker.sock");

        for (const definition of Object.values(read(built).services))
            expect(JSON.stringify(definition)).not.toContain("traefik.http.routers");

        expect(statically(built).providers.file.filename).toBe("/etc/traefik/dynamic.yml");
    });

    /**
     * The API section is absent rather than disabled: its presence is what enables it, and
     * `api.insecure` on it is the well-trodden way an unauthenticated dashboard ends up on the
     * internet. There is nothing to turn off if it was never turned on.
     */
    test("the edge exposes no dashboard and no API of its own", () => {
        expect(statically(project()).api).toBeUndefined();
    });
});

describe("the traffic shapes", () => {
    /**
     * The tunnel is the one shape with no certificate anywhere — it makes the outbound connection and
     * carries the TLS itself. Nothing inbound is published, because that is the property the shape is
     * chosen for, and no router asks for TLS, because asking would be asking for a certificate that
     * does not exist.
     */
    test("a tunnelled instance mounts no certificate and publishes nothing to the world", () => {
        const built = composeProject(answers({ traffic: { kind: "cloudflare-tunnel" } }), markedSecrets(), {
            installRoot: "/opt/argon",
        });

        for (const port of built.published) expect(port.address).toBe("127.0.0.1");

        expect(dynamic(built).tls).toBeUndefined();
        expect(statically(built).certificatesResolvers).toBeUndefined();

        for (const router of Object.values(dynamic(built).http.routers)) expect(router.tls).toBeUndefined();

        for (const definition of Object.values(read(built).services))
            for (const mount of definition.volumes ?? []) expect(mount).not.toContain(DEPLOYMENT.tls.certificate);
    });

    /**
     * Path A and path B converge: a certificate and a key on disk, mounted into the edge and named in
     * the store. Both are in `certificates` so SNI can match them, and the instance's own is the
     * default so a request arriving with no SNI still gets one.
     */
    test("a shape with material on disk serves it from the file provider", () => {
        for (const traffic of [
            { kind: "own-certificate" } as const,
            { kind: "cloudflare-proxied" } as const,
        ]) {
            const built = project({ traffic });
            const tls = dynamic(built).tls;

            expect(tls?.stores?.default?.defaultCertificate).toEqual({
                certFile: DEPLOYMENT.tls.certificate,
                keyFile: DEPLOYMENT.tls.key,
            });

            expect(tls?.certificates).toContainEqual({
                certFile: DEPLOYMENT.tls.certificate,
                keyFile: DEPLOYMENT.tls.key,
            });

            // No ACME anywhere: an Origin CA certificate is signed by a root only Cloudflare trusts, and
            // a resolver alongside it would be a second thing trying to own the same name.
            expect(statically(built).certificatesResolvers).toBeUndefined();
        }
    });

    /** And a caller that forgot the material is a mistake, not an edge that quietly serves plaintext. */
    test("a shape that terminates TLS here refuses to build without the certificate", () => {
        expect(() =>
            composeProject(answers({ traffic: { kind: "own-certificate" } }), markedSecrets(), {
                installRoot: "/opt/argon",
            }),
        ).toThrow(/options\.tls/);
    });

    /**
     * Path C stops being the path with the most moving parts.
     *
     * §5 called it that because something had to renew every sixty days and something had to make
     * Kestrel notice a rotated file. Traefik obtains and renews it itself, over TLS-ALPN-01 on the port
     * that is already published — so there is nothing on disk, no second port, and no restart.
     */
    test("Let's Encrypt is Traefik's own resolver, on the port that is already open", () => {
        const built = composeProject(answers({ traffic: { kind: "lets-encrypt" } }), markedSecrets(), {
            installRoot: "/opt/argon",
        });

        const acme = statically(built).certificatesResolvers?.["letsencrypt"]?.acme;

        expect(acme?.["tlsChallenge"]).toEqual({});
        expect(acme?.["httpChallenge"]).toBeUndefined();
        expect(acme?.["storage"]).toBe("/data/acme.json");

        // On a volume: Let's Encrypt rate-limits duplicate certificates, so an instance that re-issues
        // on every `compose down -v` runs out of issuance and then answers with nothing at all.
        expect(service(built, "argon-edge").volumes).toContain("argon-edge-data:/data");

        expect(dynamic(built).http.routers["api"]?.tls?.certResolver).toBe("letsencrypt");
        expect(dynamic(built).tls).toBeUndefined();

        // 443 and nothing else. HTTP-01 would need :80 published, which is a second firewall rule whose
        // only purpose is a challenge.
        expect(built.published.filter((port) => port.address === "0.0.0.0").map((port) => port.hostPort)).toEqual([
            443, 8443,
        ]);
    });

    /**
     * And the two ways of getting a certificate are not offered at once.
     *
     * Under Kestrel the install script obtained the Let's Encrypt certificate and left it on disk. It
     * no longer does, so material arriving with this shape means the caller meant path A and said path
     * C — and accepting both would leave nobody able to say which one renews.
     */
    test("Let's Encrypt refuses a certificate the caller also supplied", () => {
        expect(() => project({ traffic: { kind: "lets-encrypt" } })).toThrow(/own-certificate/);
    });

    /**
     * An unhandled shape is a hard failure. A `TrafficShape` this file has never heard of must stop the
     * install rather than fall through to an edge with no TLS on it — which is invisible from outside,
     * because whatever sits in front still shows a padlock.
     */
    test("a traffic shape this file does not know about stops the build", () => {
        const rogue = { kind: "carrier-pigeon" } as unknown as Answers["traffic"];

        expect(() => project({ traffic: rogue })).toThrow(/in the clear/);
    });

    /**
     * Cloudflare's HTTP proxy carries WebSockets and not the UDP that real-time media is, so §5 gives
     * media a grey-clouded subdomain of its own. The front door has to answer on that name — and on that
     * name it is LiveKit, not the API — because that is what `CallKit:Sfu:PublicUrl` was pointed at.
     */
    test("a Cloudflare instance with a media subdomain routes that name to the SFU", () => {
        const built = project(
            { traffic: { kind: "cloudflare-proxied", voiceHost: "media.example.org" }, voice: true },
            { ...OPTIONS, voiceTls: VOICE_TLS },
        );

        expect(dynamic(built).http.routers["media"]?.rule).toBe("Host(`media.example.org`)");
        expect(backendOf(built, "media")).toBe(`http://${DEPLOYMENT.hosts.sfu}:${DEPLOYMENT.ports.sfu}`);

        // And the instance's own name does not, because there the media traffic never arrives.
        expect(Object.keys(dynamic(built).http.routers)).not.toContain("voice");

        // Two certificates at once, which is what §5 says such an instance runs. The second is mounted
        // at a path of its own — over the first, it would leave one name answering for both.
        expect(service(built, "argon-edge").volumes).toContain(
            `${VOICE_TLS.certificatePath}:/etc/argon/tls/voice.crt:ro`,
        );

        expect(dynamic(built).tls?.certificates).toHaveLength(2);
    });

    /**
     * That name needs a certificate of its own, and §5 says so: Cloudflare never sees it, so it cannot
     * be an Origin CA certificate. Without one Traefik answers with the default, which does not cover
     * the name — the handshake fails and the client reports what looks like its own bug. Caught here,
     * the installer can still tell the operator to produce it.
     */
    test("a media subdomain with no certificate for it refuses to build", () => {
        expect(() =>
            project({ traffic: { kind: "cloudflare-proxied", voiceHost: "media.example.org" }, voice: true }),
        ).toThrow(/voiceTls/);
    });

    /** Everywhere else media rides the instance's own name, which is what `PublicUrl` resolves to. */
    test("voice on the instance's own name is routed off the one public port", () => {
        const built = project({ voice: true });

        expect(dynamic(built).http.routers["voice"]?.rule).toContain("PathPrefix(`/rtc`)");
        expect(dynamic(built).http.routers["voice"]?.rule).toContain("PathPrefix(`/twirp/`)");
        expect(backendOf(built, "voice")).toBe(`http://${DEPLOYMENT.hosts.sfu}:${DEPLOYMENT.ports.sfu}`);
    });

    test("voice off routes nothing to an SFU that is not running", () => {
        expect(fileNamed(project({ voice: false }), EDGE_DYNAMIC_CONFIG).contents).not.toContain(
            DEPLOYMENT.hosts.sfu,
        );
    });
});

describe("the panel", () => {
    /**
     * One `:443` on a machine, and the edge has it. The panel is reached through the edge like
     * everything else — which is also true during setup, before any of this exists: see the bootstrap
     * phase, where the same two services are the whole project.
     */
    /**
     * The mount and the path the panel dials are one decision made in two files.
     *
     * `docker.ts` reads the daemon over this socket for every status the panel reports. If the two ever
     * disagree the panel starts, answers, and says nothing is running — an instance that looks dead to
     * the one thing an operator uses to check whether it is.
     */
    test("the panel is given the socket at the path it dials", () => {
        expect(service(project(), "argon-panel").volumes).toContain(`${DOCKER_SOCKET}:${DOCKER_SOCKET}`);
    });

    test("the panel publishes nothing and is reached through the edge", () => {
        const built = project();

        expect(service(built, "argon-panel").ports).toBeUndefined();
        expect(dynamic(built).http.routers["panel"]?.rule).toBe(
            `Host(\`chat.example.org\`) && PathPrefix(\`${PANEL_PATH}\`)`,
        );
        expect(backendOf(built, "panel")).toBe(`http://argon-panel:${DEPLOYMENT.ports.plaintext}`);
    });

    /**
     * A path on the instance's own name, so the instance keeps one certificate — the same reasoning
     * `DEPLOYMENT.storagePath` already follows. A `panel.` subdomain would need a DNS record the
     * operator can only create after setup has told them about it, and setup is served over the TLS
     * that record would need.
     */
    test("the panel is a path on the domain, not a name of its own", () => {
        const hosts = Object.values(dynamic(project()).http.routers).map((router) => router.rule);

        for (const rule of hosts) expect(rule).not.toContain("panel.chat.example.org");
    });

    /**
     * Two middlewares, in this order, and the order is the point.
     *
     * The redirect has to run against the path the browser asked for; once `stripPrefix` has taken
     * `/panel` off, there is nothing left to recognise. Reversed, the chain silently stops redirecting
     * and the failure below reappears.
     */
    test.each([
        ["bootstrap", () => bootstrapProject({ domain: "chat.example.org", traffic: { kind: "own-certificate" }, panelImage: "x", root: "/opt/argon", tls: TLS })],
        ["configured", () => project()],
    ])("the %s panel redirects to a trailing slash, then loses its prefix", (_name, build) => {
        const built = build();
        const chain = dynamic(built).http.routers["panel"]?.middlewares ?? [];
        const middlewares = dynamic(built).http.middlewares;

        expect(chain).toHaveLength(2);
        expect(middlewares[chain[0]!]?.redirectRegex).toBeDefined();
        expect(middlewares[chain[1]!]?.stripPrefix?.prefixes).toEqual([PANEL_PATH]);
    });

    /**
     * The failure this prevents is invisible until the instance comes up.
     *
     * The page's links are relative, which is the only form that survives being served from two base
     * paths — and relative URLs resolve against the last slash. From `/panel`, `api/state` resolves to
     * `/api/state`; from `/panel/`, to `/panel/api/state`. Only the second reaches the panel.
     *
     * During setup the first one works anyway, because the catch-all router is *also* the panel. It
     * keeps working right up until `/` becomes Argon — and then the panel breaks for anyone whose
     * bookmark has no trailing slash, without anything about the panel having changed.
     */
    test("the redirect matches the bare path and adds the slash", () => {
        const middlewares = dynamic(project()).http.middlewares;
        const redirect = Object.values(middlewares).find((m) => m.redirectRegex)?.redirectRegex;

        expect(redirect).toBeDefined();

        const pattern = new RegExp(redirect!.regex);

        expect(pattern.test(`https://chat.example.org${PANEL_PATH}`)).toBe(true);

        // Already correct, and anything below it — redirecting those would be a loop.
        expect(pattern.test(`https://chat.example.org${PANEL_PATH}/`)).toBe(false);
        expect(pattern.test(`https://chat.example.org${PANEL_PATH}/api/state`)).toBe(false);

        expect(redirect!.replacement).toContain(`${PANEL_PATH}/`);
        expect(redirect!.permanent).toBe(false);
    });

    /**
     * The listener the router dials and the one the process binds are one decision, so the port is
     * written out rather than inherited from the panel's own default. No TLS variables: Traefik
     * terminated it, so this is plain HTTP on the compose network like everything else.
     */
    test("the panel is told what to listen on, and told nothing about TLS", () => {
        const environment = service(project(), "argon-panel").environment ?? {};

        expect(environment["ARGON_BOOTSTRAP_PORT"]).toBe(String(DEPLOYMENT.ports.plaintext));
        expect(environment["ARGON_BOOTSTRAP_CONFIG_DIR"]).toBe("/argon");
        expect(environment["ARGON_BOOTSTRAP_CODE_FILE"]).toBe(`/argon/${BOOTSTRAP_CODE_FILE}`);
        expect(environment["ARGON_BOOTSTRAP_TLS_CERT"]).toBeUndefined();
        expect(environment["ARGON_BOOTSTRAP_TLS_KEY"]).toBeUndefined();
    });

    /**
     * §10's control surface needs the docker socket for §9's lifecycle, and the install root writable
     * because §8 makes the secrets file its. That the socket is root-equivalent is exactly why this
     * service publishes nothing and why §10 insists its authentication is a real account.
     */
    test("the panel can act on the project it manages", () => {
        const volumes = service(project(), "argon-panel").volumes ?? [];

        expect(volumes).toContain("/var/run/docker.sock:/var/run/docker.sock");
        expect(volumes).toContain("/opt/argon:/argon");

        // Not read-only, unlike every role's mount, and that asymmetry is the point.
        for (const mount of volumes) expect(mount.endsWith(":ro")).toBe(false);
    });

    /**
     * A panel that waits for a healthy database is unavailable in exactly the situation an operator
     * opens it for, and it is the thing that comes back after a reboot and starts everything else.
     */
    test("the panel starts regardless of the instance, and comes back by itself", () => {
        const definition = service(project(), "argon-panel");

        expect(definition.depends_on).toBeUndefined();
        expect(definition.restart).toBe("unless-stopped");
    });

    /**
     * Pinned to the server's version: a self-hosted release is one thing an operator upgrades, and a
     * panel a version ahead of the server it manages is a combination nobody tested. A server pinned to
     * a full reference has no version to borrow, and that refuses rather than guessing.
     */
    test("the panel runs the bootstrapper image at the server's version", () => {
        expect(service(project(), "argon-panel").image).toBe(`${BOOTSTRAPPER_IMAGE_REPOSITORY}:0.4.2`);
    });

    /**
     * The two images carry the same tag, whatever the tag is.
     *
     * This is the whole reason CI builds both in one workflow off one run number — see
     * `.github/workflows/publish.yml`. The panel's reference is derived from the server's version, so a
     * server tag with no bootstrapper of the same tag beside it produces a compose file naming an image
     * that does not exist, and the operator finds out during `compose up`, after answering everything.
     *
     * The list is deliberately unlike a version number in places. CI tags with a build count, an
     * operator may pin `1.4.0`, and a hyphenated or moving tag is what a rule that split on a
     * separator would quietly diverge on.
     */
    test.each([["42"], ["dev-42"], ["1.4.0"], ["latest"]])("server and panel agree on the tag '%s'", (version) => {
        const built = project({ serverVersion: version, roles: [...REQUIRED_ROLES] });

        const serverTag = service(built, "argon-entrypoint").image?.split(":")[1];
        const panelTag = service(built, "argon-panel").image?.split(":")[1];

        expect([version, panelTag]).toEqual([version, serverTag]);
        expect(panelTag).toBe(version);

        expect(() => project({ serverVersion: "ghcr.io/argon-chat/orleans@sha256:abc" })).toThrow(
            /panelImage/,
        );

        expect(
            service(
                project({ serverVersion: "ghcr.io/argon-chat/orleans@sha256:abc" }, {
                    ...OPTIONS,
                    panelImage: "ghcr.io/argon-chat/bootstrapper@sha256:def",
                }),
                "argon-panel",
            ).image,
        ).toBe("ghcr.io/argon-chat/bootstrapper@sha256:def");
    });
});

/* ------------------------------------------------------------------------------------------------
 * The bootstrap phase.
 * ---------------------------------------------------------------------------------------------- */

describe("the front door, before there is anything behind it", () => {
    const PANEL_IMAGE = `${BOOTSTRAPPER_IMAGE_REPOSITORY}:0.4.2`;

    function phase(overrides: Partial<Parameters<typeof bootstrapProject>[0]> = {}): ComposeProject {
        return bootstrapProject({
            domain: "chat.example.org",
            traffic: { kind: "own-certificate" },
            panelImage: PANEL_IMAGE,
            root: "/opt/argon",
            tls: TLS,
            ...overrides,
        });
    }

    test("nothing runs but the door and the panel", () => {
        expect(Object.keys(read(phase()).services).sort()).toEqual(["argon-edge", "argon-panel"]);
    });

    /**
     * Compose rejects a document whose `depends_on` names a service the file does not define — the
     * whole project fails to start, not just the edge.
     *
     * The edge waits on `entrypoint` in a configured instance, and `entrypoint` is exactly what does
     * not exist yet here. Asserted over both builders and over every service, so the next dependency
     * added to either one is covered without anybody remembering to come back.
     */
    test.each([
        ["bootstrap", () => phase()],
        ["configured", () => project({ roles: [...REQUIRED_ROLES] })],
    ])("every dependency in the %s project is a service it defines", (_name, build) => {
        const document = read(build());
        const defined = Object.keys(document.services);

        for (const [name, definition] of Object.entries(document.services))
            for (const dependency of Object.keys(definition.depends_on ?? {}))
                expect([name, dependency, defined.includes(dependency)]).toEqual([name, dependency, true]);
    });

    /**
     * The two documents describe the same project, which is what makes the handover a no-op.
     *
     * `apply()` writes the configured document over this one and brings the project up again. Same
     * name, same network, same subnet, same volume: compose adds the roles and updates the edge in
     * place. Any of those differing and it would instead build a second project beside the first —
     * with the old edge still holding `:443`, so the new one could never bind it.
     */
    test("the configured project is the same project, so it replaces rather than duplicates", () => {
        const before = read(phase());
        const after = read(project({ roles: [...REQUIRED_ROLES] }));

        expect(before.name).toBe(after.name);
        expect(before.name).toBe(COMPOSE_PROJECT);
        expect(before.networks).toEqual(after.networks);
        expect(Object.keys(before.volumes)).toContain("argon-edge-data");
    });

    /**
     * The link printed during the install is the link that works afterwards.
     *
     * `/panel` is where the panel lives for the rest of the instance's life, so an operator who
     * bookmarks the setup page does not find it dead the next day. The bare domain is served too,
     * because during setup there is nothing else it could mean.
     */
    test("the panel answers on its permanent path and on the bare domain", () => {
        const built = phase();

        expect(backendOf(built, "panel")).toBe(`http://argon-panel:${DEPLOYMENT.ports.plaintext}`);
        expect(backendOf(built, "setup")).toBe(`http://argon-panel:${DEPLOYMENT.ports.plaintext}`);

        expect(dynamic(built).http.routers["panel"]?.middlewares).toContain("panel-prefix");
        expect(dynamic(built).http.middlewares["panel-prefix"]?.stripPrefix?.prefixes).toEqual([PANEL_PATH]);
    });

    test("the panel publishes no port of its own", () => {
        expect(service(phase(), "argon-panel").ports).toBeUndefined();
        expect(service(phase(), "argon-panel").image).toBe(PANEL_IMAGE);
    });

    /**
     * On the ACME path the certificate has to be obtained before the operator can reach anything.
     *
     * The resolver in the static file is what registers the account; the `certResolver` on the routers
     * is what actually asks for the name. Without the second one Traefik starts, serves its own default
     * self-signed certificate, and the browser refuses the page — with nothing in the logs about ACME,
     * because nothing ever asked it for anything.
     */
    /**
     * Where ACME keeps its account has to be somewhere the edge can actually write.
     *
     * Observed, not reasoned about: started with `/data` unmounted, Traefik 3.3.7 logs
     * `The ACME resolve is skipped from the resolvers list … open /data/acme.json: no such file or
     * directory`, drops the resolver entirely, and then serves its own self-signed certificate for
     * every router that named it. The instance comes up, answers, and every browser refuses it — with
     * the cause four lines up in a log nobody is reading, among a dozen INF lines.
     *
     * So the storage path and the mount are one decision, and this is the only place both are visible.
     */
    test("the ACME account is stored inside a volume the edge has mounted", () => {
        const built = phase({ traffic: { kind: "lets-encrypt" }, tls: undefined });
        const resolvers = statically(built).certificatesResolvers ?? {};
        const storage = resolvers[Object.keys(resolvers)[0]!]?.acme["storage"];

        expect(typeof storage).toBe("string");

        // The container-side half of every `source:target[:mode]` the edge is given.
        const mounted = (service(built, "argon-edge").volumes ?? []).map((volume) => volume.split(":")[1]);

        expect(mounted.some((target) => target !== undefined && (storage as string).startsWith(`${target}/`))).toBe(true);
    });

    test("lets-encrypt obtains the certificate for the domain being set up", () => {
        const built = phase({ traffic: { kind: "lets-encrypt" }, tls: undefined, acmeEmail: "ops@example.org" });
        const resolvers = statically(built).certificatesResolvers ?? {};
        const resolver = Object.keys(resolvers)[0];

        expect(resolver).toBeDefined();
        expect(resolvers[resolver!]?.acme["email"]).toBe("ops@example.org");

        for (const name of ["panel", "setup"])
            expect([name, dynamic(built).http.routers[name]?.tls?.certResolver]).toEqual([name, resolver]);
    });

    test("an ACME account without a contact address registers without one, rather than with an empty one", () => {
        const built = phase({ traffic: { kind: "lets-encrypt" }, tls: undefined });
        const resolvers = statically(built).certificatesResolvers ?? {};
        const resolver = Object.keys(resolvers)[0]!;

        expect(Object.keys(resolvers[resolver]!.acme)).not.toContain("email");
    });

    test("an operator's own certificate is mounted and served", () => {
        const built = phase();

        expect(service(built, "argon-edge").volumes).toContain(`${TLS.certificatePath}:${DEPLOYMENT.tls.certificate}:ro`);
        expect(dynamic(built).tls?.stores?.default?.defaultCertificate?.certFile).toBe(DEPLOYMENT.tls.certificate);
        expect(statically(built).certificatesResolvers).toBeUndefined();
    });

    /**
     * The tunnel shape has no inbound port at all, and that has to hold during setup too.
     *
     * Binding `0.0.0.0` here would put the setup page — the one holding the bootstrap code — on the
     * public internet of a machine whose whole point is that nothing reaches it directly.
     */
    test("the tunnel shape stays on the loopback while setup runs", () => {
        const built = phase({ traffic: { kind: "cloudflare-tunnel" }, tls: undefined });

        for (const port of service(built, "argon-edge").ports ?? []) expect(port.startsWith("127.0.0.1:")).toBe(true);

        expect(dynamic(built).http.routers["setup"]?.tls).toBeUndefined();
    });

    test("a certificate is required by the shapes that terminate here", () => {
        expect(() => phase({ tls: undefined })).toThrow(/must name the certificate/);
        expect(() => phase({ traffic: { kind: "cloudflare-proxied" }, tls: undefined })).toThrow(/must name the certificate/);
    });

    test("a domain that is not a hostname stops the install", () => {
        expect(() => phase({ domain: "https://chat.example.org" })).toThrow();
    });
});
