import { generateKeyPairSync, randomBytes } from "node:crypto";
import type { Answers, GeneratedFile, RoleDetail } from "./model";

/**
 * Turning what the operator answered into the files Argon reads.
 *
 * Pure: it returns files rather than writing them, so the whole of it can be exercised without a
 * filesystem and so the caller decides when — and whether — a write happens. The one impure thing is
 * {@link mintSecrets}, which is a separate call for exactly that reason.
 *
 * **How Argon reads configuration.** Layers, lowest first: the image's own `appsettings.json`, then
 * `conf.d/<feature>.json`, then the single document named by `ARGON_CONFIG_FILE`, then environment
 * variables. A `conf.d` file is section-shaped — the content is what would have been in
 * `appsettings.json` — and it may only carry sections its own feature declared; anything else is
 * reported as a diagnostic rather than quietly applied. So this generator never guesses which file a
 * section belongs in: it is told, by the {@link RoleDetail} list that `--explain` produced.
 *
 * **What that buys.** A section no chosen role reads is not written at all. That is not tidiness — a
 * section nobody reads is either a typo or configuration that used to matter, and writing it turns
 * either one into something an operator will later read as intent.
 *
 * **Secrets do not go in `conf.d`.** Settings go in the per-feature files; generated passwords, keys
 * and tokens go in one file with mode 0o600. Not for secrecy on a box the operator owns — for
 * replaceability. Vault comes back later as another configuration layer, and swapping one file for one
 * source is a change, while combing values out of a dozen files is a project. The secrets file is
 * written in the shape `ARGON_CONFIG_FILE` expects, because that is the layer it occupies and the
 * layer Vault will eventually occupy beside it.
 *
 * **The thing this exists to fix.** Argon's shipped `appsettings.json` is in a public repository and
 * it carries working cryptographic material: `Jwt:MachineSalt`, a real `Jwt:CertificateBase64` signing
 * key, `TicketJwt:Key`, `Transport:Exchange:HashKey`, `Totp:SecretPart`, and `Metrics:BasicAuth:
 * Password` of `12345678`. Every one of those is a default a self-hosted instance inherits unless
 * something overrides it, and anybody can read them. Overriding all of them is the most important
 * thing in this file; if a later edit drops one of those sections from {@link secretSpecifications},
 * the instance still boots and still looks fine.
 */

/* ------------------------------------------------------------------------------------------------
 * Where things are, inside the containers this configures.
 * ---------------------------------------------------------------------------------------------- */

/**
 * The addresses and names the generated configuration points at.
 *
 * Exported because it is a contract with whoever writes the compose file: the service names here are
 * the hostnames the server will dial, and the ports are the ones it will bind. Two files restating the
 * same hostname is how a rename breaks an instance in a way that reads as a network fault.
 */
export const DEPLOYMENT = {
    /** Directory of per-feature files, relative to the install root. */
    confD: "conf.d",

    /** The one secrets document, relative to the install root. It is what `ARGON_CONFIG_FILE` names. */
    secretsFile: "secrets.json",

    /** Compose service names, which are also the DNS names on the compose network. */
    hosts: {
        postgres: "argon-postgres",
        redis: "argon-redis",
        /** The bundled object store, present only when the operator did not bring their own. */
        storage: "argon-storage",
        sfu: "argon-livekit",
        nats: "argon-nats",
    },

    ports: {
        postgres: 5432,
        redis: 6379,
        storage: 8333,
        sfu: 7880,
        nats: 4222,
        /**
         * The container's own listener. High rather than 443 because binding a privileged port needs a
         * capability the image may not be given; compose publishes 443 onto this. Changing it here
         * without changing the published mapping produces an instance that answers on nothing.
         */
        tls: 8443,
        /** Same, for the tunnel path, where the tunnel — not Kestrel — carries TLS. */
        plaintext: 8080,
    },

    /** Where the install script leaves the certificate, as the container sees it after the mount. */
    tls: {
        certificate: "/etc/argon/tls/tls.crt",
        key: "/etc/argon/tls/tls.key",
    },

    database: { name: "argon", user: "argon" },

    buckets: { content: "argon", exports: "argon-exports" },

    /**
     * Path on the instance's own domain where the bundled object store is published.
     *
     * It has to be published somewhere, because Argon never serves file bytes: both `/files/{id}` and
     * `/files/k/{key}` end in a 302 to an origin, so the origin cannot be Argon — that is a redirect
     * loop, not a fallback.
     *
     * A path on the domain already configured rather than a subdomain of its own, so the instance keeps
     * one certificate and one name. Whatever routes traffic in front of this must send it to the store;
     * changing the value here without changing that routing produces avatars that 404 with everything
     * else working, which is the least obvious way for this to break.
     */
    storagePath: "/s3",
} as const;

/**
 * The compose network's address range, which the roles must trust as a proxy and nothing wider.
 *
 * Declared here rather than imported from `compose.ts` because the dependency would run the wrong way:
 * this module is the one the server's configuration comes from, and it would then need the module that
 * describes containers in order to write a setting. `compose.ts` imports `DEPLOYMENT` already; the two
 * values are asserted equal by a test, which is cheaper than an import cycle and catches the same drift.
 */
export const COMPOSE_NETWORK_SUBNET = "172.29.0.0/16";

/**
 * Written into `Database:Provider`, always, and spelled exactly like this.
 *
 * `DatabaseProviderKind` has two members, `CockroachDb` and `PostgreSql`, and the reader is
 * `Enum.TryParse` with a fallback: unset **or unparsable** resolves to CockroachDB. The boot path then
 * probes the server, finds PostgreSQL, and refuses to start. So `Postgres` — the spelling everybody
 * reaches for — fails in exactly the same way as writing nothing at all, and the error blames the
 * database rather than this line. Self-hosted is PostgreSQL; this is not a default worth inheriting.
 */
export const DATABASE_PROVIDER = "PostgreSql";

/** Region label for a one-machine instance. Multi-region is paused and PostgreSQL ignores placement. */
const SELF_HOSTED_REGION = "self-hosted";

/** Suffix for the export bucket when the operator brought their own object storage. */
const EXPORT_BUCKET_SUFFIX = "-exports";

/**
 * Marks a value only the deployment can supply, using the convention already in `deploy/pconf.d`.
 *
 * Emitted rather than left empty because the secrets file is a file the operator opens — the panel owns
 * it — and a marker there is a sentence they can act on. Blank is worse in two ways: it reads as
 * "configured, and empty", and nothing validates `Storage`, so the first sign of it is an upload
 * failing much later with somebody else's error message.
 */
export function unset(what: string): string {
    return `<<SET: ${what}>>`;
}

/** Readable by the containers that consume it; the mode is spelled out so a diff shows it changing. */
const SETTINGS_MODE = 0o644;

/**
 * The secrets file. 0o600 is asserted by a test rather than left to review: the difference between the
 * two kinds of file this writes is precisely this number, and it is one digit.
 */
const SECRETS_MODE = 0o600;

/* ------------------------------------------------------------------------------------------------
 * Secrets — the entropy edge.
 * ---------------------------------------------------------------------------------------------- */

/** A key pair as Argon reads it: base64 of the DER, exactly the shape the shipped values are in. */
export interface SigningKeyPair {
    readonly privateKey: string;
    readonly publicKey: string;
}

export interface EncryptionKeyPair {
    readonly privateKeyBase64: string;
    readonly publicKeyBase64: string;
}

/**
 * Everything generated for one instance.
 *
 * Minted in one call and passed in, rather than conjured inside {@link generate}, because two callers
 * need the same values: this file writes them into Argon's configuration, and whoever writes the
 * compose file needs the database password as `POSTGRES_PASSWORD` and the object-storage keys as the
 * bundled store's credentials. Reading them back out of the JSON this produced would work exactly
 * until somebody renamed a section.
 */
export interface MintedSecrets {
    readonly databasePassword: string;
    readonly jwtMachineSalt: string;
    readonly jwtSigning: SigningKeyPair;
    readonly jwtEncryption: EncryptionKeyPair;
    readonly ticketKey: string;
    readonly transportHashKey: string;
    readonly totpSecretPart: string;
    readonly metricsPassword: string;
    readonly objectStorage: { readonly accessKey: string; readonly secretKey: string };
    readonly sfu: { readonly clientId: string; readonly secret: string };
}

/**
 * Mints one instance's worth of secrets.
 *
 * `randomBytes` and nothing else. Not `Math.random`: it is a fast non-cryptographic PRNG seeded from
 * the process, its output is predictable from a handful of prior outputs, and every value below is
 * something an attacker forging a token or a hub ticket would want to predict. It is also the kind of
 * line that gets "simplified" during a cleanup because the difference does not show up in a test —
 * which is why this comment names the alternative rather than just the rule.
 *
 * Everything is minted whether or not the chosen roles need it. Deciding what is needed is
 * {@link generate}'s job and depends on what `--explain` reported; a secret that never reaches a file
 * costs a few microseconds and keeps this function free of policy.
 */
export function mintSecrets(): MintedSecrets {
    return {
        databasePassword: token(24),
        jwtMachineSalt: token(32),
        jwtSigning: ellipticKeyPair(),
        jwtEncryption: rsaKeyPair(),
        ticketKey: token(32),
        transportHashKey: token(32),
        totpSecretPart: token(16),
        metricsPassword: token(16),
        objectStorage: { accessKey: token(12), secretKey: token(24) },
        sfu: { clientId: token(12), secret: token(32) },
    };
}

/** Hex rather than base64 so a value can be pasted into a connection string or a shell without quoting. */
function token(bytes: number): string {
    return randomBytes(bytes).toString("hex");
}

/**
 * The token signing key: P-256, SEC1 private and SPKI public, base64 of the DER.
 *
 * That encoding is not a preference — it is what the loader accepts, and it is the shape the shipped
 * `Jwt:CertificateBase64` is already in. `JwtOptions.Validate` requires both halves, so a pair with a
 * missing side is refused at validation rather than at the first login.
 */
function ellipticKeyPair(): SigningKeyPair {
    const pair = generateKeyPairSync("ec", {
        namedCurve: "prime256v1",
        publicKeyEncoding: { type: "spki", format: "der" },
        privateKeyEncoding: { type: "sec1", format: "der" },
    });

    return { privateKey: pair.privateKey.toString("base64"), publicKey: pair.publicKey.toString("base64") };
}

/**
 * The token encryption key: RSA-2048, PKCS#1 private and SPKI public.
 *
 * Generated even though `JwtOptions.Validate` does not require it, because the thing that reads it —
 * `WrapperForEncryptionKey` — is constructed lazily and throws when the pair is absent. Leaving it
 * unset produces an instance that starts, validates clean, and fails on somebody's first sign-in.
 */
function rsaKeyPair(): EncryptionKeyPair {
    const pair = generateKeyPairSync("rsa", {
        modulusLength: 2048,
        publicKeyEncoding: { type: "spki", format: "der" },
        privateKeyEncoding: { type: "pkcs1", format: "der" },
    });

    return {
        privateKeyBase64: pair.privateKey.toString("base64"),
        publicKeyBase64: pair.publicKey.toString("base64"),
    };
}

/* ------------------------------------------------------------------------------------------------
 * Generating.
 * ---------------------------------------------------------------------------------------------- */

/** Credentials the operator brought, which nothing here can invent. */
export interface SuppliedSecrets {
    /** Keys for the operator's own S3, from the wizard. Absent leaves a marker rather than a blank. */
    readonly objectStorage?: { readonly accessKey: string; readonly secretKey: string };
}

export interface GenerateOptions {
    /** Mint once and pass it in when another writer needs the same values. Defaults to a fresh mint. */
    readonly secrets?: MintedSecrets;
    readonly supplied?: SuppliedSecrets;
}

/**
 * The per-feature configuration files and the one secrets file, for these answers and these roles.
 *
 * `roles` is what `--explain` said about the roles the operator chose, and it is the only source of
 * truth for which section belongs in which file. It is intersected with `answers.roles` rather than
 * trusted whole: explaining every role once and filtering later is the cheap way to call the binary,
 * and a detail list that carries roles nobody chose must not widen what gets written.
 *
 * Output is deterministic for a given set of answers — keys sorted, files in a fixed order — so that a
 * diff between two runs shows what changed rather than what got reordered. The generated secrets are
 * the only thing that differs, and they are all in one file.
 */
export function generate(
    answers: Answers,
    roles: readonly RoleDetail[],
    options: GenerateOptions = {},
): GeneratedFile[] {
    const secrets = options.secrets ?? mintSecrets();
    const owners = sectionOwners(roles, new Set(answers.roles));

    const perFeature = new Map<string, JsonObject>();

    for (const spec of settingSpecifications(answers)) {
        const feature = owners.get(spec.section.toLowerCase());

        // No chosen role declared this section. Either the operator did not pick the role that reads
        // it, or the section moved and this file has not caught up; both are reasons not to write it.
        if (feature === undefined) continue;

        merge(fileOf(perFeature, feature), nest(spec.section, spec.value));
    }

    const files: GeneratedFile[] = [...perFeature]
        .filter(([, content]) => Object.keys(content).length > 0)
        .sort(([left], [right]) => compare(left, right))
        .map(([feature, content]) => ({
            path: `${DEPLOYMENT.confD}/${feature}.json`,
            contents: render(content),
            mode: SETTINGS_MODE,
        }));

    const secretDocument: JsonObject = {};

    for (const spec of secretSpecifications(answers, secrets, options.supplied)) {
        // A secret in a section some feature declares is gated the same way its settings are: a role
        // that does not read the section has no use for the key. A secret in a section *nothing*
        // declares cannot be gated that way, because there is no declaration to ask — see `reach`.
        if (spec.reach === "declared" && !owners.has(spec.section.toLowerCase())) continue;

        merge(secretDocument, nest(spec.section, spec.value));
    }

    if (Object.keys(secretDocument).length > 0)
        files.push({ path: DEPLOYMENT.secretsFile, contents: render(secretDocument), mode: SECRETS_MODE });

    return files;
}

/**
 * Section path to the feature whose file may carry it.
 *
 * Keyed lowercase because .NET's configuration keys are case-insensitive and the ownership check
 * compares them that way. Matching case-sensitively would mean that a day the binary reports
 * `kestrel:argon` instead of `Kestrel:Argon`, this silently writes nothing for it — a mismatch that
 * produces an instance missing one section rather than an error.
 *
 * Sections are matched exactly, not by prefix. A role that declares `Storage:Limits` and not `Storage`
 * reads the limits and nothing else, so a `Storage` blob written into its file would pass the
 * ownership check — top-level key `Storage` is owned — and still be read by nobody.
 */
function sectionOwners(roles: readonly RoleDetail[], chosen: ReadonlySet<string>): Map<string, string> {
    const owners = new Map<string, string>();

    for (const role of roles) {
        if (!chosen.has(role.id)) continue;

        for (const feature of role.features)
            for (const section of feature.sections) {
                const key = section.toLowerCase();
                const existing = owners.get(key);

                // Two features declaring one section is a bug on the server's side, and there is no
                // channel here to report it. Taking the first name in order at least makes the output
                // the same on every run, so the mistake does not also move between installs.
                if (existing === undefined || compare(feature.name, existing) < 0) owners.set(key, feature.name);
            }
    }

    return owners;
}

/* ------------------------------------------------------------------------------------------------
 * What goes in the per-feature files.
 * ---------------------------------------------------------------------------------------------- */

interface SectionSpec {
    readonly section: string;
    readonly value: Json;
}

/**
 * Every setting this installer has an opinion about, before any of it is gated.
 *
 * Deliberately short. A value that matches what the image already ships is not written: it would read
 * as a decision this installer made, and the next person to change the default would have to find and
 * change it here too. What is written is either derived from an answer, or is something whose shipped
 * value is actively wrong for one machine — a CockroachDB connection string, two Redis servers, an
 * `appsettings.json` provider that resolves to the wrong engine.
 */
function settingSpecifications(answers: Answers): SectionSpec[] {
    const specs: SectionSpec[] = [
        { section: "Database", value: { Provider: DATABASE_PROVIDER } },
        { section: "Redis", value: redisProfiles() },
        { section: "Kestrel:Argon", value: kestrelSettings() },
        { section: "Storage", value: storageSettings(answers) },

        // Issuer and audience are the instance's own domain rather than the shipped "Argon", so a token
        // minted by one instance does not describe itself as belonging to another.
        { section: "Jwt", value: { Issuer: answers.domain, Audience: answers.domain } },

        /**
         * Which proxy ASP.NET is allowed to believe about the scheme.
         *
         * The shipped default is the in-cluster pod and service ranges, which match nothing on a compose
         * network — so without this the forwarded headers are ignored, and every decision that asks "was
         * this request secure" answers no. Secure cookies are not set, redirect URIs are built as http,
         * and metadata that requires HTTPS refuses. The instance looks like it is being served in the
         * clear, because as far as it can tell it is: TLS ended one hop earlier, at the edge.
         *
         * The value is the compose network's own CIDR and nothing wider. Trusting a range larger than
         * the bridge would let a client assert its own scheme.
         */
        { section: "ForwardedHeaders", value: { KnownNetworks: [COMPOSE_NETWORK_SUBNET] } },
    ];

    if (answers.voice) specs.push({ section: "CallKit", value: callKitSettings(answers) });

    /**
     * A shorter hub keep-alive when Cloudflare's proxy is in the path.
     *
     * Cloudflare closes a WebSocket that has carried nothing in either direction for a while, and does
     * not publish the number — its documentation says only that it happens, that Enterprise accounts can
     * negotiate their own, and that the answer is a heartbeat. So the number cannot be matched; it can
     * only be undercut.
     *
     * Argon's own default is a minute, which is probably inside it and has little room. Halving it costs
     * one small frame per client per thirty seconds and buys the margin. What it prevents is not an
     * error anybody would attribute to this: the socket closes, the client reconnects, and the operator
     * sees a chat that "drops sometimes" with nothing in any log — which §5 already warns produces
     * reconnect storms that read as a server fault.
     *
     * Only on the proxied path. The other three shapes have nothing between the client and the edge that
     * would time a socket out, and a heartbeat they do not need is traffic on somebody's mobile data.
     */
    if (answers.traffic.kind === "cloudflare-proxied")
        specs.push({ section: "WebSockets", value: { KeepAliveInterval: "00:00:30" } });

    return specs;
}

/**
 * The five Redis profiles, all pointed at the one cache this instance runs.
 *
 * Only the connection string is written. The shipped configuration already gives each profile its own
 * logical database — 0, 10, 3, 1 and 2 — and restating that here would be a second copy of a split
 * that has to stay consistent. What is wrong in the shipped configuration is the *server*: it names
 * two of them, on 6380 and 6379, because that is how the hosted deployment is laid out.
 *
 * `abortConnect=false` matters on first boot specifically: compose starts the containers together, and
 * without it StackExchange.Redis fails the very first connection and takes the process with it.
 */
function redisProfiles(): Json {
    const connection = [
        `${DEPLOYMENT.hosts.redis}:${DEPLOYMENT.ports.redis}`,
        "abortConnect=false",
        "connectRetry=5",
        "connectTimeout=10000",
    ].join(",");

    const profiles: JsonObject = {};

    for (const profile of ["Cache", "HybridCache", "Orleans", "OrleansStorage", "Backplane"])
        profiles[profile] = { ConnectionString: connection };

    return profiles;
}

/**
 * The listener. All of §5's paths but one end with a certificate and a key on disk that Kestrel
 * serves; the tunnel is the exception, because it makes the outbound connection and carries the TLS
 * itself, so the origin needs no certificate at all.
 *
 * The paths are written even though they match the image's defaults today. They are the point at which
 * a compose mount and a configuration file have to agree, and a default that moves under a mount that
 * did not is a process that starts, binds nothing on TLS, and looks healthy to everything except a
 * client trying to connect.
 */
function kestrelSettings(): Json {
    // Plaintext, always, and the port is the one Traefik dials.
    //
    // Traefik terminates TLS for every traffic shape, so no role container is handed a certificate any
    // more — the private key exists in exactly one place. What makes writing this unconditionally
    // *necessary* rather than tidy is how Kestrel fails otherwise: `WebFeatures` treats TLS as
    // configured only when the flag is set AND both files exist, so a configuration naming a
    // certificate that is not mounted does not error. It falls through and binds the named port in the
    // clear. Ask for 8443 with TLS and you get 8443 without it, while Traefik dials 8080, and every
    // request in the instance answers 502 with nothing in any log saying why.
    return { Port: DEPLOYMENT.ports.plaintext, UseFileCertificate: false };
}

/**
 * Object storage. The two answers want different things filled in: the bundled store is reached over
 * the compose network in plaintext and its buckets are ours to name, while the operator's own S3 is
 * reached at whatever they gave us and its bucket is theirs.
 *
 * The two URL-shaped settings here are not the same shape, which is worth knowing before editing
 * either: `Cdn.PublicBaseUrl` is a URL and `Endpoint` is a bare host — see {@link splitEndpoint}.
 * `PublicBaseUrl` is the instance's own domain in both branches, because every file URL the server
 * emits is `{PublicBaseUrl}/files/{id}` pointing back at this API, which then redirects. It is the
 * instance's public name, never the bucket's.
 */
function storageSettings(answers: Answers): Json {
    const storage = answers.storage;

    /**
     * Two URLs that look alike and are not.
     *
     * `PublicBaseUrl` is this instance: every file URL the server hands out is `{PublicBaseUrl}/files/…`
     * pointing back at its own API, which then redirects. `Default.BaseUrl` is where that redirect
     * *goes* — the origin holding the bytes — and it is the half that was missing. Written empty, the
     * 302 lands on `/{key}`, and every avatar and attachment in the instance 404s while nothing else
     * looks wrong.
     *
     * The two branches differ only in which origin that is: the store this install publishes, or the
     * bucket the operator already had.
     */
    const cdnFor = (origin: string, prefix: string): Json => ({
        PublicBaseUrl: `https://${answers.domain}`,
        Default: { Name: "default", BaseUrl: origin, PathPrefix: prefix, Countries: [] },
    });

    if (storage.kind === "local")
        return {
            Endpoint: `${DEPLOYMENT.hosts.storage}:${DEPLOYMENT.ports.storage}`,
            BucketName: DEPLOYMENT.buckets.content,
            ExportBucketName: DEPLOYMENT.buckets.exports,
            Region: "auto",
            UseSsl: false,
            // Through the instance's own name and the published path, because the store sits on the
            // compose network and a browser cannot reach `argon-storage:8333`. The bucket is the prefix:
            // the store serves `{bucket}/{key}` and the redirect has to arrive with both.
            Cdn: cdnFor(`https://${answers.domain}${DEPLOYMENT.storagePath}`, DEPLOYMENT.buckets.content),
        };

    const endpoint = splitEndpoint(storage.endpoint);

    return {
        Endpoint: endpoint.host,
        BucketName: storage.bucket,
        // A separate bucket, not a prefix in theirs: export archives are a whole account's data and are
        // meant to expire after about two days, and a lifecycle rule that short on the content bucket
        // would delete everyone's avatars. The operator was never asked for this name — see the report.
        ExportBucketName: `${storage.bucket}${EXPORT_BUCKET_SUFFIX}`,
        Region: storage.region ?? "auto",
        UseSsl: endpoint.useSsl,
        // Straight at the operator's own endpoint. Their bucket has to be readable by whoever follows
        // the redirect — a browser — and this installer cannot make it so; §6 says an operator who
        // brings a bucket owns its access policy, and the panel should show what this resolved to so a
        // private bucket is discovered here rather than by a user with a broken avatar.
        Cdn: cdnFor(`${endpoint.useSsl ? "https" : "http"}://${endpoint.host}`, storage.bucket),
    };
}

/**
 * Splits an object-storage endpoint into the bare host Argon wants and the flag that carries its
 * scheme.
 *
 * **`Storage:Endpoint` is a host, not a URL.** Its only two readers are `S3ClientPool`, which builds
 * `{UseSsl ? https : http}://{Endpoint}`, and the presigner, which builds `{bucket}.{Endpoint}` — so a
 * URL written into it comes back with a second scheme in front of it and fails at the first upload
 * looking like a network fault. (`deploy/dev/conf.d/file-storage.json` has a URL in it today.) The
 * operator will type a URL, because everything else in the world takes one, so it is normalised here
 * rather than asked about twice or explained in the wizard.
 *
 * No scheme is treated as TLS. Guessing the other way silently downgrades a connection the operator
 * believed was encrypted, which is the one error here that is worse than failing to connect.
 */
export function splitEndpoint(endpoint: string): { readonly host: string; readonly useSsl: boolean } {
    const trimmed = endpoint.trim().replace(/\/+$/, "");
    const scheme = /^(https?):\/\/(.*)$/i.exec(trimmed);

    if (scheme === null) return { host: trimmed, useSsl: true };

    return { host: scheme[2] ?? "", useSsl: (scheme[1] ?? "").toLowerCase() === "https" };
}

/**
 * Voice. Two URLs that must not be the same one:
 *
 * `PublicUrl` is handed to clients as the RTC endpoint, so it is the public media host — which for a
 * Cloudflare-proxied instance is the second, grey-clouded subdomain, because the HTTP proxy carries
 * WebSockets and not the UDP that real-time media is. `CommandUrl` is what the server itself builds its
 * LiveKit API clients from; it stays on the compose network, where it does not depend on the public
 * name resolving from inside the box and does not leave the machine to manage a room.
 */
function callKitSettings(answers: Answers): Json {
    return {
        Sfu: {
            Region: SELF_HOSTED_REGION,
            PublicUrl: `wss://${mediaHost(answers)}`,
            CommandUrl: `http://${DEPLOYMENT.hosts.sfu}:${DEPLOYMENT.ports.sfu}`,
            Geo: { ln: 0, lt: 0 },
        },
    };
}

/**
 * The hostname clients reach media on.
 *
 * Only the Cloudflare-proxied shape carries a separate one, and only that shape needs one: everywhere
 * else the machine is reached directly and media rides the same name. Note what this cannot express —
 * a tunnelled instance with voice gets the tunnel's hostname, and media does not travel through a
 * tunnel. That combination has to be refused by the wizard, because by the time it reaches here there
 * is nowhere left to put the answer.
 */
function mediaHost(answers: Answers): string {
    const traffic = answers.traffic;

    return traffic.kind === "cloudflare-proxied" && traffic.voiceHost !== undefined
        ? traffic.voiceHost
        : answers.domain;
}

/* ------------------------------------------------------------------------------------------------
 * What goes in the secrets file.
 * ---------------------------------------------------------------------------------------------- */

interface SecretSpec extends SectionSpec {
    /**
     * `declared` — a feature owns this section, so the same gate the settings get applies: a role that
     * does not read it has no use for the key.
     *
     * `host` — nothing declares it. `TicketJwt`, `Transport` and `Totp` are read straight out of
     * `IConfiguration` by the code that needs them, so there is no declaration to gate on and they are
     * always written. They are also all published in `appsettings.json`, which is what makes writing
     * them non-optional. If one of them ever grows an owning feature, move it to `declared`.
     */
    readonly reach: "declared" | "host";
}

function secretSpecifications(
    answers: Answers,
    secrets: MintedSecrets,
    supplied: SuppliedSecrets | undefined,
): SecretSpec[] {
    const specs: SecretSpec[] = [
        {
            section: "Database",
            reach: "declared",
            // The whole connection string, not just the password: it is one setting, and splitting a
            // password out of it across two layers would need string surgery at read time.
            value: { ConnectionString: connectionString(secrets.databasePassword) },
        },
        {
            section: "Jwt",
            reach: "declared",
            value: {
                MachineSalt: secrets.jwtMachineSalt,
                CertificateBase64: {
                    privateKey: secrets.jwtSigning.privateKey,
                    publicKey: secrets.jwtSigning.publicKey,
                    password: "",
                },
                EncryptionBase64: {
                    PrivateKeyBase64: secrets.jwtEncryption.privateKeyBase64,
                    PublicKeyBase64: secrets.jwtEncryption.publicKeyBase64,
                },
            },
        },
        { section: "Storage", reach: "declared", value: storageCredentials(answers, secrets, supplied) },

        // Only the password. The shipped username is already `prom` and is not a secret; writing it
        // here would put a setting in the secrets file and start the erosion this file exists to stop.
        { section: "Metrics:BasicAuth", reach: "declared", value: { Password: secrets.metricsPassword } },

        /**
         * NATS, and it is not optional: `ArgonOrleansHosting` calls `AddNatsCtx()` on both the client and
         * the silo path, so every role dials it whatever else that role does. The shipped default is
         * `nats://localhost:4222`, which inside a role's own container is that container — so leaving
         * this unwritten does not produce a warning, it produces every role failing to reach a bus that
         * is running one hostname away.
         *
         * `host` reach because `ConnectionStrings` is the framework's own section and no feature declares
         * it, so a `conf.d` file carrying it would be rejected as a section its feature does not own.
         *
         * That puts a plain address in the 0o600 document, which is worth naming: that file is what
         * `ARGON_CONFIG_FILE` points at, not "the secrets", and it is already the home for every
         * host-level value nothing declares. A second unscoped document to keep addresses apart from
         * passwords would be a third layer to explain for no gain today.
         */
        {
            section: "ConnectionStrings",
            reach: "host",
            value: { nats: `nats://${DEPLOYMENT.hosts.nats}:${DEPLOYMENT.ports.nats}` },
        },

        { section: "TicketJwt", reach: "host", value: { Key: secrets.ticketKey } },
        { section: "Transport:Exchange", reach: "host", value: { HashKey: secrets.transportHashKey } },
        { section: "Totp", reach: "host", value: { SecretPart: secrets.totpSecretPart } },
    ];

    if (answers.voice)
        specs.push({
            section: "CallKit",
            reach: "declared",
            // The other half of `callKitSettings`. `SfuInstanceCfg` requires all of these, and it is
            // satisfied after the layers merge — which is the whole reason the split is possible.
            value: { Sfu: { ClientId: secrets.sfu.clientId, Secret: secrets.sfu.secret } },
        });

    return specs;
}

function connectionString(password: string): string {
    return [
        `Host=${DEPLOYMENT.hosts.postgres}`,
        `Port=${DEPLOYMENT.ports.postgres}`,
        `Username=${DEPLOYMENT.database.user}`,
        `Password=${password}`,
        `Database=${DEPLOYMENT.database.name}`,
        "ConnectionIdleLifetime=15",
        "ConnectionPruningInterval=10",
    ].join(";");
}

/**
 * Object-storage credentials.
 *
 * The bundled store gets minted keys, which the compose file hands it as its own credentials — the
 * same {@link MintedSecrets} both sides read. The operator's own S3 gets whatever they typed; when
 * they were not asked, a marker goes in rather than a blank, so `--validate-config` names the missing
 * value instead of the instance failing on its first upload.
 */
function storageCredentials(
    answers: Answers,
    secrets: MintedSecrets,
    supplied: SuppliedSecrets | undefined,
): Json {
    if (answers.storage.kind === "local")
        return { AccessKey: secrets.objectStorage.accessKey, SecretKey: secrets.objectStorage.secretKey };

    const operators = supplied?.objectStorage;

    return {
        AccessKey: operators?.accessKey ?? unset("object storage access key"),
        SecretKey: operators?.secretKey ?? unset("object storage secret key"),
    };
}

/* ------------------------------------------------------------------------------------------------
 * JSON, deterministically.
 * ---------------------------------------------------------------------------------------------- */

/**
 * Mutable on purpose: these trees are built key by key and never leave this module — what leaves is
 * the rendered string. A readonly variant would buy nothing and cost a cast at every merge.
 */
type Json = string | number | boolean | null | Json[] | JsonObject;

interface JsonObject {
    [key: string]: Json;
}

function isObject(value: Json | undefined): value is JsonObject {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Codepoint order, spelled out, because the default comparator sorts by stringified value. */
function compare(left: string, right: string): number {
    return left < right ? -1 : left > right ? 1 : 0;
}

/** `Kestrel:Argon` + `{…}` becomes `{ "Kestrel": { "Argon": {…} } }` — the shape a section has in a file. */
function nest(section: string, value: Json): JsonObject {
    const built = section.split(":").reverse().reduce<Json>((inner, key) => ({ [key]: inner }), value);

    // A section path always produces at least one wrapping object, so this holds by construction.
    return isObject(built) ? built : {};
}

function fileOf(files: Map<string, JsonObject>, feature: string): JsonObject {
    const existing = files.get(feature);

    if (existing !== undefined) return existing;

    const created: JsonObject = {};
    files.set(feature, created);

    return created;
}

/**
 * Merges one section tree into a file, throwing when two specifications set the same leaf.
 *
 * A throw rather than a last-writer-wins, because both sides of a collision are written by this file:
 * it is a mistake in {@link settingSpecifications} or {@link secretSpecifications}, not something an
 * operator can cause, and silently keeping one of the two values is how a setting gets changed in the
 * wrong place and stays that way.
 */
function merge(target: JsonObject, source: JsonObject, at = ""): void {
    for (const [key, value] of Object.entries(source)) {
        const path = at === "" ? key : `${at}:${key}`;
        const existing = target[key];

        if (isObject(value) && isObject(existing)) {
            merge(existing, value, path);
            continue;
        }

        if (existing !== undefined)
            throw new Error(`two specifications both set '${path}'; one of them is in the wrong place`);

        target[key] = value;
    }
}

/**
 * Sorts every key, so that two runs of the same answers produce the same bytes. Arrays keep their
 * order, which is meaningful in Argon's configuration — a list of ICE servers is a preference order.
 */
function canonical(value: Json): Json {
    if (Array.isArray(value)) return value.map(canonical);
    if (!isObject(value)) return value;

    const sorted: JsonObject = {};

    for (const [key, inner] of Object.entries(value).sort(([left], [right]) => compare(left, right)))
        sorted[key] = canonical(inner);

    return sorted;
}

/** Two-space indent and a trailing newline, matching the files already in `deploy/`. */
function render(value: JsonObject): string {
    return `${JSON.stringify(canonical(value), null, 2)}\n`;
}
