import { connect } from "node:tls";
import { DEPLOYMENT } from "../generate";
import type { Answers } from "../model";

/**
 * When the instance's TLS certificate stops working, and whether it ever covered the name it serves.
 *
 * ## Why the panel has to answer this
 *
 * §5, path A: renewal belongs to the operator, and "an instance that stops answering in ninety days
 * with no warning is the worst outcome of this path". Nothing else on the box is watching. Traefik logs
 * a failed renewal and keeps serving the old certificate until the day it does not, and by then the
 * operator is debugging a dead instance rather than reading a warning.
 *
 * ## Why it reads the wire rather than a file
 *
 * The certificate is in a different place on each of §5's four paths, and only one of those places is
 * one the panel can open:
 *
 *  - **own-certificate** and **cloudflare-proxied**: two host files bind-mounted into the *edge*
 *    container. The panel gets neither mount, and giving it one would put the instance's private key in
 *    a second container for the sake of a date.
 *  - **lets-encrypt**: inside Traefik's `acme.json`, on the named volume `argon-edge-data`. Mounting
 *    that into the panel hands it the ACME account key as well, and the file's layout is Traefik's
 *    internal storage with no compatibility promise attached to it.
 *  - **cloudflare-tunnel**: there is no local certificate at all. The tunnel carries the TLS and the
 *    edge serves plaintext on the loopback.
 *
 * So this opens a TLS connection to the front door over the compose network, with the served name as
 * SNI, and reads the certificate the handshake produced. That works for all three terminating paths
 * with one mechanism, and it answers a better question than any of the files would: a file says what
 * was *installed*, and the edge is what is *served*. Traefik reads its certificates once at start and
 * the file provider here is deliberately unwatched (see `EDGE_STATIC_CONFIG`), so a rotated file with
 * no restart behind it is exactly the case where the file says everything is fine and every browser
 * disagrees. The handshake cannot be wrong about what is served, because it is the same handshake the
 * browser makes — with one deliberate difference, which is that this one does not verify the chain (see
 * {@link edgeProbe} for why it cannot, and for what is checked in its place).
 *
 * Traefik's own API would have answered too, and is not available: the static configuration enables no
 * `api` section, and turning one on adds surface to the one container that faces the internet.
 *
 * ## Two axes, not one
 *
 * A certificate has a lifetime and it has a set of names, and they fail independently. `verdict`
 * carries the first; {@link Coverage} carries the second. An origin certificate issued for the apex on
 * an instance served from a subdomain is `valid` for years and covers nothing anybody types — see
 * {@link Coverage} for why rendering the verdict alone puts a green tick on an unreachable instance.
 *
 * The lifetime is the whole served chain's and not the leaf's, which is a distinction that costs nothing
 * until it costs everything: an intermediate that lapses before the certificate it signed takes the
 * front door down on its own date, and reading the leaf alone reported that instance as `valid` with two
 * hundred days left. {@link CertificateReport.expiry} says which certificate the count is about, because
 * reissuing the leaf is no repair for an expired issuer.
 */

/* ------------------------------------------------------------------------------------------------
 * Where to look.
 * ---------------------------------------------------------------------------------------------- */

/**
 * The front door's service name, which is also its DNS name on the compose network.
 *
 * `compose.ts` names it privately in `SERVICES.edge` and does not export it, so this is a second copy
 * of a hostname — the thing that module's header warns produces an instance that starts and cannot talk
 * to itself. It is kept honest by a test that builds a real compose project and asserts the edge
 * service is called this and publishes onto {@link EDGE_PORT}; a rename in `compose.ts` fails there
 * rather than turning into a panel that reports every certificate unreadable.
 */
export const EDGE_HOST = "argon-edge";

/**
 * The port the edge's public entry point binds *inside* its container.
 *
 * Not 443. That is the host mapping; from the compose network the container is reached on the listener
 * itself, and `generate.ts` names it because binding a privileged port needs a capability the image may
 * not be given. Taken from {@link DEPLOYMENT} rather than written out, so the two cannot drift.
 */
export const EDGE_PORT: number = DEPLOYMENT.ports.tls;

/* ------------------------------------------------------------------------------------------------
 * The port.
 * ---------------------------------------------------------------------------------------------- */

/** What a handshake is asked for. `servername` is the SNI, and it is the whole reason this is a port. */
export interface ProbeTarget {
    readonly host: string;
    readonly port: number;

    /**
     * The name the connection claims to want.
     *
     * Traefik's certificate store matches on it and falls back to a default certificate when nothing
     * matches, so the SNI is not decoration: dialling with the wrong one returns a certificate that is
     * real, valid, and about some other name.
     */
    readonly servername: string;
}

/**
 * One peer certificate, in the shape `node:tls` hands it over.
 *
 * Field names are OpenSSL's rather than this codebase's because they are not this codebase's to choose:
 * `getPeerCertificate()` returns `valid_from` and `subjectaltname`, and renaming them here would mean a
 * translation layer between the port and its only real implementation. Everything is `unknown` for the
 * reason `docker.ts` narrows the Engine API's rows: this arrives from outside and every field has a way
 * of being absent that is not a crash.
 */
export interface PeerCertificate {
    readonly subject?: unknown;
    readonly issuer?: unknown;
    readonly subjectaltname?: unknown;
    readonly valid_from?: unknown;
    readonly valid_to?: unknown;

    /**
     * The next certificate up the chain the edge served, in the same shape as this one.
     *
     * Node's name and Node's linked list rather than an array, for the reason the rest of these fields
     * keep theirs. Absent when the edge sent nothing above this certificate, and — for a self-signed
     * root — a reference to the certificate itself, which is how the walk in {@link readCertificate}
     * knows it has reached the top rather than looping for ever.
     */
    readonly issuerCertificate?: unknown;
}

/**
 * One TLS handshake, resolving with what the peer presented.
 *
 * A port so that the tests hand in a certificate rather than opening a socket — a test that needs a
 * running edge is a test that gets skipped the first time CI has none, which is every time. It rejects
 * rather than resolving with `undefined`, because "nothing answered" and "something answered with
 * nothing" are the same answer here and an exception carries the sentence explaining which.
 */
export type TlsProbe = (target: ProbeTarget) => Promise<PeerCertificate>;

/* ------------------------------------------------------------------------------------------------
 * What is served, and who renews it.
 * ---------------------------------------------------------------------------------------------- */

/** Which of §5's certificates a report is about. An instance can be running both at once. */
export type Purpose = "instance" | "media";

/**
 * Whose job it is when this expires, which is also what makes a warning actionable.
 *
 * `traefik` means the edge renews it unattended and a warning is news about a *failure*; `operator`
 * means nothing on this machine will ever renew it and the warning is the only thing that will happen.
 * The two want different notice — see {@link WARNING_DAYS}.
 */
export type Renewal = "operator" | "traefik";

/**
 * A name the edge answers to, and what stands behind it.
 *
 * `terminated: false` is the tunnel and only the tunnel: TLS for that name is Cloudflare's, ends at
 * their edge, and is not visible from this machine at any price. It is carried as a name with a reason
 * rather than dropped from the list, because a panel that silently lists nothing for the tunnel shape
 * is indistinguishable from a panel whose certificate check is broken.
 */
export type ServedName =
    | { readonly host: string; readonly purpose: Purpose; readonly terminated: true; readonly renewal: Renewal }
    | { readonly host: string; readonly purpose: Purpose; readonly terminated: false; readonly why: string };

/**
 * Every name this instance terminates TLS for, in the order the panel should show them.
 *
 * The media subdomain is the second one, and §5 is explicit that it exists: a Cloudflare-proxied
 * instance publishing voice directly "ends up running two TLS paths at once ... with different renewal
 * stories and different expiry dates. The panel has to show both." Reporting only the instance's own
 * name would leave the certificate nobody thinks about as the one nobody watches.
 *
 * The condition for that second name — voice on, Cloudflare-proxied, a media host named — is decided
 * privately by `compose.ts`'s `mediaHostFor`, and this is a second copy of it. A test asserts the
 * agreement against a generated compose project rather than against this reasoning.
 */
export function servedNames(answers: Answers): readonly ServedName[] {
    const traffic = answers.traffic;

    switch (traffic.kind) {
        case "own-certificate":
            return [{ host: answers.domain, purpose: "instance", terminated: true, renewal: "operator" }];

        case "lets-encrypt":
            return [{ host: answers.domain, purpose: "instance", terminated: true, renewal: "traefik" }];

        case "cloudflare-proxied": {
            const names: ServedName[] = [
                // An Origin CA certificate, which lasts years and which Cloudflare will not renew for
                // them — §5 takes no API token, so there is nothing here that could. Still `operator`.
                { host: answers.domain, purpose: "instance", terminated: true, renewal: "operator" },
            ];

            const media = answers.voice ? traffic.voiceHost : undefined;

            if (media !== undefined)
                names.push({ host: media, purpose: "media", terminated: true, renewal: "operator" });

            return names;
        }

        case "cloudflare-tunnel":
            return [
                {
                    host: answers.domain,
                    purpose: "instance",
                    terminated: false,
                    why: "the tunnel carries the TLS and terminates it at Cloudflare's edge, so this machine holds no certificate for this name and cannot see the expiry of the one that is served",
                },
            ];

        default: {
            // A fifth shape must stop here rather than fall through to an empty list. An empty list is
            // what a healthy tunnel-shaped instance would produce, and reporting "nothing to warn
            // about" for a shape this has never heard of is the silent nothing §5 is trying to prevent.
            const unhandled: never = traffic;

            throw new Error(
                `traffic shape ${JSON.stringify(unhandled)} is not one this knows how to find a certificate for, and reporting nothing would read as an instance with nothing to worry about`,
            );
        }
    }
}

/* ------------------------------------------------------------------------------------------------
 * The certificate itself.
 * ---------------------------------------------------------------------------------------------- */

/**
 * A subject or an issuer, reduced to the two attributes that identify one to a person.
 *
 * `CN` alone is not enough: a Let's Encrypt issuer's `CN` is a code like `R11` and its `O` is what says
 * "Let's Encrypt", which is the difference between an operator recognising their own setup and not.
 * Both are optional because both genuinely are — a certificate may carry no `CN` at all, and Traefik's
 * built-in placeholder carries almost nothing.
 */
export interface Party {
    readonly commonName?: string;
    readonly organization?: string;
}

export interface Certificate {
    readonly subject: Party;
    readonly issuer: Party;
    readonly notBefore: Date;
    readonly notAfter: Date;

    /**
     * Every `DNS:` entry of the subjectAltName extension, lowercased and without a trailing dot.
     *
     * DNS entries only. `IP Address:`, `email:` and `URI:` entries are dropped rather than carried,
     * because the only question asked of this list is whether it covers a hostname, and a client
     * matching a hostname consults nothing else.
     */
    readonly names: readonly string[];

    /**
     * The certificates the edge sent above this one, nearest first, and empty when it sent none.
     *
     * Here because the leaf is not the only thing that expires. A leaf good for another two hundred days
     * under an intermediate that lapses tomorrow is a front door that dies tomorrow, and reading only the
     * leaf reported it `valid` with two hundred days left — a green tick on an instance about to fall
     * over, which is the failure this module exists to prevent rather than to commit. {@link expiryOf}
     * is what turns this into a date, and {@link CertificateReport.expiry} names which certificate it
     * came from.
     *
     * A *short* chain is deliberately not judged. The obvious companion check — "the edge sent no issuer,
     * so a client that will not fetch one from the AIA extension refuses the connection" — cannot be made
     * here, because sending nothing above the leaf is the correct configuration on two of §5's paths: a
     * Cloudflare Origin CA certificate is served alone and Cloudflare holds the root, and an operator's
     * own certificate from a corporate CA is served alone to clients that already have that CA installed.
     * A warning that fires on two correct configurations trains the operator to ignore this panel, which
     * costs more than the case it would catch.
     */
    readonly issuers: readonly Issuer[];
}

/** One certificate above the leaf, reduced to what a lifetime judgement needs of it. */
export interface Issuer {
    readonly subject: Party;
    readonly notAfter: Date;
}

/**
 * When the served chain stops working, and whose certificate decides that.
 *
 * `own: false` is a different repair and worth saying out loud: reissuing the leaf will not fix an
 * expired intermediate, and the operator who is only shown a date will spend the afternoon reissuing the
 * wrong certificate.
 */
export interface Expiry {
    readonly at: Date;
    readonly of: Party;
    readonly own: boolean;
}

/**
 * The certificate in the served chain that lapses first.
 *
 * Only expiry is taken from the issuers, not `notBefore`. An issuer whose validity has not started yet
 * is a CA that signed something before its own start date — a fault at the CA, not a thing the operator
 * of this box can act on, and not worth a second verdict that would read as their mistake.
 */
export function expiryOf(certificate: Certificate): Expiry {
    let soonest: Expiry = { at: certificate.notAfter, of: certificate.subject, own: true };

    for (const issuer of certificate.issuers)
        if (issuer.notAfter.getTime() < soonest.at.getTime())
            soonest = { at: issuer.notAfter, of: issuer.subject, own: false };

    return soonest;
}

/**
 * Whether the certificate covers the name it is being served for.
 *
 * The second axis, and the one that gets missed. A certificate can be `valid` for another two years and
 * still fail every connection, and §5 names the two ways it happens here: an Origin CA certificate
 * issued for the apex when the instance is on a subdomain, and the media subdomain answered by the
 * store's default certificate because the second one was never mounted. Both produce a browser error
 * that reads as the client's fault.
 *
 * There is a third, worth recognising from `subject.commonName`: an edge on the Let's Encrypt path that
 * has not obtained anything yet answers with Traefik's own placeholder, whose `CN` is `TRAEFIK DEFAULT
 * CERT`. It is a real certificate, it is in date, and it covers nothing.
 *
 * So a caller that renders `verdict` and not this shows a green tick on an instance nobody can reach.
 */
export type Coverage =
    /** Which entry matched, because `*.example.org` matching and `example.org` matching mean different things to an operator about to move the instance to another subdomain. */
    | { readonly covers: true; readonly by: string }
    | { readonly covers: false; readonly why: string };

/**
 * What a report says about one name.
 *
 * `not-applicable` and `unreadable` are distinct on purpose. The first is a settled answer — the tunnel
 * shape has no local certificate and never will — and the second is the absence of one, which on a box
 * mid-install is the ordinary state of an edge that has not started yet. Collapsing them would make an
 * instance whose front door is down look like an instance with nothing to check.
 */
export type CertificateReport =
    | { readonly host: string; readonly purpose: Purpose; readonly verdict: "not-applicable"; readonly why: string }
    | { readonly host: string; readonly purpose: Purpose; readonly verdict: "unreadable"; readonly why: string }
    | {
          readonly host: string;
          readonly purpose: Purpose;
          readonly verdict: "expired" | "not-yet-valid" | "expiring" | "valid";
          readonly certificate: Certificate;
          readonly renewal: Renewal;

          /**
           * Which certificate in the served chain goes first, and when.
           *
           * Usually the served one, in which case `expiry.at` is {@link Certificate.notAfter} and
           * `own` is true. When an issuer goes first this is the only place that says so, and a panel
           * rendering {@link Certificate.notAfter} instead of this shows a date the instance will not
           * survive to.
           */
          readonly expiry: Expiry;

          /**
           * Whole days from now until {@link expiry}, and negative once that is past.
           *
           * Counted to the chain's earliest expiry rather than to the served certificate's own, so this
           * can be smaller than `certificate.notAfter` implies. That is the point: it is the number of
           * days the front door has left, not the number of days printed on one of the certificates
           * behind it.
           */
          readonly days: number;

          readonly coverage: Coverage;
      };

/**
 * How much notice each renewal owner needs, in days.
 *
 * Two numbers rather than one, because one would be wrong for one of the paths. Traefik renews when
 * about a third of a certificate's lifetime remains — thirty days for Let's Encrypt's ninety — so a
 * panel warning at thirty would fire on every healthy instance for the moment before the renewal it is
 * warning about. Fourteen means the renewal has had a fortnight of retries and has genuinely failed,
 * which is worth waking somebody for. *(The third is Traefik's documented default and should be checked
 * against the version pinned in `INFRASTRUCTURE_IMAGES` before this number is trusted in a release.)*
 *
 * Thirty for the operator's own, because that path has no retries in it: a corporate CA reissues on
 * their timetable, and a fortnight's notice is not enough time to get one.
 */
export const WARNING_DAYS: Readonly<Record<Renewal, number>> = { operator: 30, traefik: 14 };

const DAY = 86_400_000;

export interface InspectOptions {
    /** Taken rather than read, so the tests can stand next to an expiry instead of minting one. */
    readonly now?: Date;

    /** Overrides {@link WARNING_DAYS}, per renewal owner. */
    readonly warnWithinDays?: Partial<Record<Renewal, number>>;
}

/**
 * One report per name the instance serves.
 *
 * Never empty and never throwing for a reachability problem: a front door that is not up yet is an
 * `unreadable` report with the reason in it, because the panel calls this on a page that also has to
 * render while nothing is running.
 */
export async function inspectCertificates(
    answers: Answers,
    probe: TlsProbe,
    options: InspectOptions = {},
): Promise<readonly CertificateReport[]> {
    const now = options.now ?? new Date();
    const reports: CertificateReport[] = [];

    for (const name of servedNames(answers)) {
        if (!name.terminated) {
            reports.push({ host: name.host, purpose: name.purpose, verdict: "not-applicable", why: name.why });
            continue;
        }

        reports.push(await inspectOne(name, probe, now, options));
    }

    return reports;
}

async function inspectOne(
    name: Extract<ServedName, { readonly terminated: true }>,
    probe: TlsProbe,
    now: Date,
    options: InspectOptions,
): Promise<CertificateReport> {
    let certificate: Certificate;

    try {
        certificate = readCertificate(await probe({ host: EDGE_HOST, port: EDGE_PORT, servername: name.host }));
    } catch (cause) {
        return {
            host: name.host,
            purpose: name.purpose,
            verdict: "unreadable",
            why: `nothing here could read the certificate the front door serves for ${name.host}: ${describe(cause)}`,
        };
    }

    const within = options.warnWithinDays?.[name.renewal] ?? WARNING_DAYS[name.renewal];
    const expiry = expiryOf(certificate);

    return {
        host: name.host,
        purpose: name.purpose,
        verdict: verdictFor(certificate, now, within),
        certificate,
        renewal: name.renewal,
        expiry,

        // Floored, so "0 days" means it goes today rather than "some time in the last twenty-four
        // hours". The threshold below is compared against the real remaining time and not against this:
        // rounding first would make a certificate with twenty-nine and a half days left read as
        // twenty-nine and answer a "within thirty days?" question with the wrong number.
        days: Math.floor((expiry.at.getTime() - now.getTime()) / DAY),

        coverage: coverageOf(certificate, name.host),
    };
}

function verdictFor(certificate: Certificate, now: Date, withinDays: number): "expired" | "not-yet-valid" | "expiring" | "valid" {
    const at = now.getTime();

    // Checked first, and not folded into "expired". A certificate whose validity has not started is
    // refused by every client exactly as a lapsed one is, and the operator's cause is entirely
    // different: a clock that is wrong, or material pasted in before it was meant to be used.
    if (at < certificate.notBefore.getTime()) return "not-yet-valid";

    // Judged against the whole served chain and not against the leaf alone — see {@link Certificate}'s
    // `issuers` for the case that costs, which is an intermediate that goes before the certificate it
    // signed and takes the front door with it.
    const expires = expiryOf(certificate).at.getTime();

    // The boundary counts as expired. It turns on a millisecond either way, and of the two mistakes
    // available the one that says "fine" about a certificate a client with a slightly fast clock has
    // already begun refusing is the worse one.
    if (at >= expires) return "expired";

    return expires - at <= withinDays * DAY ? "expiring" : "valid";
}

/**
 * The fields this reads, out of whatever the handshake produced.
 *
 * Rejects rather than filling in a default when a date is missing or unreadable. A certificate with no
 * `valid_to` cannot be judged, and the only defaults available are "expired" — which cries wolf on a
 * healthy instance — and "valid", which is the silence this whole module exists to break.
 */
export function readCertificate(peer: PeerCertificate): Certificate {
    return {
        subject: partyOf(peer.subject),
        issuer: partyOf(peer.issuer),
        notBefore: instantOf(peer.valid_from, "valid_from"),
        notAfter: instantOf(peer.valid_to, "valid_to"),
        names: dnsNamesOf(peer.subjectaltname),
        issuers: issuersOf(peer.issuerCertificate),
    };
}

/**
 * How far up the chain this will walk before it stops.
 *
 * A limit and not a belief about certificates: the thing being walked is a linked list built from bytes
 * a peer sent, and a peer that sends a loop this does not recognise as one gets a hung panel rather than
 * a report. Ten is far past anything real — a public chain is three certificates and a private one two.
 */
const MAX_CHAIN = 10;

function issuersOf(value: unknown): readonly Issuer[] {
    const issuers: Issuer[] = [];
    const seen = new Set<unknown>();

    let current = value;

    while (typeof current === "object" && current !== null && issuers.length < MAX_CHAIN) {
        // A self-signed root's `issuerCertificate` is a reference to itself, which is how Node says "this
        // is the top" and also how a naive walk here never returns.
        if (seen.has(current)) break;

        seen.add(current);

        const link = current as PeerCertificate;

        // Refused by the same rule the leaf's date is refused by. An issuer with no readable expiry is
        // the one link that would be silently exempt from the only check being made of the chain, and
        // exempting it quietly is how a chain judgement becomes worse than no chain judgement.
        issuers.push({ subject: partyOf(link.subject), notAfter: instantOf(link.valid_to, "valid_to for an issuer it sent") });

        current = link.issuerCertificate;
    }

    return issuers;
}

/**
 * Whether a certificate covers a hostname, by the rules a browser applies.
 *
 * Wildcards match one label and only the leftmost: `*.example.org` covers `chat.example.org`, does not
 * cover `example.org`, and does not cover `a.b.example.org`. That last one is the rule people expect to
 * work and it does not, and it is worth catching here rather than in a support thread.
 */
export function coverageOf(certificate: Certificate, host: string): Coverage {
    const wanted = normalise(host);

    // Not a fallback to the common name. Chrome stopped consulting the CN in 2017 and the others
    // followed, so a certificate with no subjectAltName covers nothing whatever its subject says — and
    // a check here that read the CN would pass on a certificate no browser will accept.
    if (certificate.names.length === 0)
        return {
            covers: false,
            why: `the certificate served for ${wanted} carries no subjectAltName, and no current browser will match a hostname against the common name, so it covers nothing`,
        };

    const matched = certificate.names.find((name) => matches(name, wanted));

    if (matched === undefined)
        return {
            covers: false,
            why: `the certificate served for ${wanted} covers ${certificate.names.join(", ")}, which does not include it`,
        };

    return { covers: true, by: matched };
}

function matches(pattern: string, host: string): boolean {
    if (pattern === host) return true;

    if (!pattern.startsWith("*.")) return false;

    const suffix = pattern.slice(1);

    // A wildcard has to leave at least two labels standing behind it, so `*.org` and `*.internal` cover
    // nothing here. This was found reported as `covers: true`, which is the worst answer available: every
    // client refuses such a pattern — Node's own `checkServerIdentity` rejects any wildcard of two labels
    // or fewer, and browsers refuse the public-suffix cases besides — so agreeing that the certificate is
    // fine leaves the operator debugging everything except the one thing that is wrong. The cost is that
    // an operator who genuinely wanted `*.internal` on a private CA is told it covers nothing, which is
    // what their own browser will tell them a moment later.
    if (!suffix.slice(1).includes(".")) return false;

    if (!host.endsWith(suffix)) return false;

    const label = host.slice(0, host.length - suffix.length);

    return label.length > 0 && !label.includes(".");
}

/** Lowercased, and the root's trailing dot removed: `Chat.Example.Org.` and `chat.example.org` are one name. */
function normalise(host: string): string {
    const trimmed = host.trim().toLowerCase();

    return trimmed.endsWith(".") ? trimmed.slice(0, -1) : trimmed;
}

function instantOf(value: unknown, field: string): Date {
    if (typeof value !== "string")
        throw new Error(`the certificate carries no ${field}, so there is no date here to judge`);

    const parsed = Date.parse(value);

    // OpenSSL's `MMM D HH:MM:SS YYYY GMT`, which `Date.parse` reads. Checked anyway rather than trusted:
    // an unparsed date becomes an Invalid Date, every comparison against it is false, and the certificate
    // would come out as `valid` forever.
    if (Number.isNaN(parsed)) throw new Error(`the certificate's ${field} is ${JSON.stringify(value)}, which is not a date`);

    return new Date(parsed);
}

function partyOf(value: unknown): Party {
    if (typeof value !== "object" || value === null) return {};

    const record = value as Record<string, unknown>;
    const commonName = attributeOf(record["CN"]);
    const organization = attributeOf(record["O"]);

    return { ...(commonName === undefined ? {} : { commonName }), ...(organization === undefined ? {} : { organization }) };
}

/** An RDN attribute is a string, or an array of them when the name repeated it. Node gives back both. */
function attributeOf(value: unknown): string | undefined {
    if (typeof value === "string") return value.length > 0 ? value : undefined;

    if (Array.isArray(value)) return value.find((entry): entry is string => typeof entry === "string" && entry.length > 0);

    return undefined;
}

/**
 * The SAN list, out of the one flat string `getPeerCertificate()` returns for it.
 *
 * Split on top-level commas only. Node quotes an entry whose name contains a comma or a quote, and it
 * does that for a reason worth restating: a certificate carrying the single name
 * `"evil.test, DNS:chat.example.org"` splits naively into two entries, the second of which matches the
 * host being checked — so a plain `split(",")` reports full coverage for a certificate that covers only
 * a name the attacker chose. The tokenizer below honours the quoting; a runtime that emitted such a
 * name *unquoted* would still fool it, and there is nothing at this layer that could tell.
 */
function dnsNamesOf(value: unknown): readonly string[] {
    if (typeof value !== "string") return [];

    const names: string[] = [];

    for (const entry of entriesOf(value)) {
        const separator = entry.indexOf(":");

        if (separator < 0) continue;

        // Exactly `DNS`, so `IP Address:`, `email:`, `URI:` and `othername:` fall out here rather than
        // becoming hostnames. Nothing matches an `IP Address:` entry in this project — the served name
        // is always a hostname, because the certificate is for a domain the operator typed.
        if (entry.slice(0, separator) !== "DNS") continue;

        const name = normalise(unquote(entry.slice(separator + 1).trim()));

        if (name.length > 0) names.push(name);
    }

    return names;
}

function entriesOf(list: string): string[] {
    const found: string[] = [];

    let current = "";
    let quoted = false;

    for (let index = 0; index < list.length; index += 1) {
        const character = list.charAt(index);

        if (quoted && character === "\\") {
            current += character + list.charAt(index + 1);
            index += 1;
            continue;
        }

        if (character === '"') {
            quoted = !quoted;
            current += character;
            continue;
        }

        if (character === "," && !quoted) {
            found.push(current);
            current = "";
            continue;
        }

        current += character;
    }

    found.push(current);

    return found.map((entry) => entry.trim()).filter((entry) => entry.length > 0);
}

function unquote(value: string): string {
    if (value.length < 2 || !value.startsWith('"') || !value.endsWith('"')) return value;

    return value.slice(1, -1).replace(/\\(.)/g, "$1");
}

function describe(cause: unknown): string {
    return cause instanceof Error ? cause.message : String(cause);
}

/* ------------------------------------------------------------------------------------------------
 * The real handshake.
 * ---------------------------------------------------------------------------------------------- */

/**
 * The transport: one TLS connection to the front door, taken no further than the handshake.
 *
 * Nothing is sent. The certificate arrives before any application data would, so a request would only
 * add a route that has to exist and a status code that has to be interpreted.
 *
 * **`rejectUnauthorized` is off, and that is the point rather than a shortcut.** Two of the three
 * terminating paths present a certificate no public trust store knows — an Origin CA certificate is
 * trusted by Cloudflare and by nothing else, which §5 calls exactly the property wanted for an origin —
 * so verification here would fail on a correctly configured instance. What the handshake would have
 * checked is checked above instead, in the open, where a failure comes back as a sentence about a name
 * or a date instead of as one opaque handshake error: {@link coverageOf} for the name, and
 * {@link verdictFor} for the dates of every certificate the edge served rather than only the leaf's.
 *
 * The part that is genuinely given up is the signatures and the trust anchor — nothing here proves the
 * chain is signed by anything, or that a client would trust the top of it. It could not: the trust
 * anchor differs per path and per client, and on the Cloudflare path the only party whose trust store
 * matters is Cloudflare's. So a certificate the operator swapped for one their clients do not trust
 * reads as healthy here, and the report is about expiry and names, which is what it claims to be.
 *
 * **No ALPN is offered**, and `acme-tls/1` in particular must never be. That is TLS-ALPN-01, the
 * challenge Traefik is configured to use on the Let's Encrypt path, and a connection offering it is
 * answered with the throwaway challenge certificate — a real handshake, a real certificate, and nothing
 * to do with the one the instance serves.
 */
export function edgeProbe(timeoutMs = 5_000): TlsProbe {
    return (target) =>
        new Promise<PeerCertificate>((resolve, reject) => {
            const socket = connect({
                host: target.host,
                port: target.port,
                servername: target.servername,
                rejectUnauthorized: false,
            });

            const fail = (cause: Error): void => {
                socket.destroy();
                reject(cause);
            };

            // A deadline and not just an error handler: the failure this guards is the edge accepting
            // the connection and never finishing the handshake — a front door mid-restart, or one
            // serving plaintext because the shape was changed to the tunnel underneath this. Neither
            // emits an error, and without this the panel's page never renders.
            socket.setTimeout(timeoutMs, () =>
                fail(new Error(`${target.host}:${target.port} did not finish a TLS handshake for ${target.servername} within ${timeoutMs}ms`)),
            );

            socket.once("error", fail);

            socket.once("secureConnect", () => {
                // `true`, so the certificates above the leaf come with it. With `false` the chain is
                // discarded, and an instance whose intermediate expires tomorrow reports the leaf's own
                // two hundred days and a green tick — see {@link Certificate}'s `issuers`.
                const peer = socket.getPeerCertificate(true);

                socket.destroy();

                try {
                    resolve(presentedCertificate(peer, target));
                } catch (cause) {
                    reject(cause);
                }
            });
        });
}

/**
 * What the handshake presented, or the sentence saying it presented nothing.
 *
 * `getPeerCertificate()` is typed as always returning an object and does not: an empty one is what comes
 * back when the peer sent no certificate — a resumed session is the way that happens without anything
 * having gone wrong — and that is a different thing from the connection failing. Parsing it would
 * produce a certificate with no dates and no names, which {@link readCertificate} would then refuse with
 * a sentence about a missing `valid_to` that says nothing about what actually happened.
 *
 * Separate from {@link edgeProbe} and exported because it is the narrowing at the edge of the port, the
 * same job {@link readCertificate} does one layer in — and because a branch reachable only through a
 * resumed TLS session is otherwise a branch no test can hold.
 */
export function presentedCertificate(peer: unknown, target: ProbeTarget): PeerCertificate {
    if (peer === null || typeof peer !== "object" || Object.keys(peer).length === 0)
        throw new Error(`${target.host}:${target.port} completed a handshake for ${target.servername} and presented no certificate`);

    return peer as PeerCertificate;
}
