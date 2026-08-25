import type { Answers, GeneratedFile, RoleSummary, TrafficShape } from "./model";
import { DEPLOYMENT, type MintedSecrets } from "./generate";

/**
 * The compose project a self-hosted install actually runs.
 *
 * Pure, and the same shape as `generate.ts` for the same reason: it returns the document and the files
 * beside it rather than writing them, so the whole of it can be exercised without a docker daemon and
 * so the caller decides when — and whether — anything lands on disk.
 *
 * **Why the document is JSON.** Compose reads YAML, and YAML 1.2 is a superset of JSON, so a JSON
 * document is a valid compose file. Writing YAML instead would mean writing a YAML *emitter* — quoting,
 * escaping, when a string needs quotes and when it does not — and the only thing that would ever check
 * it is this module's own tests, which cannot parse YAML back because Bun ships no parser and this
 * project takes no dependencies. That is the trade: a hand-rolled emitter nobody verifies, or JSON that
 * `JSON.parse` reads and that the tests can therefore assert *structure* on. The properties this file
 * has to hold — no silo publishes a port, every role mounts its configuration, no secret is in here —
 * are all structural. A test reaching for them through substrings is a test that passes on a document
 * that happens to contain the right words in the wrong places.
 *
 * The cost is real and worth stating: an operator opening `compose.yaml` sees JSON, which is not what
 * they expect. It is still legal YAML, `docker compose` reads it unchanged, and the panel owns editing
 * it. If someone later adds a YAML emitter that a real parser checks, the tests below are what tells
 * them whether it still says the same thing — and it will have to remember that `restart: no` is the
 * boolean `false` in YAML and the string `"no"` in JSON, which is one of the traps this side-steps.
 *
 * **Keys are in insertion order, not sorted.** The opposite of `generate.ts`, deliberately: a `conf.d`
 * file is diffed, so sorting is what makes two runs comparable; a compose file is read top to bottom by
 * a person, and sorting it would put `volumes` above `image`. Determinism comes from construction here
 * — nothing below iterates a set or a map built from unordered input.
 *
 * ## The front door, and why there is one
 *
 * §5 of the design says Kestrel terminates TLS and there is no reverse proxy. That holds for *one*
 * public surface. This project has four that must answer on the operator's single domain:
 *
 *  - the API and hub, which is `entrypoint`;
 *  - the bundled object store, which `generate.ts` publishes at `DEPLOYMENT.storagePath` on that same
 *    domain — because Argon never serves file bytes, both of its file routes 302 to an origin, and an
 *    origin that cannot be reached by a browser is every avatar in the instance 404ing;
 *  - LiveKit, whose `PublicUrl` for every traffic shape except Cloudflare-with-a-media-subdomain is
 *    `wss://{domain}` — the same name, the same port 443;
 *  - the panel, which §10 says is this same bootstrapper image turned into a control surface, and which
 *    served the setup UI on that domain minutes earlier.
 *
 * One machine has one `:443`. No arrangement of published ports resolves four claims on it; path
 * routing does, and nothing in Argon does path routing (`RewriteMiddleware` filters paths per host, it
 * does not forward). So this file emits a front door whose entire configuration it generates — §5's
 * "no second product to configure" survives, because the operator configures nothing. What does not
 * survive is the claim that no proxy exists, and that is reported rather than buried.
 *
 * ## Traefik, and what that decided
 *
 * The front door is Traefik. It is the *only* thing on this machine that terminates TLS, and that is
 * the load-bearing consequence rather than a preference between proxies:
 *
 *  - Kestrel serves **plain HTTP** on the compose network in every traffic shape. `generate.ts` has to
 *    agree — see the report — and the payoff is that the internal hop needs no certificate, no
 *    `tls_insecure_skip_verify`, and no paragraph justifying a disabled verification. Nothing but the
 *    edge container ever holds the private key.
 *  - Because Kestrel is plaintext, no role may publish a port of its own that a browser would reach:
 *    that would be a sign-in page or a bot API in the clear. The two that used to — `aegis` and
 *    `botapi` — are now Traefik entry points on the same certificate. See {@link edgeService}.
 *  - Argon's `ForwardedHeaders:KnownNetworks` stops being a nicety and becomes required: with the TLS
 *    hop terminated in front of it, ASP.NET only knows the request was HTTPS if it believes
 *    `X-Forwarded-Proto`, and it only believes it from a network it was told about. See
 *    {@link NETWORK_SUBNET}.
 *
 * **Configuration comes from files, not from container labels.** Traefik's docker provider would let
 * each service carry its own routing, which reads well — and it requires the docker socket inside the
 * container that faces the internet. §10 already calls that socket root-equivalent. Mounting it into
 * the *edge* would make one compromise of the most exposed process on the box a compromise of the box,
 * and buy nothing here: this module generates every service and every route in the same pass, so there
 * is no dynamic discovery to do. The file provider needs no socket. The cost is that a route added by
 * hand to `compose.yaml` does nothing until it is also added to the dynamic file — which is the
 * property wanted, because the panel owns both.
 *
 * Every port that faces the world for HTTP belongs to the edge, and there are at most three of them:
 * the instance's own `:443`, and — because `aegis` and `botapi` have no public name of their own — one
 * apiece for those, on the same certificate. No role publishes anything a browser reaches, and no silo
 * publishes anything at all, which is the property §6 cares about. The remaining two are LiveKit's
 * media ports, which cannot go through an HTTP proxy because they are not HTTP.
 *
 * Every one of them carries its own sentence: see {@link publishedFor}, {@link edgeService} and the
 * {@link PublishedPort} list handed back to the caller, which is what the installer prints to an
 * operator about to open a firewall.
 */

/* ------------------------------------------------------------------------------------------------
 * Names, images, and the numbers that leave this machine.
 * ---------------------------------------------------------------------------------------------- */

/**
 * The compose project name, fixed rather than derived from the directory.
 *
 * The panel addresses this project by name for every lifecycle operation in §9, and a project named
 * after whatever directory the install happened to land in is a project the panel cannot find after
 * somebody moves it.
 */
export const COMPOSE_PROJECT = "argon";

/** Written into the install root. `.yaml` because that is what `docker compose` finds unprompted. */
export const COMPOSE_FILENAME = "compose.yaml";

/**
 * Where compose reads interpolation values from — the project directory, by its own convention.
 *
 * This file exists because of the one rule this module will not bend: no secret goes in the compose
 * document. Postgres needs a password, the bundled store needs its keys and LiveKit needs its API key,
 * and all three arrive by `${VARIABLE}` interpolation out of here. It is mode 0o600 and it is a second
 * file with secrets in it — which is a tension with §7's "one secrets file", named here rather than
 * pretended away: §7's file is *Argon's* configuration layer, and Postgres does not read Argon's
 * configuration. See the report.
 */
export const ENV_FILENAME = ".env";

/**
 * Sidecar configuration, relative to the install root, written by the caller.
 *
 * Traefik's two halves are two files on purpose, and they must not be collapsed into one directory the
 * file provider watches. The static file configures the process — entry points, providers, the ACME
 * resolver — and is read once at start; the dynamic file configures routing and certificates. Pointing
 * `providers.file.directory` at a directory holding both makes Traefik try to read the static file as
 * routing and fail at boot with a message about an unknown field, which is a confusing way to discover
 * a layout decision.
 */
export const EDGE_STATIC_CONFIG = "traefik/traefik.yml";
export const EDGE_DYNAMIC_CONFIG = "traefik/dynamic.yml";
export const SFU_CONFIG = "sfu/livekit.yaml";
export const STORAGE_IDENTITIES = "storage/identities.json";

/**
 * Where the panel answers, on the instance's own domain.
 *
 * A path and not a subdomain, for the reason `DEPLOYMENT.storagePath` is a path: the instance keeps one
 * name and one certificate. A `panel.` subdomain would need a second DNS record, and then — on the
 * Cloudflare path — either a second Origin CA certificate or a wildcard, and on the Let's Encrypt path
 * a second ACME validation. Worse, the operator would have to create that record *before* setup, which
 * they cannot: setup is the thing that would have told them about it. §1's whole ordering is that the
 * panel is served over the TLS being configured, and a name that does not exist yet cannot be.
 *
 * The alternative that was actually on the table is a published port of its own, and that is the
 * collision this settles: the bootstrapper serves the setup UI on `:443`, the edge publishes `:443`,
 * and `docker compose up` would fail to bind *after* every other service had started — an instance
 * whose only public door never opened. The ingress routes; the panel goes behind it.
 */
export const PANEL_PATH = "/panel";

/**
 * The bootstrap code file, relative to the install root, as §4 describes it.
 *
 * A contract with an install script that does not exist yet, so it is named here rather than assumed:
 * the script writes the code to this path at mode 0600, and the panel container is handed the same path
 * through `ARGON_BOOTSTRAP_CODE_FILE`. A disagreement is a panel that refuses to start naming a file
 * nobody wrote. See the report.
 */
export const BOOTSTRAP_CODE_FILE = "bootstrap.code";

/** The published server image. `--role` picks what a container is; the image is the same for all of them. */
export const SERVER_IMAGE_REPOSITORY = "ghcr.io/argon-chat/orleans";

/**
 * The bootstrapper's own image — this process, run again as the panel.
 *
 * Tagged with the *server's* version by default, and that is a decision rather than laziness: a
 * self-hosted release is one thing an operator upgrades, §9 makes upgrading the risky operation, and a
 * panel a version ahead of the server it manages is a combination nobody tested. When the operator
 * pinned a full reference instead of picking a version there is no tag to derive, and
 * {@link ComposeOptions.panelImage} has to say — this refuses rather than guessing.
 */
export const BOOTSTRAPPER_IMAGE_REPOSITORY = "ghcr.io/argon-chat/bootstrapper";

/**
 * Every image this project runs that is not Argon's own, pinned.
 *
 * Pinned because an installer that writes `latest` produces two different instances from the same
 * answers a month apart, and the second one is the one that breaks. **These tags are the one thing in
 * this file that cannot be checked without a network**, so they are all in one place, overridable
 * through {@link ComposeOptions.images}, and they are what a release check should verify against the
 * registries. A tag that does not exist fails at `docker compose pull`, loudly and early, which is the
 * best failure mode available for a guess.
 */
export const INFRASTRUCTURE_IMAGES = {
    database: "postgres:17-alpine",
    /** Redis-compatible. The hosted deployment runs Dragonfly; both speak the protocol Argon uses. */
    cache: "valkey/valkey:8-alpine",
    bus: "nats:2.10-alpine",
    storage: "chrislusf/seaweedfs:3.80",
    sfu: "livekit/livekit-server:v1.8.4",
    /** Traefik v3. The major matters: v2's rule syntax and v3's differ, and the rules below are v3's. */
    edge: "traefik:v3.3",
} as const;

/**
 * Service names, which are also the DNS names on the compose network.
 *
 * The five that `generate.ts` already fixed come from {@link DEPLOYMENT} rather than being restated —
 * a second copy of a hostname is how a rename produces an instance that starts and cannot talk to
 * itself, and the failure reads as a network fault rather than as a typo.
 */
const SERVICES = {
    postgres: DEPLOYMENT.hosts.postgres,
    cache: DEPLOYMENT.hosts.redis,
    bus: DEPLOYMENT.hosts.nats,
    storage: DEPLOYMENT.hosts.storage,
    sfu: DEPLOYMENT.hosts.sfu,

    /** Not in {@link DEPLOYMENT}: nothing Argon reads dials these, so nothing there had to name them. */
    storageInit: "argon-storage-init",
    edge: "argon-edge",
    panel: "argon-panel",
} as const;

/**
 * The panel's compose service.
 *
 * Exported because `setup.ts` has to *exclude* it: the apply runs `compose up` from inside this
 * container, and a `up` that names the panel would recreate the container issuing the command — killing
 * the process half-way through starting everything else. See the apply's own note.
 */
export const PANEL_SERVICE: string = SERVICES.panel;

/** A role's service name. `argon-core`, `argon-entrypoint`, and so on. */
function serviceFor(role: string): string {
    return `argon-${role}`;
}

/**
 * Ports that leave this machine.
 *
 * Every number here is a decision, so every number here has a sentence. The first four belong to the
 * edge and the last two to LiveKit; the only other thing in the project that publishes at all is a
 * console, on the loopback. Roles reach each other over the compose network, and Orleans membership is
 * in Redis, so a silo's gateway is discovered by address on that network and never needs a host port.
 */
const PUBLIC = {
    /** The one public door. 443 externally; internally the front door binds the same high port Kestrel
     *  would have, because binding a privileged port needs a capability the image may not be given. */
    https: 443,

    /** The tunnel shape terminates TLS at Cloudflare's end, so this is plaintext and stays on loopback. */
    tunnel: 8080,

    /** The identity server, reached through the edge and not from its own container. See {@link aegisNote}. */
    aegis: 8443,

    /** The bot API, which bots reach from the internet and which has no other name to be reached at. */
    botapi: 8444,

    /** LiveKit's ICE/TCP fallback, for clients whose network blocks UDP outright. */
    sfuTcp: 7881,

    /** LiveKit's UDP mux. One port, not a range — see {@link livekitConfiguration}. */
    sfuUdp: 7882,
} as const;

/**
 * Traefik's entry points: the name in the dynamic file, and the port the edge container binds.
 *
 * Three, because three host ports arrive and each has to be told apart by *something*. They could have
 * been told apart by hostname instead, and that is the arrangement this does not have: `aegis` needs a
 * public name of its own and there is only one name here (see {@link aegisNote}), so the port is the
 * discriminator. Every one of them is TLS, terminated here, with the same certificate — which is the
 * whole reason `aegis` and `botapi` stopped publishing their own listeners when Kestrel went plaintext.
 *
 * The container-side numbers are high on purpose: binding a privileged port needs a capability the
 * image may not be given, so the host mapping does the translation. Changing one of these without
 * changing the `address` written into the static configuration produces an edge that starts, listens on
 * nothing anybody reaches, and looks healthy.
 */
const ENTRY_POINTS = {
    /** The product's own surface. Its container port follows the traffic shape — see {@link edgeService}. */
    public: { name: "public", hostPort: PUBLIC.https },
    identity: { name: "identity", hostPort: PUBLIC.aegis, containerPort: 9443 },
    bots: { name: "bots", hostPort: PUBLIC.botapi, containerPort: 9444 },
} as const;

/** The consoles' own listeners, which the server image `EXPOSE`s and which carry no TLS of their own. */
const CONSOLE_PORTS: Readonly<Record<string, number>> = { admin: 8920, account: 8930 };

/**
 * Which router wins when two match.
 *
 * Traefik's default is to rank by rule length, which would make the order below an accident of how many
 * characters a hostname happens to have. Every router that is *not* the catch-all has to beat the
 * catch-all, and the numbers are spelled out so that adding a fifth route is a decision rather than a
 * surprise. The specific routers do not overlap each other — `/s3/argon/`, `/rtc`, `/twirp/` and
 * `/panel` are disjoint — so only the distance to {@link ROUTER_PRIORITY.api} carries meaning.
 */
const ROUTER_PRIORITY = { media: 400, storage: 300, voice: 200, panel: 100, api: 1 } as const;

/**
 * The compose network's subnet, pinned.
 *
 * Pinned because everything arrives at `entrypoint` through the front door, so
 * `ForwardedHeaders:KnownNetworks` has to name a CIDR for ASP.NET to believe `X-Forwarded-For` — and a
 * CIDR docker picked at random is not a CIDR anything can be configured to trust. The shipped default
 * is `10.42.0.0/16` and `10.43.0.0/16`, which are Kubernetes pod ranges and match nothing here.
 *
 * With Traefik terminating TLS this stopped being about client addresses in a log. Kestrel now serves
 * plain HTTP, so `X-Forwarded-Proto` is the *only* thing that tells ASP.NET the request was HTTPS, and
 * an unbelieved header means secure cookies, redirect URIs and `RequireHttpsMetadata` all decide as if
 * the instance were served in the clear. Nothing writes that setting yet — see the report; this
 * constant is exported so that whatever does can use it instead of inventing a second copy. The
 * alternative, trusting all of `172.16.0.0/12`, trusts every container on the box.
 *
 * The cost is a collision on a machine that already has many docker networks, which fails at
 * `compose up` with a clear message rather than silently.
 */
export const NETWORK_SUBNET = "172.29.0.0/16";

/* ------------------------------------------------------------------------------------------------
 * Which roles run.
 * ---------------------------------------------------------------------------------------------- */

/**
 * The roles a self-hosted instance always runs, in start-to-finish reading order.
 *
 * Not the `dev` role. That is every role in one process, it exists in no other deployment, and so it
 * breaks in ways production never sees and gets fixed last — §6 is explicit about it and
 * {@link REFUSED_ROLES} keeps it out even if an answer asks for it.
 */
export const REQUIRED_ROLES = ["entrypoint", "aegis", "core", "media", "jobs"] as const;

/** Roles the operator opts into. `voice` is driven by {@link Answers.voice}, not by the role list. */
export const OPTIONAL_ROLES = ["voice", "botapi", "admin", "account"] as const;

/**
 * Roles a self-hosted instance does not run, whatever it was asked for.
 *
 * `commerce` is entitlements and payments, which have no meaning on one operator's box. `moderation`
 * is ONNX image moderation and is memory-bound. `dev` is the single-process role above. Dropped
 * silently rather than refused because the wizard should not have offered them; a test pins that they
 * stay out, so a wizard that starts offering them fails here rather than shipping.
 */
export const REFUSED_ROLES = ["commerce", "moderation", "dev"] as const;

/**
 * Whether a role is a client or a silo, as the server declares it.
 *
 * A copy of `IArgonRole.IsClient`, which is exactly the kind of second copy this project avoids — so
 * {@link ComposeOptions.knownRoles} takes what `--roles` reported and wins over this table whenever the
 * caller has it. This exists for the callers that do not, and for the tests.
 *
 * What it decides is publishing, and only publishing. If it drifts, the damage is bounded in one
 * direction and not the other: calling a client a silo publishes nothing and makes a console
 * unreachable, calling a silo a client puts an Orleans gateway on the internet. So the code below asks
 * "is this a client" and never "is this a silo", and a role this table has never heard of is a silo.
 */
const CLIENT_ROLES: ReadonlySet<string> = new Set(["entrypoint", "aegis", "botapi", "admin", "account"]);

/**
 * The roles this project will run, given what the operator answered.
 *
 * Exported because the caller has to hand the *same* list to `generate()`. A role compose runs that
 * `--explain` was never asked about is a role whose features declared no sections, so nothing was
 * written for it: it starts, reads the image's defaults, and points at a CockroachDB that is not there.
 * The required roles are unioned in rather than trusted from `answers.roles` for the same reason a
 * wizard is allowed to store only what it asked about.
 */
export function rolesFor(answers: Answers): string[] {
    const chosen = new Set(answers.roles);
    const roles: string[] = [...REQUIRED_ROLES];

    for (const role of OPTIONAL_ROLES) {
        // `voice` is not an answer in the role list — it is its own question, and it is the same flag
        // `generate.ts` gates `CallKit` on. Reading it from two places is how an instance ends up with
        // a voice role and no SFU configuration, or the reverse.
        const wanted = role === "voice" ? answers.voice : chosen.has(role);

        if (wanted) roles.push(role);
    }

    return roles.filter((role) => !REFUSED_ROLES.includes(role as (typeof REFUSED_ROLES)[number]));
}

/** The image reference for a role container. */
export function serverImageFor(version: string): string {
    const trimmed = version.trim();

    // Already a reference — a registry host, a repository path, or a tag on one. The wizard may offer a
    // version list and it may equally be handed `ghcr.io/argon-chat/orleans@sha256:…` by an operator
    // pinning a digest, and turning that into `…/orleans:ghcr.io/…` would be a confusing pull error.
    if (trimmed.includes("/") || trimmed.includes("@")) return trimmed;

    return `${SERVER_IMAGE_REPOSITORY}:${trimmed}`;
}

/* ------------------------------------------------------------------------------------------------
 * What the caller gets back.
 * ---------------------------------------------------------------------------------------------- */

export interface ComposeOptions {
    /**
     * The install root **as the host sees it**.
     *
     * Not `ARGON_BOOTSTRAP_CONFIG_DIR`, which is where that directory is mounted inside *this*
     * container. Every bind mount below is resolved by the docker daemon on the host, so a container
     * path here produces mounts of directories that do not exist and roles that start with no
     * configuration at all. The install script knows the host path and does not currently pass it —
     * see the report.
     */
    readonly installRoot?: string;

    /**
     * Host paths of the certificate and key the install script established.
     *
     * Required by the two shapes that bring their own material — `own-certificate` and
     * `cloudflare-proxied`. Refused by `lets-encrypt`, which is Traefik's ACME resolver and has nothing
     * on disk to be given, and unused by `cloudflare-tunnel`, which serves plain HTTP behind a tunnel
     * that carries the TLS. See {@link tlsPlanFor}.
     */
    readonly tls?: TlsMaterial;

    /**
     * A second certificate, for the media subdomain a Cloudflare-proxied instance publishes directly.
     *
     * §5 says such an instance runs two TLS paths at once, with different expiry dates, because
     * Cloudflare never sees the media name and cannot issue an Origin CA certificate for it. Required
     * whenever voice is on and the traffic shape named a media host: absent, Traefik would answer that
     * name with the default certificate, which does not cover it, and voice would fail its handshake
     * for a reason that reads as a client bug.
     */
    readonly voiceTls?: TlsMaterial;

    /** What `--roles` reported. Authoritative over {@link CLIENT_ROLES} when present. */
    readonly knownRoles?: readonly RoleSummary[];

    readonly images?: Partial<Record<keyof typeof INFRASTRUCTURE_IMAGES, string>>;

    /** A full image reference, when the operator pinned one rather than picking a version. */
    readonly serverImage?: string;

    /**
     * The image the panel runs, when it is not derivable from the server's version.
     *
     * This process is the panel, so the honest value is "whatever image this container was started
     * from" — which a process cannot read about itself. See {@link BOOTSTRAPPER_IMAGE_REPOSITORY} for
     * the default and the report for what should eventually supply it.
     */
    readonly panelImage?: string;

    /**
     * Where Let's Encrypt sends expiry warnings, on the `lets-encrypt` shape.
     *
     * The one channel that reaches the operator when renewal has been failing for a fortnight and
     * nobody has looked at the edge's logs. Traefik registers an ACME account with it as the contact;
     * the other three shapes never reach the resolver and ignore this.
     */
    readonly acmeEmail?: string;
}

export interface TlsMaterial {
    readonly certificatePath: string;
    readonly keyPath: string;
}

/** One port that leaves the machine, and the sentence explaining it. */
export interface PublishedPort {
    readonly service: string;

    /** `0.0.0.0` for the world, `127.0.0.1` for the operator's own SSH tunnel. */
    readonly address: string;
    readonly hostPort: number;
    readonly containerPort: number;
    readonly protocol: "tcp" | "udp";

    /** Printed to the operator by the installer. They are about to open a firewall for these. */
    readonly why: string;
}

export interface ComposeProject {
    /** The compose file. Contains no secret; see {@link files} for the ones that do. */
    readonly document: string;

    /** Everything else this project needs on disk, in the same shape `generate.ts` returns. */
    readonly files: readonly GeneratedFile[];

    /** Compose service names, for the panel and for whatever waits on health. */
    readonly services: readonly string[];

    /** The roles this project runs. The caller owes `generate()` the same list. */
    readonly roles: readonly string[];

    readonly published: readonly PublishedPort[];
}

/* ------------------------------------------------------------------------------------------------
 * Building it.
 * ---------------------------------------------------------------------------------------------- */

/** Interpolation names. In one place because the document and the `.env` have to agree on them. */
export const ENVIRONMENT_VARIABLES = {
    databasePassword: "ARGON_POSTGRES_PASSWORD",
    storageAccessKey: "ARGON_STORAGE_ACCESS_KEY",
    storageSecretKey: "ARGON_STORAGE_SECRET_KEY",
    sfuKeys: "ARGON_SFU_KEYS",
} as const;

const SETTINGS_MODE = 0o644;
const SECRETS_MODE = 0o600;

const DEFAULT_INSTALL_ROOT = "/opt/argon";

/** Where the two generated configuration layers land inside every role container. */
const CONTAINER_CONFIG_DIR = "/conf.d";
const CONTAINER_CONFIG_FILE = "/argon.secrets.json";

/**
 * Where the media subdomain's certificate lands inside the front door.
 *
 * A path of its own rather than the one `generate.ts` named, because §5 says a Cloudflare instance with
 * media published directly runs two certificates at once with two expiry dates. Mounting the second
 * over the first would leave the front door serving one name's certificate for both.
 */
const CONTAINER_VOICE_TLS = { certificate: "/etc/argon/tls/voice.crt", key: "/etc/argon/tls/voice.key" } as const;

/**
 * The edge's own filesystem.
 *
 * `DEPLOYMENT.tls` is reused for the main certificate even though no Argon container mounts it any
 * more: it is one name for one file, and the day the panel reports an expiry date it should be reading
 * the path the generator already knows about rather than a second one invented here.
 */
const CONTAINER_EDGE = {
    staticConfig: "/etc/traefik/traefik.yml",
    dynamicConfig: "/etc/traefik/dynamic.yml",

    /**
     * Traefik's ACME account and the certificates it obtained, on a named volume.
     *
     * On a volume and not in the install root because losing it is not a cold start: Let's Encrypt
     * rate-limits duplicate certificates to five a week, so an instance that re-issues on every
     * `compose down -v` runs out of issuance and then answers with no certificate at all.
     */
    acme: "/data/acme.json",
} as const;

/** Where the panel sees the install root, and the two files inside it that it is told about by name. */
const CONTAINER_PANEL_ROOT = "/argon";

type Json = string | number | boolean | null | Json[] | { [key: string]: Json };
type JsonObject = { [key: string]: Json };

/**
 * The whole project: the document, the files beside it, and what faces the world.
 *
 * `secrets` is required rather than optional even though the document holds none of it. A compose file
 * whose `${…}` interpolations have no `.env` beside them does not fail at `up` with "you forgot the
 * secrets" — Postgres starts with an empty password, or refuses, depending on the variable, and the
 * operator reads a database error. Taking the mint here means the `.env` is produced by the same call
 * that produced the references to it.
 */
export function composeProject(
    answers: Answers,
    secrets: MintedSecrets,
    options: ComposeOptions = {},
): ComposeProject {
    const root = trimTrailingSlash(options.installRoot ?? DEFAULT_INSTALL_ROOT);
    const images = { ...INFRASTRUCTURE_IMAGES, ...options.images };
    const roles = rolesFor(answers);
    const bundledStorage = answers.storage.kind === "local";

    // Both hostnames reach a Traefik matcher below, so both are checked before anything is built. See
    // `assertHostname`: a rule is a `Host(`…`)` expression, and JSON escaping does not help inside one.
    assertHostname(answers.domain, "the instance's domain");

    const voiceHost = mediaHostFor(answers);

    if (voiceHost !== undefined) assertHostname(voiceHost, "the media subdomain");

    // Decided once, here, and handed to everything that needs it. Doing it per call site is how one of
    // them ends up serving plain HTTP while the rest think they are behind TLS.
    const tls = tlsPlanFor(answers.traffic, options);
    const edge = edgePlanFor(answers, options, roles, tls);

    const services: JsonObject = {};
    const published: PublishedPort[] = [];

    // Outside in: the door, the panel behind it, then the product, then what the product stands on.
    services[SERVICES.edge] = edgeService(edge, options, images.edge, root, published);
    services[SERVICES.panel] = panelService(options.panelImage ?? panelImageFor(answers.serverVersion), root);

    for (const role of roles) services[serviceFor(role)] = roleService(role, answers, options, root, published);

    services[SERVICES.postgres] = databaseService(images.database);
    services[SERVICES.cache] = cacheService(images.cache);
    services[SERVICES.bus] = busService(images.bus);

    if (bundledStorage) {
        services[SERVICES.storage] = storageService(images.storage, root);
        services[SERVICES.storageInit] = storageInitService(images.storage);
    }

    if (answers.voice) services[SERVICES.sfu] = sfuService(images.sfu, root, published);

    const document: JsonObject = {
        name: COMPOSE_PROJECT,
        services,
        networks: {
            argon: {
                driver: "bridge",
                // See NETWORK_SUBNET: something has to be able to name this CIDR to trust the proxy's
                // forwarded headers, and a CIDR docker chose is not a CIDR anything can be told about.
                ipam: { config: [{ subnet: NETWORK_SUBNET }] },
            },
        },
        volumes: volumesFor(bundledStorage),
    };

    return {
        document: render(document),
        files: sidecarFiles(answers, secrets, edge, bundledStorage),
        services: Object.keys(services),
        roles,
        published,
    };
}

/* ------------------------------------------------------------------------------------------------
 * The bootstrap phase: the front door, before there is anything behind it.
 * ---------------------------------------------------------------------------------------------- */

/**
 * The little that has to be known before the operator can be shown a question.
 *
 * Everything else in this file describes an instance that has been configured. This describes the
 * twenty minutes before that: the install script has asked for a domain and which of §5's paths the
 * operator is taking, and nothing else exists yet.
 */
export interface BootstrapPhase {
    readonly domain: string;

    /** Which of §5's paths. Decides whether a certificate is mounted, obtained, or not needed. */
    readonly traffic: TrafficShape;

    /**
     * The panel's image, always — never derived.
     *
     * There is no server version yet to derive it from. The install script pulled this image to run
     * this code, so it is the one thing here that is known exactly rather than chosen.
     */
    readonly panelImage: string;

    /** The install root as the host sees it; every bind mount below is resolved by the daemon. */
    readonly root: string;

    readonly tls?: TlsMaterial;
    readonly acmeEmail?: string;
}

/**
 * The edge and the panel, and nothing else.
 *
 * Traefik comes up **first**, before the operator has answered a single question, and this is the
 * function that makes that possible. The alternative — the panel holding `:443` itself and handing it
 * over once setup finishes — needs the panel to answer a request while closing the listener that
 * carried it, and on the Let's Encrypt path it needs a second ACME client in the install script to
 * obtain the certificate that Traefik will then obtain again. Starting the door first dissolves both:
 * the panel is behind a proxy from its first second, and never holds a public port at all.
 *
 * The project name, the network and its subnet, the edge's volume and the two configuration paths are
 * all the ones {@link composeProject} uses, and that is the point. `apply()` writes the full document
 * over this one and brings the same project up again: compose diffs them, adds the roles and the
 * infrastructure, and updates the edge in place with its new routing. There is no moment where the
 * front door is down, because it is the same container throughout.
 *
 * `roles` is empty and `files` holds no secret: nothing here has a password, because nothing here has
 * a database.
 */
export function bootstrapProject(phase: BootstrapPhase): ComposeProject {
    assertHostname(phase.domain, "the instance's domain");

    const options: ComposeOptions = { tls: phase.tls, acmeEmail: phase.acmeEmail };
    const tls = tlsPlanFor(phase.traffic, options);

    const plan: EdgePlan = {
        domain: phase.domain,
        tls,
        listener: tls.kind === "none" ? DEPLOYMENT.ports.plaintext : DEPLOYMENT.ports.tls,
        address: tls.kind === "none" ? "127.0.0.1" : "0.0.0.0",

        // No voice, no bucket, no identity server and no bot API: none of them are running, and a
        // router pointing at a service this project does not define is a 502 on a door that is
        // otherwise working — which reads as the install having failed rather than as a route.
        voiceOnMainHost: false,
        bundledStorage: false,
        identity: false,
        bots: false,

        startsAfter: SERVICES.panel,
    };

    const published: PublishedPort[] = [];
    const services: JsonObject = {};

    services[SERVICES.edge] = edgeService(plan, options, INFRASTRUCTURE_IMAGES.edge, phase.root, published);
    services[SERVICES.panel] = panelService(phase.panelImage, phase.root);

    const document: JsonObject = {
        name: COMPOSE_PROJECT,
        services,
        networks: {
            argon: { driver: "bridge", ipam: { config: [{ subnet: NETWORK_SUBNET }] } },
        },

        // Only the edge's. Declaring the rest here would have compose create four empty volumes for
        // services that do not exist yet, and then the operator sees them in `docker volume ls` and
        // cannot tell an install that stopped half way from one that has not started.
        volumes: { "argon-edge-data": {} },
    };

    return {
        document: render(document),
        files: [
            { path: EDGE_STATIC_CONFIG, contents: traefikStaticConfiguration(plan), mode: SETTINGS_MODE },
            { path: EDGE_DYNAMIC_CONFIG, contents: bootstrapDynamicConfiguration(plan), mode: SETTINGS_MODE },
        ],
        services: Object.keys(services),
        roles: [],
        published,
    };
}

/**
 * Routing while the panel is the only thing running.
 *
 * Two routers onto one service. `/panel` is where the panel lives for the rest of the instance's life,
 * so the link the install script prints is the link that keeps working after setup — an operator who
 * bookmarks it during the install does not find it dead the next day. `/` is the courtesy: during setup
 * there is nothing else to serve, and someone who types the bare domain should not get a 404 from a
 * machine that is waiting for them.
 *
 * After `apply()` the full document replaces this, `/` goes to `entrypoint`, and `/panel` does not move.
 */
function bootstrapDynamicConfiguration(plan: EdgePlan): string {
    const tls = routerTls(plan, plan.domain);

    const routers: JsonObject = {
        panel: {
            rule: `Host(\`${plan.domain}\`) && PathPrefix(\`${PANEL_PATH}\`)`,
            priority: ROUTER_PRIORITY.panel,
            entryPoints: [ENTRY_POINTS.public.name],
            service: "panel",
            middlewares: PANEL_MIDDLEWARE_CHAIN,
            ...tls,
        },

        setup: {
            rule: `Host(\`${plan.domain}\`)`,
            priority: ROUTER_PRIORITY.api,
            entryPoints: [ENTRY_POINTS.public.name],
            service: "panel",
            ...tls,
        },
    };

    const document: JsonObject = {
        http: {
            routers,
            middlewares: PANEL_MIDDLEWARES,
            services: {
                panel: {
                    loadBalancer: { servers: [{ url: `http://${SERVICES.panel}:${DEPLOYMENT.ports.plaintext}` }] },
                },
            },
        },
    };

    const store = certificateStore(plan);

    if (store !== undefined) document["tls"] = store;

    return render(document);
}

/* ------------------------------------------------------------------------------------------------
 * TLS: §5's three paths, as Traefik expresses them.
 * ---------------------------------------------------------------------------------------------- */

/**
 * How the edge gets a certificate, or why it does not need one.
 *
 * Three shapes and not four, because §5's path A and path B converge mechanically: an operator's own
 * certificate and a Cloudflare Origin CA certificate are both a certificate and a key on disk, arriving
 * the same way, mounted the same way and served the same way. What differs between them is renewal and
 * who to complain to, and neither of those is a Traefik setting.
 */
type TlsPlan =
    /** Traefik's file provider, serving material the install script established. */
    | { readonly kind: "files"; readonly material: TlsMaterial }
    /** Traefik's own ACME resolver. Nothing on disk; §5's "renewal is ours" becomes Traefik's. */
    | { readonly kind: "acme"; readonly email?: string }
    /** No TLS anywhere on this machine: the tunnel makes the outbound connection and carries it. */
    | { readonly kind: "none" };

/** The resolver's name, used by the static file that declares it and every router that asks for it. */
const ACME_RESOLVER = "letsencrypt";

/**
 * Which of the three a traffic shape means, refusing every combination that is not one of them.
 *
 * The refusals are the point of this function. An edge that quietly serves plain HTTP because a
 * certificate was not passed is the failure §5 spends a table warning about — Cloudflare's "Flexible"
 * mode, auth tokens in clear across the internet — and it is invisible from the outside, because
 * whatever is in front still shows a padlock. So each shape either produces material or is named here
 * as a shape that needs none, and the `never` branch means a fifth member added to `TrafficShape` in
 * model.ts fails to compile rather than falling through to plaintext.
 */
/**
 * Whether a traffic shape terminates TLS on this machine, and so wants a certificate on disk.
 *
 * Exported because `setup.ts` has to decide whether to hand {@link ComposeOptions.tls} in, and had grown
 * its own copy of this list to do it. Two lists mean a fifth shape gets added to one of them: the
 * generator would then refuse a certificate the caller never sent, or — worse in the other direction —
 * emit an edge with no certificate and no complaint.
 */
export function needsCertificate(traffic: TrafficShape): boolean {
    return traffic.kind === "own-certificate" || traffic.kind === "cloudflare-proxied";
}

function tlsPlanFor(traffic: TrafficShape, options: ComposeOptions): TlsPlan {
    switch (traffic.kind) {
        case "own-certificate":
        case "cloudflare-proxied": {
            if (options.tls === undefined)
                throw new Error(
                    `the ${traffic.kind} traffic shape terminates TLS on this machine, so options.tls must name the certificate and key the install script established`,
                );

            return { kind: "files", material: options.tls };
        }

        case "lets-encrypt": {
            // Refused rather than ignored. Under Kestrel this shape needed the script to obtain the
            // certificate and something to make Kestrel notice a rotated file (§5, path C); Traefik
            // obtains and renews it itself, so material on disk means the caller meant path A and said
            // path C. Accepting both would leave nobody able to say which one renews.
            if (options.tls !== undefined)
                throw new Error(
                    "the lets-encrypt traffic shape obtains its own certificate through Traefik's ACME resolver, so options.tls has nothing to name; a certificate the install script established is the own-certificate shape",
                );

            return { kind: "acme", email: options.acmeEmail };
        }

        case "cloudflare-tunnel":
            return { kind: "none" };

        default: {
            // Unreachable while `TrafficShape` has four members, and deliberately not an `assertNever`
            // helper that only fails at compile time: a shape arriving from JSON that this file has
            // never heard of must stop the install rather than produce an edge with no TLS on it.
            const unhandled: never = traffic;

            throw new Error(
                `traffic shape ${JSON.stringify(unhandled)} has no TLS configuration here, and an edge without one serves the whole instance in the clear`,
            );
        }
    }
}

/**
 * Everything the edge needs to know, resolved once.
 *
 * A record rather than six arguments threaded through four functions, because the service definition
 * and the two configuration files have to agree about every field in it: a listener the static file
 * does not declare, a router on an entry point that does not exist, or a published port that maps to
 * neither, are all silent — the edge starts and something is unreachable.
 */
interface EdgePlan {
    /**
     * The name this instance answers to.
     *
     * The whole {@link Answers} used to sit here, and every reader of it wanted this field. Narrowing
     * it is what lets the bootstrap phase build an edge before there are any answers — at that point
     * the domain is the only thing the operator has told anybody.
     */
    readonly domain: string;
    readonly tls: TlsPlan;

    /** The port the `public` entry point binds inside the container. */
    readonly listener: number;

    /** `0.0.0.0`, or the loopback when a tunnel is the only thing that should reach this. */
    readonly address: string;

    /** The media subdomain, when voice is on and the shape gave it a name of its own. */
    readonly voiceHost?: string;

    /** Whether voice rides the instance's own name, which is where `/rtc` and `/twirp/…` then live. */
    readonly voiceOnMainHost: boolean;

    /** Routed only when the store is ours to run; the operator's own S3 is reached at their endpoint. */
    readonly bundledStorage: boolean;

    /** Gated on {@link isClient} for the same reason publishing is: a silo has no Kestrel to route to. */
    readonly identity: boolean;
    readonly bots: boolean;

    /**
     * The compose service the edge starts behind.
     *
     * Named rather than assumed because it is not the same service in both phases: during setup the
     * only thing behind the door is the panel, and a `depends_on` naming a role that this project does
     * not define is not a warning — compose refuses the file outright.
     */
    readonly startsAfter: string;
}

function edgePlanFor(answers: Answers, options: ComposeOptions, roles: readonly string[], tls: TlsPlan): EdgePlan {
    const voiceHost = answers.voice ? mediaHostFor(answers) : undefined;

    // §5 says a Cloudflare instance with media published directly runs two certificates at once, and
    // this is where that stops being a sentence in a document. Without the second one Traefik answers
    // the media name with the default certificate, which does not cover it: the handshake fails, voice
    // does not connect, and the client reports a TLS error that reads as its own bug. Caught here, the
    // installer can still tell the operator to produce the certificate §5 already told them about.
    if (voiceHost !== undefined && options.voiceTls === undefined)
        throw new Error(
            `voice is published on ${voiceHost}, a name Cloudflare never sees and this instance's certificate does not cover, so options.voiceTls must name a certificate for it`,
        );

    const runs = (role: string): boolean => roles.includes(role) && isClient(role, options.knownRoles);

    return {
        domain: answers.domain,
        tls,

        // The tunnel shape is plaintext all the way through, so the edge binds the port everything else
        // in this project uses for plain HTTP rather than the one named for TLS. Cosmetic to docker and
        // not to the person reading `docker ps` trying to work out whether anything is encrypted.
        listener: tls.kind === "none" ? DEPLOYMENT.ports.plaintext : DEPLOYMENT.ports.tls,
        address: tls.kind === "none" ? "127.0.0.1" : "0.0.0.0",
        voiceHost,
        voiceOnMainHost: answers.voice && voiceHost === undefined,
        bundledStorage: answers.storage.kind === "local",
        identity: runs("aegis"),
        bots: runs("botapi"),
        startsAfter: serviceFor("entrypoint"),
    };
}

/* ------------------------------------------------------------------------------------------------
 * The roles.
 * ---------------------------------------------------------------------------------------------- */

/**
 * One role, running the server image with `--role <name>`.
 *
 * The configuration mounts are the pair `argon.ts`'s `dockerCommandFor` uses when it interrogates the
 * image, and for the same reason: `conf.d` alone makes the server judge a configuration it can only
 * half read. Read-only, because nothing in a role should be able to rewrite what it was configured
 * with — an upgrade that rewrote its own `conf.d` would be undiagnosable.
 *
 * **No role mounts a certificate.** Traefik terminates TLS, so Kestrel serves plain HTTP on the compose
 * network in every traffic shape and there is nothing for a role to open. That is the point of the
 * move: the private key exists in exactly one container, and a role that is compromised cannot hand out
 * the instance's identity. Putting the mounts back means either two terminations for one connection or
 * a private key in six containers, and `generate.ts` has to be changed with it — see the report.
 */
function roleService(
    role: string,
    answers: Answers,
    options: ComposeOptions,
    root: string,
    published: PublishedPort[],
): JsonObject {
    const client = isClient(role, options.knownRoles);

    const volumes = [
        `${root}/${DEPLOYMENT.confD}:${CONTAINER_CONFIG_DIR}:ro`,
        `${root}/${DEPLOYMENT.secretsFile}:${CONTAINER_CONFIG_FILE}:ro`,
    ];

    const service: JsonObject = {
        image: options.serverImage ?? serverImageFor(answers.serverVersion),

        // The image's entrypoint is the server itself, so this is its argv. There is deliberately no
        // ARGON_ROLE fallback in the server: a process started without `--role` is an error there.
        command: ["--role", role],

        // `on-failure` and not `unless-stopped`, and the count is the point. A role that has exited
        // badly five times in a row is failing on its configuration, not on a blip, and the operator
        // needs the container to stop and stay stopped: the panel can then show it as failed and the
        // last thing in `docker logs` is the failure rather than the four-hundredth retry of it.
        // What this costs is a restart after a host reboot, which the panel owns — §9 says start and
        // stop are compose operations it performs, and it is the thing that comes up on boot.
        restart: "on-failure:5",

        environment: {
            ARGON_CONFIG_DIR: CONTAINER_CONFIG_DIR,
            ARGON_CONFIG_FILE: CONTAINER_CONFIG_FILE,
        },
        volumes,
        depends_on: roleDependencies(role, answers),
        networks: ["argon"],

        // Orleans deactivates grains on shutdown and hands their state back to storage. Docker's
        // default is ten seconds, which is enough for an idle silo and not for a busy one; the cost of
        // being generous is a slower `compose down` and the cost of being mean is grain state written
        // by nobody.
        stop_grace_period: "30s",
    };

    // Gated because a silo's ports are its Orleans endpoints: anything that reaches a gateway can
    // address a grain, and nothing authenticates in front of that.
    const ports = client ? publishedFor(role, published) : [];

    if (ports.length > 0) service["ports"] = ports;

    return service;
}

/**
 * What a role has to be able to reach before it is worth starting.
 *
 * Every role dials NATS — `ArgonOrleansHosting` calls `AddNatsCtx()` on both the client and the silo
 * path — and every role dials Redis, because Orleans membership lives there. Neither of those is a
 * per-role decision, so neither is written as one.
 *
 * Migrations need no ordering among the roles: §9 says they run on boot under a lease, so whichever
 * role gets there first does the work and the rest wait. Adding an order here would be a second
 * mechanism for something that already has one.
 */
function roleDependencies(role: string, answers: Answers): JsonObject {
    const dependencies: JsonObject = {
        // `service_healthy` rather than `service_started` for these two specifically: a role that
        // starts against a Postgres still running initdb fails its migration and restarts, which is
        // survivable and noisy, and a health condition costs nothing.
        [SERVICES.postgres]: { condition: "service_healthy" },
        [SERVICES.cache]: { condition: "service_healthy" },
        [SERVICES.bus]: { condition: "service_started" },
    };

    if (role === "media" && answers.storage.kind === "local")
        // Not the store — the thing that made its buckets. SeaweedFS does not create a bucket on first
        // write, so a media role that starts before this has finished uploads into `NoSuchBucket`.
        dependencies[SERVICES.storageInit] = { condition: "service_completed_successfully" };

    if (role === "voice") dependencies[SERVICES.sfu] = { condition: "service_started" };

    return dependencies;
}

/**
 * Ports a role publishes, which since Traefik took over TLS is the two consoles and nothing else.
 *
 * Only ever reached for a role the image calls a client — see the call site. The rule is `is this a
 * client`, never `is this a silo`, so a role nobody has heard of publishes nothing: when the server
 * grows a role, this fails as an unreachable surface rather than as an exposed gateway.
 *
 * `aegis` and `botapi` used to be here, each publishing its own Kestrel listener on a high port. They
 * cannot be any more, and the reason is not tidiness: Kestrel now serves plain HTTP, so publishing it
 * would put a sign-in page and a bot API on the public internet in the clear. Both moved to Traefik
 * entry points on the same certificate — {@link ENTRY_POINTS}, {@link edgeService} — which is the same
 * host port, reachable at the same URL, with the TLS terminated by the one process that has a key.
 */
function publishedFor(role: string, published: PublishedPort[]): string[] {
    const record = (port: PublishedPort): string[] => {
        published.push(port);

        return [`${port.address}:${port.hostPort}:${port.containerPort}${port.protocol === "udp" ? "/udp" : ""}`];
    };

    const console = CONSOLE_PORTS[role];

    if (console !== undefined)
        return record({
            service: serviceFor(role),

            // Loopback, deliberately, and this is the one publishing decision worth arguing about. The
            // consoles answer on listeners of their own — the server image EXPOSEs 8920 and 8930 — and
            // those listeners carry no TLS: `Kestrel:Argon` configures the *other* port. Publishing
            // them on the public interface would put the operator console, which §10 calls the
            // highest-value target on the machine, on the internet in the clear. So they are reachable
            // over an SSH tunnel and nothing else until something terminates TLS for them.
            address: "127.0.0.1",
            hostPort: console,
            containerPort: console,
            protocol: "tcp",
            why: `the ${role} console listens on ${console} without TLS of its own, so it is published on the loopback interface only and reached through an SSH tunnel`,
        });

    return [];
}

/**
 * The sentence the installer prints about the identity server. It is not a happy one — see the report.
 *
 * The unhappiness is unchanged by Traefik and worth not papering over: `aegis` wants a public name and
 * this install has one name. A port is the discriminator instead, which works and is ugly in exactly
 * one visible way — the issuer in every token says `https://domain:8443`, so it cannot later move to
 * `auth.domain` without invalidating what is already signed.
 *
 * The tunnel shape cannot even do that. A Cloudflare tunnel maps hostnames to local services, not
 * ports, so `https://domain:8443` reaches nothing through it: the operator points a second hostname at
 * the loopback port below, and this says so rather than printing a URL that does not resolve.
 */
function aegisNote(plan: EdgePlan): string {
    const domain = plan.domain;

    if (plan.tls.kind === "none")
        return `sign-in happens on the identity server, which needs a public name of its own and has none here; a tunnel routes hostnames rather than ports, so point a second tunnel hostname at 127.0.0.1:${PUBLIC.aegis} on this machine`;

    return `sign-in happens on the identity server, which needs a public name of its own and has none here, so the front door terminates TLS for it on a port instead: https://${domain}:${PUBLIC.aegis}`;
}

function isClient(role: string, known: readonly RoleSummary[] | undefined): boolean {
    const summary = known?.find((candidate) => candidate.id === role);

    // What the image said, when the caller asked it. This table is the fallback, not the source.
    if (summary !== undefined) return summary.kind === "client";

    return CLIENT_ROLES.has(role);
}

/* ------------------------------------------------------------------------------------------------
 * The front door.
 * ---------------------------------------------------------------------------------------------- */

/**
 * Traefik.
 *
 * No `command`, which is deliberate. Every setting is in the static file at the path Traefik searches
 * unprompted (`/etc/traefik/traefik.yml`), and a flag on the command line would win over the file for
 * whatever it named — leaving the configuration split between two places, one of which is in a
 * different file from the rest of it. The way to change how the edge starts is to change the file this
 * module generates.
 *
 * There is no docker socket here, and there must not be: see the module header. This container is the
 * one that faces the internet.
 */
function edgeService(
    plan: EdgePlan,
    options: ComposeOptions,
    image: string,
    root: string,
    published: PublishedPort[],
): JsonObject {
    const volumes = [
        `${root}/${EDGE_STATIC_CONFIG}:${CONTAINER_EDGE.staticConfig}:ro`,
        `${root}/${EDGE_DYNAMIC_CONFIG}:${CONTAINER_EDGE.dynamicConfig}:ro`,
        "argon-edge-data:/data",
    ];

    // The mount and the certificate named in the dynamic file are one decision, so they are made from
    // one value: a certificate mounted and not served is dead weight, and one served and not mounted is
    // a front door that starts and answers every handshake with an error.
    if (plan.tls.kind === "files")
        volumes.push(
            `${plan.tls.material.certificatePath}:${DEPLOYMENT.tls.certificate}:ro`,
            `${plan.tls.material.keyPath}:${DEPLOYMENT.tls.key}:ro`,
        );

    // Gated on the media host and not merely on the option being present, so that the mount and
    // {@link certificateStore} are decided by the same condition. A certificate mounted into a front
    // door that serves no name it covers is the sort of thing that looks configured for a year.
    if (plan.voiceHost !== undefined && options.voiceTls !== undefined)
        volumes.push(
            `${options.voiceTls.certificatePath}:${CONTAINER_VOICE_TLS.certificate}:ro`,
            `${options.voiceTls.keyPath}:${CONTAINER_VOICE_TLS.key}:ro`,
        );

    const hostPort = plan.tls.kind === "none" ? PUBLIC.tunnel : ENTRY_POINTS.public.hostPort;
    const ports: string[] = [];

    const publish = (hostPortNumber: number, containerPort: number, why: string): void => {
        published.push({
            service: SERVICES.edge,
            address: plan.address,
            hostPort: hostPortNumber,
            containerPort,
            protocol: "tcp",
            why,
        });

        ports.push(`${plan.address}:${hostPortNumber}:${containerPort}`);
    };

    // The tunnel makes an outbound connection from the host, so nothing has to be reachable from
    // outside; `plan.address` binding this to the loopback is what makes "no inbound firewall rule
    // exists" true rather than merely intended.
    publish(
        hostPort,
        plan.listener,
        plan.tls.kind === "none"
            ? "the Cloudflare tunnel connects to this from the host; it carries the TLS, so this is plaintext and never leaves the machine"
            : `the instance's one public surface: the API and hub, the panel under ${PANEL_PATH}, the object store under ${DEPLOYMENT.storagePath}, and voice signalling`,
    );

    if (plan.identity) publish(ENTRY_POINTS.identity.hostPort, ENTRY_POINTS.identity.containerPort, aegisNote(plan));

    if (plan.bots)
        publish(
            ENTRY_POINTS.bots.hostPort,
            ENTRY_POINTS.bots.containerPort,
            `bots reach the bot API from the internet and it has no name of its own here either, so the front door terminates TLS for it on https://${plan.domain}:${PUBLIC.botapi}`,
        );

    return {
        image,
        restart: "unless-stopped",
        volumes,
        ports,
        depends_on: { [plan.startsAfter]: { condition: "service_started" } },
        networks: ["argon"],
    };
}

/**
 * The edge's static configuration: what the process is, before any routing exists.
 *
 * Written as JSON in a `.yml` file, for the reason the compose document is JSON — YAML is a superset,
 * Traefik reads the file with a YAML parser, and this module's tests can therefore `JSON.parse` what it
 * produced and assert that the ACME resolver exists rather than that the string "acme" appears
 * somewhere. The cost is the same one the compose document pays: an operator opening it sees JSON.
 *
 * Two absences are decisions:
 *
 *  - **no `api` section at all.** Its mere presence enables Traefik's API, and `api.insecure` on it is
 *    the well-trodden way people put an unauthenticated dashboard on the internet. There is nothing to
 *    turn off if it was never turned on.
 *  - **no `:80` entry point.** Nothing here needs one: TLS-ALPN-01 does the ACME challenge on 443, so
 *    an HTTP entry point would exist only to redirect, and it would be a second published port and a
 *    second firewall rule for the operator to open. An instance reached over plain `http://` gets a
 *    connection refused, which is a worse error message and a better outcome than a redirect that
 *    teaches clients the plaintext name works.
 */
function traefikStaticConfiguration(plan: EdgePlan): string {
    const entryPoints: JsonObject = { [ENTRY_POINTS.public.name]: entryPoint(plan.listener) };

    if (plan.identity) entryPoints[ENTRY_POINTS.identity.name] = entryPoint(ENTRY_POINTS.identity.containerPort);
    if (plan.bots) entryPoints[ENTRY_POINTS.bots.name] = entryPoint(ENTRY_POINTS.bots.containerPort);

    const document: JsonObject = {
        global: { checkNewVersion: false, sendAnonymousUsage: false },
        log: { level: "INFO" },
        entryPoints,
        providers: {
            file: {
                // `filename` and not `directory`. A directory holding both of this module's files would
                // make Traefik read this one as routing and refuse to start; see EDGE_STATIC_CONFIG.
                filename: CONTAINER_EDGE.dynamicConfig,

                // Not watched, and that is honest rather than lazy: this is a single-file bind mount, so
                // a rewrite that replaces the inode — which is what every safe write does — is invisible
                // inside the container, and a watch that works half the time is worse than none. The
                // panel restarts the edge after it changes routing.
                watch: false,
            },
        },
    };

    if (plan.tls.kind === "acme")
        document["certificatesResolvers"] = {
            [ACME_RESOLVER]: {
                acme: {
                    storage: CONTAINER_EDGE.acme,

                    // Optional, and checked rather than assumed: Traefik 3.3.7 with no `email` reaches
                    // `Register...` against Let's Encrypt and registers an account without a contact.
                    // Omitted when the operator gave none rather than written as an empty string —
                    // an account registers with a contact or without one, and "" is neither.
                    ...(plan.tls.email === undefined ? {} : { email: plan.tls.email }),

                    // TLS-ALPN-01 and not HTTP-01. §5 says either works and each needs its port
                    // reachable; 443 is already published and already justified, and 80 would be a
                    // second one existing only for the challenge. What this cannot do is issue a
                    // wildcard or validate through a CDN, and neither is wanted here — the proxied
                    // shape does not use ACME at all.
                    tlsChallenge: {},
                },
            },
        };

    return render(document);
}

/**
 * One entry point, with the timeouts spelled out.
 *
 * The default `readTimeout` would cut the request off after sixty seconds, and Argon's hub is a
 * WebSocket that is expected to stay open for hours and to be idle for most of them. §5 already warns
 * about exactly this failure in Cloudflare's proxy: a socket closed sooner than the client's reconnect
 * logic expects produces reconnect storms that read as a server fault. Adding a second proxy with the
 * same behaviour would be adding the bug the design already flagged.
 *
 * What is given up is the slow-request protection those timeouts are for — an attacker holding
 * connections open by sending a header every so often. On a self-hosted box that is a nuisance; a hub
 * that drops every minute is the product not working. `idleTimeout` is kept, generous, because it
 * applies to idle keep-alive connections rather than to an upgraded one.
 */
function entryPoint(port: number): JsonObject {
    return {
        address: `:${port}`,
        transport: { respondingTimeouts: { readTimeout: "0s", writeTimeout: "0s", idleTimeout: "600s" } },
    };
}

/**
 * The edge's dynamic configuration: routing, and which certificate answers which name.
 *
 * The routes, in the order {@link ROUTER_PRIORITY} matches them:
 *
 *  1. the media subdomain, when the Cloudflare shape gave voice a name of its own. Everything on that
 *     name is LiveKit, because that name resolves straight to this machine and Cloudflare is not in its
 *     path at all — which is the entire point of publishing it grey-clouded.
 *  2. reads of the content bucket, under {@link DEPLOYMENT.storagePath}. Scoped to `GET`/`HEAD` and to
 *     that one bucket on purpose: the bundled store is unauthenticated to everything on the compose
 *     network, and the export bucket holds whole-account GDPR archives. Routing `/s3/…` wholesale would
 *     publish those, and routing the write verbs would let anybody upload. The trailing slash in the
 *     prefix is load-bearing — without it `/s3/argon-exports/…` matches too.
 *  3. LiveKit's signalling paths, when voice rides the instance's own name. `/rtc` is its WebSocket and
 *     `/twirp/…` its API; Argon claims neither.
 *  4. the panel, under {@link PANEL_PATH}.
 *  5. everything else to `entrypoint`, over **plain HTTP**. That is the change Traefik bought: the hop
 *     is a bridge network on one machine between two containers this file started, it carries no
 *     certificate, and there is no verification to disable and explain.
 */
function traefikDynamicConfiguration(plan: EdgePlan): string {
    const routers: JsonObject = {};
    const services: JsonObject = {};
    const middlewares: JsonObject = {};
    const domain = plan.domain;

    const backend = (host: string, port: number): JsonObject => ({
        loadBalancer: { servers: [{ url: `http://${host}:${port}` }] },
    });

    const route = (
        name: string,
        rule: string,
        priority: number,
        service: string,
        extra: JsonObject = {},
    ): void => {
        routers[name] = {
            rule,
            priority,
            entryPoints: [ENTRY_POINTS.public.name],
            service,
            ...extra,
            ...routerTls(plan, domain),
        };
    };

    if (plan.voiceHost !== undefined) {
        route("media", `Host(\`${plan.voiceHost}\`)`, ROUTER_PRIORITY.media, "sfu", {});
        services["sfu"] = backend(SERVICES.sfu, DEPLOYMENT.ports.sfu);
    }

    if (plan.bundledStorage) {
        // The store serves `{bucket}/{key}`, so the published prefix comes off and the bucket stays on
        // — which is why `generate.ts` writes the bucket into `Cdn.Default.PathPrefix`. Stripping both
        // would 404 every avatar with everything else working.
        middlewares["storage-prefix"] = { stripPrefix: { prefixes: [DEPLOYMENT.storagePath] } };

        route(
            "storage",
            `Host(\`${domain}\`) && PathPrefix(\`${DEPLOYMENT.storagePath}/${DEPLOYMENT.buckets.content}/\`) && (Method(\`GET\`) || Method(\`HEAD\`))`,
            ROUTER_PRIORITY.storage,
            "storage",
            { middlewares: ["storage-prefix"] },
        );

        services["storage"] = backend(SERVICES.storage, DEPLOYMENT.ports.storage);
    }

    if (plan.voiceOnMainHost) {
        route(
            "voice",
            `Host(\`${domain}\`) && (PathPrefix(\`/rtc\`) || PathPrefix(\`/twirp/\`))`,
            ROUTER_PRIORITY.voice,
            "sfu",
        );

        services["sfu"] = backend(SERVICES.sfu, DEPLOYMENT.ports.sfu);
    }

    Object.assign(middlewares, PANEL_MIDDLEWARES);

    route("panel", `Host(\`${domain}\`) && PathPrefix(\`${PANEL_PATH}\`)`, ROUTER_PRIORITY.panel, "panel", {
        middlewares: PANEL_MIDDLEWARE_CHAIN,
    });

    services["panel"] = backend(SERVICES.panel, DEPLOYMENT.ports.plaintext);

    route("api", `Host(\`${domain}\`)`, ROUTER_PRIORITY.api, "entrypoint");
    services["entrypoint"] = backend(serviceFor("entrypoint"), DEPLOYMENT.ports.plaintext);

    // The two extra entry points, each carrying one name on one port. They are separate routers rather
    // than extra entry points on the router above because a router answers every entry point it lists:
    // `aegis` on the public port would shadow the API for the whole domain.
    if (plan.identity) {
        routers["identity"] = {
            rule: `Host(\`${domain}\`)`,
            entryPoints: [ENTRY_POINTS.identity.name],
            service: "aegis",
            ...routerTls(plan, domain),
        };

        services["aegis"] = backend(serviceFor("aegis"), DEPLOYMENT.ports.plaintext);
    }

    if (plan.bots) {
        routers["bots"] = {
            rule: `Host(\`${domain}\`)`,
            entryPoints: [ENTRY_POINTS.bots.name],
            service: "botapi",
            ...routerTls(plan, domain),
        };

        services["botapi"] = backend(serviceFor("botapi"), DEPLOYMENT.ports.plaintext);
    }

    const document: JsonObject = { http: { routers, middlewares, services } };
    const tls = certificateStore(plan);

    if (tls !== undefined) document["tls"] = tls;

    return render(document);
}

/**
 * How the panel is reached, in one place because both phases have to reach it the same way.
 *
 * ## Why the redirect is not cosmetic
 *
 * The page is served at `/panel` and its own links are relative, which is the only form that survives
 * being served from two base paths. Relative resolution is defined against the *last slash* of the
 * current URL — so from `/panel` a link to `api/state` resolves to `/api/state`, and from `/panel/` it
 * resolves to `/panel/api/state`. Only the second one reaches the panel: the first matches the catch-all
 * router and lands on `entrypoint`, which knows nothing about it.
 *
 * During setup that failure is invisible, because the catch-all is also the panel — everything works,
 * and it keeps working right up until the instance comes up and `/` becomes Argon. Then the panel breaks
 * for anybody whose bookmark lacks a trailing slash, with no change having been made to it.
 *
 * So the trailing slash is established at the door rather than assumed by the page. `permanent: false` —
 * 302, not 301: a browser that cached a permanent redirect for this host would keep it across a
 * reinstall on a different layout.
 *
 * ## Why the prefix still comes off
 *
 * The panel's own routes start at `/`, so it never learns where it was mounted. `stripPrefix` also sets
 * `X-Forwarded-Prefix`, which is what a page building an *absolute* URL should read; a page building one
 * from the request path alone would build the wrong one.
 */
const PANEL_MIDDLEWARES: JsonObject = {
    "panel-slash": {
        redirectRegex: {
            regex: `^(https?://[^/]+)${PANEL_PATH}$`,
            replacement: `\${1}${PANEL_PATH}/`,
            permanent: false,
        },
    },
    "panel-prefix": { stripPrefix: { prefixes: [PANEL_PATH] } },
};

/** In order: the redirect has to run before the prefix it is redirecting to is taken off. */
const PANEL_MIDDLEWARE_CHAIN = ["panel-slash", "panel-prefix"];

/**
 * What a router says about TLS, which for the tunnel shape is nothing at all.
 *
 * Omitted rather than set to something falsy on purpose: a router with no `tls` key serves plain HTTP,
 * which is precisely right behind a tunnel that carries the encryption and precisely wrong anywhere
 * else — so the only thing that may produce this is {@link TlsPlan}, and the only way to get the empty
 * object is to have said the shape needs no certificate.
 */
function routerTls(plan: EdgePlan, domain: string): JsonObject {
    switch (plan.tls.kind) {
        case "none":
            return {};

        case "files":
            // The certificates are in the store below; naming them per router would be a second copy of
            // the same two paths and a way for one router to serve the wrong name.
            return { tls: {} };

        case "acme":
            // `domains` rather than letting Traefik infer them from the rule: the media router's rule
            // names a different host, and the inference would then ask Let's Encrypt for a certificate
            // covering a name the ACME shape never has. (It cannot happen today — a media host only
            // exists on the Cloudflare path — and stating it costs one line.)
            return { tls: { certResolver: ACME_RESOLVER, domains: [{ main: domain }] } };
    }
}

/**
 * Which certificate answers which name.
 *
 * Both files appear in `certificates`, where Traefik reads their SANs and matches by SNI, *and* the
 * instance's own is the default. The default alone would work for one name and silently answer the
 * media subdomain with the wrong certificate; SNI matching alone would leave a request with no SNI at
 * all — an old client, or a probe by IP — with no certificate to be handed.
 */
function certificateStore(plan: EdgePlan): JsonObject | undefined {
    if (plan.tls.kind !== "files") return undefined;

    const main = { certFile: DEPLOYMENT.tls.certificate, keyFile: DEPLOYMENT.tls.key };
    const certificates: Json[] = [main];

    if (plan.voiceHost !== undefined)
        certificates.push({ certFile: CONTAINER_VOICE_TLS.certificate, keyFile: CONTAINER_VOICE_TLS.key });

    return { stores: { default: { defaultCertificate: main } }, certificates };
}

/* ------------------------------------------------------------------------------------------------
 * The panel.
 * ---------------------------------------------------------------------------------------------- */

/**
 * The panel: this same image, run again as §10's control surface.
 *
 * **Publishes nothing.** It is reached through the front door at {@link PANEL_PATH}, which is what
 * settles the collision described there — the bootstrapper holds `:443` while it serves the setup UI,
 * the edge publishes `:443`, and only one of them can win. The ingress wins, and this is how the panel
 * stays reachable afterwards.
 *
 * **Holds the docker socket**, which §10 calls root-equivalent and accepts, because the panel's whole
 * job is §9's lifecycle: start, stop, upgrade, logs. Without it this service is decorative. That it is
 * root-equivalent is the reason it publishes nothing and the reason §10 insists its authentication is a
 * real account by the time setup finishes — the bootstrap code from §4 is not enough to sit in front of
 * this.
 *
 * **Depends on nothing.** Deliberately: a panel that waits for a healthy database is unavailable in
 * exactly the situation an operator opens it for.
 */
function panelService(image: string, root: string): JsonObject {
    return {
        image,

        // `unless-stopped`, unlike the roles. §9 makes the panel the thing that starts everything else,
        // so it is also the thing that has to come back by itself after a host reboot.
        restart: "unless-stopped",
        environment: {
            ARGON_BOOTSTRAP_CONFIG_DIR: CONTAINER_PANEL_ROOT,
            ARGON_BOOTSTRAP_CODE_FILE: `${CONTAINER_PANEL_ROOT}/${BOOTSTRAP_CODE_FILE}`,

            // Written out rather than left to the process's own default, because the number here and the
            // one the router dials are one decision: a default that moves is a 502 on the panel and
            // nothing else, discovered by the operator rather than by us. No TLS variables — Traefik
            // terminated it, so this listener is plain HTTP on the compose network like every other.
            ARGON_BOOTSTRAP_HOST: "0.0.0.0",
            ARGON_BOOTSTRAP_PORT: String(DEPLOYMENT.ports.plaintext),
        },
        volumes: [
            // Writable, unlike every role's mount: the panel owns conf.d, the secrets file and this
            // compose document — §8 says the secrets are its, §9 says upgrades are its.
            `${root}:${CONTAINER_PANEL_ROOT}`,
            "/var/run/docker.sock:/var/run/docker.sock",
        ],
        networks: ["argon"],
    };
}

/** The panel's image, when the caller did not name one. See {@link BOOTSTRAPPER_IMAGE_REPOSITORY}. */
function panelImageFor(version: string): string {
    const trimmed = version.trim();

    if (trimmed.includes("/") || trimmed.includes("@"))
        throw new Error(
            `the server was pinned to the reference '${trimmed}', which is not a version this can tag the panel's own image with, so options.panelImage has to name it`,
        );

    return `${BOOTSTRAPPER_IMAGE_REPOSITORY}:${trimmed}`;
}

/**
 * The hostname media is reached on, when it is not the instance's own.
 *
 * The same condition `generate.ts` uses to build `CallKit:Sfu:PublicUrl`, and it has to stay the same:
 * the front door routes what that URL points at, so a disagreement is a client told to connect
 * somewhere nothing is listening.
 */
function mediaHostFor(answers: Answers): string | undefined {
    const traffic = answers.traffic;

    return traffic.kind === "cloudflare-proxied" ? traffic.voiceHost : undefined;
}

/* ------------------------------------------------------------------------------------------------
 * Infrastructure.
 * ---------------------------------------------------------------------------------------------- */

function databaseService(image: string): JsonObject {
    return {
        image,
        restart: "unless-stopped",
        environment: {
            POSTGRES_DB: DEPLOYMENT.database.name,
            POSTGRES_USER: DEPLOYMENT.database.user,

            // `:?` and not a plain reference: compose refuses to start the project when the variable is
            // missing, naming it. Without it the image starts with an empty password and refuses for a
            // reason that reads as a Postgres problem.
            POSTGRES_PASSWORD: required(ENVIRONMENT_VARIABLES.databasePassword),
            POSTGRES_INITDB_ARGS: "--data-checksums",
        },
        volumes: ["argon-postgres-data:/var/lib/postgresql/data"],
        healthcheck: {
            test: ["CMD-SHELL", `pg_isready -U ${DEPLOYMENT.database.user} -d ${DEPLOYMENT.database.name}`],
            interval: "5s",
            timeout: "5s",
            retries: 20,
            // initdb on a cold volume is the slow case, and a first boot that fails health while it is
            // still creating the cluster takes every role down with it.
            start_period: "60s",
        },
        // The default 64MB is what makes a parallel query fail on a machine that had the memory for it.
        shm_size: "256mb",
        networks: ["argon"],
    };
}

/**
 * The cache, which is not only a cache.
 *
 * `Redis:OrleansStorage` is grain persistence and `Redis:Orleans` is cluster membership — so losing
 * this volume is losing grain state, not a cold start. That is why the append-only log is on: a
 * snapshot every sixty seconds would silently discard the last minute of every grain that was written
 * to, and the instance would come back looking healthy.
 *
 * The five profiles use logical databases 0, 1, 2, 3 and 10, which is why the count is spelled out
 * rather than left to the image's default. An image that ships fewer answers `SELECT 10` with an error
 * that reads as a connection fault.
 */
function cacheService(image: string): JsonObject {
    return {
        image,
        restart: "unless-stopped",
        command: ["valkey-server", "--appendonly", "yes", "--save", "60", "1", "--databases", "16"],
        volumes: ["argon-cache-data:/data"],
        healthcheck: {
            test: ["CMD", "valkey-cli", "ping"],
            interval: "5s",
            timeout: "3s",
            retries: 20,
            start_period: "10s",
        },
        networks: ["argon"],
    };
}

/**
 * NATS, with JetStream, because the hosted deployment runs it that way and a stream that exists in one
 * and not the other is a feature that works everywhere except here.
 *
 * No health condition depends on this one. The official image is minimal and what it does or does not
 * carry to probe itself with is not something this file can check without a daemon — and a healthcheck
 * that can never pass is worse than none, because every role then waits on it forever.
 */
function busService(image: string): JsonObject {
    return {
        image,
        restart: "unless-stopped",
        command: ["--jetstream", "--store_dir", "/data", "--http_port", "8222", "--server_name", "argon-nats"],
        volumes: ["argon-nats-data:/data"],
        networks: ["argon"],
    };
}

/**
 * The bundled object store.
 *
 * Reachable from a browser only through the front door's read-only route — see
 * {@link traefikDynamicConfiguration}. Its own port is not published: a store on the internet with its
 * identities in a file is a store whose only protection is that file being right, and the edge route is
 * a smaller thing to get right.
 */
function storageService(image: string, root: string): JsonObject {
    return {
        image,
        restart: "unless-stopped",
        command: [
            "server",
            "-dir=/data",
            "-s3",
            `-s3.port=${DEPLOYMENT.ports.storage}`,
            "-s3.config=/etc/seaweedfs/identities.json",
        ],
        volumes: ["argon-storage-data:/data", `${root}/${STORAGE_IDENTITIES}:/etc/seaweedfs/identities.json:ro`],
        networks: ["argon"],
    };
}

/**
 * Making the two buckets, once, and refusing to report success until they are there.
 *
 * SeaweedFS does not create a bucket on first write, so without this every upload fails with
 * `NoSuchBucket` and every avatar 404s — the same visible symptom as the store being unreachable, from
 * a different cause. `media` waits on this completing rather than on the store starting, which is what
 * makes the ordering real.
 *
 * **The check is on the buckets, not on the shell.** This used to loop on `weed shell`'s exit status,
 * and `weed shell` prints `error: …` to stderr and exits 0 — so a bucket that was not created reported
 * success, `media` started against a store with no buckets, and the first anybody heard of it was an
 * upload failing much later with somebody else's error message. So the creates and an `s3.bucket.list`
 * go in together and the output has to name both buckets. Whole fields and not substrings: `argon` is a
 * prefix of `argon-exports`, and a `grep` for the first would be satisfied by the second.
 *
 * **Every `$` is doubled**, and this is the thing to be careful about when editing. Compose interpolates
 * variables in the value below before the container ever sees it, so a single `$i` reaches the shell as
 * an empty string — the awk program silently compares nothing to the bucket names, never matches, and
 * this loops until it gives up on a store that was fine. `$$` is compose's escape for a literal `$`.
 *
 * It gives up rather than looping forever. The old loop could not fail, and this one can: a store that
 * never makes its buckets is a broken install, and an installer that hangs on `compose up` with no
 * verdict is worse than one that stops and says which container to look at.
 */
function storageInitService(image: string): JsonObject {
    const buckets = [DEPLOYMENT.buckets.content, DEPLOYMENT.buckets.exports];
    const create = buckets.map((bucket) => `s3.bucket.create -name ${bucket}`).join("\\n");

    // One field-wise comparison per bucket, so the loop ends only when every one of them was listed.
    const found = buckets.map((bucket, index) => `if ($$i == "${bucket}") f${index} = 1`).join("; ");
    const all = buckets.map((_, index) => `f${index}`).join(" && ");

    const check = `awk '{ for (i = 1; i <= NF; i++) { ${found} } } END { exit !(${all}) }' /tmp/buckets`;

    const script = [
        "attempts=0",
        "while [ $$attempts -lt 60 ]; do",
        "attempts=$$((attempts + 1))",
        `printf '${create}\\ns3.bucket.list\\n' | weed shell -master=${SERVICES.storage}:9333 > /tmp/buckets 2>&1`,
        // Echoed rather than swallowed: whatever the store said about why it would not make a bucket is
        // the only diagnostic there is, and the check below consumes the file.
        "cat /tmp/buckets",
        `if ${check}; then echo 'the object store has both buckets'; exit 0; fi`,
        "sleep 2",
        "done",
        "echo 'the object store never listed both buckets; every upload would fail with NoSuchBucket' >&2",
        "exit 1",
    ].join("\n");

    return {
        image,
        // Not `unless-stopped`: this is meant to exit, and a restart policy would run it in a loop
        // forever and never let anything depending on its completion start.
        restart: "no",
        entrypoint: ["/bin/sh", "-c"],
        command: [script],
        depends_on: { [SERVICES.storage]: { condition: "service_started" } },
        networks: ["argon"],
    };
}

/**
 * LiveKit.
 *
 * Its API key and secret arrive through `LIVEKIT_KEYS` rather than in the configuration file, because
 * the file is written next to the compose document and this way it holds no secret at all. The two
 * media ports are published and nothing else is: 7880, its signalling port, is reached through the
 * front door, so it does not need a host port and does not get one.
 */
function sfuService(image: string, root: string, published: PublishedPort[]): JsonObject {
    published.push(
        {
            service: SERVICES.sfu,
            address: "0.0.0.0",
            hostPort: PUBLIC.sfuUdp,
            containerPort: PUBLIC.sfuUdp,
            protocol: "udp",
            why: "real-time media is UDP and this is the port it arrives on; without it calls connect and carry no audio",
        },
        {
            service: SERVICES.sfu,
            address: "0.0.0.0",
            hostPort: PUBLIC.sfuTcp,
            containerPort: PUBLIC.sfuTcp,
            protocol: "tcp",
            why: "the ICE fallback for clients on networks that block UDP outright; without it those clients cannot join a call at all",
        },
    );

    return {
        image,
        restart: "unless-stopped",
        command: ["--config", "/etc/livekit.yaml"],
        environment: { LIVEKIT_KEYS: required(ENVIRONMENT_VARIABLES.sfuKeys) },
        volumes: [`${root}/${SFU_CONFIG}:/etc/livekit.yaml:ro`],
        ports: [`0.0.0.0:${PUBLIC.sfuTcp}:${PUBLIC.sfuTcp}`, `0.0.0.0:${PUBLIC.sfuUdp}:${PUBLIC.sfuUdp}/udp`],
        networks: ["argon"],
    };
}

function volumesFor(bundledStorage: boolean): JsonObject {
    const volumes: JsonObject = {
        "argon-postgres-data": {},
        "argon-cache-data": {},
        "argon-nats-data": {},
        "argon-edge-data": {},
    };

    if (bundledStorage) volumes["argon-storage-data"] = {};

    return volumes;
}

/** Compose's "refuse to start, and say which variable" form. See {@link databaseService}. */
function required(variable: string): string {
    return `\${${variable}:?set in the .env beside this file; the installer wrote it}`;
}

/* ------------------------------------------------------------------------------------------------
 * The files beside the document.
 * ---------------------------------------------------------------------------------------------- */

function sidecarFiles(
    answers: Answers,
    secrets: MintedSecrets,
    edge: EdgePlan,
    bundledStorage: boolean,
): GeneratedFile[] {
    const files: GeneratedFile[] = [
        { path: ENV_FILENAME, contents: envFile(answers, secrets, bundledStorage), mode: SECRETS_MODE },
        { path: EDGE_STATIC_CONFIG, contents: traefikStaticConfiguration(edge), mode: SETTINGS_MODE },
        { path: EDGE_DYNAMIC_CONFIG, contents: traefikDynamicConfiguration(edge), mode: SETTINGS_MODE },
    ];

    if (bundledStorage)
        files.push({
            path: STORAGE_IDENTITIES,
            contents: storageIdentities(secrets),
            mode: SECRETS_MODE,
        });

    if (answers.voice)
        files.push({ path: SFU_CONFIG, contents: livekitConfiguration(), mode: SETTINGS_MODE });

    return files;
}

/**
 * The interpolation values, and the only reason this module takes the mint.
 *
 * Every value is hex or a pair of hex tokens, which matters more than it looks: compose expands `$` in
 * a `.env` value, so a generated secret containing one would be silently truncated or substituted.
 * `mintSecrets` produces hex and this is the second place that depends on it.
 */
function envFile(answers: Answers, secrets: MintedSecrets, bundledStorage: boolean): string {
    const lines: string[] = [
        "# Generated by the Argon installer. Mode 0600, and the only file in this project with secrets",
        "# in it that Argon itself does not read — compose interpolates these into the services that do.",
        "",
        `${ENVIRONMENT_VARIABLES.databasePassword}=${secrets.databasePassword}`,
    ];

    if (bundledStorage)
        lines.push(
            `${ENVIRONMENT_VARIABLES.storageAccessKey}=${secrets.objectStorage.accessKey}`,
            `${ENVIRONMENT_VARIABLES.storageSecretKey}=${secrets.objectStorage.secretKey}`,
        );

    // LiveKit's own format: one `id: secret` pair per line. The same values `generate.ts` wrote into
    // `CallKit:Sfu`, because the server signs room tokens with them and LiveKit verifies with them.
    if (answers.voice)
        lines.push(`${ENVIRONMENT_VARIABLES.sfuKeys}=${secrets.sfu.clientId}: ${secrets.sfu.secret}`);

    return `${lines.join("\n")}\n`;
}

/**
 * Who may do what to the bundled store.
 *
 * Two identities. `argon` is the minted key pair `generate.ts` wrote into `Storage:AccessKey` and
 * `Storage:SecretKey`, and it is the only one that may write. `anonymous` is SeaweedFS's name for an
 * unsigned request, and it gets `Read` on the content bucket and nothing else — because the browser
 * following a 302 to an avatar carries no credentials, and because the export bucket holds whole-account
 * GDPR archives that must never be one guessed key away from public.
 *
 * Without this file the store accepts anything from anyone, including the exports.
 */
function storageIdentities(secrets: MintedSecrets): string {
    const document = {
        identities: [
            {
                name: "argon",
                credentials: [
                    { accessKey: secrets.objectStorage.accessKey, secretKey: secrets.objectStorage.secretKey },
                ],
                actions: ["Admin", "Read", "Write", "List", "Tagging"],
            },
            {
                name: "anonymous",
                actions: [`Read:${DEPLOYMENT.buckets.content}`],
            },
        ],
    };

    return `${JSON.stringify(document, null, 2)}\n`;
}

/**
 * LiveKit, on one UDP port rather than a range.
 *
 * A range is what the hosted deployment uses and it is the wrong shape here: docker publishes a range
 * by starting a userspace proxy per port, so a thousand-port range is a thousand processes and a
 * minute of startup. UDP mux puts every peer connection on one port, which is one published port and
 * one firewall rule for the operator to open.
 *
 * `use_external_ip` makes LiveKit discover the machine's public address over STUN rather than
 * advertising the compose network's private one, which is what every candidate would otherwise be —
 * and a call in which every candidate is `172.29.x.x` connects to nothing.
 */
function livekitConfiguration(): string {
    return [
        "# Generated by the Argon installer. Keys arrive through LIVEKIT_KEYS, not through this file.",
        `port: ${DEPLOYMENT.ports.sfu}`,
        "rtc:",
        `  tcp_port: ${PUBLIC.sfuTcp}`,
        `  udp_port: ${PUBLIC.sfuUdp}`,
        "  use_external_ip: true",
        "logging:",
        "  level: info",
        "",
    ].join("\n");
}

/* ------------------------------------------------------------------------------------------------
 * Rendering, and the one thing an operator can type that reaches a text file.
 * ---------------------------------------------------------------------------------------------- */

/**
 * Refuses a hostname that is not one.
 *
 * The domain and the media subdomain are the only operator-supplied strings that reach a Traefik rule,
 * and a rule is an expression rather than data: `Host(\`value\`)`, delimited by backticks. The dynamic
 * file being JSON does not help — `JSON.stringify` escapes the value for the *file*, and Traefik then
 * parses what comes out as a matcher, where a backtick ends the literal and `||` starts a new clause. A
 * value that can add a clause is a front door with somebody else's route in it, which is the instance.
 * Everything else this module emits is a constant.
 *
 * Deliberately narrow — letters, digits, hyphens and dots. An IDN has a punycode form and that is what
 * belongs in a certificate anyway, so refusing the unicode form here refuses it where it is cheap to
 * explain instead of where it produces a certificate that does not match.
 */
export function assertHostname(value: string, what: string): void {
    const ok = /^(?=.{1,253}$)[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*$/i.test(value);

    if (!ok)
        throw new Error(
            `${what} is '${value}', which is not a hostname. It reaches a Traefik routing rule, which is an expression rather than a string with escaping in it.`,
        );
}

function trimTrailingSlash(path: string): string {
    return path.length > 1 ? path.replace(/\/+$/, "") : path;
}

/** Two-space indent and a trailing newline, matching everything else this installer writes. */
function render(document: JsonObject): string {
    return `${JSON.stringify(document, null, 2)}\n`;
}
