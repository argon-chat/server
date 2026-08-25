import { describe, expect, test } from "bun:test";
import type { Answers, GeneratedFile, RoleDetail } from "./model";
import {
    COMPOSE_NETWORK_SUBNET,
    DATABASE_PROVIDER,
    DEPLOYMENT,
    generate,
    mintSecrets,
    splitEndpoint,
    type MintedSecrets,
} from "./generate";
import { NETWORK_SUBNET } from "./compose";

/* ------------------------------------------------------------------------------------------------
 * Fixtures.
 *
 * The feature names and section paths below are the real ones — `database` owns `Database` and
 * `Database:Regions`, `file-storage` owns `Storage` and `Storage:Limits`, and so on. They are copied
 * from what the server declares rather than invented, because these tests are only meaningful if the
 * ownership they assert against is the ownership `--explain` would report.
 * ---------------------------------------------------------------------------------------------- */

function role(id: string, features: Record<string, readonly string[]>): RoleDetail {
    return { id, features: Object.entries(features).map(([name, sections]) => ({ name, sections })) };
}

/**
 * Feature ownership as `--explain entrypoint` reports it, not as it reads well here.
 *
 * `forwarded-headers` is on the list because every client role now declares it: the role sits behind
 * Traefik on a self-hosted box and behind an ingress in a cluster, and without it every request appears
 * to arrive from that hop over plain http. It used to belong to `aegis` alone, which is why the section
 * below was once generated only when `aegis` was among the chosen roles — a default install has no
 * `aegis`, so the trusted-proxy list it needs was never written at all.
 */
const ENTRYPOINT = role("entrypoint", {
    kestrel: ["Kestrel:Argon"],
    "forwarded-headers": ["ForwardedHeaders"],
    websockets: ["WebSockets"],
    jwt: ["Jwt"],
    cache: ["Redis"],
    telemetry: ["Metrics:BasicAuth"],
});

const CORE = role("core", {
    database: ["Database", "Database:Regions"],
    cache: ["Redis"],
    messages: ["Messages"],
});

const MEDIA = role("media", { "file-storage": ["Storage", "Storage:Limits"] });

const VOICE = role("voice", { sfu: ["CallKit"] });

const AEGIS = role("aegis", {
    "forwarded-headers": ["ForwardedHeaders"],
    jwt: ["Jwt"],
});

const ALL_ROLES = [ENTRYPOINT, AEGIS, CORE, MEDIA, VOICE];

function answers(overrides: Partial<Answers> = {}): Answers {
    return {
        domain: "chat.example.org",
        serverVersion: "1.4.0",
        roles: ["entrypoint", "aegis", "core", "media", "voice"],
        storage: { kind: "local" },
        traffic: { kind: "lets-encrypt" },
        voice: true,
        ...overrides,
    };
}

/** Recognisable in a way random hex is not, so a leak test can assert on the value and not on entropy. */
function markedSecrets(): MintedSecrets {
    return {
        databasePassword: "MARKED-database-password",
        jwtMachineSalt: "MARKED-machine-salt",
        jwtSigning: { privateKey: "MARKED-signing-private", publicKey: "MARKED-signing-public" },
        jwtEncryption: { privateKeyBase64: "MARKED-encryption-private", publicKeyBase64: "MARKED-encryption-public" },
        ticketKey: "MARKED-ticket-key",
        transportHashKey: "MARKED-transport-hash",
        totpSecretPart: "MARKED-totp-part",
        metricsPassword: "MARKED-metrics-password",
        objectStorage: { accessKey: "MARKED-storage-access", secretKey: "MARKED-storage-secret" },
        sfu: { clientId: "MARKED-sfu-client", secret: "MARKED-sfu-secret" },
    };
}

/** Every string in a minted bundle, however deeply it is nested. */
function everyValue(source: object): string[] {
    return Object.values(source).flatMap((value) =>
        typeof value === "string" ? [value] : typeof value === "object" && value !== null ? everyValue(value) : [],
    );
}

function fileNamed(files: readonly GeneratedFile[], path: string): GeneratedFile {
    const found = files.find((file) => file.path === path);

    if (found === undefined) throw new Error(`no file '${path}' among ${files.map((f) => f.path).join(", ")}`);

    return found;
}

function parsed(files: readonly GeneratedFile[], path: string): Record<string, any> {
    return JSON.parse(fileNamed(files, path).contents) as Record<string, any>;
}

const confD = (feature: string) => `${DEPLOYMENT.confD}/${feature}.json`;

const settingsFiles = (files: readonly GeneratedFile[]) =>
    files.filter((file) => file.path !== DEPLOYMENT.secretsFile);

describe("the generated configuration", () => {
    /**
     * The single most likely thing to be quietly broken by a later edit, so it is pinned by value.
     *
     * `Database:Provider` is read with `Enum.TryParse` against `DatabaseProviderKind`, and unset *or
     * unparsable* both resolve to CockroachDB. The boot path then probes the server, finds PostgreSQL,
     * and refuses to start — so writing nothing and writing `Postgres` fail identically, and the error
     * points at the database rather than at this file.
     */
    test("the database provider is written, and is spelled exactly PostgreSql", () => {
        const files = generate(answers(), ALL_ROLES);

        expect(parsed(files, confD("database"))["Database"]["Provider"]).toBe("PostgreSql");
        expect(DATABASE_PROVIDER).toBe("PostgreSql");
        expect(DATABASE_PROVIDER).not.toBe("Postgres");
    });

    /**
     * The property: no generated password, key or token reaches a file that is not the secrets file.
     *
     * It is what makes the secrets file replaceable — when Vault returns as a configuration layer,
     * swapping one file for one source is only a change if every secret is in that one file. A single
     * value that leaked into `conf.d` turns that swap into a search.
     */
    test("no secret reaches a conf.d file", () => {
        const secrets = markedSecrets();
        const files = generate(answers(), ALL_ROLES, { secrets });

        for (const file of settingsFiles(files))
            for (const value of everyValue(secrets)) expect(file.contents).not.toContain(value);
    });

    /** And the same values are in fact somewhere, so the test above cannot pass by generating nothing. */
    test("every minted secret the chosen roles need is in the secrets file", () => {
        const secrets = markedSecrets();
        const files = generate(answers(), ALL_ROLES, { secrets });
        const contents = fileNamed(files, DEPLOYMENT.secretsFile).contents;

        for (const value of everyValue(secrets)) expect(contents).toContain(value);
    });

    /**
     * The difference between the two kinds of file this writes is one digit, which is exactly why it is
     * asserted rather than reviewed.
     */
    test("the secrets file is 0o600 and nothing else is", () => {
        const files = generate(answers(), ALL_ROLES);

        expect(fileNamed(files, DEPLOYMENT.secretsFile).mode).toBe(0o600);

        for (const file of settingsFiles(files)) expect(file.mode).not.toBe(0o600);
    });

    /**
     * Same answers, same bytes — except for the one file that is supposed to be different every time.
     *
     * Run with real entropy on both sides on purpose: a stubbed mint would make the first half of this
     * true for the wrong reason, and the second half unprovable.
     */
    test("two runs of the same answers differ only in the secrets", () => {
        const input = answers();
        const first = generate(input, ALL_ROLES);
        const second = generate(input, ALL_ROLES);

        expect(first.map((file) => file.path)).toEqual(second.map((file) => file.path));

        for (const [index, file] of first.entries()) {
            const other = second[index]!;

            if (file.path === DEPLOYMENT.secretsFile) expect(file.contents).not.toBe(other.contents);
            else expect(file.contents).toBe(other.contents);
        }
    });

    /**
     * A section nobody reads is either a typo or configuration that used to matter. Written anyway, it
     * becomes something a later reader takes for intent — and it is reported by the server as a
     * diagnostic rather than applied, so it is not even inert.
     */
    test("a section no chosen role reads is not written", () => {
        // Only `core`: no kestrel, no jwt, no storage, no sfu, no telemetry.
        const files = generate(answers({ roles: ["core"] }), ALL_ROLES);

        expect(files.map((file) => file.path)).toEqual([
            confD("cache"),
            confD("database"),
            DEPLOYMENT.secretsFile,
        ]);

        // Asserted as the whole set of top-level keys rather than as absences, because a substring
        // search for a section name finds it inside another one — `Storage` lives in `OrleansStorage`.
        expect(Object.keys(parsed(files, confD("cache")))).toEqual(["Redis"]);
        expect(Object.keys(parsed(files, confD("database")))).toEqual(["Database"]);

        // Storage, the listener, voice and the metrics credential are all gone from the secrets too:
        // what remains is the database, the bus address, and the three sections no feature declares.
        //
        // `ConnectionStrings` is here for every role and not only for the chosen ones, deliberately:
        // AddNatsCtx runs on both the client and the silo path, so a role that reads nothing else still
        // dials the bus, and a default of localhost inside a container points it at itself.
        expect(Object.keys(parsed(files, DEPLOYMENT.secretsFile))).toEqual([
            "ConnectionStrings",
            "Database",
            "TicketJwt",
            "Totp",
            "Transport",
        ]);
    });

    /**
     * `--explain` is cheap to run over every role once and filter afterwards, so the detail list is
     * expected to carry roles the operator did not choose. Trusting it whole would write the media and
     * voice configuration onto an instance running neither.
     */
    test("a role that was explained but not chosen does not widen the output", () => {
        const secrets = markedSecrets();
        const chosen = generate(answers({ roles: ["core"] }), ALL_ROLES, { secrets });
        const only = generate(answers({ roles: ["core"] }), [CORE], { secrets });

        expect(chosen.map((file) => file.contents)).toEqual(only.map((file) => file.contents));
    });

    /**
     * A feature with nothing to configure gets no file. `telemetry` is the live case: its only
     * generated value is the metrics password, which belongs in the secrets file, so an empty
     * `telemetry.json` would be a file that exists to say nothing.
     */
    test("a feature whose only generated value is a secret gets no conf.d file", () => {
        const files = generate(answers(), ALL_ROLES);

        expect(files.map((file) => file.path)).not.toContain(confD("telemetry"));
        expect(fileNamed(files, DEPLOYMENT.secretsFile).contents).toContain("BasicAuth");
    });

    /**
     * These six sections ship in `appsettings.json` with working values, in a public repository. An
     * instance that does not override them runs on cryptographic material anybody can read: the ticket
     * key that signs hub tickets, the salt behind every device binding, the transport exchange key, the
     * TOTP secret part, the signing key itself, and a metrics password of `12345678`.
     *
     * Two of them — `TicketJwt` and `Transport` — belong to no feature, so nothing in the ownership
     * data can make them appear. They are written unconditionally, and that is what this pins.
     */
    test("every secret Argon ships a published default for is overridden", () => {
        const document = parsed(generate(answers(), ALL_ROLES), DEPLOYMENT.secretsFile);

        expect(document["TicketJwt"]["Key"]).toBeString();
        expect(document["Transport"]["Exchange"]["HashKey"]).toBeString();
        expect(document["Totp"]["SecretPart"]).toBeString();
        expect(document["Metrics"]["BasicAuth"]["Password"]).toBeString();
        expect(document["Jwt"]["MachineSalt"]).toBeString();
        expect(document["Jwt"]["CertificateBase64"]["privateKey"]).toBeString();

        // The shipped values themselves, in case a later edit copies one in as a "default".
        const contents = fileNamed(generate(answers(), ALL_ROLES), DEPLOYMENT.secretsFile).contents;

        expect(contents).not.toContain("12345678");
        expect(contents).not.toContain("fgdsk39fj23jk0dg89u4ihjg8092o4gjhw8herg838i45hgosdklfuhbgkuw3");
    });

    /**
     * `JSON.parse` keeps a document's key order for keys that are not array indices, so reading the
     * keys off the parsed object reads them off the file. Sorting is per object, not per file, so a
     * nested one is checked too.
     */
    test("keys are sorted, so a diff between two versions shows what changed", () => {
        const files = generate(answers(), ALL_ROLES);

        for (const file of files) {
            const keys = Object.keys(JSON.parse(file.contents) as object);

            expect(keys).toEqual([...keys].sort());
        }

        const listener = Object.keys(parsed(files, confD("kestrel"))["Kestrel"]["Argon"] as object);

        expect(listener).toEqual([...listener].sort());
    });
});

describe("the storage answer", () => {
    test("local storage points at the bundled store, in plaintext on the compose network", () => {
        const files = generate(answers({ storage: { kind: "local" } }), ALL_ROLES);
        const storage = parsed(files, confD("file-storage"))["Storage"];

        expect(storage["Endpoint"]).toBe(`${DEPLOYMENT.hosts.storage}:${DEPLOYMENT.ports.storage}`);
        expect(storage["UseSsl"]).toBe(false);
        expect(storage["BucketName"]).toBe(DEPLOYMENT.buckets.content);
        expect(storage["ExportBucketName"]).toBe(DEPLOYMENT.buckets.exports);
    });

    /**
     * The operator types a URL, because everything else takes one; Argon wants a bare host and carries
     * the scheme in `UseSsl`. Written through unchanged, `S3ClientPool` produces `http://https://…` and
     * every upload fails in a way that reads as a network fault.
     */
    test("an endpoint the operator typed as a URL is written as a host and a flag", () => {
        const files = generate(
            answers({ storage: { kind: "s3", endpoint: "https://s3.eu-central-1.amazonaws.com", bucket: "argon-prod", region: "eu-central-1" } }),
            ALL_ROLES,
        );
        const storage = parsed(files, confD("file-storage"))["Storage"];

        expect(storage["Endpoint"]).toBe("s3.eu-central-1.amazonaws.com");
        expect(storage["BucketName"]).toBe("argon-prod");
        expect(storage["Region"]).toBe("eu-central-1");
        expect(storage["UseSsl"]).toBe(true);
    });

    /**
     * Exports are a whole account's data and are meant to expire in about two days. A lifecycle rule
     * that short on the bucket holding avatars and attachments would delete them, so the two cannot
     * share — even though it means naming a bucket the operator was never asked about.
     */
    test("exports get a bucket of their own", () => {
        const files = generate(
            answers({ storage: { kind: "s3", endpoint: "https://s3.example.com", bucket: "argon-prod" } }),
            ALL_ROLES,
        );

        expect(parsed(files, confD("file-storage"))["Storage"]["ExportBucketName"]).toBe("argon-prod-exports");
    });

    /**
     * A blank credential reads as "configured, and empty" and fails at the first upload with a
     * permissions error nobody connects back to setup. A marker fails `--validate-config` instead, and
     * names the value.
     */
    test("external storage with no credentials leaves a marker rather than a blank", () => {
        const files = generate(
            answers({ storage: { kind: "s3", endpoint: "https://s3.example.com", bucket: "argon-prod" } }),
            ALL_ROLES,
        );
        const storage = parsed(files, DEPLOYMENT.secretsFile)["Storage"];

        expect(storage["AccessKey"]).toContain("<<SET:");
        expect(storage["SecretKey"]).toContain("<<SET:");
    });

    test("external storage uses the credentials the operator gave", () => {
        const files = generate(
            answers({ storage: { kind: "s3", endpoint: "https://s3.example.com", bucket: "argon-prod" } }),
            ALL_ROLES,
            { supplied: { objectStorage: { accessKey: "AKIAEXAMPLE", secretKey: "wJalrEXAMPLEKEY" } } },
        );
        const storage = parsed(files, DEPLOYMENT.secretsFile)["Storage"];

        expect(storage["AccessKey"]).toBe("AKIAEXAMPLE");
        expect(storage["SecretKey"]).toBe("wJalrEXAMPLEKEY");
    });

    /** Guessing "not TLS" for an endpoint the operator wrote without a scheme downgrades their traffic. */
    test("an endpoint is treated as plaintext only when it says http", () => {
        expect(splitEndpoint("http://minio.internal:9000")).toEqual({ host: "minio.internal:9000", useSsl: false });
        expect(splitEndpoint("HTTP://minio.internal:9000")).toEqual({ host: "minio.internal:9000", useSsl: false });
        expect(splitEndpoint("https://s3.example.com/")).toEqual({ host: "s3.example.com", useSsl: true });
        expect(splitEndpoint("  s3.example.com  ")).toEqual({ host: "s3.example.com", useSsl: true });
    });
});

describe("the traffic answer", () => {
    /**
     * No role holds a certificate, whatever the traffic shape.
     *
     * Traefik terminates for all four, so the private key exists in one container instead of every one
     * of them. Asserted across the shapes rather than for one, because this used to branch and the
     * branch is exactly what has to not come back.
     *
     * The failure it prevents is silent. `WebFeatures` treats TLS as configured only when the flag is
     * set AND both files exist, so naming a certificate that is not mounted does not error — it binds
     * the named port in the clear. Ask for 8443 with TLS, get 8443 without it, while Traefik dials
     * 8080, and the whole instance answers 502 with nothing in any log explaining it.
     */
    test.each([
        ["own-certificate"],
        ["cloudflare-proxied"],
        ["lets-encrypt"],
        ["cloudflare-tunnel"],
    ] as const)("%s gets a plaintext listener on the port the edge dials", (kind) => {
        const files = generate(answers({ traffic: { kind } as never }), ALL_ROLES);
        const kestrel = parsed(files, confD("kestrel"))["Kestrel"]["Argon"];

        expect(kestrel["UseFileCertificate"]).toBe(false);
        expect(kestrel["Port"]).toBe(DEPLOYMENT.ports.plaintext);
        expect(kestrel["CertificatePath"]).toBeUndefined();
    });

    /**
     * Cloudflare closes idle WebSockets and does not publish the number.
     *
     * Its own documentation says only that it happens, that Enterprise accounts can negotiate their own
     * timeout, and that the answer is a heartbeat — so the number cannot be matched, only undercut.
     * Argon's default is a minute, which has little room in it.
     *
     * Written on that path and nowhere else: the other three shapes have nothing between client and edge
     * that would time a socket out, and a heartbeat they do not need is traffic on somebody's mobile
     * data. The failure it prevents is a chat that "drops sometimes", with nothing in any log.
     */
    test("the hub pings twice as often behind Cloudflare's proxy, and only there", () => {
        const proxied = generate(answers({ traffic: { kind: "cloudflare-proxied" } }), ALL_ROLES);

        expect(parsed(proxied, confD("websockets"))["WebSockets"]).toEqual({ KeepAliveInterval: "00:00:30" });

        for (const kind of ["own-certificate", "lets-encrypt", "cloudflare-tunnel"] as const) {
            const files = generate(answers({ traffic: { kind } }), ALL_ROLES);

            expect([kind, files.some((file) => file.path === confD("websockets"))]).toEqual([kind, false]);
        }
    });

    /**
     * The proxy has to be believed, and only the proxy.
     *
     * The shipped default is a pair of Kubernetes ranges that match nothing on a compose bridge, so
     * without this every "was this request secure" answers no: no secure cookies, redirect URIs built
     * as http, metadata requiring HTTPS refusing. The instance behaves as though it were served in the
     * clear, which from where it sits is true — TLS ended one hop earlier.
     */
    test("the compose network is trusted as a proxy, and nothing wider", () => {
        const files = generate(answers({}), ALL_ROLES);
        const forwarded = parsed(files, confD("forwarded-headers"))["ForwardedHeaders"] as {
            KnownNetworks: string[];
        };

        expect(forwarded.KnownNetworks).toEqual([COMPOSE_NETWORK_SUBNET]);
        expect(COMPOSE_NETWORK_SUBNET).toBe(NETWORK_SUBNET);
    });

    /**
     * The tunnel makes the outbound connection and carries the TLS, so the origin needs no certificate
     * at all. Naming one it does not have is the failure the Kestrel options warn about: the process
     * starts, binds nothing on TLS, and looks healthy to everything except a client.
     */
    test("a tunnel gets no certificate, because the tunnel carries the TLS", () => {
        const files = generate(answers({ traffic: { kind: "cloudflare-tunnel" } }), ALL_ROLES);
        const kestrel = parsed(files, confD("kestrel"))["Kestrel"]["Argon"];

        expect(kestrel["UseFileCertificate"]).toBe(false);
        expect(kestrel["CertificatePath"]).toBeUndefined();
        expect(kestrel["Port"]).toBe(DEPLOYMENT.ports.plaintext);
    });
});

describe("the voice answer", () => {
    /**
     * Cloudflare's HTTP proxy carries HTTP and WebSockets and does not carry the UDP that real-time
     * media is. When the operator published a second, grey-clouded subdomain for media, the endpoint
     * handed to clients has to be that one — sending them to the proxied hostname is how voice ends up
     * silently dead on an instance whose chat works.
     */
    test("clients are pointed at the media host, not at the proxied one", () => {
        const files = generate(
            answers({ traffic: { kind: "cloudflare-proxied", voiceHost: "voice.example.org" } }),
            ALL_ROLES,
        );

        expect(parsed(files, confD("sfu"))["CallKit"]["Sfu"]["PublicUrl"]).toBe("wss://voice.example.org");
    });

    /**
     * The server's own LiveKit API client is a different URL from the one clients get: it manages rooms
     * over the compose network, where it does not depend on the public name resolving from inside the
     * box and never leaves the machine.
     */
    test("the server reaches the SFU internally", () => {
        const files = generate(answers(), ALL_ROLES);
        const sfu = parsed(files, confD("sfu"))["CallKit"]["Sfu"];

        expect(sfu["CommandUrl"]).toBe(`http://${DEPLOYMENT.hosts.sfu}:${DEPLOYMENT.ports.sfu}`);
        expect(sfu["PublicUrl"]).toBe("wss://chat.example.org");
    });

    test("no voice means no SFU configuration and no SFU credentials", () => {
        const files = generate(answers({ voice: false }), ALL_ROLES);

        expect(files.map((file) => file.path)).not.toContain(confD("sfu"));
        expect(fileNamed(files, DEPLOYMENT.secretsFile).contents).not.toContain("CallKit");
    });
});

describe("minting", () => {
    /**
     * Not an entropy test — it cannot be one — but it does catch the shape of the mistake: a mint that
     * returns a constant, or one value reused for two purposes because a field was copied and the
     * source not changed.
     */
    test("no two secrets are the same, and no two mints agree", () => {
        const first = everyValue(mintSecrets());
        const second = everyValue(mintSecrets());

        expect(new Set(first).size).toBe(first.length);
        expect(first.some((value, index) => value === second[index])).toBe(false);
    });

    /**
     * The signing pair is base64 of DER — SEC1 for the private half, SPKI for the public — which is
     * what the loader accepts and what the shipped value is already in. The prefixes are the DER header
     * for each, so they are a cheap way to notice an encoding that changed underneath.
     */
    test("the signing key is in the encoding the loader reads", () => {
        const secrets = mintSecrets();

        expect(secrets.jwtSigning.privateKey.startsWith("MHcCAQEEI")).toBe(true);
        expect(secrets.jwtSigning.publicKey.startsWith("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE")).toBe(true);
        expect(secrets.jwtEncryption.publicKeyBase64.startsWith("MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8A")).toBe(true);
    });

    /**
     * Whoever writes the compose file needs the same database password as `POSTGRES_PASSWORD` and the
     * same object-storage keys as the bundled store's credentials. Passing one bundle to both is what
     * makes that possible; reading them back out of the generated JSON would work until a rename.
     */
    test("a caller can mint once and have the same values used", () => {
        const secrets = mintSecrets();
        const files = generate(answers(), ALL_ROLES, { secrets });

        expect(fileNamed(files, DEPLOYMENT.secretsFile).contents).toContain(secrets.databasePassword);
        expect(fileNamed(files, DEPLOYMENT.secretsFile).contents).toContain(secrets.objectStorage.secretKey);
    });
});

/**
 * The randomness source, pinned by reading the source rather than the output.
 *
 * Quality of randomness cannot be asserted from a sample — a stream of `Math.random` passes every test
 * that a stream of `randomBytes` passes, because the tests can only see that the values differ. So this
 * checks the one thing that is checkable: which function was called.
 *
 * It exists because the swap was tried. Replacing `randomBytes` with `Math.random` in the generator left
 * all twenty-four tests green, which means the comment above it was the only thing standing between this
 * installer and predictable database passwords. A comment is not a guard.
 *
 * The same technique is used in the server's own suite, where a scan over migration files is what keeps
 * CockroachDB-only syntax out of them.
 */
describe("the secrets are generated the only acceptable way", () => {
    test("nothing in the generator reaches for Math.random", async () => {
        const source = await Bun.file(new URL("./generate.ts", import.meta.url)).text();

        // Comments are stripped first: this file discusses Math.random on purpose, and a scan that could
        // not tell prose from a call would either fail on the explanation or force it to be deleted.
        const code = source
            .replace(/\/\*[\s\S]*?\*\//g, "")
            .replace(/^[ \t]*\/\/.*$/gm, "");

        expect(code).not.toContain("Math.random");
        expect(code).toContain("randomBytes");
    });
});

/**
 * The redirect has somewhere to land.
 *
 * Argon never serves file bytes: `/files/{id}` and `/files/k/{key}` both end in a 302 built from
 * `Storage:Cdn:Default:BaseUrl`. Left unwritten it is the empty string, the redirect lands on `/{key}`,
 * and every avatar and attachment 404s while the rest of the instance looks healthy — which is why this
 * is asserted rather than trusted to the reviewer who found it missing.
 *
 * Asserted for both branches because they resolve to different origins for the same reason: the bundled
 * store is only reachable through the instance's published path, and the operator's own bucket is
 * reachable at their endpoint.
 */
describe("where a file redirect goes", () => {
    test("the bundled store is published under the instance's own name", () => {
        const files = generate(answers({ storage: { kind: "local" } }), ALL_ROLES);
        const storage = parsed(files, confD("file-storage"))["Storage"] as any;

        expect(storage.Cdn.Default.BaseUrl).toBe("https://chat.example.org/s3");
        expect(storage.Cdn.Default.BaseUrl).not.toBe("");

        // The bucket travels as the prefix: the store answers on {bucket}/{key} and the redirect has to
        // carry both halves.
        expect(storage.Cdn.Default.PathPrefix).toBe(DEPLOYMENT.buckets.content);
    });

    test("an operator's own bucket is its own origin", () => {
        const files = generate(
            answers({ storage: { kind: "s3", endpoint: "https://s3.example.com", bucket: "theirs" } }),
            ALL_ROLES,
        );

        const storage = parsed(files, confD("file-storage"))["Storage"] as any;

        expect(storage.Cdn.Default.BaseUrl).toBe("https://s3.example.com");
        expect(storage.Cdn.Default.PathPrefix).toBe("theirs");
    });

    /**
     * The two are not the same value and swapping them is silent: `PublicBaseUrl` is this instance and
     * `Default.BaseUrl` is the origin, so a swap makes the API redirect to itself and loop.
     */
    test("the instance's own name is not the file origin", () => {
        const files = generate(answers({ storage: { kind: "local" } }), ALL_ROLES);
        const cdn = (parsed(files, confD("file-storage"))["Storage"] as any).Cdn;

        expect(cdn.PublicBaseUrl).not.toBe(cdn.Default.BaseUrl);
    });
});
