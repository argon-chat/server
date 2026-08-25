import { describe, expect, test } from "bun:test";
import { createServer as createNetServer, type Server as NetServer } from "node:net";
import { createServer as createTlsServer } from "node:tls";
import { composeProject } from "../compose";
import type { MintedSecrets } from "../generate";
import type { Answers } from "../model";
import {
    EDGE_HOST,
    EDGE_PORT,
    WARNING_DAYS,
    coverageOf,
    edgeProbe,
    inspectCertificates,
    presentedCertificate,
    readCertificate,
    servedNames,
    type CertificateReport,
    type PeerCertificate,
    type ProbeTarget,
    type TlsProbe,
} from "./certificate";

/* ------------------------------------------------------------------------------------------------
 * Fixtures.
 *
 * Nothing here opens a socket, up to the last section. The probe is handed in, so every assertion about
 * what this module *decides* is about a certificate rather than about whether a daemon happened to be
 * running.
 *
 * The last section is the exception, and it has to be. `edgeProbe` is the half that touches the network,
 * and the invariants its doc block calls load-bearing — no ALPN offered, verification deliberately off,
 * a handshake deadline — are not decisions any injected probe could hold. Those run against a loopback
 * server started by the test itself, which needs no daemon and no running edge, so it is not the kind of
 * test that gets skipped the first time CI has neither.
 * ---------------------------------------------------------------------------------------------- */

const DAY = 86_400_000;

/** A fixed instant with no milliseconds in it, because OpenSSL's date format carries none. */
const NOW = new Date("2026-08-25T12:00:00.000Z");

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

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

/**
 * A date as OpenSSL prints it — `Aug 25 12:00:00 2026 GMT`.
 *
 * Every fixture below goes through this rather than through an ISO string, so the parsing under test is
 * the parsing that will actually run. An ISO fixture would pass on a module that could not read a
 * single real certificate.
 */
function openssl(date: Date): string {
    const day = String(date.getUTCDate()).padStart(2, " ");
    const time = [date.getUTCHours(), date.getUTCMinutes(), date.getUTCSeconds()]
        .map((part) => String(part).padStart(2, "0"))
        .join(":");

    return `${MONTHS[date.getUTCMonth()]!} ${day} ${time} ${date.getUTCFullYear()} GMT`;
}

interface PeerOverrides {
    readonly notBefore?: Date;
    readonly notAfter?: Date;
    readonly names?: string;
    readonly subject?: unknown;
    readonly issuer?: unknown;

    /** The chain above the leaf, as `getPeerCertificate(true)` links it: one object, pointing at the next. */
    readonly issuerCertificate?: unknown;
}

/** What a handshake produced, in `getPeerCertificate()`'s shape. */
function peer(overrides: PeerOverrides = {}): PeerCertificate {
    return {
        subject: overrides.subject ?? { CN: "chat.example.org" },
        issuer: overrides.issuer ?? { C: "US", O: "Let's Encrypt", CN: "R11" },
        valid_from: openssl(overrides.notBefore ?? new Date(NOW.getTime() - 30 * DAY)),
        valid_to: openssl(overrides.notAfter ?? new Date(NOW.getTime() + 60 * DAY)),
        subjectaltname: overrides.names ?? "DNS:chat.example.org",
        ...(overrides.issuerCertificate === undefined ? {} : { issuerCertificate: overrides.issuerCertificate }),
    };
}

/** One certificate above the leaf, in the same shape the leaf arrives in, because that is how Node sends it. */
function issuerCert(commonName: string, notAfter: Date, above?: unknown): PeerCertificate {
    return {
        subject: { CN: commonName, O: "Example Trust" },
        issuer: { CN: commonName === "Example Root" ? commonName : "Example Root" },
        valid_from: openssl(new Date(NOW.getTime() - 400 * DAY)),
        valid_to: openssl(notAfter),
        ...(above === undefined ? {} : { issuerCertificate: above }),
    };
}

interface Probed {
    readonly probe: TlsProbe;
    readonly targets: ProbeTarget[];
}

/** A probe that answers per SNI, and remembers what it was asked. */
function probing(answersByName: Readonly<Record<string, PeerCertificate | Error>>): Probed {
    const targets: ProbeTarget[] = [];

    return {
        targets,
        probe: async (target) => {
            targets.push(target);

            const found = answersByName[target.servername];

            if (found === undefined) throw new Error(`no fixture for ${target.servername}`);
            if (found instanceof Error) throw found;

            return found;
        },
    };
}

/** One report, narrowed to the variant that carries a certificate. Fails loudly rather than casting. */
function judged(report: CertificateReport | undefined): Extract<CertificateReport, { readonly certificate: unknown }> {
    if (report === undefined) throw new Error("no report");
    if (report.verdict === "not-applicable" || report.verdict === "unreadable")
        throw new Error(`expected a judged certificate, got ${report.verdict}: ${report.why}`);

    return report;
}

/* ------------------------------------------------------------------------------------------------
 * Which names are served.
 * ---------------------------------------------------------------------------------------------- */

describe("what the front door is asked for", () => {
    test("the operator's own certificate is theirs to renew", () => {
        expect(servedNames(answers())).toEqual([
            { host: "chat.example.org", purpose: "instance", terminated: true, renewal: "operator" },
        ]);
    });

    test("Let's Encrypt is Traefik's to renew, which is a different warning", () => {
        const [name] = servedNames(answers({ traffic: { kind: "lets-encrypt" } }));

        expect(name?.terminated).toBe(true);
        expect(name?.terminated === true ? name.renewal : undefined).toBe("traefik");
    });

    /**
     * §5: a Cloudflare instance with voice published directly runs two certificates at once, with
     * different expiry dates, and "the panel has to show both". The media one is the certificate nobody
     * thinks about, which makes it the one that lapses.
     */
    test("voice on a media subdomain is a second certificate, reported beside the first", () => {
        const names = servedNames(
            answers({ traffic: { kind: "cloudflare-proxied", voiceHost: "media.example.org" }, voice: true }),
        );

        // Whole objects rather than host-and-purpose pairs, because `renewal` is the field this path
        // argues hardest about and the one a pair comparison leaves free to change. Both are the
        // operator's: an Origin CA certificate lasts years and nothing in §5 can renew it, since §5 takes
        // no API token — so calling either of them Traefik's would hand the panel the fourteen-day notice
        // meant for a renewal that retries, on two certificates where nothing will ever retry.
        expect(names).toEqual([
            { host: "chat.example.org", purpose: "instance", terminated: true, renewal: "operator" },
            { host: "media.example.org", purpose: "media", terminated: true, renewal: "operator" },
        ]);
    });

    test("a media subdomain with voice switched off is not served, so it is not reported", () => {
        const names = servedNames(
            answers({ traffic: { kind: "cloudflare-proxied", voiceHost: "media.example.org" }, voice: false }),
        );

        expect(names).toHaveLength(1);
    });

    test("proxied without a media subdomain is one name", () => {
        expect(servedNames(answers({ traffic: { kind: "cloudflare-proxied" }, voice: true }))).toHaveLength(1);
    });

    test("the tunnel terminates nothing here, and says so rather than reporting nothing", () => {
        const [name] = servedNames(answers({ traffic: { kind: "cloudflare-tunnel" } }));

        expect(name?.terminated).toBe(false);
        expect(name?.terminated === false ? name.why : "").toContain("Cloudflare");
    });

    /**
     * A shape this has never heard of must stop rather than produce an empty list — an empty list is
     * indistinguishable from a healthy tunnel, and "nothing to warn about" is the exact silence §5 is
     * trying to prevent.
     */
    test("an unknown traffic shape is an error, not an empty list", () => {
        const strange = { domain: "chat.example.org", traffic: { kind: "carrier-pigeon" } } as unknown as Answers;

        expect(() => servedNames(strange)).toThrow(/carrier-pigeon/);
    });
});

/* ------------------------------------------------------------------------------------------------
 * Agreement with the compose project.
 *
 * The two constants this dials with are copies of things `compose.ts` decides privately. These are the
 * tests that turn a rename there into a failure here rather than into a panel that reports every
 * certificate unreadable.
 * ---------------------------------------------------------------------------------------------- */

describe("the address dialled is the address compose built", () => {
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

    const built = composeProject(answers({ roles: ["entrypoint"] }), markedSecrets(), {
        installRoot: "/opt/argon",
        tls: { certificatePath: "/etc/argon/tls.crt", keyPath: "/etc/argon/tls.key" },
    });

    test("the edge is a compose service by that name, and it listens on that port", () => {
        const document = JSON.parse(built.document) as {
            services: Record<string, { ports?: string[] } | undefined>;
        };

        const edge = document.services[EDGE_HOST];

        expect(edge).toBeDefined();
        expect(edge?.ports?.some((mapping) => mapping.endsWith(`:${EDGE_PORT}`))).toBe(true);
    });

    /**
     * The media host this reports is derived here and privately by `compose.ts`, so the two can drift.
     * The router that serves it is the thing that decides which certificate the handshake returns.
     */
    test("the media name reported is the name the media router answers", () => {
        const proxied = answers({ traffic: { kind: "cloudflare-proxied", voiceHost: "media.example.org" }, voice: true });

        const project = composeProject(proxied, markedSecrets(), {
            installRoot: "/opt/argon",
            tls: { certificatePath: "/etc/argon/tls.crt", keyPath: "/etc/argon/tls.key" },
            voiceTls: { certificatePath: "/etc/argon/voice.crt", keyPath: "/etc/argon/voice.key" },
        });

        const dynamic = project.files.find((file) => file.path.endsWith("dynamic.yml"));
        const routing = JSON.parse(dynamic?.contents ?? "{}") as {
            http?: { routers?: Record<string, { rule?: string } | undefined> };
        };

        const media = servedNames(proxied).find((name) => name.purpose === "media");

        expect(media?.host).toBeDefined();
        expect(routing.http?.routers?.["media"]?.rule).toContain(`Host(\`${media?.host}\`)`);
    });
});

/* ------------------------------------------------------------------------------------------------
 * Reading one certificate.
 * ---------------------------------------------------------------------------------------------- */

describe("what is read off the wire", () => {
    test("subject, issuer, both dates and the names", () => {
        const read = readCertificate(
            peer({
                notBefore: new Date("2026-06-01T09:30:00.000Z"),
                notAfter: new Date("2026-11-14T09:30:00.000Z"),
                names: "DNS:chat.example.org, DNS:*.chat.example.org",
            }),
        );

        expect(read.subject).toEqual({ commonName: "chat.example.org" });
        expect(read.issuer).toEqual({ commonName: "R11", organization: "Let's Encrypt" });
        expect(read.notBefore.toISOString()).toBe("2026-06-01T09:30:00.000Z");
        expect(read.notAfter.toISOString()).toBe("2026-11-14T09:30:00.000Z");
        expect(read.names).toEqual(["chat.example.org", "*.chat.example.org"]);
    });

    test("a certificate with neither attribute is an empty party rather than an invented one", () => {
        const read = readCertificate({ ...peer(), subject: {}, issuer: undefined });

        expect(read.subject).toEqual({});
        expect(read.issuer).toEqual({});
    });

    /** Node gives back an array when a distinguished name repeated an attribute. */
    test("a repeated attribute is taken, not dropped and not stringified", () => {
        expect(readCertificate(peer({ subject: { CN: ["chat.example.org", "other"], O: [] } })).subject).toEqual({
            commonName: "chat.example.org",
        });
    });

    test("names are lowercased and the root's trailing dot is not a different name", () => {
        expect(readCertificate(peer({ names: "DNS:Chat.Example.ORG." })).names).toEqual(["chat.example.org"]);
    });

    test("only DNS entries become names", () => {
        const read = readCertificate(
            peer({ names: "DNS:chat.example.org, IP Address:203.0.113.7, email:ops@example.org, URI:https://example.org" }),
        );

        expect(read.names).toEqual(["chat.example.org"]);
    });

    test("no subjectAltName at all reads as no names rather than throwing", () => {
        expect(readCertificate({ ...peer(), subjectaltname: undefined }).names).toEqual([]);
    });

    /**
     * The whole string below is *one* quoted SAN entry. A plain `split(",")` turns it into two, the
     * second of which is `DNS:chat.example.org` — and the certificate would then be reported as
     * covering a host it does not cover, which is the one mistake in this module that helps an
     * attacker rather than merely confusing an operator.
     */
    test("a quoted name carrying a comma stays one name", () => {
        const read = readCertificate({ ...peer(), subjectaltname: 'DNS:"evil.test, DNS:chat.example.org"' });

        expect(read.names).toEqual(["evil.test, dns:chat.example.org"]);
        expect(coverageOf(read, "chat.example.org").covers).toBe(false);
    });

    /**
     * The fixture above cannot tell an escape-aware tokenizer from one that ignores backslashes, because
     * it contains no backslash — and the difference is the whole attack. Node quotes a name containing a
     * quote and *escapes the quote inside it*, so the single SAN entry `evil", DNS:chat.example.org, x`
     * arrives as the string below. A tokenizer that honours the quoting but not the escape sees the
     * embedded `"` as the end of the name, splits there, and reads `DNS:chat.example.org` as a second
     * entry — full coverage reported for a certificate that covers only the name its holder chose. This
     * is the one mistake in this module that helps an attacker rather than merely confusing an operator,
     * so it is pinned with a fixture that actually contains the escape.
     */
    test("an escaped quote inside a quoted name does not end the name", () => {
        const read = readCertificate({ ...peer(), subjectaltname: 'DNS:"evil\\", DNS:chat.example.org, x"' });

        expect(read.names).toEqual(['evil", dns:chat.example.org, x']);
        expect(coverageOf(read, "chat.example.org").covers).toBe(false);
    });

    /** The other half of the escape: what is unescaped on the way out, so a name is not silently rewritten. */
    test("an escaped backslash reads back as one backslash", () => {
        expect(readCertificate({ ...peer(), subjectaltname: 'DNS:"a\\\\b"' }).names).toEqual(["a\\b"]);
    });

    test("a missing expiry is refused by name, because there is nothing there to judge", () => {
        expect(() => readCertificate({ ...peer(), valid_to: undefined })).toThrow(/valid_to/);
    });

    /**
     * An unparsed date becomes an Invalid Date, every comparison against which is false — so the
     * certificate would come out `valid` for ever, which is the failure this module exists to prevent.
     */
    test("an unreadable date is refused rather than becoming an Invalid Date", () => {
        expect(() => readCertificate({ ...peer(), valid_to: "whenever" })).toThrow(/not a date/);
    });
});

/* ------------------------------------------------------------------------------------------------
 * The chain the edge served.
 *
 * The leaf is not the only thing with an expiry date on it. Reading only the leaf reported a front door
 * dying tomorrow as `valid` with two hundred days left, which is the green tick on a dead instance this
 * module exists to prevent rather than to hand out.
 * ---------------------------------------------------------------------------------------------- */

describe("the chain above the certificate", () => {
    async function reportOn(overrides: PeerOverrides): Promise<ReturnType<typeof judged>> {
        const reports = await inspectCertificates(answers(), probing({ "chat.example.org": peer(overrides) }).probe, {
            now: NOW,
        });

        return judged(reports[0]);
    }

    test("the issuers the edge sent are carried, nearest first", () => {
        const root = issuerCert("Example Root", new Date(NOW.getTime() + 3_000 * DAY));
        const read = readCertificate(
            peer({ issuerCertificate: issuerCert("Example Intermediate", new Date(NOW.getTime() + 800 * DAY), root) }),
        );

        expect(read.issuers.map((issuer) => issuer.subject.commonName)).toEqual(["Example Intermediate", "Example Root"]);
        expect(read.issuers[0]?.notAfter.getTime()).toBe(new Date(NOW.getTime() + 800 * DAY).getTime());
    });

    /**
     * Serving nothing above the leaf is *correct* on two of §5's three terminating paths — an Origin CA
     * certificate goes up alone because Cloudflare holds the root, and so does a corporate one to clients
     * that already have the CA. So it is an empty list and not a complaint. Pinned because the obvious
     * "the edge sent no issuer" warning is the thing this deliberately does not do, and a future reader
     * would otherwise add it and get a warning on two working configurations.
     */
    test("an edge that sent only the leaf is not a complaint", async () => {
        const report = await reportOn({});

        expect(report.certificate.issuers).toEqual([]);
        expect(report.expiry.own).toBe(true);
        expect(report.verdict).toBe("valid");
    });

    /**
     * §5's own case for this, and the reason the leaf alone is not enough: the intermediate goes first
     * and takes the front door with it, whatever the certificate under it says. The verdict, the day
     * count and the name of what has to be replaced all have to follow the chain, because reissuing the
     * leaf — the only repair a date on the leaf suggests — fixes nothing here.
     */
    test("an issuer that expires first is what the report counts down to", async () => {
        const report = await reportOn({
            notAfter: new Date(NOW.getTime() + 200 * DAY),
            issuerCertificate: issuerCert("Stale Intermediate", new Date(NOW.getTime() + 20 * DAY)),
        });

        expect(report.verdict).toBe("expiring");
        expect(report.days).toBe(20);
        expect(report.expiry.own).toBe(false);
        expect(report.expiry.of.commonName).toBe("Stale Intermediate");
    });

    test("an issuer already past is expired, on a leaf good for another two hundred days", async () => {
        const report = await reportOn({
            notAfter: new Date(NOW.getTime() + 200 * DAY),
            issuerCertificate: issuerCert("Stale Intermediate", new Date(NOW.getTime() - 1 * DAY)),
        });

        expect(report.verdict).toBe("expired");
        expect(report.days).toBe(-1);
    });

    test("an issuer with more life left than the leaf changes nothing", async () => {
        const report = await reportOn({
            notAfter: new Date(NOW.getTime() + 20 * DAY),
            issuerCertificate: issuerCert("Example Intermediate", new Date(NOW.getTime() + 900 * DAY)),
        });

        expect(report.expiry.own).toBe(true);
        expect(report.days).toBe(20);
    });

    /**
     * Node points a self-signed root's `issuerCertificate` at the root itself, so the walk has to stop on
     * a certificate it has already seen. Without that it never returns, and the panel's page never
     * renders — a hang, on the ordinary case of an edge that serves its own root.
     */
    test("a self-signed root ends the walk instead of continuing round it", () => {
        const root: Record<string, unknown> = { ...issuerCert("Example Root", new Date(NOW.getTime() + 3_000 * DAY)) };

        root["issuerCertificate"] = root;

        expect(readCertificate(peer({ issuerCertificate: root })).issuers.map((issuer) => issuer.subject.commonName)).toEqual([
            "Example Root",
        ]);
    });

    /** The same guard against a peer whose chain loops through two certificates rather than one. */
    test("a chain that loops back on itself ends too", () => {
        const first: Record<string, unknown> = { ...issuerCert("First", new Date(NOW.getTime() + 900 * DAY)) };
        const second: Record<string, unknown> = { ...issuerCert("Second", new Date(NOW.getTime() + 900 * DAY)) };

        first["issuerCertificate"] = second;
        second["issuerCertificate"] = first;

        expect(readCertificate(peer({ issuerCertificate: first })).issuers).toHaveLength(2);
    });

    /**
     * The one link that would otherwise be silently exempt from the only check being made of it. An
     * issuer whose date cannot be read is refused exactly as the leaf's would be, because a chain
     * judgement with a quiet hole in it is worse than no chain judgement at all.
     */
    test("an issuer with no readable expiry is unreadable, not skipped", async () => {
        const reports = await inspectCertificates(
            answers(),
            probing({ "chat.example.org": peer({ issuerCertificate: { subject: { CN: "Example Intermediate" } } }) }).probe,
            { now: NOW },
        );

        expect(reports[0]?.verdict).toBe("unreadable");
    });
});

/* ------------------------------------------------------------------------------------------------
 * Whether it covers the name being served.
 * ---------------------------------------------------------------------------------------------- */

describe("coverage", () => {
    function covering(names: string): ReturnType<typeof readCertificate> {
        return readCertificate(peer({ names }));
    }

    test("an exact name matches, and says which entry did it", () => {
        expect(coverageOf(covering("DNS:other.example.org, DNS:chat.example.org"), "chat.example.org")).toEqual({
            covers: true,
            by: "chat.example.org",
        });
    });

    test("a wildcard covers one label", () => {
        expect(coverageOf(covering("DNS:*.example.org"), "chat.example.org")).toEqual({
            covers: true,
            by: "*.example.org",
        });
    });

    /**
     * §5's own example of the misconfiguration: an Origin CA certificate issued for the apex on an
     * instance served from a subdomain. `*.example.org` does not cover `example.org`, and every client
     * agrees about that.
     */
    test("a wildcard does not cover the apex", () => {
        expect(coverageOf(covering("DNS:*.example.org"), "example.org").covers).toBe(false);
    });

    test("a wildcard does not cover two labels", () => {
        expect(coverageOf(covering("DNS:*.example.org"), "a.chat.example.org").covers).toBe(false);
    });

    test("a bare wildcard covers nothing", () => {
        expect(coverageOf(covering("DNS:*"), "chat.example.org").covers).toBe(false);
    });

    /**
     * A wildcard has to leave two labels standing behind it. This was reported as covered, and that is
     * the worst kind of wrong here: Node's own `checkServerIdentity` refuses both patterns below and
     * browsers refuse the public-suffix one besides, so the panel agreed an instance was fine while every
     * client on earth refused it — the operator then debugs DNS, the firewall and the compose file,
     * because the one check built to catch this said the certificate was covered.
     */
    test("a wildcard over a single label covers nothing, however it was issued", () => {
        expect(coverageOf(covering("DNS:*.org"), "example.org").covers).toBe(false);
        expect(coverageOf(covering("DNS:*.internal"), "a.internal").covers).toBe(false);
    });

    test("case and a trailing dot are the same name", () => {
        expect(coverageOf(covering("DNS:chat.example.org"), "CHAT.Example.org.").covers).toBe(true);
    });

    test("a certificate for the wrong name says what it does cover", () => {
        const coverage = coverageOf(covering("DNS:example.org, DNS:www.example.org"), "chat.example.org");

        expect(coverage.covers).toBe(false);
        expect(coverage.covers === false ? coverage.why : "").toContain("www.example.org");
    });

    /**
     * No fallback to the common name. Browsers stopped consulting it in 2017, so a check that read it
     * would pass on a certificate nothing will accept — and Traefik's own placeholder, served on the
     * Let's Encrypt path before ACME has produced anything, is exactly that certificate.
     */
    test("a certificate with no subjectAltName covers nothing, whatever its common name says", () => {
        const placeholder = readCertificate({
            ...peer({ subject: { CN: "chat.example.org" } }),
            subjectaltname: undefined,
        });

        const coverage = coverageOf(placeholder, "chat.example.org");

        expect(coverage.covers).toBe(false);
        expect(coverage.covers === false ? coverage.why : "").toContain("subjectAltName");
    });
});

/* ------------------------------------------------------------------------------------------------
 * The judgement.
 * ---------------------------------------------------------------------------------------------- */

describe("the judgement", () => {
    async function verdictOf(overrides: PeerOverrides, over: Partial<Answers> = {}): Promise<CertificateReport> {
        const settings = answers(over);
        const [first] = servedNames(settings);
        const host = first?.host ?? settings.domain;

        const reports = await inspectCertificates(settings, probing({ [host]: peer(overrides) }).probe, { now: NOW });

        return judged(reports[0]);
    }

    test("a certificate with months left is fine", async () => {
        const report = judged(await verdictOf({ notAfter: new Date(NOW.getTime() + 60 * DAY) }));

        expect(report.verdict).toBe("valid");
        expect(report.days).toBe(60);
        expect(report.coverage.covers).toBe(true);
    });

    test("one already past is expired, and says how long ago in negative days", async () => {
        const report = judged(await verdictOf({ notAfter: new Date(NOW.getTime() - 3 * DAY) }));

        expect(report.verdict).toBe("expired");
        expect(report.days).toBe(-3);
    });

    /**
     * Floored and not rounded, in both directions. Rounding turns a certificate with fourteen hours
     * left into "1 day" and one that lapsed twelve hours ago into "0 days" — the first is a day of
     * notice that does not exist, and the second reads as an instance that is still up.
     */
    test("part of a day is not a whole one, in either direction", async () => {
        expect(judged(await verdictOf({ notAfter: new Date(NOW.getTime() + 29.6 * DAY) })).days).toBe(29);
        expect(judged(await verdictOf({ notAfter: new Date(NOW.getTime() - 0.5 * DAY) })).days).toBe(-1);
    });

    /**
     * The instant itself counts as expired. It turns on a millisecond either way, and of the two
     * mistakes available, saying "fine" about a certificate a client with a fast clock has already
     * begun refusing is the worse one.
     */
    test("the expiry instant itself is expired", async () => {
        expect((await verdictOf({ notAfter: NOW })).verdict).toBe("expired");
    });

    /**
     * A wrong clock, or material pasted in before the day it was issued for. Every client refuses it
     * exactly as it refuses a lapsed one, and the operator's cause is entirely different — so it is not
     * folded into `expired`.
     */
    test("one whose validity has not started is not-yet-valid, not expired", async () => {
        const report = judged(
            await verdictOf({
                notBefore: new Date(NOW.getTime() + 2 * DAY),
                notAfter: new Date(NOW.getTime() + 90 * DAY),
            }),
        );

        expect(report.verdict).toBe("not-yet-valid");
    });

    /**
     * The two thresholds, on one certificate. Twenty days is inside the operator's thirty and outside
     * Traefik's fourteen, and that is the whole reason there are two numbers: warning at thirty on the
     * ACME path would fire on every healthy instance for the moment before Traefik renews it.
     */
    test("twenty days out is a warning for the operator and not for Traefik", async () => {
        const notAfter = new Date(NOW.getTime() + 20 * DAY);

        expect(judged(await verdictOf({ notAfter })).verdict).toBe("expiring");
        expect(judged(await verdictOf({ notAfter }, { traffic: { kind: "lets-encrypt" } })).verdict).toBe("valid");
    });

    test("inside Traefik's fortnight it is a warning there too, because renewal has failed", async () => {
        const notAfter = new Date(NOW.getTime() + 10 * DAY);

        expect(judged(await verdictOf({ notAfter }, { traffic: { kind: "lets-encrypt" } })).verdict).toBe("expiring");
    });

    /**
     * "Within thirty days" includes the thirtieth. Both certificates below report `days: 30`, so this
     * also pins that the threshold is compared against the real remaining time rather than against the
     * rounded number the panel displays.
     */
    test("the threshold is inclusive, and is not the rounded day count", async () => {
        const onIt = judged(await verdictOf({ notAfter: new Date(NOW.getTime() + 30 * DAY) }));
        const justOver = judged(await verdictOf({ notAfter: new Date(NOW.getTime() + 30 * DAY + 60_000) }));

        expect(onIt.verdict).toBe("expiring");
        expect(justOver.verdict).toBe("valid");
        expect([onIt.days, justOver.days]).toEqual([30, 30]);
    });

    /**
     * A Cloudflare instance twenty days out, which is the gap between the two notices. It is the
     * operator's certificate — an Origin CA one, which nothing on this box renews — so twenty days is a
     * warning, and a report that called this Traefik's would say `valid` right up to the fortnight.
     */
    test("a proxied certificate is the operator's, and gets the operator's notice", async () => {
        const proxied = answers({ traffic: { kind: "cloudflare-proxied", voiceHost: "media.example.org" }, voice: true });
        const notAfter = new Date(NOW.getTime() + 20 * DAY);

        const reports = await inspectCertificates(
            proxied,
            probing({
                "chat.example.org": peer({ notAfter }),
                "media.example.org": peer({ names: "DNS:media.example.org", notAfter }),
            }).probe,
            { now: NOW },
        );

        expect(reports.map((report) => (report.verdict === "expiring" ? report.renewal : report.verdict))).toEqual([
            "operator",
            "operator",
        ]);
    });

    /** The field the panel renders to say whose job an expiry is. Nothing else in this file reads it. */
    test("a report says who renews what it is reporting on", async () => {
        expect(judged(await verdictOf({})).renewal).toBe("operator");
        expect(judged(await verdictOf({}, { traffic: { kind: "lets-encrypt" } })).renewal).toBe("traefik");
    });

    test("the notice can be overridden per renewal owner", async () => {
        const reports = await inspectCertificates(
            answers(),
            probing({ "chat.example.org": peer({ notAfter: new Date(NOW.getTime() + 45 * DAY) }) }).probe,
            { now: NOW, warnWithinDays: { operator: 60 } },
        );

        expect(judged(reports[0]).verdict).toBe("expiring");
    });

    /**
     * The override is keyed by renewal owner, and the test above cannot show that: it hands in the key
     * belonging to the path it runs on, so an implementation that ignored the key and always read
     * `operator` would pass it. These two run the operator's key against Traefik's path and back, where
     * an unkeyed lookup moves the wrong threshold — a lets-encrypt instance forty-five days out reported
     * `expiring` on every render, which is a warning nobody can act on and everybody learns to ignore.
     */
    test("an override for one owner leaves the other's notice alone", async () => {
        const acme = { traffic: { kind: "lets-encrypt" } } as const;
        const notAfter = new Date(NOW.getTime() + 45 * DAY);

        const under = async (warnWithinDays: Partial<Record<"operator" | "traefik", number>>): Promise<string> => {
            const reports = await inspectCertificates(
                answers(acme),
                probing({ "chat.example.org": peer({ notAfter }) }).probe,
                { now: NOW, warnWithinDays },
            );

            return judged(reports[0]).verdict;
        };

        expect(await under({ operator: 60 })).toBe("valid");
        expect(await under({ traefik: 60 })).toBe("expiring");
    });

    /**
     * The values, not a relation between them. `traefik` was pinned by nothing: the twenty-day pair
     * requires only that it is under twenty and the ten-day one only that it is at or above ten, so it
     * could be moved to nineteen with the whole file green — and it is not an arbitrary number. It is
     * Traefik's renewal point, a third of a Let's Encrypt lifetime, doubled back into a deadline that
     * means "the retries have genuinely failed" rather than "a renewal is due about now".
     */
    test("the default notice is the one the module publishes", () => {
        expect(WARNING_DAYS).toEqual({ operator: 30, traefik: 14 });
    });

    /**
     * The two axes are independent, and this is the pair that gets missed: a certificate good for
     * another two months that covers a name nobody types. A caller rendering the verdict alone puts a
     * green tick on an instance every browser refuses.
     */
    test("a certificate for the wrong name is still valid in time, and says so separately", async () => {
        const report = judged(
            await verdictOf({ names: "DNS:example.org", notAfter: new Date(NOW.getTime() + 60 * DAY) }),
        );

        expect(report.verdict).toBe("valid");
        expect(report.coverage.covers).toBe(false);
    });
});

/* ------------------------------------------------------------------------------------------------
 * Reaching the edge, or failing to.
 * ---------------------------------------------------------------------------------------------- */

describe("what is dialled, and what happens when it does not answer", () => {
    test("the front door on the compose network, with the served name as SNI", async () => {
        const probed = probing({ "chat.example.org": peer() });

        await inspectCertificates(answers(), probed.probe, { now: NOW });

        expect(probed.targets).toEqual([{ host: EDGE_HOST, port: EDGE_PORT, servername: "chat.example.org" }]);
    });

    /**
     * Traefik's certificate store falls back to a default certificate when the SNI matches nothing, so
     * dialling both names with the same SNI would return the instance's certificate twice and report
     * the media one as healthy however badly it was configured.
     */
    test("two certificates are two handshakes, each with its own SNI", async () => {
        const proxied = answers({ traffic: { kind: "cloudflare-proxied", voiceHost: "media.example.org" }, voice: true });

        const probed = probing({
            "chat.example.org": peer(),
            "media.example.org": peer({ names: "DNS:media.example.org", notAfter: new Date(NOW.getTime() + 5 * DAY) }),
        });

        const reports = await inspectCertificates(proxied, probed.probe, { now: NOW });

        expect(probed.targets.map((target) => target.servername)).toEqual(["chat.example.org", "media.example.org"]);
        expect(reports.map((report) => report.verdict)).toEqual(["valid", "expiring"]);
        expect(reports.map((report) => report.purpose)).toEqual(["instance", "media"]);
    });

    /**
     * The panel renders this page while the edge is still coming up, so a refused connection is a
     * reported absence rather than a thrown error — and it carries the reason, because "unreadable"
     * with no cause is a support thread.
     */
    test("an edge that does not answer is unreadable, with the reason attached", async () => {
        const probed = probing({ "chat.example.org": new Error("connect ECONNREFUSED 172.29.0.4:8443") });

        const [report] = await inspectCertificates(answers(), probed.probe, { now: NOW });

        expect(report?.verdict).toBe("unreadable");
        expect(report?.verdict === "unreadable" ? report.why : "").toContain("ECONNREFUSED");
        expect(report?.verdict === "unreadable" ? report.why : "").toContain("chat.example.org");
    });

    test("a handshake that produced an unreadable certificate is unreadable too, not a guess", async () => {
        const probed = probing({ "chat.example.org": { ...peer(), valid_to: undefined } });

        const [report] = await inspectCertificates(answers(), probed.probe, { now: NOW });

        expect(report?.verdict).toBe("unreadable");
    });

    /**
     * One name failing must not take the other's report with it. On the Cloudflare path the media
     * certificate is the one likeliest to be missing, and it is also the one nobody would go looking
     * for if its failure hid the instance's own report.
     */
    test("one name failing leaves the other's report intact", async () => {
        const proxied = answers({ traffic: { kind: "cloudflare-proxied", voiceHost: "media.example.org" }, voice: true });

        const probed = probing({
            "chat.example.org": peer(),
            "media.example.org": new Error("socket hang up"),
        });

        const reports = await inspectCertificates(proxied, probed.probe, { now: NOW });

        expect(reports.map((report) => report.verdict)).toEqual(["valid", "unreadable"]);
    });

    /**
     * §5's B2: the tunnel carries the TLS and terminates it at Cloudflare's edge. There is no local
     * certificate, so the honest answer is "none" — not an error, and not a silent empty list that
     * would read as an instance with nothing to check.
     */
    test("the tunnel is not applicable, and nothing is dialled", async () => {
        const probed = probing({});

        const reports = await inspectCertificates(answers({ traffic: { kind: "cloudflare-tunnel" } }), probed.probe, {
            now: NOW,
        });

        expect(reports).toHaveLength(1);
        expect(reports[0]?.verdict).toBe("not-applicable");
        expect(probed.targets).toHaveLength(0);
    });
});

/* ------------------------------------------------------------------------------------------------
 * The handshake itself.
 *
 * `edgeProbe` had no test of any kind. Everything above hands in a probe, which is right for the
 * decisions and useless for the transport: the four claims its doc block calls load-bearing —
 * verification deliberately off, no ALPN ever offered, a deadline on the handshake, an empty peer
 * certificate refused rather than parsed — could each be reversed with the whole file still green, and
 * two of them reverse into silence rather than into an error. `rejectUnauthorized: true` reports
 * `unreadable` for every Origin CA and corporate-CA instance on earth; offering `acme-tls/1` gets a
 * cheerful green report about Traefik's throwaway challenge certificate on an instance whose renewal
 * failed ninety days ago.
 *
 * So these run against a listener the test starts on 127.0.0.1. That needs no daemon, no edge and no
 * network, which is what keeps it from being the test that gets skipped.
 * ---------------------------------------------------------------------------------------------- */

/**
 * A throwaway chain, generated once for this file with `openssl` and used nowhere else.
 *
 * Real material rather than a fake socket, because what is being pinned is precisely what a fake would
 * be free to agree with: that the probe accepts a certificate no trust store on the machine knows, and
 * that it asks the handshake for the chain rather than for the leaf alone. The key opens nothing — it
 * belongs to a listener that exists for the length of one test — and both certificates run to 2126, so
 * this file does not begin failing on a date.
 */
const PROBE_CA = `-----BEGIN CERTIFICATE-----
MIIB0DCCAXegAwIBAgIUTX3u4+q3e/yGgA7gnioGhJd5q7owCgYIKoZIzj0EAwIw
PTEdMBsGA1UECgwUQXJnb24gUHJvYmUgRml4dHVyZXMxHDAaBgNVBAMME0FyZ29u
IFByb2JlIFRlc3QgQ0EwIBcNMjYwODI1MDYxMDQzWhgPMjEyNjA4MDEwNjEwNDNa
MD0xHTAbBgNVBAoMFEFyZ29uIFByb2JlIEZpeHR1cmVzMRwwGgYDVQQDDBNBcmdv
biBQcm9iZSBUZXN0IENBMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAED6XZLj2d
vQCXGntP4qn1E3bycp3agbaCzHI5vVyP+BL2guh3dW9a4kK3VsQNHJKGYTHxo2yY
PZDedjvT1LIAMqNTMFEwHQYDVR0OBBYEFIuamzRdH36t90IOmQsGgJSCNb4eMB8G
A1UdIwQYMBaAFIuamzRdH36t90IOmQsGgJSCNb4eMA8GA1UdEwEB/wQFMAMBAf8w
CgYIKoZIzj0EAwIDRwAwRAIgT5C/aU+6E4QHvuJkJJVVXP0Uj0Q1fgUuNTKokFd6
9NMCIBqkUMjcD1zBllahhhNin0uL4ljXyv/3ifA3kwflNGMR
-----END CERTIFICATE-----
`;

const PROBE_LEAF = `-----BEGIN CERTIFICATE-----
MIIBxzCCAWygAwIBAgIUMiHjhpHeWe/udPvds321zkkURKAwCgYIKoZIzj0EAwIw
PTEdMBsGA1UECgwUQXJnb24gUHJvYmUgRml4dHVyZXMxHDAaBgNVBAMME0FyZ29u
IFByb2JlIFRlc3QgQ0EwIBcNMjYwODI1MDYxMDQ0WhgPMjEyNjA4MDEwNjEwNDRa
MBsxGTAXBgNVBAMMEGNoYXQuZXhhbXBsZS5vcmcwWTATBgcqhkjOPQIBBggqhkjO
PQMBBwNCAAQxJL7IPUBPboeIhUrIdFpTxPkd/5hUhQOchly2H2RXpzeukzjHQlTf
OEIgoz7e2bNLXIlqm18aQyc60K8aO9pco2owaDAbBgNVHREEFDASghBjaGF0LmV4
YW1wbGUub3JnMAkGA1UdEwQCMAAwHQYDVR0OBBYEFJZraHJN2av/ulEezDgKsghF
mH66MB8GA1UdIwQYMBaAFIuamzRdH36t90IOmQsGgJSCNb4eMAoGCCqGSM49BAMC
A0kAMEYCIQDMvbGhabWadsFvETgy6rxoj6v3vdXiPK28V1s3oLcc6AIhAOVJSpvl
Q+OEwx9Cd4guVt6sLG6G5jqx/P/kmWL2dHVn
-----END CERTIFICATE-----
`;

const PROBE_KEY = `-----BEGIN EC PRIVATE KEY-----
MHcCAQEEIJHtg22D3C2XcI7reTVsr8Y+rtdurZpLl9wbYtCgaftKoAoGCCqGSM49
AwEHoUQDQgAEMSS+yD1AT26HiIVKyHRaU8T5Hf+YVIUDnIZcth9kV6c3rpM4x0JU
3zhCIKM+3tmzS1yJaptfGkMnOtCvGjvaXA==
-----END EC PRIVATE KEY-----
`;

/** An ephemeral port on the loopback, so nothing here collides with a machine that is running something. */
async function loopback(server: NetServer): Promise<number> {
    await new Promise<void>((ready) => {
        server.listen(0, "127.0.0.1", ready);
    });

    const address = server.address();

    if (address === null || typeof address === "string") throw new Error("the test server did not bind a port");

    return address.port;
}

/** SNI and ALPN, by their IANA extension numbers. */
const SERVER_NAME_EXTENSION = 0x0000;
const ALPN_EXTENSION = 0x0010;

/**
 * The extensions a ClientHello offered, by number.
 *
 * Parsed rather than searched for as text, because the assertion that matters is a negative one — that
 * ALPN is *not* offered — and "the bytes `acme-tls/1` do not appear" would also pass on a hello offering
 * `h2`, which is the change somebody copying a browser-like option set would actually make.
 *
 * The layout: a 5-byte record header, a 4-byte handshake header, then the ClientHello body — 2 bytes of
 * legacy version, 32 of random, and three length-prefixed lists before the extension block.
 */
function extensionsOf(hello: Buffer): number[] {
    let at = 5 + 4 + 2 + 32;

    at += 1 + hello.readUInt8(at); // legacy session id
    at += 2 + hello.readUInt16BE(at); // cipher suites
    at += 1 + hello.readUInt8(at); // compression methods

    const end = at + 2 + hello.readUInt16BE(at);
    const offered: number[] = [];

    for (at += 2; at < end; ) {
        offered.push(hello.readUInt16BE(at));
        at += 4 + hello.readUInt16BE(at + 2);
    }

    return offered;
}

describe("the probe that opens the connection", () => {
    const target = { host: "127.0.0.1", port: 0, servername: "chat.example.org" };

    /**
     * Two invariants at once, and both of them are about what the probe *accepts*. The chain below is
     * signed by a CA no trust store on this machine has ever heard of — which is what an Origin CA
     * certificate and a corporate one both look like from here — so a probe that verified the chain
     * would reject it, and every instance on two of §5's three terminating paths would report
     * `unreadable` for its front door. And the issuer only arrives at all because the probe asks for the
     * chain rather than for the leaf, which is the difference between seeing an intermediate that
     * expires next week and counting down to a leaf that outlives it.
     */
    test("a certificate no trust store knows is read, chain and all", async () => {
        const server = createTlsServer({ cert: PROBE_LEAF + PROBE_CA, key: PROBE_KEY }, (socket) => socket.end());

        try {
            const port = await loopback(server);
            const read = readCertificate(await edgeProbe(2_000)({ ...target, port }));

            expect(read.names).toEqual(["chat.example.org"]);
            expect(read.subject.commonName).toBe("chat.example.org");
            expect(read.issuers.map((issuer) => issuer.subject.commonName)).toEqual(["Argon Probe Test CA"]);
        } finally {
            server.close();
        }
    });

    /**
     * The ClientHello, read off the wire by a socket that never answers it.
     *
     * `acme-tls/1` is TLS-ALPN-01, the challenge Traefik is configured to use on the Let's Encrypt path,
     * and a connection offering it is answered with the throwaway challenge certificate — in date,
     * self-issued, carrying the served name, and seen by no browser ever. A panel built on that reports a
     * cheerful green for an instance whose renewal has been failing since spring. Nothing else in this
     * file could catch an `ALPNProtocols` line being added to the connect options, so this reads the
     * bytes.
     */
    test("no ALPN is offered, and the served name is what the hello asks for", async () => {
        const hello: Buffer[] = [];
        const server = createNetServer((socket) => {
            socket.on("data", (chunk: Buffer) => hello.push(chunk));
        });

        try {
            const port = await loopback(server);

            await expect(edgeProbe(250)({ ...target, port })).rejects.toThrow();

            const sent = Buffer.concat(hello);

            expect(extensionsOf(sent)).toContain(SERVER_NAME_EXTENSION);
            expect(extensionsOf(sent)).not.toContain(ALPN_EXTENSION);
            expect(sent.includes(Buffer.from("chat.example.org"))).toBe(true);
        } finally {
            server.close();
        }
    });

    /**
     * The failure the deadline is for: a socket that accepts the connection and then says nothing. A
     * front door mid-restart does this, and so does one serving plaintext because the shape was changed
     * to the tunnel underneath the panel. Neither emits an error, so without the deadline this promise
     * never settles and the page never renders — which is why the assertion below is on the *sentence*
     * as much as on the rejection: an operator reading "did not finish a TLS handshake" knows something
     * answered, and an operator reading a timeout with no subject knows nothing at all.
     */
    test("an edge that accepts and never handshakes fails on the deadline, with the target in it", async () => {
        const server = createNetServer(() => {
            // Accepts the connection and sends nothing, for ever.
        });

        try {
            const port = await loopback(server);

            await expect(edgeProbe(150)({ ...target, port })).rejects.toThrow(
                /127\.0\.0\.1:\d+ did not finish a TLS handshake for chat\.example\.org within 150ms/,
            );
        } finally {
            server.close();
        }
    });

    /**
     * `getPeerCertificate()` is typed as always returning an object and does not — a resumed session is
     * the ordinary way an empty one comes back. Parsing it would produce a certificate with no dates and
     * no names, and the operator would be told their `valid_to` was missing rather than that the
     * handshake carried no certificate at all.
     */
    test("a handshake that presented nothing says so, rather than being parsed into an empty certificate", () => {
        expect(() => presentedCertificate({}, target)).toThrow(/presented no certificate/);
        expect(() => presentedCertificate(undefined, target)).toThrow(/chat\.example\.org/);
        expect(presentedCertificate({ valid_to: "Nov 14 09:30:00 2026 GMT" }, target).valid_to).toBe(
            "Nov 14 09:30:00 2026 GMT",
        );
    });
});
