import { chmod, link, mkdir, readFile, readdir, realpath, rm, stat, writeFile } from "node:fs/promises";
import { randomBytes } from "node:crypto";
import { isAbsolute, join, relative, resolve } from "node:path";
import {
    BOOTSTRAP_CODE_FILE,
    COMPOSE_FILENAME,
    COMPOSE_PROJECT,
    EDGE_DYNAMIC_CONFIG,
    EDGE_STATIC_CONFIG,
    ENV_FILENAME,
    SFU_CONFIG,
    STORAGE_IDENTITIES,
} from "../compose";
import { CREDENTIAL_FILE } from "../credential";
import { COMPOSE_PROJECT_LABEL, COMPOSE_SERVICE_LABEL, type EngineRequest } from "../docker";
import { DEPLOYMENT } from "../generate";
import { MINT_FILE } from "../setup";

/**
 * Taking a backup of an instance, and saying which ones exist.
 *
 * §10 lists backups among the things the panel does and nothing implemented them. This is creating and
 * listing, and deliberately not restoring: a restore drops a live database and overwrites a live
 * configuration, and the questions it raises — what happens to the volumes, what happens to a schema
 * newer than the dump, what happens to the operator's session while the panel's own container is being
 * replaced — have no answers yet. A half-built restore is worse than none, because it is the button
 * somebody presses at three in the morning.
 *
 * ## What is in one, and why the answer is not "everything in the install root"
 *
 * A backup is a database dump plus the install root's *configuration*. The dump comes out of `pg_dump`
 * run inside the postgres container rather than out of a client here, because the panel has no postgres
 * client and adding one would pin a client version against whatever server version the operator is
 * running — a mismatch that produces a dump the same operator cannot restore.
 *
 * The install root is not swept wholesale. It is a directory on a machine somebody administers, so
 * anything can end up in it, and a backup whose contents depend on what the operator left lying around
 * is a backup whose *secret* content is not knowable. Every path is therefore classified — see
 * {@link classify} — and one this does not recognise is either reported and left out, or, where it sits
 * in a directory that holds key material, read as key material.
 *
 * "In the install root" means what a name *opens*, not what it looks like: a link that leaves the root
 * is reported like anything else this cannot archive. See {@link installStore}, where the archive that
 * arrived at the host's `/etc/passwd` under the name `conf.d/api.json` is what made that the rule.
 *
 * ## The part that matters: a backup is a credential-bearing artifact
 *
 * There is no version of this that is not. The dump alone holds every account row on the instance, so
 * the archive is written 0600 and its directory 0700 whatever else is in it, and gzip is compression
 * rather than encryption however much a `.tar.gz` looks like an opaque blob.
 *
 * On top of that sits a second, different exposure. `secrets.json`, the `.env`, the object store's key
 * pair and the mint are not data *about* the instance — they are keys that keep working after the
 * archive has been copied somewhere else. A stolen dump is a breach that happened; stolen keys are a
 * machine somebody else still controls. So they are opt-in ({@link BackupOptions.includeSecrets}), they
 * are named in the manifest either way — as `secrets` when present and as `omitted` when not, because a
 * restore has to know what it will have to re-establish — and an archive that carries them says so *in
 * its filename*. An operator copying `argon-…-with-secrets.tar.gz` onto a laptop is doing something
 * visible; the whole point of the suffix is that they cannot do it by accident.
 *
 * Two files are in no backup at any setting. {@link CREDENTIAL_FILE} is the panel's own password hash
 * and {@link BOOTSTRAP_CODE_FILE} is the code that opened the panel before it had one. Neither is
 * instance state — whoever restores an instance sets a new panel password, which is the right answer
 * regardless — and including them would make every copy of every backup an offline Argon2id target for
 * the credential to a container that holds the docker socket. That is the one case where "a restore
 * would be useless without it" is simply false, so there is nothing to weigh.
 *
 * ## Ports
 *
 * Four, for the reason `setup.ts` gives for its own: the decisions worth testing here are about
 * framing, truncation and classification, and a test that needs a docker daemon and a real disk to
 * reach them is a test that gets skipped the first time CI has neither. {@link ContainerExec} is the
 * dump, {@link BackupStore} is every byte read or written, {@link BackupPorts.engine} finds the
 * database, and {@link BackupPorts.now} names the archive.
 */

/* ------------------------------------------------------------------------------------------------
 * Ports.
 * ---------------------------------------------------------------------------------------------- */

/**
 * A view over an ordinary `ArrayBuffer`, which is what every byte in this module is.
 *
 * Spelled out rather than left as a bare `Uint8Array`, because that type also admits a view over a
 * `SharedArrayBuffer` — which nothing in this process produces and which the hashing and compression
 * calls below will not take. Narrowing it once here is cheaper than a cast at each of them.
 */
export type Bytes = Uint8Array<ArrayBuffer>;

/** What to run inside a container, and as whom. */
export interface ExecSpec {
    readonly command: readonly string[];

    /**
     * The OS user inside the container. Absent means the image's own default, which is root.
     *
     * The dump names `postgres`, and that is what replaces a password: the official image's `pg_hba`
     * trusts local socket connections, so a dump taken over the socket needs no credential at all. The
     * alternative is `PGPASSWORD` in the exec's environment, which puts the database password into a
     * structure the daemon holds and `docker inspect` prints — one more place for it to live, in
     * exchange for nothing.
     */
    readonly user?: string;
}

/** Where an exec got to. `exitCode` is absent while it is still running. */
export interface ExecState {
    readonly running: boolean;
    readonly exitCode: number | undefined;
}

/**
 * One command inside a running container, in the three calls the Engine API splits it into.
 *
 * Three and not one because the third is not optional. `POST /exec/{id}/start` answers with the output
 * and closes; it carries no status, so a `pg_dump` that died half way through answers exactly like one
 * that finished, with fewer bytes. The exit code lives behind a separate inspection, and reading it is
 * the difference between a backup and a file that looks like one.
 *
 * {@link ContainerExec.start} hands back the body **as the daemon sent it**, still framed. The
 * de-framing is {@link demultiplex}, above the port on purpose: a port that returned tidy strings would
 * be a port whose fake could never be wrong about the format, which is the one thing here that is easy
 * to be wrong about.
 */
export interface ContainerExec {
    /** `POST /containers/{id}/exec`, returning the created exec's id. */
    create(container: string, spec: ExecSpec): Promise<string>;

    /** `POST /exec/{id}/start`, returning the raw response body. */
    start(exec: string): Promise<Bytes>;

    /** `GET /exec/{id}/json`. */
    inspect(exec: string): Promise<ExecState>;
}

/** One file out of the install root, with the mode it is stored under. */
export interface StoredFile {
    readonly bytes: Bytes;
    readonly mode: number;
}

/** One archive that already exists. */
export interface StoredArchive {
    readonly name: string;
    readonly bytes: number;
}

/**
 * The install root, as the four things a backup does to it.
 *
 * `read` returns the mode as well as the contents because the archive carries modes: extracting a
 * backup must not be what widens `secrets.json` from 0600 to whatever the extracting user's umask
 * allows. Reading the mode back out of a `tar` entry is the only way it survives the round trip.
 *
 * `taken` reports names and sizes and no timestamps, and {@link listBackups} derives the time from the
 * name instead. Modification times are rewritten by every tool that moves a file — `cp` without `-p`,
 * an rsync without `-t`, a restore from some other backup system — and a listing that reorders itself
 * because somebody copied the directory is a listing nobody can trust about the one thing it is for.
 */
export interface BackupStore {
    /** Every file under the install root, as paths relative to it, recursively. Directories excluded. */
    list(): Promise<readonly string[]>;

    /**
     * One file, or `undefined` when it is not there any more — or is not a file this may read.
     *
     * The second half is not a detail. A name in the install root can be a link to anywhere on the
     * host, and a backup is only the install root's configuration if what it opens is in the install
     * root. See {@link installStore}, where the answer to a link that leaves it is "absent", so the
     * path lands in {@link BackupContents.skipped} instead of in the archive.
     */
    read(path: string): Promise<StoredFile | undefined>;

    /**
     * Writes one archive into the backup directory at exactly `mode`, creating it 0700 if missing.
     *
     * Answers `false` — and writes nothing — when that name is already there, which is the half of the
     * already-taken refusal that cannot be raced. {@link createBackup} asks {@link BackupStore.taken}
     * first as well, but a `taken()` and a `put()` have a whole `pg_dump` between them.
     */
    put(name: string, bytes: Bytes, mode: number): Promise<boolean>;

    /** What is in the backup directory now. Empty when none has ever been taken. */
    taken(): Promise<readonly StoredArchive[]>;
}

export interface BackupPorts {
    /** Reads `/containers/json`, to find the database. The same port `docker.ts` defines and tests. */
    readonly engine: EngineRequest;

    readonly exec: ContainerExec;
    readonly store: BackupStore;

    /** The archive's name and the manifest's timestamp come from here, so a test can fix them. */
    readonly now: () => Date;

    /** Which compose project to back up. {@link COMPOSE_PROJECT} unless something else installed it. */
    readonly project?: string;
}

/* ------------------------------------------------------------------------------------------------
 * Constants that are contracts.
 * ---------------------------------------------------------------------------------------------- */

/** Bumped only when a reader of an older archive would have to branch on the difference. */
export const BACKUP_FORMAT = 1;

/** Where archives live, relative to the install root. */
export const BACKUP_DIRECTORY = "backups";

/** Names inside an archive. `install/` mirrors the install root, so a restore is a `cp -a` of it. */
export const MANIFEST_FILE = "manifest.json";
export const DATABASE_FILE = "database.sql";
export const INSTALL_PREFIX = "install";

/**
 * 0600 on the archive and 0700 on the directory, unconditionally.
 *
 * Not conditional on {@link BackupOptions.includeSecrets}, because the dump is in every one of these
 * and the dump is every account on the instance. What the two settings change is what an attacker can
 * still *do* with the archive, not whether reading it matters.
 */
export const ARCHIVE_MODE = 0o600;
export const BACKUP_DIRECTORY_MODE = 0o700;

/**
 * The largest dump this will wrap up, past which it refuses rather than tries.
 *
 * The exec port has already buffered the whole thing by the time this is checked, so the cap is not
 * protecting the first copy — it is protecting the two after it, since the tar and the gzip each hold
 * another. Three copies of a gigabyte inside the panel's container is the panel dying, and a panel that
 * dies taking a backup is also the thing that would have reported the failure. Refusing is worse than
 * succeeding and much better than that.
 *
 * The real fix is streaming the exec response through the archive instead of buffering it, which needs
 * a port shaped around a stream. Worth doing when somebody's instance actually reaches this.
 */
export const MAXIMUM_DUMP_BYTES = 1024 * 1024 * 1024;

/**
 * The last thing `pg_dump --format=plain` writes.
 *
 * Checked at the tail of the dump and nowhere else, because the phrase can occur anywhere: it is
 * ordinary text and the dump contains the instance's messages. Found at the end it says the stream ran
 * to completion; found anywhere it says one of the users typed it.
 *
 * This is the check that catches what the exit code misses. A connection dropped mid-stream leaves a
 * truncated dump, and a truncated SQL file restores most of the way and then stops — the shape of
 * "corrupt in a way nobody notices until they need it". If some future pg_dump stops emitting the line
 * this refuses backups until somebody updates it, which is the direction to fail in.
 */
export const DUMP_TERMINATOR = "PostgreSQL database dump complete";

/** How much of the tail to look in. Comfortably more than pg_dump's footer, far less than a row. */
const TERMINATOR_WINDOW = 512;

/* ------------------------------------------------------------------------------------------------
 * What goes in, and what does not.
 * ---------------------------------------------------------------------------------------------- */

/**
 * Files that are the keys to the machine rather than facts about it.
 *
 * `sfu/livekit.yaml` is deliberately **not** here despite being the SFU's configuration: LiveKit's API
 * key and secret arrive through `ARGON_SFU_KEYS` out of the `.env`, and the generated file says so in
 * its own first line. Classifying it as secret would be a guess made from the filename, and it is the
 * wrong guess — one that leaves the SFU unconfigured after a restore for no gain.
 */
const SECRET_FILES: ReadonlySet<string> = new Set([
    DEPLOYMENT.secretsFile,
    ENV_FILENAME,

    // The bundled object store's key pair, in the clear. The same pair is in `secrets.json` under
    // `Storage:AccessKey`, so leaving this one out would hide nothing that the other one does not.
    STORAGE_IDENTITIES,

    // Every generated secret, in the shape the installer reads them back in. Excluding it while
    // including `secrets.json` would be theatre.
    MINT_FILE,
]);

/**
 * Files that are in no backup at any setting.
 *
 * The panel's credential and the bootstrap code, for the reason at the top of this file: neither is
 * instance state, a restore establishes a new one, and a copy of either is an offline attack on the
 * front door of a container that holds the docker socket.
 */
const EXCLUDED_FILES: ReadonlySet<string> = new Set([CREDENTIAL_FILE, BOOTSTRAP_CODE_FILE]);

/** Single files at the root that are configuration. */
const CONFIGURATION_FILES: ReadonlySet<string> = new Set([COMPOSE_FILENAME]);

/**
 * Directories this looks inside at all.
 *
 * Derived from the constants that name the files within them rather than written out again, so that a
 * generator which starts writing into a fourth directory does not need this list edited to notice it.
 *
 * Being in one of these is what makes a path *considered*, not what makes it configuration — see
 * {@link classify}, where the default inside one of them is `secret`.
 */
const SWEPT_DIRECTORIES: ReadonlySet<string> = new Set(
    [DEPLOYMENT.confD, EDGE_STATIC_CONFIG, EDGE_DYNAMIC_CONFIG, SFU_CONFIG].map(topLevel),
);

/**
 * The files this installer itself writes into those directories.
 *
 * The only paths in there that can be called configuration without a guess, because they are the ones
 * `generate.ts` produced and their contents are known. Anything else arrived from somewhere this module
 * cannot ask about.
 */
const GENERATED_CONFIGURATION: ReadonlySet<string> = new Set([EDGE_STATIC_CONFIG, EDGE_DYNAMIC_CONFIG, SFU_CONFIG]);

/** Directories nothing looks inside. `backups` above all: an archive of the archives grows forever. */
const EXCLUDED_DIRECTORIES: ReadonlySet<string> = new Set([BACKUP_DIRECTORY]);

/**
 * Names that are key material wherever they turn up, lowercased because filesystems disagree.
 *
 * Short, and it is meant to be: the default inside a swept directory is already `secret`, so this list
 * is not what catches `traefik/acme.json` any more. What it is for is the one shape below that is still
 * generous — `conf.d/<feature>.json` — where a `.env` or a `secrets.json` an operator kept beside the
 * configuration it belongs to would otherwise read as a feature's settings.
 */
const KEY_MATERIAL_NAMES: ReadonlySet<string> = new Set(
    [ENV_FILENAME, fileName(DEPLOYMENT.secretsFile)].map((name) => name.toLowerCase()),
);

/**
 * Words that make a name key material even where the sweep is otherwise generous.
 *
 * Matched against the name's own words — split on anything that is not a letter or a digit, then
 * `startsWith` — rather than as substrings, so `certificates.json` and `keys.json` and `api-secret.txt`
 * are caught while `monkey.json` is not. Case-insensitively, because `ACME.json` and `Acme.Json` are
 * the same file to Traefik and to every filesystem an operator is likely to be on.
 *
 * `auth` and `identity` are deliberately absent, and they are the reason this list is not simply "every
 * word that sounds like a key". Both are real Argon role names, so `conf.d/auth.json` and
 * `conf.d/identity.json` are ordinary generated settings — reading them as secret would leave them out
 * of every plain backup and a restore short of the API's own configuration, which is the failure the
 * whole directory sweep exists to avoid.
 */
const KEY_MATERIAL_WORDS: readonly string[] = [
    "acme",
    "cert",
    "credential",
    "key",
    "passwd",
    "password",
    "pki",
    "private",
    "secret",
    "tls",
    "token",
];

/** Extensions that are a private key, a keystore or a password file in every ecosystem these serve. */
const KEY_MATERIAL_EXTENSIONS = /\.(key|pem|p12|pfx|jks|htpasswd|asc|gpg)$/i;

/** Whether a file's own name says it is key material, before anything about where it sits is asked. */
function isKeyMaterial(name: string): boolean {
    const lowered = name.toLowerCase();

    if (KEY_MATERIAL_NAMES.has(lowered) || KEY_MATERIAL_EXTENSIONS.test(lowered)) return true;

    return lowered
        .split(/[^a-z0-9]+/)
        .some((word) => KEY_MATERIAL_WORDS.some((material) => word.startsWith(material)));
}

/**
 * `conf.d/<feature>.json`, the one shape in a swept directory that is trusted without being named.
 *
 * It has to be a shape rather than a list because the feature names come out of the server binary's own
 * `--explain` at generation time — `generate.ts` writes one file per role that owns a section — so an
 * allowlist here would be a list this file cannot know and a new role would drop out of every backup.
 * The other two directories have no such excuse: the installer writes exactly one file into each.
 */
function isFeatureSettings(segments: readonly string[]): boolean {
    return segments.length === 2 && segments[0] === DEPLOYMENT.confD && /\.json$/i.test(segments[1] ?? "");
}

export type Classification = "configuration" | "secret" | "excluded" | "unknown";

/**
 * Which of the four kinds a path in the install root is.
 *
 * Exported because it is the whole of the decision this module exists to make deliberately, and because
 * the interesting case is the fourth one. An unknown path is neither included nor quietly dropped: it
 * comes back in {@link BackupContents.skipped}, so the operator who put a file there finds out it is
 * not being backed up on the day they take the backup rather than on the day they need it.
 *
 * A dotfile at the root is machinery — `setup.ts` stages into `.staging-…`, and a staging directory can
 * hold a half-written secrets file — so the rule for those is exclusion, and the one dotfile that is
 * genuinely instance state is named above and matched before the rule runs.
 *
 * ## Inside a swept directory the default is `secret`
 *
 * This used to read the other way round: everything under `traefik/`, `sfu/` and `conf.d/` was
 * configuration unless its name was on a list of known key names. A review found that list too trusting
 * and it was widened, and a second review found the widening was the same mistake one level down —
 * `traefik/acme.json` was caught and `traefik/ACME.json`, `traefik/acme-staging.json`,
 * `traefik/certificates.json` and `sfu/keys.json` were not. There is no list of key names that is
 * finished, because the operator writes the names.
 *
 * So the question is turned around. `traefik/` holds the ACME store and `sfu/` holds LiveKit's keys;
 * they are directories where key material lives, and a file this has never heard of in one of them is
 * treated as the dangerous thing rather than the safe one. Only two kinds of path in there are
 * configuration: the files the installer wrote itself ({@link GENERATED_CONFIGURATION}), and the
 * per-feature settings shape ({@link isFeatureSettings}) whose names this file genuinely cannot
 * enumerate.
 *
 * That costs something real and it is worth stating. A `traefik/README.md` an operator left behind now
 * reads as `secret`: out of the default backup, into the `-with-secrets` one, and named in the manifest
 * as `omitted` either way. Anything the installer starts generating into `traefik/` or `sfu/` without
 * being added to {@link GENERATED_CONFIGURATION} lands there too, and a restore of a plain backup would
 * come up short of it. The direction is deliberate: a file wrongly called secret is one the operator can
 * see in the manifest and ask for; a file wrongly called configuration is a private key inside an
 * archive whose name and manifest both say it holds none, already on somebody's laptop.
 */
export function classify(path: string): Classification {
    const normalised = path.replaceAll("\\", "/").replace(/^\.\//, "").replace(/^\/+/, "");

    if (normalised.length === 0) return "unknown";

    if (SECRET_FILES.has(normalised)) return "secret";
    if (EXCLUDED_FILES.has(normalised)) return "excluded";
    if (CONFIGURATION_FILES.has(normalised)) return "configuration";

    // A path that climbs out of the root cannot be classified, and guessing would put whatever it names
    // into an archive labelled as this instance's configuration.
    if (normalised.split("/").includes("..")) return "unknown";

    const segments = normalised.split("/");
    const head = topLevel(normalised);

    if (EXCLUDED_DIRECTORIES.has(head)) return "excluded";
    if (head.startsWith(".")) return "excluded";

    // `head !== normalised` is the difference between `conf.d/api.json` and a *file* called `conf.d`.
    // Something that took the name of a swept directory is not thereby anything.
    if (head === normalised || !SWEPT_DIRECTORIES.has(head)) return "unknown";

    // Written by this installer, so its contents are known rather than guessed at. First, because it is
    // the only claim here that rests on something other than a name — and because a file whose whole
    // path this module emitted must not be knocked out of a backup by a word in it.
    if (GENERATED_CONFIGURATION.has(normalised)) return "configuration";

    if (isKeyMaterial(fileName(normalised))) return "secret";

    // A dotfile further down is machinery for the same reason a dotfile at the root is — an editor's
    // swap file, a `.git`, a half-written rename — except that a `traefik/.dynamic.yml.swp` is not
    // something this can say is disposable, so it is reported as unrecognised rather than dropped. It is
    // not archived under either setting, so reading it as machinery leaks nothing; what it costs is a
    // line in `skipped` for the operator to act on.
    if (segments.some((segment) => segment.startsWith("."))) return "unknown";

    if (isFeatureSettings(segments)) return "configuration";

    // Unrecognised, in a directory where the ACME store and the SFU's keys live. See the note above for
    // why this is `secret` and not `configuration`, and what it costs.
    return "secret";
}

function topLevel(path: string): string {
    const cut = path.indexOf("/");

    return cut === -1 ? path : path.slice(0, cut);
}

/** The last segment of a `/`-separated path. Not `basename`, which also splits on a backslash. */
function fileName(path: string): string {
    return path.slice(path.lastIndexOf("/") + 1);
}

/* ------------------------------------------------------------------------------------------------
 * The daemon's framing.
 * ---------------------------------------------------------------------------------------------- */

export type Demultiplexed =
    | { readonly ok: true; readonly stdout: Bytes; readonly stderr: string }
    | { readonly ok: false; readonly problem: "unframed" | "truncated" };

/** Docker's stream identifiers, as `stdcopy` writes them into the first header byte. */
const STDOUT_FRAME = 1;
const STDERR_FRAME = 2;
const FRAME_HEADER = 8;

/**
 * Splits an exec response into the two streams the daemon interleaved into it.
 *
 * `POST /exec/{id}/start` without a TTY answers with `stdout` and `stderr` in one body, each chunk
 * behind an eight-byte header: one byte of stream identifier, three reserved zeroes, then the chunk's
 * length as a big-endian `uint32`. Concatenating the body as it arrives puts pg_dump's warnings
 * *inside* the SQL — a dump that restores until it reaches the first one, and a failure that surfaces
 * on the day it is needed rather than on the day it was taken.
 *
 * The three reserved bytes are what makes this safe to run against a body that might not be framed at
 * all. If a TTY was requested somewhere the daemon sends the raw stream, the identifier byte is then
 * whatever the dump starts with, and the odds of `0x01` or `0x02` followed by three zeroes at the head
 * of a SQL file are small enough to build on. Guessing wrong in the other direction is the one that has
 * to be avoided: accepting raw bytes as framed slices a dump apart at offsets read out of its own text.
 *
 * Both stream identifiers are demanded explicitly rather than "anything that is not stdout is stderr",
 * for the same reason — `0` is `stdin` and never appears in a response, so a zero here is evidence that
 * the body is not what it is being read as.
 *
 * `stderr` is decoded once, at the end, from the concatenated bytes. Decoding each frame as it arrived
 * would corrupt any multi-byte character the daemon happened to split across a chunk boundary, and the
 * message that gets split is a long one — which is the message that mattered.
 */
export function demultiplex(body: Bytes): Demultiplexed {
    const out: Bytes[] = [];
    const err: Bytes[] = [];

    const view = new DataView(body.buffer, body.byteOffset, body.byteLength);

    let at = 0;

    while (at < body.length) {
        if (body.length - at < FRAME_HEADER) return { ok: false, problem: "truncated" };

        const stream = body[at];

        if (stream !== STDOUT_FRAME && stream !== STDERR_FRAME) return { ok: false, problem: "unframed" };
        if (body[at + 1] !== 0 || body[at + 2] !== 0 || body[at + 3] !== 0) return { ok: false, problem: "unframed" };

        const size = view.getUint32(at + 4);

        at += FRAME_HEADER;

        // A declared length running past the end of the body is the shape of a connection that dropped
        // mid-chunk. It is the case worth being loud about: what arrived is a valid prefix of a dump,
        // and nothing else in the response says it is a prefix.
        if (body.length - at < size) return { ok: false, problem: "truncated" };

        (stream === STDOUT_FRAME ? out : err).push(body.subarray(at, at + size));

        at += size;
    }

    return { ok: true, stdout: concatenate(out), stderr: new TextDecoder().decode(concatenate(err)) };
}

function concatenate(chunks: readonly Bytes[]): Bytes {
    let total = 0;

    for (const chunk of chunks) total += chunk.length;

    const whole = new Uint8Array(total);
    let at = 0;

    for (const chunk of chunks) {
        whole.set(chunk, at);
        at += chunk.length;
    }

    return whole;
}

/* ------------------------------------------------------------------------------------------------
 * The dump.
 * ---------------------------------------------------------------------------------------------- */

/**
 * `pg_dump`, as the flags a restore two years from now will need.
 *
 * Exported because every flag here is a decision about a restore nobody has written, and they are
 * easier to argue with in one list than buried in the call:
 *
 *  - `--format=plain`, so restoring is `psql < file` with no `pg_restore` whose version has to match
 *    anything, and so {@link DUMP_TERMINATOR} exists to be checked at all. The custom format compresses
 *    better and cannot be verified or read without the tool that wrote it.
 *  - `--no-owner --no-privileges`, because the roles a dump names are roles the restore target may not
 *    have, and a dump that stops on `role "argon" does not exist` stops a third of the way in.
 *  - `--clean --if-exists`, because restoring over something is the case that happens, and without
 *    `--if-exists` the generated `DROP`s are errors on a database that is empty.
 *  - `--quote-all-identifiers`, because the dump may be restored into a later major version in which a
 *    word that was ordinary has become reserved.
 */
export function dumpCommand(): readonly string[] {
    return [
        "pg_dump",
        `--username=${DEPLOYMENT.database.user}`,
        `--dbname=${DEPLOYMENT.database.name}`,
        "--format=plain",
        "--no-owner",
        "--no-privileges",
        "--clean",
        "--if-exists",
        "--quote-all-identifiers",
    ];
}

export type DatabaseLookup =
    | { readonly found: true; readonly id: string }
    | { readonly found: false; readonly reason: "missing" | "stopped" };

interface ContainerRow {
    readonly Id?: unknown;
    readonly State?: unknown;
}

/**
 * The project's postgres container, by compose's labels rather than by its name.
 *
 * `all=true` with the running check made here, rather than a `status` filter on the daemon, because the
 * two failures deserve different sentences: an instance that was never installed has no container, and
 * one whose database is down has a container that needs starting rather than reinstalling. A
 * server-side filter collapses both into an empty list.
 */
export async function databaseContainer(project: string, request: EngineRequest): Promise<DatabaseLookup> {
    const filters = JSON.stringify({
        label: [`${COMPOSE_PROJECT_LABEL}=${project}`, `${COMPOSE_SERVICE_LABEL}=${DEPLOYMENT.hosts.postgres}`],
    });

    const rows = await request(`/containers/json?all=true&filters=${encodeURIComponent(filters)}`);

    if (!Array.isArray(rows) || rows.length === 0) return { found: false, reason: "missing" };

    for (const row of rows as ContainerRow[]) {
        const id = typeof row?.Id === "string" ? row.Id : undefined;

        if (id !== undefined && row?.State === "running") return { found: true, id };
    }

    return { found: false, reason: "stopped" };
}

export type DumpOutcome =
    | { readonly ok: true; readonly sql: Bytes; readonly warnings: string }
    | { readonly ok: false; readonly reason: DumpRefusal; readonly detail: string };

export type DumpRefusal = "dump-failed" | "dump-truncated" | "unframed" | "still-running" | "too-large";

/**
 * One dump, taken inside the container and checked three ways before it is called one.
 *
 * The exit code, the framing and the terminator each catch a different failure, and none of them
 * catches the others'. A `pg_dump` that could not connect exits non-zero with an empty stdout; a
 * connection that dropped mid-stream exits zero with a valid prefix; a body that came back unframed is
 * a dump with eight bytes of garbage every few kilobytes. Any one of the three checks alone leaves an
 * archive that passes inspection and fails at the moment it is needed.
 *
 * `warnings` is `pg_dump`'s stderr on a run that succeeded, which is not nothing: it is where a
 * `circular foreign-key constraint` notice comes out, and that notice is about the restore rather than
 * about the dump.
 *
 * `ceiling` is a parameter only so that the refusal can be reached from a test without allocating a
 * gigabyte to do it — a review pointed out that the cap was the one guard between a large instance and
 * the panel dying, and that nothing exercised it. Every caller passes {@link MAXIMUM_DUMP_BYTES}.
 */
export async function dumpDatabase(
    container: string,
    exec: ContainerExec,
    ceiling: number = MAXIMUM_DUMP_BYTES,
): Promise<DumpOutcome> {
    const id = await exec.create(container, { command: dumpCommand(), user: "postgres" });
    const body = await exec.start(id);
    const streams = demultiplex(body);

    if (!streams.ok)
        return streams.problem === "unframed"
            ? {
                  ok: false,
                  reason: "unframed",
                  detail: "The daemon answered without stream framing, which means a TTY was attached. A dump read that way has frame headers spliced through it.",
              }
            : {
                  ok: false,
                  reason: "dump-truncated",
                  detail: "The dump stopped part-way through a chunk. What arrived is the beginning of a dump and nothing more.",
              };

    // Asked after the body has been read, because reading the body is what lets the exec finish:
    // inspecting first reports `Running` on every dump that has anything to say.
    const state = await exec.inspect(id);

    if (state.running || state.exitCode === undefined)
        return { ok: false, reason: "still-running", detail: "pg_dump had not finished when its output ended." };

    if (state.exitCode !== 0)
        return {
            ok: false,
            reason: "dump-failed",
            detail: `pg_dump exited ${state.exitCode}: ${streams.stderr.trim() || "it said nothing about why."}`,
        };

    if (streams.stdout.length > ceiling)
        return {
            ok: false,
            reason: "too-large",
            detail: `The dump is ${streams.stdout.length} bytes, past the ${ceiling} this can archive without running the panel out of memory.`,
        };

    if (!endsWithTerminator(streams.stdout))
        return {
            ok: false,
            reason: "dump-truncated",
            detail: `pg_dump exited cleanly but its output does not end with '${DUMP_TERMINATOR}', so the stream was cut short.`,
        };

    return { ok: true, sql: streams.stdout, warnings: streams.stderr };
}

function endsWithTerminator(sql: Bytes): boolean {
    const tail = sql.subarray(Math.max(0, sql.length - TERMINATOR_WINDOW));

    return new TextDecoder().decode(tail).includes(DUMP_TERMINATOR);
}

/* ------------------------------------------------------------------------------------------------
 * Naming, and reading a name back.
 * ---------------------------------------------------------------------------------------------- */

/** Says out loud, in the filename, that this one carries the machine's keys. */
export const SECRETS_SUFFIX = "-with-secrets";

export const ARCHIVE_EXTENSION = ".tar.gz";

const NAME_PATTERN = /^argon-(\d{4})(\d{2})(\d{2})T(\d{2})(\d{2})(\d{2})Z(-with-secrets)?\.tar\.gz$/;

/**
 * What an archive is called.
 *
 * UTC with the separators taken out, because these files get copied to wherever the operator keeps
 * backups and a colon is a filename Windows will not open and an S3 key that needs escaping. Sorting
 * lexicographically is then the same as sorting by time, which is what makes a plain directory listing
 * useful without this module.
 *
 * The suffix is the visible half of the secrets decision. It is on the filename rather than only in the
 * manifest because the manifest is inside the gzip, and the moment that matters — dragging the file
 * onto a laptop, attaching it to a ticket — is a moment when nobody has opened it.
 */
export function backupName(at: Date, containsSecrets: boolean): string {
    const stamp = at.toISOString().replace(/[-:]/g, "").replace(/\.\d+Z$/, "Z");

    return `argon-${stamp}${containsSecrets ? SECRETS_SUFFIX : ""}${ARCHIVE_EXTENSION}`;
}

export interface BackupSummary {
    readonly name: string;

    /** ISO 8601, from the name. See {@link BackupStore.taken} for why not from a modification time. */
    readonly takenAt: string;

    readonly bytes: number;
    readonly containsSecrets: boolean;
}

/**
 * A name read back into what it says, or nothing when it says nothing.
 *
 * Anything that does not match is not reported as a backup with a guessed date. The backup directory is
 * a directory on somebody's machine — an editor's swap file, a half-finished `scp`, a `.tmp` from a
 * sync tool — and a listing that offers those as restorable points is a listing that gets acted on.
 */
export function parseBackupName(name: string): Omit<BackupSummary, "bytes" | "name"> | undefined {
    const match = NAME_PATTERN.exec(name);

    if (match === null) return undefined;

    const [, year, month, day, hour, minute, second, secrets] = match;
    const takenAt = `${year}-${month}-${day}T${hour}:${minute}:${second}Z`;

    // A stamp can be shaped right and still be a date that does not exist. Checked by round trip rather
    // than by `Number.isNaN`, because the engines disagree: month 13 is rejected, and the 31st of
    // February is quietly rolled forward into March — which would report a backup as having been taken
    // on a day it was not, and sort it against the real ones accordingly.
    const parsed = new Date(takenAt);

    if (Number.isNaN(parsed.getTime())) return undefined;
    if (parsed.toISOString() !== `${year}-${month}-${day}T${hour}:${minute}:${second}.000Z`) return undefined;

    return { takenAt, containsSecrets: secrets !== undefined };
}

/** Every archive in the backup directory, newest first. */
export async function listBackups(store: BackupStore): Promise<readonly BackupSummary[]> {
    const summaries: BackupSummary[] = [];

    for (const archive of await store.taken()) {
        const parsed = parseBackupName(archive.name);

        if (parsed === undefined) continue;

        summaries.push({ name: archive.name, bytes: archive.bytes, ...parsed });
    }

    return summaries.sort((left, right) => right.takenAt.localeCompare(left.takenAt));
}

/* ------------------------------------------------------------------------------------------------
 * The archive itself.
 * ---------------------------------------------------------------------------------------------- */

export interface TarEntry {
    readonly path: string;
    readonly bytes: Bytes;
    readonly mode: number;
}

const BLOCK = 512;

/** ustar's name field. Long paths could use the 155-byte prefix; refusing is honest and ours are short. */
const MAXIMUM_NAME = 100;

/** The size field is eleven octal digits, so this is what the format can describe at all. */
const MAXIMUM_ENTRY = 0o77777777777;

/**
 * A POSIX ustar archive, for the caller to gzip.
 *
 * `tar` rather than a container format of this project's own, and that is the decision that makes the
 * missing restore survivable: an operator holding a backup and no panel has `tar -xf` and `psql <` and
 * needs nothing from us. A JSON envelope written here would be a format whose only reader is the code
 * that has not been written.
 *
 * Every entry gets uid and gid zero and the same timestamp rather than whatever the panel container's
 * process happens to be, so two backups of an unchanged instance differ only where the instance
 * differs. The mode is the one thing carried through faithfully — `secrets.json` extracts as 0600, or
 * the extraction is what exposed it.
 */
export function tar(entries: readonly TarEntry[], modified: Date): Bytes {
    const blocks: Bytes[] = [];
    const seconds = Math.floor(modified.getTime() / 1000);

    for (const entry of entries)
        blocks.push(header(entry, seconds), entry.bytes, new Uint8Array(padding(entry.bytes.length)));

    // Two zero blocks are the end-of-archive marker. GNU tar and bsdtar both accept an archive that
    // stops there rather than being padded out to a full 10 KiB record; only tape drives needed that.
    blocks.push(new Uint8Array(BLOCK * 2));

    return concatenate(blocks);
}

function padding(length: number): number {
    return (BLOCK - (length % BLOCK)) % BLOCK;
}

function header(entry: TarEntry, seconds: number): Bytes {
    const encoder = new TextEncoder();
    const name = encoder.encode(entry.path);

    if (name.length > MAXIMUM_NAME) throw new Error(`'${entry.path}' is too long for a ustar name field`);

    // Silently writing a truncated size field would produce an archive that extracts to the wrong
    // length and then reads the next header out of the middle of this entry's data.
    if (entry.bytes.length > MAXIMUM_ENTRY) throw new Error(`'${entry.path}' is too large for a ustar archive`);

    const block = new Uint8Array(BLOCK);
    const put = (offset: number, text: string): void => void block.set(encoder.encode(text), offset);

    block.set(name, 0);
    put(100, octal(entry.mode & 0o7777, 8));
    put(108, octal(0, 8));
    put(116, octal(0, 8));
    put(124, octal(entry.bytes.length, 12));
    put(136, octal(seconds, 12));

    // The checksum is defined over a header whose own checksum field is eight spaces, so the spaces go
    // in before the sum and the digits go in after it. Leaving the field zeroed instead produces a
    // checksum that every tar rejects.
    block.fill(0x20, 148, 156);

    block[156] = 0x30;

    put(257, "ustar");
    block[262] = 0;
    put(263, "00");
    put(265, "root");
    put(297, "root");

    let sum = 0;

    for (const byte of block) sum += byte;

    // Six digits and a NUL, which leaves the eighth byte as the space already sitting there.
    put(148, octal(sum, 7));

    return block;
}

function octal(value: number, width: number): string {
    return `${value.toString(8).padStart(width - 1, "0")}\0`;
}

/* ------------------------------------------------------------------------------------------------
 * Creating one.
 * ---------------------------------------------------------------------------------------------- */

export interface BackupOptions {
    /**
     * Whether to put the machine's keys in the archive.
     *
     * Off by default, and the default is the decision: the usual reason to take a backup is to have the
     * data, and the usual thing done with the file afterwards is to move it off the machine. On means
     * the archive can rebuild an instance that talks to the same object store with the same signing
     * keys; it also means every copy of that file is those keys.
     */
    readonly includeSecrets?: boolean;
}

/** What the backup did with every path it found. Names only — never contents. */
export interface BackupContents {
    readonly configuration: readonly string[];

    /** Secret-bearing files that went in. Empty unless {@link BackupOptions.includeSecrets}. */
    readonly secrets: readonly string[];

    /** Secret-bearing files deliberately left out, so a restore knows what it must re-establish. */
    readonly omitted: readonly string[];

    /** Never in a backup at any setting. */
    readonly excluded: readonly string[];

    /** Unrecognised, and so not archived. The operator who left them there should hear about it. */
    readonly skipped: readonly string[];
}

/** One entry as the manifest records it, so an archive can be checked years later without this code. */
export interface ManifestEntry {
    readonly path: string;
    readonly bytes: number;
    readonly sha256: string;
}

export interface BackupManifest {
    readonly format: number;
    readonly takenAt: string;
    readonly project: string;
    readonly containsSecrets: boolean;
    readonly contents: BackupContents;
    readonly entries: readonly ManifestEntry[];

    /** `pg_dump`'s stderr on a successful run. Empty most of the time; about the restore when not. */
    readonly databaseWarnings: string;
}

export type BackupOutcome =
    | { readonly ok: true; readonly backup: BackupSummary; readonly contents: BackupContents }
    | { readonly ok: false; readonly reason: BackupRefusal; readonly detail: string };

export type BackupRefusal = DumpRefusal | "no-database" | "database-stopped" | "already-taken" | "path-too-long";

/**
 * Takes one backup: dump, then configuration, then one archive written once.
 *
 * The dump goes first because it is the part that fails — a database that is down, a `pg_dump` that
 * cannot connect — and failing before anything has been read means the refusal costs nothing and leaves
 * nothing behind. The archive is assembled whole in memory and handed to {@link BackupStore.put} in a
 * single call for the reason `setup.ts` writes through a rename: a backup file that exists and is half
 * an archive is worse than no file at all, because it is a file somebody will find and trust.
 */
export async function createBackup(ports: BackupPorts, options: BackupOptions = {}): Promise<BackupOutcome> {
    const project = ports.project ?? COMPOSE_PROJECT;
    const includeSecrets = options.includeSecrets === true;

    const database = await databaseContainer(project, ports.engine);

    if (!database.found)
        return database.reason === "missing"
            ? {
                  ok: false,
                  reason: "no-database",
                  detail: `No ${DEPLOYMENT.hosts.postgres} container belongs to the '${project}' project. There is nothing installed here to back up.`,
              }
            : {
                  ok: false,
                  reason: "database-stopped",
                  detail: `The ${DEPLOYMENT.hosts.postgres} container exists but is not running, and a dump has to be taken from a database that is up.`,
              };

    const dump = await dumpDatabase(database.id, ports.exec);

    if (!dump.ok) return { ok: false, reason: dump.reason, detail: dump.detail };

    const at = ports.now();
    const name = backupName(at, includeSecrets);

    // Two backups in the same second is a double click, and the second `put` would replace the first.
    // Replacing a backup is the one thing this must never do quietly. Asked here so that the refusal
    // costs nothing to reach; the guarantee itself is in `put`, which is where it can be atomic.
    if ((await ports.store.taken()).some((existing) => existing.name === name)) return alreadyTaken(name);

    const configuration: string[] = [];
    const secrets: string[] = [];
    const omitted: string[] = [];
    const excluded: string[] = [];
    const skipped: string[] = [];

    const files: TarEntry[] = [];
    const encoder = new TextEncoder();

    // The archive's own name, without the extension, as the single directory everything sits under.
    // Extracting a tar whose entries are `conf.d/…` into a home directory overwrites whatever is there;
    // one enclosing directory is what makes `tar -xf` in the wrong place a recoverable mistake.
    const prefix = name.slice(0, name.length - ARCHIVE_EXTENSION.length);

    // Sorted, so that two backups of an unchanged instance produce the same archive and a directory
    // listing whose order changed cannot look like a configuration change.
    for (const path of [...(await ports.store.list())].sort()) {
        const kind = classify(path);

        if (kind === "excluded") {
            excluded.push(path);
            continue;
        }

        if (kind === "unknown") {
            skipped.push(path);
            continue;
        }

        if (kind === "secret" && !includeSecrets) {
            omitted.push(path);
            continue;
        }

        const entry = `${prefix}/${INSTALL_PREFIX}/${path}`;

        // The name this same file would carry in a `-with-secrets` archive, which is the longer of the
        // two and therefore the budget both kinds are measured against.
        const widest = includeSecrets ? entry : `${prefix}${SECRETS_SUFFIX}/${INSTALL_PREFIX}/${path}`;

        // ustar's name field is 100 bytes and `header` throws past it. Thrown from here that is a
        // rejected promise out of a function whose whole contract is a {@link BackupOutcome}, so the
        // panel gets a stack trace where every other failure is a sentence — and it happens after the
        // dump has been taken.
        //
        // Measured against the wider name because the threshold used to differ between the two kinds of
        // backup by the 13 bytes of {@link SECRETS_SUFFIX}: a path in that window let an install take
        // plain backups for months and made the first deliberate, key-bearing one — the one taken
        // before a migration, at the moment it is least welcome — the one that died. A limit that moves
        // with an option is a limit nobody can act on, so this refuses the same path at the same length
        // whatever was asked for, and the operator meets it on an ordinary backup.
        //
        // Refused rather than skipped: an over-long path is not the transient "gone by the time we read
        // it" below, it is permanent, and quietly leaving a file out of every future archive is the
        // failure this module exists to avoid. Shortening the in-archive prefix instead — the manifest
        // already records `containsSecrets`, so the directory inside the tar need not carry the suffix —
        // was rejected because it costs the property that an extracted directory names the archive it
        // came out of, and two archives of the same second would then extract over one another.
        if (encoder.encode(widest).length > MAXIMUM_NAME)
            return {
                ok: false,
                reason: "path-too-long",
                detail: `'${path}' becomes '${widest}' in an archive carrying secrets, which is past the ${MAXIMUM_NAME} bytes a tar name field holds. Every backup is measured against that longer name, so that this refusal cannot wait for the one backup you needed to work. Shorten the name or move the file out of the install root.`,
            };

        const file = await ports.store.read(path);

        // Named by `list` and gone by `read`: an apply running underneath this, or a file the panel
        // cannot open. Reporting it as skipped beats failing a whole backup over one file, and beats an
        // archive that quietly has a hole in it.
        if (file === undefined) {
            skipped.push(path);
            continue;
        }

        (kind === "secret" ? secrets : configuration).push(path);
        files.push({ path: entry, bytes: file.bytes, mode: file.mode });
    }

    const contents: BackupContents = { configuration, secrets, omitted, excluded, skipped };

    const dumpEntry: TarEntry = {
        path: `${prefix}/${DATABASE_FILE}`,
        bytes: dump.sql,

        // The dump is every account row on the instance, so it carries the mode `secrets.json` carries
        // rather than the one the configuration files carry, whatever else ended up in this archive.
        mode: ARCHIVE_MODE,
    };

    const manifest: BackupManifest = {
        format: BACKUP_FORMAT,
        takenAt: at.toISOString(),
        project,
        containsSecrets: includeSecrets,
        contents,
        entries: [dumpEntry, ...files].map(describe),
        databaseWarnings: dump.warnings,
    };

    const manifestEntry: TarEntry = {
        path: `${prefix}/${MANIFEST_FILE}`,
        bytes: encoder.encode(`${JSON.stringify(manifest, null, 2)}\n`),
        mode: ARCHIVE_MODE,
    };

    // The manifest first, so that anything reading the archive as a stream gets the description before
    // the hundred megabytes it describes.
    const archive = Bun.gzipSync(tar([manifestEntry, dumpEntry, ...files], at));

    // The same refusal as the check above and, unlike it, one that holds however two of these
    // interleave: the check ran before a dump that takes long enough for a second click to overtake it.
    if (!(await ports.store.put(name, archive, ARCHIVE_MODE))) return alreadyTaken(name);

    return {
        ok: true,
        backup: { name, takenAt: at.toISOString(), bytes: archive.length, containsSecrets: includeSecrets },
        contents,
    };
}

/** Said in one place, because it is reached from both sides of the dump and has to read the same. */
function alreadyTaken(name: string): BackupOutcome {
    return {
        ok: false,
        reason: "already-taken",
        detail: `${name} already exists. A second backup within the same second would overwrite it; wait a moment and take it again.`,
    };
}

/**
 * The manifest's record of one entry.
 *
 * A digest per entry rather than one over the whole archive, because the question asked of an old
 * backup is "is this dump intact", and an archive-wide checksum can only answer "something changed".
 * The paths recorded are the paths inside the archive, so checking one is `sha256sum` against what came
 * out of `tar -x` and nothing else.
 */
function describe(entry: TarEntry): ManifestEntry {
    return {
        path: entry.path,
        bytes: entry.bytes.length,
        sha256: new Bun.CryptoHasher("sha256").update(entry.bytes).digest("hex"),
    };
}

/* ------------------------------------------------------------------------------------------------
 * The real exec port.
 * ---------------------------------------------------------------------------------------------- */

/**
 * HTTP over the docker socket, the same transport `docker.ts` uses for reads.
 *
 * Separate from `dockerEngine` because these are POSTs with bodies and one of them answers with a
 * stream rather than with JSON. Folding that into a port whose whole signature is
 * `(path) => Promise<unknown>` would mean a caller that has to know which paths lie about their type.
 *
 * **`Tty` is false in both calls and has to stay that way.** A TTY is what makes the daemon send the
 * raw stream instead of the framed one, and the raw stream merges `stderr` into `stdout` — which puts
 * pg_dump's warnings inside the SQL, in a dump that looks completely normal until a restore reaches
 * one. See {@link demultiplex}, which refuses such a body rather than parsing it. `AttachStderr` is the
 * quieter half of the same decision: without it the dump still succeeds and `warnings` is empty
 * forever, so the one notice that is about the *restore* never reaches the manifest.
 *
 * That used to be a paragraph and nothing else — a review pointed out that flipping either flag was a
 * change the suite would not notice, and it was right. The test swaps `fetch` itself for the duration
 * and reads the request bodies, which is the only place a function whose whole job is to *be* the
 * boundary can be tested from; `docker.ts` left its own transport untested on the argument that a fake
 * there proves nothing, and the difference is that these two flags are decisions rather than plumbing.
 * What the fake still cannot see is whether the daemon behaves as documented when it reads them.
 */
export function dockerExec(socket: string): ContainerExec {
    const post = async (path: string, body: unknown): Promise<Response> => {
        const response = await fetch(`http://docker${path}`, {
            unix: socket,
            method: "POST",
            headers: { "content-type": "application/json" },
            body: JSON.stringify(body),
        });

        if (!response.ok) throw new Error(`docker answered ${response.status} for ${path}`);

        return response;
    };

    return {
        async create(container, spec) {
            const response = await post(`/containers/${container}/exec`, {
                AttachStdin: false,
                AttachStdout: true,
                AttachStderr: true,
                Tty: false,
                Cmd: [...spec.command],
                ...(spec.user === undefined ? {} : { User: spec.user }),
            });

            const created = (await response.json()) as { Id?: unknown };

            if (typeof created?.Id !== "string") throw new Error("docker created an exec without an id");

            return created.Id;
        },

        async start(exec) {
            const response = await post(`/exec/${exec}/start`, { Detach: false, Tty: false });

            return new Uint8Array(await response.arrayBuffer());
        },

        async inspect(exec) {
            const response = await fetch(`http://docker/exec/${exec}/json`, { unix: socket });

            if (!response.ok) throw new Error(`docker answered ${response.status} for /exec/${exec}/json`);

            const state = (await response.json()) as { Running?: unknown; ExitCode?: unknown };

            return {
                running: state?.Running === true,
                exitCode: typeof state?.ExitCode === "number" ? state.ExitCode : undefined,
            };
        },
    };
}

/* ------------------------------------------------------------------------------------------------
 * The real store.
 * ---------------------------------------------------------------------------------------------- */

/**
 * The install root on disk, and the backup directory inside it.
 *
 * Shaped after `setup.ts`'s `localStore` and for the same two reasons: the archive is written under a
 * temporary name and only then given the name it will be found under, so nothing ever finds a file that
 * is half an archive; and the mode is set with an explicit `chmod` rather than left to `writeFile`'s
 * argument, because the umask this process inherited subtracts from that argument and never adds to it.
 * 0600 survives any umask. Where `setup.ts` renames, this links — see {@link BackupStore.put}, which
 * has a name it must not take from an archive that already holds it.
 *
 * The directory is chmod'ed on every write rather than only on the one that created it. A backup
 * directory that is group-readable is every account row on the instance, group-readable, and the way
 * that happens is a `mkdir` under a permissive umask months before anyone looks.
 *
 * Containment is asked twice, of two different things. {@link inside} asks it of the name, and throws:
 * a `..` in a path is a caller with a bug or an HTTP request that should never have reached here.
 * {@link opens} asks it of what the name resolves to, and answers absent: a link is something the
 * operator put in their own install root, so it belongs in {@link BackupContents.skipped} rather than
 * failing the backup. One check without the other is what let an archive carry `/etc/passwd`.
 *
 * Paths come back joined with `/` on every platform. They become names inside a `tar`, and a Windows
 * separator in a tar entry is not a directory — it is one file whose name contains a backslash, which
 * is what the operator extracting the archive on the server would get.
 */
export function installStore(root: string): BackupStore {
    const directory = join(root, BACKUP_DIRECTORY);

    /** Whether one path is under another. Two strings and no disk; the disk is {@link opens} below. */
    const under = (base: string, target: string): boolean => {
        const step = relative(base, target);

        return step.length > 0 && !step.startsWith("..") && !isAbsolute(step);
    };

    /**
     * Where a *name* is allowed to land, which is the lexical half of the question.
     *
     * `read` is a port, and a port is a thing that will one day be handed a path from somewhere else —
     * §10's download and restore each take one out of an HTTP request. A name that climbs out with `..`
     * is a caller with a bug or an attacker, so it throws rather than answering "not there": those two
     * deserve a stack trace, and nothing the walk produces can reach this.
     */
    const inside = (path: string): string => {
        const target = resolve(root, path);

        if (!under(root, target)) throw new Error(`'${path}' is outside ${root}`);

        return target;
    };

    /**
     * The install root with its own links already followed, resolved once and kept.
     *
     * Both sides of the question below have to be resolved or neither: a `mkdtemp` root on macOS sits
     * under `/var/folders`, which is itself a link into `/private/var`, so measuring a resolved file
     * against an unresolved root would refuse every file on the machine. Kept rather than asked per
     * read, because the root does not move underneath a backup.
     */
    let resolvedRoot: Promise<string> | undefined;

    /**
     * What a name actually opens, or nothing when that turns out to be outside the install root.
     *
     * {@link inside} is lexical and both `stat` and `readFile` follow links, so a link at
     * `conf.d/api.json` pointing at `/etc/passwd` passed that check and the host's file went into an
     * archive named `argon-….tar.gz` — no {@link SECRETS_SUFFIX} — whose manifest listed it under
     * `configuration`. Anybody who can write into the install root can leave that link, and after an
     * upgrade that includes anything the panel itself writes there.
     *
     * The resolved name is classified again, which closes the same escape one step short of the root's
     * edge: `conf.d/api.json` pointing at `../panel.credential` lands *inside* the root, so containment
     * lets it through and {@link classify} only ever saw the name it was given. The panel's own password
     * hash is the one thing this module promises is in no archive at any setting, and a link must not be
     * the way round that promise.
     *
     * The hard link is the case this does not catch, having no target to resolve: it is a second name
     * for the same inode. It costs an attacker read access to the file already and a filesystem shared
     * with it, and on Linux `fs.protected_hardlinks` — on by default — refuses the ones that matter.
     */
    const opens = async (target: string): Promise<string | undefined> => {
        const resolved = await realpath(target);
        const base = await (resolvedRoot ??= realpath(root));

        if (!under(base, resolved)) return undefined;

        return classify(relative(base, resolved)) === "excluded" ? undefined : resolved;
    };

    const walk = async (from: string, prefix: string, found: string[]): Promise<void> => {
        for (const entry of await readdir(from, { withFileTypes: true })) {
            const path = prefix.length === 0 ? entry.name : `${prefix}/${entry.name}`;

            // Not descended into rather than merely classified as excluded: the archives are the largest
            // files on the box and there is nothing in them a listing needs.
            if (entry.isDirectory()) {
                if (path !== BACKUP_DIRECTORY) await walk(join(from, entry.name), path, found);

                continue;
            }

            // Everything else is listed, including the entries that are neither a file nor a directory.
            // A symlink is one of those — `readdir` reports the link and not what it points at — and a
            // review found that dropping them here made a symlinked `conf.d/api.json`, which is what
            // keeping configuration under version control elsewhere looks like, vanish out of every
            // backup *and* out of `skipped`, `omitted` and `excluded`. The archive was complete by its
            // own digests and missing the API's configuration, and nobody would learn that until a
            // restore. `read` is where it is decided: a link to a file *inside the root* is read
            // through, and everything else — a link out of the root, a link to a directory, a dangling
            // one — comes back absent and is reported as skipped.
            found.push(path);
        }
    };

    return {
        async list() {
            const found: string[] = [];

            await walk(root, "", found);

            return found;
        },

        async read(path) {
            const named = inside(path);

            try {
                const target = await opens(named);

                // A link that leaves the root is answered as absent rather than thrown, because the
                // walk lists links on purpose: this is a name the operator put there, so it belongs in
                // `skipped`, where they are told on the day the backup is taken. Throwing would fail a
                // whole backup over one link, out of a function whose contract is a `BackupOutcome`.
                //
                // What it costs is the case the walk was fixed for: an operator who keeps
                // `conf.d/api.json` under version control elsewhere and links it into place no longer
                // has it in any backup. That is a real loss and it is still the trade to make — it is
                // reported every single time rather than discovered at a restore, and the alternative
                // is an archive whose contents are whatever the links in the install root point at.
                if (target === undefined) return undefined;

                const facts = await stat(target);

                // `stat` and not `lstat`, though after `realpath` there is no link left to follow —
                // what this asks is whether the thing at the end is a regular file. A directory, a
                // socket or a fifo is absent as far as a backup is concerned, which puts it in
                // `skipped` instead of throwing EISDIR out of the middle of an otherwise fine backup.
                if (!facts.isFile()) return undefined;

                // Opened by its resolved path and not by the name, so the link cannot be re-pointed
                // between the check and the read: there is no link left in what is being opened.
                const contents = await readFile(target);

                // Copied into an array buffer of its own. `readFile` hands back a `Buffer` over Node's
                // shared pool, and a view over a pool is a view whose bytes something else may reuse.
                return { bytes: new Uint8Array(contents), mode: facts.mode & 0o777 };
            } catch (cause) {
                // A dangling link answers ENOENT from `realpath`, which is the same "gone" the walk's
                // race produces and gets the same answer.
                if ((cause as NodeJS.ErrnoException).code === "ENOENT") return undefined;

                throw cause;
            }
        },

        async put(name, bytes, mode) {
            // A name, not a path: this one is joined onto the backup directory and nothing else looked
            // at it. A review demonstrated `put("../compose.yaml", …)` replacing the live compose file
            // on disk while `read("../compose.yaml")` on the same store refused — the containment check
            // was on one method of the pair. Nothing reaches this with a hostile name today, because
            // `createBackup` passes a name it generated; §10's download, delete and restore each take a
            // backup name out of an HTTP request, and the first of them would otherwise turn a panel
            // session into an arbitrary overwrite under the install root and above it.
            if (name.length === 0 || /[\\/]/.test(name) || name === "." || name === "..")
                throw new Error(`'${name}' is not a backup file name`);

            await mkdir(directory, { recursive: true, mode: BACKUP_DIRECTORY_MODE });
            await chmod(directory, BACKUP_DIRECTORY_MODE);

            const target = join(directory, name);
            const temporary = `${target}.${randomBytes(6).toString("hex")}.partial`;

            await writeFile(temporary, bytes, { mode });
            await chmod(temporary, mode);

            // `link` rather than `rename`, and that is the difference between a promise and a hope.
            // `rename` replaces whatever is at the target without a word, so the already-taken refusal
            // in `createBackup` was a check with a whole `pg_dump` between it and the write: two clicks
            // a moment apart both saw an empty directory, and the second archive silently replaced the
            // first — the one thing this module says it must never do. `link` fails with EEXIST however
            // the two interleave. It costs a filesystem that supports hard links, which every ext4, xfs,
            // overlay and NTFS install root does and an exotic network mount may not; refusing to write
            // there is a louder failure than overwriting a backup somewhere else.
            try {
                await link(temporary, target);
            } catch (cause) {
                if ((cause as NodeJS.ErrnoException).code === "EEXIST") return false;

                throw cause;
            } finally {
                // Either the target now has the bytes under its own name or the write was refused, and
                // in both cases the temporary is litter that looks like half an archive.
                await rm(temporary, { force: true });
            }

            return true;
        },

        async taken() {
            const archives: StoredArchive[] = [];
            let entries;

            try {
                entries = await readdir(directory, { withFileTypes: true });
            } catch (cause) {
                // No directory means no backup has ever been taken here, which is every instance until
                // the first one. Anything else is a backup directory that exists and cannot be read, and
                // reporting that as "none yet" would let the next `put` land beside archives nobody
                // could see.
                if ((cause as NodeJS.ErrnoException).code !== "ENOENT") throw cause;

                return archives;
            }

            for (const entry of entries) {
                if (entry.isDirectory()) continue;

                try {
                    // Followed rather than filtered on `isFile()`, so an operator who keeps the archives
                    // on another disk and links them in still has a listing. A dangling link answers
                    // ENOENT here and is dropped by the catch below, which is the right answer for it,
                    // and a link to a directory is not an archive however it is named.
                    const facts = await stat(join(directory, entry.name));

                    if (facts.isFile()) archives.push({ name: entry.name, bytes: facts.size });
                } catch (cause) {
                    // One entry vanishing between the listing and its `stat` is a `put` finishing
                    // underneath this one — it renames a `.partial` away, which is exactly a name that
                    // `readdir` has already handed over. Wrapping the whole loop in the catch above, as
                    // this used to, turned that into a *truncated* listing: a review watched `taken()`
                    // answer with 1 archive out of 201, which makes `listBackups` lie and makes the
                    // already-taken check pass over a name that is already on disk. A file that goes
                    // away removes its own row and nothing else.
                    if ((cause as NodeJS.ErrnoException).code !== "ENOENT") throw cause;
                }
            }

            return archives;
        },
    };
}
