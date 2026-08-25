import { chmod, mkdir, mkdtemp, readFile, readdir, rename, rm, writeFile } from "node:fs/promises";
import { randomBytes } from "node:crypto";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import {
    ArgonCliError,
    dockerImage,
    explainRole,
    interrogate,
    validateConfig,
    type Pairing,
    type ServerImage,
    type ServerVersion,
} from "./argon";
import {
    COMPOSE_FILENAME,
    COMPOSE_PROJECT,
    OPTIONAL_ROLES,
    PANEL_PATH,
    PANEL_SERVICE,
    REFUSED_ROLES,
    // Re-exported below: this module's callers ask it what an instance needs, and it is the generator
    // that decides. It used to be declared here as well, with the same five names — two lists, one of
    // which would eventually gain a role the other did not, producing a wizard that offers a choice
    // the generator refuses or hides one it requires.
    REQUIRED_ROLES,
    SERVER_IMAGE_REPOSITORY as COMPOSE_SERVER_IMAGE,
    composeProject,
    needsCertificate,
    type ComposeOptions,
    type ComposeProject,
    type TlsMaterial,
} from "./compose";
import { ENVIRONMENT } from "./config";
import { dockerEngine, projectStatus, type EngineRequest } from "./docker";
import { DEPLOYMENT, generate, mintSecrets, type MintedSecrets, type SuppliedSecrets } from "./generate";
import type {
    Answers,
    GeneratedFile,
    RoleDetail,
    RoleSummary,
    ServiceStatus,
    StorageChoice,
    TopologySummary,
    TrafficShape,
} from "./model";

/**
 * The setup in progress: the operator's answers, what the image said about itself, and the one step that
 * touches the disk.
 *
 * Everything this coordinates already existed in pieces — `argon.ts` interrogates the image, `generate.ts`
 * turns answers into files, `compose.ts` describes the project that runs them — and none of them knew
 * about each other. This is the order they go in, and the order is the point:
 *
 *   answers -> interrogate -> generate -> **validate** -> write -> pull ->
 *   `compose up -d` (everything but this container) -> wait for it to answer
 *
 * **Writing comes last of the reversible steps and it is the first destructive one.** Validation runs
 * against generated content staged where nothing reads it; a refusal leaves the install exactly as it
 * was. Write first and validate after — the obvious shape, since `--validate-config` wants files on a
 * disk — and a failed validation leaves half a configuration behind that the next attempt has to reason
 * about, on a box where the operator cannot tell which half is which.
 *
 * ## The line the apply crosses, and why the outcome names it
 *
 * There is a second, harder line after that one: `docker compose up`. Before it, nothing is running and
 * a retry is clean — the operator changes an answer and applies again. After it, containers are up
 * against configuration that may be wrong, a retry reconciles a live project rather than creating one,
 * and "just run it again" is advice with consequences. Every {@link ApplyOutcome} therefore says which
 * side of that line it is on, in the outcome and not only in a log: see {@link ApplyOutcome.running} on
 * the two failures that can appear on either side of it.
 *
 * ## Why this does not start itself
 *
 * The panel is a service in the project it is bringing up — Traefik was started in front of it before
 * setup began, so it has been behind the front door since the first request. That means `compose up`
 * with no arguments would recreate *this* container, killing the process part-way through starting
 * everything else and leaving a project nobody was watching finish. So the apply names the services to
 * start, and the panel is not among them.
 *
 * An earlier design had the opposite arrangement: this process bound `:443` itself and handed it to
 * Traefik at exactly this point. That needed a port release that answered the request which asked for
 * it on the connection it had already arrived on, a rule about how the install script must publish this
 * container's port, and this process exiting afterwards. Starting the door first removed all of it.
 *
 * ## Ports
 *
 * Four, for the same reason `config.ts` takes an `InstallerFiles`: the interesting decisions here are
 * about ordering and refusal, and a test that needs docker and a filesystem to reach them is a test that
 * gets deleted the first time CI has no docker. {@link ImageFor} is every container this starts to *ask*
 * something, {@link ConfigStore} is every byte it writes, and {@link ComposeRunner} is every container
 * it leaves running. The real ones are at the bottom of the file.
 *
 * ## Where the secrets line is
 *
 * Generated secrets are minted once and then never change:
 *
 *  - **Within a run** they live in this object, so going back a step and re-applying regenerates the same
 *    files. The database password is the one that shows why: Postgres takes `POSTGRES_PASSWORD` when its
 *    data directory is first initialised and ignores it forever after, so a second mint produces a
 *    configuration that cannot log in to the database that was created from the first one.
 *  - **Across runs** they live in {@link MINT_FILE}, mode 0600, written at the moment of commit. A
 *    restarted process reads it back and reuses it.
 *
 * The mint is stored as {@link MintedSecrets} in a file of its own rather than read back out of the
 * generated configuration, because the generated shape is a map from these values into Argon's sections
 * and that map is allowed to change — `generate.ts` says as much where it explains why the mint is passed
 * in. Taking `Database:ConnectionString` apart again would work until somebody renamed a section, and
 * then it would mint a new password against a live database.
 *
 * A mint file that exists and cannot be read is **not** a reason to mint a fresh one: that is exactly the
 * case where an instance is already running on the old values. It blocks the apply instead, and says why.
 *
 * ## What a restart costs
 *
 * The answers are in memory and nowhere else. A process that dies mid-wizard loses them and the operator
 * gets an empty wizard on reload — so {@link SetupState.restarted} says out loud that this is what
 * happened and `note` says what was kept. Persisting the answers too would be a second file with its own
 * staleness story; what is worth persisting is the material whose loss corrupts an install, not the
 * material whose loss costs five minutes of typing.
 *
 * ## Secrets never leave
 *
 * Nothing here forms a log line — `main.ts` owns the log, and a value never handed to it cannot be
 * printed by it. The one thing that leaves through {@link SetupPorts.progress} is a subprocess's own
 * output, already redacted, and it exists because the alternative is a container that sits silent for
 * the length of an image pull; see that port for why the container log is the channel that is there
 * whether or not anyone is still holding the request. Nothing here returns a secret either: {@link SetupState} carries answers, and `Answers`
 * cannot hold a credential by type, which is why {@link Setup.submit} splits the object-storage keys out
 * of the storage answer at the door rather than trusting a serialiser to leave them out later.
 *
 * The server's own words are the leak that is easy to miss: `--validate-config` prints diagnostics about
 * configuration it has just read, so its output can quote a value we generated. `docker compose` is the
 * same hazard wearing overalls — it echoes the service definitions it is reconciling, and it reads the
 * `.env` beside the compose document. Everything coming back from a container, or from the command that
 * starts one, goes through {@link redact} before it is returned or reported.
 *
 * ## One constraint on the install script
 *
 * The mounts built here are resolved by the **docker daemon**, on the host — not inside this container.
 * So the configuration directory has to be bind-mounted into the bootstrapper at the same path it has on
 * the host (`-v /etc/argon:/etc/argon`), or every path handed to `--volume` names something that is not
 * there. It is also why staging happens *inside* the configuration directory rather than in `/tmp`: a
 * temporary directory in this container's own filesystem does not exist as far as the daemon is
 * concerned, so the mount would quietly become an empty directory — which validates clean and means
 * nothing.
 */

/* ------------------------------------------------------------------------------------------------
 * Ports.
 * ---------------------------------------------------------------------------------------------- */

/**
 * What a validation run is allowed to see.
 *
 * The secrets document is optional in the shape and not in practice: mounting `conf.d` alone leaves every
 * generated secret invisible to the container, and a required setting with no value is an Error rather
 * than a warning — so a good configuration comes back invalid, confidently. It is optional here only
 * because naming a file that is not there is an Error too, which is the same false red wearing a
 * different hat.
 */
export interface Mounts {
    readonly configDir: string;
    readonly secretsFile?: string;
}

/**
 * An image handle for a chosen server version.
 *
 * It takes the mounts because they are fixed when the handle is made: interrogation asks the image about
 * itself and must see no configuration at all, while validation must see exactly the configuration under
 * test. One handle that did both would be one that sometimes validated the image's own defaults.
 */
export type ImageFor = (version: string, mounts?: Mounts) => ServerImage;

/**
 * Where files go, and where they are staged first.
 *
 * `write` takes the directory rather than assuming the root, because the same set of files is written
 * twice: once into a staging directory to be judged, once into the install for real.
 */
export interface ConfigStore {
    /** The install root. Generated paths are relative to it. */
    readonly root: string;

    /** Writes each file with the mode it carries, creating directories that are missing. */
    write(directory: string, files: readonly GeneratedFile[]): Promise<void>;

    /** One file's text, relative to the root; `undefined` when it is not there. Anything else rejects. */
    read(path: string): Promise<string | undefined>;

    /** An empty directory to stage into, which the docker daemon can also see. */
    scratch(): Promise<string>;

    /** Removes a directory {@link ConfigStore.scratch} produced, and everything under it. */
    discard(directory: string): Promise<void>;
}

/**
 * Where a compose project is, as the three things every invocation has to be told.
 *
 * `project` rather than "whatever directory this happens to be in" is the whole of the idempotence
 * story: compose reconciles by project name, so two applies against the same name converge on one stack
 * and one network instead of building a second beside the first. {@link COMPOSE_PROJECT} is that name,
 * fixed in `compose.ts` and read from there — the document already carries it, and passing it on the
 * command line as well means a document that was hand-edited to a different name fails loudly rather
 * than quietly becoming a second stack.
 */
export interface ComposeInvocation {
    readonly project: string;

    /** The install root. Compose reads the `.env` beside the document out of this directory. */
    readonly directory: string;

    /** The compose document's name inside that directory. */
    readonly file: string;
}

export interface ComposeResult {
    readonly ok: boolean;

    /** Everything the command printed, both streams. Redacted by the caller before it goes anywhere. */
    readonly output: string;
}

/** One service, as `docker compose ps` reports it. Compose's own words; nothing here interprets them. */
/**
 * Every container this leaves running, which is the third port and the one that changes what an apply is.
 *
 * A port for the same reason {@link ImageFor} is one: the decisions worth testing here are about
 * ordering — pull before `up`, the panel excluded from what `up` starts, and a readiness wait that
 * names a service rather than a duration — and a test that needs a docker daemon to reach them is a test that gets skipped the
 * first time CI has none, which is every time.
 *
 * Progress arrives line by line rather than as a returned blob because the returned blob arrives at the
 * end, and the end of an image pull is several minutes away. See {@link SetupPorts.progress}.
 */
export type { ServiceStatus };

export interface ComposeRunner {
    /**
     * Fetches every image the project names.
     *
     * Its own call, and it happens before anything is started. Pulling is the longest part of the whole
     * install and also the part with the most ways to fail that are nobody's fault — a registry that is
     * down, a tag that was never published, a disk with no room. Kept separate, those failures land
     * while nothing is running and the operator can retry from the same button. Folded into `up` they
     * would instead leave a half-created project behind to explain.
     */
    pull(where: ComposeInvocation, onOutput: (line: string) => void): Promise<ComposeResult>;

    /**
     * `up --detach`, for the named services and whatever they depend on.
     *
     * Named rather than "everything in the file" because one service in that file is the container
     * making this call. Compose starts what it is given plus their dependencies, and leaves the rest
     * of the project alone — which is the difference between starting an instance and interrupting
     * yourself. Idempotent by {@link ComposeInvocation.project}: a second call reconciles.
     */
    up(where: ComposeInvocation, services: readonly string[], onOutput: (line: string) => void): Promise<ComposeResult>;

    /** What compose says exists, including containers that have exited. Empty when nothing was created. */
    status(where: ComposeInvocation): Promise<readonly ServiceStatus[]>;
}

/**
 * Where the two certificates the install script established live, as paths **on the host**.
 *
 * Paths and not contents, unlike `ServerConfig.tls`: these become bind mounts in the generated compose
 * project and the docker daemon resolves them on the host. Handing over PEM text would mean writing it
 * out again beside the install, which is a second copy of a private key for no gain.
 */
export interface Certificates {
    /** The instance's own. Wanted by the traffic shapes that terminate TLS on this machine. */
    readonly instance?: TlsMaterial;

    /** The media subdomain's, for a Cloudflare-proxied instance publishing voice directly. See §5. */
    readonly media?: TlsMaterial;
}

/* ------------------------------------------------------------------------------------------------
 * Constants that are contracts.
 * ---------------------------------------------------------------------------------------------- */

/**
 * Where the minted secrets are kept between runs. A dotfile at the install root, deliberately outside
 * `conf.d/`: the server scans that directory for `<feature>.json` files and this is not one of those.
 */
export const MINT_FILE = ".argon-bootstrap-mint.json";

/** Bumped only if the stored shape changes in a way a reader has to branch on. */
const MINT_FORMAT = 1;

/** Staging directories, swept on the next run. The prefix is what makes them recognisably ours. */
const STAGING_PREFIX = ".staging-";

/** Where the server image comes from when the operator named a version rather than a whole reference. */
// The image repository is compose.ts's to state. It used to be declared here too, as
// `argonchat/argon-server`, taken from deploy/docker/docker-compose.yml — which is stale: the workflow
// that would publish that name is commented out, and CI actually pushes ghcr.io/argon-chat/orleans.
// Two names meant the image this module interrogated was not the image the compose file would run, so
// the validation step guaranteed nothing about what started.

/** The role that is every other role in one process. Development only, and §6 says why it is refused. */
const DEV_ROLE = "dev";

/** Roles that install fine and that §6 says a self-hosted instance should think twice about. */
const QUESTIONABLE_ROLES: Readonly<Record<string, string>> = {
    commerce: "entitlements and payments, which a self-hosted instance has no business for",
    moderation: "ONNX image moderation, which is memory-bound and off by default",
};

/** The compose document. Readable, like the `conf.d` files; the secrets are in the `.env` beside it. */
const COMPOSE_MODE = 0o644;

/**
 * The traffic shapes that want the certificate the install script established.
 *
 * A second copy of the table in `compose.ts`'s `tlsPlanFor`, which is the kind of copy this project
 * refuses to have — stated here rather than hidden, because it cannot be avoided from outside that
 * module. `composeProject` refuses in *both* directions: material that a shape has no use for is an
 * error there, and so is material a shape needs and did not get. So a caller that hands the same
 * environment to every shape cannot install on the Let's Encrypt path at all, where Traefik obtains its
 * own certificate and material on disk means the caller meant a different path.
 *
 * If it drifts, the damage is a refusal rather than a wrong install: the worst case is `composeProject`
 * throwing, which lands as {@link ApplyOutcome} `not-startable` before anything has been written. The
 * change that removes the copy is `compose.ts` exporting the predicate; see the report.
 */


/**
 * How long the stack gets to come up before the apply stops waiting and says what is not ready.
 *
 * Five minutes is what the generated project's own ordering costs on a cold machine: Postgres has a
 * sixty-second health `start_period` while `initdb` runs, the object store's bucket-init loop gives
 * itself two minutes, and only then does the first role start and run migrations. A tighter bound
 * reports a healthy install as broken on a slow disk, which is the more expensive mistake — the
 * operator tears down something that was about to work.
 */
const READY_TIMEOUT_MS = 5 * 60_000;

/** How often the wait asks. Cheap — `compose ps` reads the daemon's own records and starts nothing. */
const READY_POLL_MS = 3_000;

/**
 * How much of the start's output is kept for {@link SetupState.progress}.
 *
 * A bound, because an image pull prints a line per layer per image and this is held in memory in a
 * process that has other things to do with it. The tail is what is wanted anyway: the interesting line
 * is the last one.
 */
const PROGRESS_LINES = 200;

/* ------------------------------------------------------------------------------------------------
 * What the operator answers.
 * ---------------------------------------------------------------------------------------------- */

export type PartialAnswers = Partial<Answers>;

/**
 * Credentials the operator typed. The same shape `generate.ts` takes, aliased rather than restated so the
 * two cannot drift into disagreeing about what a supplied credential is.
 */
export type OperatorCredentials = SuppliedSecrets;

/**
 * The storage step, as the form the operator fills in.
 *
 * The keys ride with the bucket because that is one screen to a person, and they are split off the moment
 * the step is taken: what is kept is a {@link StorageChoice}, which has nowhere to put them. That split is
 * what makes "a secret never appears in a response" a property of the types rather than a property of
 * remembering.
 */
export type StorageAnswer =
    | { readonly kind: "local" }
    | {
          readonly kind: "s3";
          readonly endpoint: string;
          readonly bucket: string;
          readonly region?: string;
          readonly accessKey?: string;
          readonly secretKey?: string;
      };

/** One step of the wizard. Every field optional: the UI decides how many questions fit on a screen. */
export interface Step {
    readonly domain?: string;
    readonly serverVersion?: string;
    readonly roles?: readonly string[];
    readonly storage?: StorageAnswer;
    readonly traffic?: TrafficShape;
    readonly voice?: boolean;
}

/** Why an answer was not taken. `field` names the answer, so the UI can put the sentence beside it. */
export interface Rejection {
    readonly field: string;
    readonly problem: string;
}

/* ------------------------------------------------------------------------------------------------
 * What the UI reads.
 * ---------------------------------------------------------------------------------------------- */

export type Stage =
    /** Still collecting. The wizard is not finished. */
    | "awaiting-configuration"
    /** Every answer is in and consistent; the apply can run. */
    | "ready"
    /** An apply is running: generating, validating, writing. Nothing has been started. */
    | "applying"
    /** The server refused the generated configuration. Nothing was written. */
    | "invalid"
    /** The files are on disk and nothing is running against them. A retry from here is clean. */
    | "configured"
    /** The images are pulled and the stack is coming up. */
    | "starting"
    /** Every service came up. This process carries on, now as the panel behind the front door. */
    | "running"
    /**
     * Containers are running against what this apply wrote, and the instance did not come up.
     *
     * Its own stage rather than `invalid` or `blocked`, because it is the only one where a retry is not
     * free: something is live, and the next thing the operator does happens to a running system.
     */
    | "degraded"
    /** Something already on disk makes it unsafe to go on. `problem` says what. */
    | "blocked"
    /** No setup machine was wired into the server. See {@link setupFromEnvironment}. */
    | "unavailable";

/** A file that landed. Path and mode only — a route that returned contents would return the secrets. */
export interface WrittenFile {
    readonly path: string;
    readonly mode: number;
}

/** What one role's `--validate-config` said, in the server's own words, redacted. */
export interface RoleReport {
    readonly role: string;
    readonly ok: boolean;
    readonly output: string;
}

/**
 * Where the panel is once the front door is up, and what moved.
 *
 * The path is read from `compose.ts` rather than restated: it is the same constant the generated Traefik
 * router matches on, so the two cannot disagree about where the operator was sent. A second copy here
 * would be a URL printed in a browser that 404s behind a proxy configured from the first.
 */
export interface PanelLocation {
    readonly url: string;

    /** What `/` is now, said plainly. The operator was looking at a wizard there sixty seconds ago. */
    readonly note: string;
}

export { REQUIRED_ROLES };

/** {@link SetupState.policy}, from the one place that decides it. */
const ROLE_POLICY = {
    required: REQUIRED_ROLES,
    optional: OPTIONAL_ROLES,
    refused: REFUSED_ROLES,
} as const;

export interface SetupState {
    readonly stage: Stage;

    /** Everything answered so far. Cannot carry a credential; see {@link StorageAnswer}. */
    readonly answers: PartialAnswers;

    /** The answers still wanted, so the UI knows which step to open on. */
    readonly missing: readonly string[];

    /** Which credentials are held, by name and never by value. */
    readonly credentials: readonly string[];

    /**
     * Which roles the operator may decide about, and which are decided for them.
     *
     * The image reports every role it can run; only some of those are a self-hosted instance's to
     * choose. Sent rather than known by the page, for the reason nothing else here is known by the
     * page either: a second copy of these three lists would drift, and it would drift into a wizard
     * offering a role the generator refuses or hiding one it needs.
     */
    readonly policy: {
        readonly required: readonly string[];
        readonly optional: readonly string[];
        readonly refused: readonly string[];
    };

    /** What the image said about itself, once asked. Asking is `POST /api/setup/interrogate`. */
    readonly image?: {
        readonly reference: string;
        readonly version: ServerVersion;
        readonly pairing: Pairing;
        readonly roles: readonly RoleSummary[];
        readonly topologies: readonly TopologySummary[];
    };

    /** True, unwelcome, and not a refusal. */
    readonly warnings: readonly string[];

    /** This process found an install it did not write. The answers from that run are gone. */
    readonly restarted: boolean;
    readonly note?: string;

    /** What the last apply put on disk. It outlives a later answer change, because the disk does. */
    readonly written?: readonly WrittenFile[];

    /** The last verdict from the server. Cleared when an answer changes, because it stops being about it. */
    readonly validation?: readonly RoleReport[];

    /**
     * Where the panel will be, as soon as there is a domain to build it from.
     *
     * Present long before the apply, and that is the point. The apply can take minutes, and a browser
     * that gives up waiting takes the response with it — so a UI that only learned the panel's address
     * from that response would strand the operator. Told beforehand, they can walk there whatever
     * happens to the request.
     */
    readonly panel?: PanelLocation;

    /**
     * The tail of what `docker compose` printed, redacted.
     *
     * Here so that a second tab polling `/api/state` during the pull sees something moving. Not the
     * only place progress goes: a browser that gave up waiting is reading nothing, and the container's
     * own log is there either way. See {@link SetupPorts.progress}.
     */
    readonly progress?: readonly string[];

    /** What compose last said about each service. Present once a start has been attempted. */
    readonly services?: readonly ServiceStatus[];

    /** Why the next apply will refuse, when something already makes that certain. */
    readonly problem?: string;
}

export type Submission =
    | { readonly ok: true; readonly state: SetupState }
    | { readonly ok: false; readonly rejections: readonly Rejection[] };

export type InterrogationOutcome =
    | {
          readonly ok: true;
          readonly reference: string;
          readonly version: ServerVersion;
          readonly pairing: Pairing;
          readonly roles: readonly RoleSummary[];
          readonly topologies: readonly TopologySummary[];
      }
    | { readonly ok: false; readonly reason: "no-version" | "image"; readonly problem: string };

/**
 * How an apply ended, and — for the two that can end on either side of it — which side of `compose up`.
 *
 * The first five failures all mean **nothing was started**: the answers were not usable, the image could
 * not be asked, the server refused what was generated, the disk refused the write, or the project could
 * not be assembled. Every one of them leaves the machine able to take another apply with no ceremony.
 *
 * `start-failed` and `not-ready` are the ones that needed a field rather than a sentence, because an
 * operator reading "the install failed" has to know whether there are now containers on their box.
 * {@link StartFailure.running} says so, and it is answered by asking compose what exists rather than by
 * guessing from where the code got to.
 */
export type ApplyOutcome =
    | {
          readonly ok: true;
          readonly written: readonly WrittenFile[];
          readonly services: readonly ServiceStatus[];
          readonly panel: PanelLocation;
      }
    | { readonly ok: false; readonly reason: "incomplete"; readonly rejections: readonly Rejection[] }
    | { readonly ok: false; readonly reason: "blocked"; readonly problem: string }
    | { readonly ok: false; readonly reason: "image"; readonly problem: string }
    | { readonly ok: false; readonly reason: "invalid"; readonly reports: readonly RoleReport[] }
    | { readonly ok: false; readonly reason: "write-failed"; readonly problem: string }
    /** The compose project could not be built, or this process cannot let go of the port the edge wants. */
    | { readonly ok: false; readonly reason: "not-startable"; readonly problem: string }
    | StartFailure;

/** Every way an apply can end badly. Narrowed out of the union so a refusal cannot claim to be a success. */
export type ApplyRefusal = Extract<ApplyOutcome, { readonly ok: false }>;

export type StartFailure =
    /** `docker compose` refused. Before `up` this is a clean retry; after it, it is not. */
    | {
          readonly ok: false;
          readonly reason: "start-failed";
          readonly problem: string;
          readonly output: string;
          readonly running: boolean;
          readonly services: readonly ServiceStatus[];
          readonly panel: PanelLocation;
      }
    /** Compose accepted it and something never came up. Always after `up`, so always running. */
    | {
          readonly ok: false;
          readonly reason: "not-ready";
          readonly problem: string;
          readonly running: true;
          readonly services: readonly ServiceStatus[];
          readonly panel: PanelLocation;
      };

/* ------------------------------------------------------------------------------------------------
 * Checking answers. Pure, and separate from the machine that holds them.
 * ---------------------------------------------------------------------------------------------- */

/** A hostname and nothing else: no scheme, no port, no path. Kept permissive about single labels. */
const HOSTNAME = /^(?=.{1,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$/;

/** Lowercased, with the root dot dropped: `Chat.Example.Org.` and `chat.example.org` are one answer. */
function normaliseHost(value: string): string {
    return value.trim().toLowerCase().replace(/\.$/, "");
}

function hostProblem(value: string): string | undefined {
    const host = normaliseHost(value);

    if (host.length === 0) return "it is empty";

    if (/^[a-z][a-z0-9+.-]*:\/\//.test(host))
        return "it is a URL. What goes here is the host on its own, without a scheme — the certificate, the DNS record and Argon's issuer are all named by the host";

    if (host.includes("/")) return "it has a path in it. What goes here is the host on its own";

    if (host.includes(":"))
        return "it has a port in it. Argon terminates TLS on the port the compose file publishes, and the name is what a certificate covers";

    return HOSTNAME.test(host) ? undefined : `'${host}' is not a hostname`;
}

/**
 * The object-storage endpoint, which is a host and not a URL.
 *
 * `splitEndpoint` takes a scheme off the front because everything else in the world takes a URL, but it
 * cannot rescue a path: `Storage:Endpoint` has two readers and one of them builds `{bucket}.{Endpoint}`,
 * so a bucket path left on the end comes back as `argon.s3.example.com/argon` and fails at the first
 * upload looking like a network fault.
 */
function endpointProblem(value: string): string | undefined {
    const endpoint = value.trim().replace(/^https?:\/\//i, "").replace(/\/+$/, "");

    if (endpoint.length === 0) return "it is empty";

    if (endpoint.includes("/"))
        return "it has a path in it. This is the storage host — the bucket is a separate answer, and a path here ends up in front of the bucket name";

    const [host = "", port, ...rest] = endpoint.split(":");

    if (rest.length > 0) return `'${endpoint}' is not a host, or a host and a port`;
    if (port !== undefined && !/^\d{1,5}$/.test(port)) return `'${port}' is not a port`;

    return HOSTNAME.test(host.toLowerCase()) ? undefined : `'${host}' is not a hostname`;
}

/**
 * The image reference, checked here rather than at a container start a minute later.
 *
 * It becomes one element of an argv array — `Bun.spawn` takes an array, so this is never a shell word and
 * a space in it cannot become a second argument. Do not "simplify" that into a shell string. What the
 * check is for is the typo: a version with a space or a quote in it is a mistake, and finding out from
 * `docker: invalid reference format` after the wizard has finished is finding out late.
 */
function versionProblem(value: string): string | undefined {
    const version = value.trim();

    if (version.length === 0) return "it is empty";
    if (version.length > 200) return "it is too long to be a version or an image reference";

    return /^[A-Za-z0-9][\w./:@+-]*$/.test(version)
        ? undefined
        : "it is not a version or an image reference; letters, digits and . _ - / : @ are what a reference holds";
}

/** Every answer {@link Answers} needs, in the order a wizard would ask for them. */
const ANSWER_ORDER: readonly (keyof Answers)[] = [
    "domain",
    "serverVersion",
    "traffic",
    "roles",
    "voice",
    "storage",
];

export function missingAnswers(answers: PartialAnswers): (keyof Answers)[] {
    return ANSWER_ORDER.filter((key) => answers[key] === undefined);
}

/** Narrowing helper: the answers are complete, so they are an {@link Answers}. */
function complete(answers: PartialAnswers): answers is Answers {
    return missingAnswers(answers).length === 0;
}

/**
 * Everything wrong with the answers as they stand.
 *
 * Cross-field rules fire as soon as both halves are present, and not before: a wizard that rejects the
 * voice answer because the roles have not been chosen yet is a wizard that cannot be filled in in any
 * order. `offered` is what the image said it has, and is absent until the image has been interrogated —
 * before that, a role name is taken on trust and checked again at the apply.
 */
export function checkAnswers(
    answers: PartialAnswers,
    credentials: OperatorCredentials,
    offered?: readonly RoleSummary[],
): Rejection[] {
    const rejections: Rejection[] = [];
    const reject = (field: string, problem: string): void => void rejections.push({ field, problem });

    if (answers.domain !== undefined) {
        const problem = hostProblem(answers.domain);

        if (problem !== undefined) reject("domain", `that is not a domain: ${problem}.`);
    }

    if (answers.serverVersion !== undefined) {
        const problem = versionProblem(answers.serverVersion);

        if (problem !== undefined) reject("serverVersion", `that is not a server version: ${problem}.`);
    }

    if (answers.traffic?.kind === "cloudflare-proxied" && answers.traffic.voiceHost !== undefined) {
        const problem = hostProblem(answers.traffic.voiceHost);

        if (problem !== undefined) reject("traffic", `that is not a media hostname: ${problem}.`);
    }

    if (answers.storage?.kind === "s3") {
        const problem = endpointProblem(answers.storage.endpoint);

        if (problem !== undefined) reject("storage", `that is not a storage endpoint: ${problem}.`);

        if (answers.storage.bucket.trim().length === 0) reject("storage", "the bucket has no name.");

        // Never says what the value was: the field it is complaining about is a credential.
        if (credentials.objectStorage === undefined)
            reject(
                "storage",
                "an access key and a secret key are needed for that bucket. Nothing here can invent them, and an instance without them fails on its first upload rather than at startup.",
            );
    }

    if (answers.roles !== undefined) rejections.push(...checkRoles(answers, offered));

    // Media is UDP and a Cloudflare tunnel carries HTTP. `generate.ts` cannot express this combination —
    // a tunnelled instance has one hostname and it is the tunnel's — so it has to be refused here, while
    // there is still somewhere to put the answer.
    if (answers.voice === true && answers.traffic?.kind === "cloudflare-tunnel")
        reject(
            "voice",
            "voice cannot work through a Cloudflare tunnel: the tunnel carries HTTP and WebSockets, and real-time media is UDP. Publish the media endpoint directly, or leave voice off.",
        );

    return rejections;
}

function checkRoles(answers: PartialAnswers, offered?: readonly RoleSummary[]): Rejection[] {
    const roles = answers.roles ?? [];
    const rejections: Rejection[] = [];
    const reject = (problem: string): void => void rejections.push({ field: "roles", problem });
    const chosen = new Set(roles);

    // On its own, and early: every rule below would also fire, and four sentences about an empty answer
    // read as four problems rather than as one unanswered question.
    if (roles.length === 0) return [{ field: "roles", problem: "no roles were chosen; an instance is the roles it runs." }];

    if (chosen.has(DEV_ROLE))
        reject(
            `'${DEV_ROLE}' is every role in one process, which is a shape that exists nowhere else — so it breaks in ways a real deployment never sees and gets fixed last. Choose the roles themselves.`,
        );

    const absent = REQUIRED_ROLES.filter((role) => !chosen.has(role));

    if (absent.length > 0)
        reject(
            `${absent.join(", ")} ${absent.length === 1 ? "is" : "are"} not optional: the API, sign-in, spaces, attachments and the background jobs are what an instance is made of.`,
        );

    if (offered !== undefined) {
        const known = new Set(offered.map((role) => role.id));
        const unknown = roles.filter((role) => !known.has(role));

        if (unknown.length > 0)
            reject(`this server image has no role called ${unknown.join(", ")}.`);
    }

    // Both directions. Voice with no voice role installs an instance whose calls go nowhere; the voice
    // role with voice off starts a container that nothing is configured to talk to.
    if (answers.voice === true && !chosen.has("voice"))
        reject("voice was asked for, so the voice role has to run.");

    if (answers.voice === false && chosen.has("voice"))
        reject("the voice role was chosen but voice is off; one of the two answers is wrong.");

    return rejections;
}

/**
 * Things the operator should see and that are nobody's mistake.
 *
 * Warnings rather than refusals because every one of them is a choice somebody may know better about
 * than we do — §5 is explicit that where LiveKit lives is theirs to answer, because it depends on what
 * their Cloudflare plan carries and they know that and we do not.
 */
export function warningsFor(answers: PartialAnswers, pairing?: Pairing): string[] {
    const warnings: string[] = [];

    if (pairing !== undefined && !pairing.ok) warnings.push(pairing.detail);

    if (answers.voice === true && answers.traffic?.kind === "cloudflare-proxied" && answers.traffic.voiceHost === undefined)
        warnings.push(
            "voice will be published on the same hostname as everything else, behind the Cloudflare proxy. That proxy carries HTTP and WebSockets, not the UDP real-time media is, so this only works if your plan carries that traffic — otherwise chat works and calls are silent. The alternative is a second, DNS-only subdomain pointing straight at this machine.",
        );

    if (answers.storage?.kind === "s3")
        warnings.push(
            "whoever follows a file link has to be able to read that bucket, and this installer cannot make it so. A private bucket produces an instance where everything works except that every avatar and attachment is a broken image.",
        );

    for (const role of answers.roles ?? []) {
        const caution = QUESTIONABLE_ROLES[role];

        if (caution !== undefined) warnings.push(`the '${role}' role is ${caution}.`);
    }

    if (answers.traffic?.kind === "own-certificate")
        warnings.push(
            "renewing that certificate is yours. An instance that stops answering in ninety days with no warning is the worst way for this path to end.",
        );

    return warnings;
}

/* ------------------------------------------------------------------------------------------------
 * Taking a step.
 * ---------------------------------------------------------------------------------------------- */

/** Answers and credentials, kept apart everywhere they travel together. */
interface Held {
    readonly answers: PartialAnswers;
    readonly credentials: OperatorCredentials;
}

/**
 * Merges a step into what is held, splitting the storage credentials out of the storage answer.
 *
 * Pure, so the split can be proved without a machine around it. Two things it decides:
 *
 *  - Keys are kept when a later step changes the bucket without repeating them, because a form that
 *    demands a secret be retyped to fix a typo in a bucket name is a form people paste secrets into.
 *  - Choosing local storage drops them. Nothing should hold a credential it has no use for, and the
 *    operator who switches back can type them again — which they would have to anyway.
 */
export function takeStep(held: Held, step: Step): Held {
    // The one place the answers are mutable, and it is a local copy. `Answers` is readonly everywhere it
    // is handed out, which is what stops a route handler from editing the state it was given.
    const answers: { -readonly [K in keyof Answers]?: Answers[K] } = { ...held.answers };
    let credentials: OperatorCredentials = held.credentials;

    if (step.domain !== undefined) answers.domain = normaliseHost(step.domain);
    if (step.serverVersion !== undefined) answers.serverVersion = step.serverVersion.trim();
    if (step.roles !== undefined) answers.roles = [...step.roles];
    if (step.voice !== undefined) answers.voice = step.voice;

    if (step.traffic !== undefined) answers.traffic = normaliseTraffic(step.traffic, answers.domain);

    if (step.storage !== undefined) {
        answers.storage = choiceOf(step.storage);

        if (step.storage.kind === "local") credentials = {};
        else if (step.storage.accessKey !== undefined && step.storage.secretKey !== undefined)
            credentials = {
                objectStorage: { accessKey: step.storage.accessKey, secretKey: step.storage.secretKey },
            };
    }

    return { answers, credentials };
}

function choiceOf(storage: StorageAnswer): StorageChoice {
    if (storage.kind === "local") return { kind: "local" };

    const endpoint = storage.endpoint.trim().replace(/\/+$/, "");
    const region = storage.region?.trim();

    return region === undefined || region.length === 0
        ? { kind: "s3", endpoint, bucket: storage.bucket.trim() }
        : { kind: "s3", endpoint, bucket: storage.bucket.trim(), region };
}

/**
 * A media hostname equal to the domain is not a second hostname.
 *
 * "Behind the proxy" is what leaving it out means, and the two answers would otherwise generate the same
 * files while reading as different decisions — one of which the operator would later be told to change.
 */
function normaliseTraffic(traffic: TrafficShape, domain: string | undefined): TrafficShape {
    if (traffic.kind !== "cloudflare-proxied" || traffic.voiceHost === undefined) return traffic;

    const voiceHost = normaliseHost(traffic.voiceHost);

    return voiceHost === domain || voiceHost.length === 0
        ? { kind: "cloudflare-proxied" }
        : { kind: "cloudflare-proxied", voiceHost };
}

/* ------------------------------------------------------------------------------------------------
 * Keeping secrets out of what leaves.
 * ---------------------------------------------------------------------------------------------- */

/**
 * Short values are left alone.
 *
 * Redaction is a blind string replacement, so a two-character secret would black out every occurrence of
 * those two characters in the server's diagnostics and leave the operator reading a censored sentence
 * about a problem they now cannot see. Everything this mints is far longer than this; the bound exists
 * for what an operator typed.
 */
const SHORTEST_REDACTABLE = 8;

/** Every string in a minted bundle, however deeply nested. */
function everyValue(source: object): string[] {
    return Object.values(source).flatMap((value) =>
        typeof value === "string" ? [value] : typeof value === "object" && value !== null ? everyValue(value) : [],
    );
}

/**
 * Takes every known secret out of text that came from a container.
 *
 * `--validate-config` reports on configuration it has just read, so its diagnostics can quote a value
 * this installer generated — and that text is exactly what a wizard wants to show the operator. Rather
 * than choose between showing the server's own words and not leaking, this shows them with the words we
 * know to be secret removed.
 *
 * It is a backstop and not a permission: nothing should be handing secrets to a route in the first place.
 */
export function redact(text: string, ...bundles: readonly object[]): string {
    let redacted = text;

    for (const bundle of bundles)
        for (const value of everyValue(bundle))
            if (value.length >= SHORTEST_REDACTABLE) redacted = redacted.split(value).join("<redacted>");

    return redacted;
}

/* ------------------------------------------------------------------------------------------------
 * Reading what compose said. Pure, so the readiness rule can be argued with in a test.
 * ---------------------------------------------------------------------------------------------- */

/**
 * Where the operator goes now, built from the path the generated router actually matches.
 *
 * `https://` for every traffic shape, including the tunnel: the tunnel terminates TLS at Cloudflare's
 * end, so the name is still reached over HTTPS even though nothing on this machine holds a certificate.
 */
export function panelFor(domain: string): PanelLocation {
    return {
        url: `https://${domain}${PANEL_PATH}`,
        note: `https://${domain}/ is the API and the realtime hub from now on — this wizard was only ever standing in its place. The panel moved behind the front door at ${PANEL_PATH}, on the same name and the same certificate, and it signs in with the same bootstrap code until there is a real operator account.`,
    };
}

/**
 * Why one service is not ready yet, or `undefined` when it is.
 *
 * Two states count as ready and the second is the one worth stating: a container that has **exited
 * zero** is ready, because the project has a service that is supposed to exit — `argon-storage-init`
 * creates the two buckets and stops, and `media` waits on it having *completed*. A readiness rule that
 * only accepted `running` would wait out its whole bound on a service that did its job in nine seconds.
 *
 * `restarting` is reported as not-ready rather than as failed, and that is a deliberate refusal to be
 * clever: a role restarting sixty seconds in is a role that lost a race with Postgres, and a role
 * restarting four minutes in has failed for good. Nothing here can tell them apart, so it waits out the
 * bound rather than declaring a failure that was about to fix itself. The cost is a slow failure; the
 * alternative is a false one.
 */
export function unreadiness(service: string, status: ServiceStatus | undefined): string | undefined {
    if (status === undefined) return `${service} has no container yet`;

    const state = status.state.trim().toLowerCase();
    const health = status.health?.trim().toLowerCase() ?? "";

    if (state === "running") {
        if (health === "" || health === "healthy") return undefined;

        return `${service} is running and its healthcheck says '${health}'`;
    }

    if (state === "exited")
        return status.exitCode === 0
            ? undefined
            : `${service} exited ${status.exitCode ?? "without a code"} — its logs are the only thing that says why`;

    return `${service} is '${state}'`;
}

/**
 * Every service that is not ready, named.
 *
 * The point of naming them is the sentence the operator gets. "Timed out after five minutes" tells them
 * to look at everything; "argon-core is 'restarting'" tells them which `docker logs` to run, and that is
 * the entire difference between a report and an apology.
 */
export function unreadyServices(
    expected: readonly string[],
    statuses: readonly ServiceStatus[],
): string[] {
    const byService = new Map(statuses.map((status) => [status.service, status]));

    return expected.flatMap((service) => unreadiness(service, byService.get(service)) ?? []);
}

/* ------------------------------------------------------------------------------------------------
 * The mint, on disk.
 * ---------------------------------------------------------------------------------------------- */

interface AdoptedMint {
    readonly secrets: MintedSecrets;
    /** Names of the values that were not in the stored file and had to be minted now. Never values. */
    readonly added: readonly string[];
}

function storedText(source: Record<string, unknown>, key: string): string | undefined {
    const value = source[key];

    return typeof value === "string" && value.length > 0 ? value : undefined;
}

function nested(source: Record<string, unknown>, key: string): Record<string, unknown> {
    const value = source[key];

    return typeof value === "object" && value !== null ? (value as Record<string, unknown>) : {};
}

/**
 * Reads a stored mint, keeping every value it holds and minting only what it does not.
 *
 * The alternative — refusing anything that is not exactly the current shape — makes a bootstrapper
 * upgrade that adds one new secret unable to run against an install that predates it. The alternative in
 * the other direction, minting the whole bundle fresh when one field is missing, rotates a database
 * password that a running Postgres will not accept. So: what is there is kept, what is missing is new
 * material for something that did not exist before, and {@link AdoptedMint.added} names it so the operator
 * is told rather than surprised.
 *
 * Key pairs are all-or-nothing. Half a pair is worse than a new pair: the private key would not match the
 * public one and every token signed by it would be rejected by the same process that signed it.
 */
export function adoptMint(stored: unknown): AdoptedMint {
    if (typeof stored !== "object" || stored === null) throw new Error("it is not an object");

    const held = "secrets" in stored ? nested(stored as Record<string, unknown>, "secrets") : {};

    if (Object.keys(held).length === 0) throw new Error("it holds no secrets");

    const fresh = mintSecrets();
    const added: string[] = [];

    const keep = <T>(name: string, found: T | undefined, replacement: T): T => {
        if (found !== undefined) return found;

        added.push(name);

        return replacement;
    };

    const signing = nested(held, "jwtSigning");
    const signingPrivate = storedText(signing, "privateKey");
    const signingPublic = storedText(signing, "publicKey");

    const encryption = nested(held, "jwtEncryption");
    const encryptionPrivate = storedText(encryption, "privateKeyBase64");
    const encryptionPublic = storedText(encryption, "publicKeyBase64");

    const objectStorage = nested(held, "objectStorage");
    const storageAccess = storedText(objectStorage, "accessKey");
    const storageSecret = storedText(objectStorage, "secretKey");

    const sfu = nested(held, "sfu");
    const sfuClient = storedText(sfu, "clientId");
    const sfuSecret = storedText(sfu, "secret");

    return {
        secrets: {
            databasePassword: keep("databasePassword", storedText(held, "databasePassword"), fresh.databasePassword),
            jwtMachineSalt: keep("jwtMachineSalt", storedText(held, "jwtMachineSalt"), fresh.jwtMachineSalt),
            jwtSigning: keep(
                "jwtSigning",
                signingPrivate !== undefined && signingPublic !== undefined
                    ? { privateKey: signingPrivate, publicKey: signingPublic }
                    : undefined,
                fresh.jwtSigning,
            ),
            jwtEncryption: keep(
                "jwtEncryption",
                encryptionPrivate !== undefined && encryptionPublic !== undefined
                    ? { privateKeyBase64: encryptionPrivate, publicKeyBase64: encryptionPublic }
                    : undefined,
                fresh.jwtEncryption,
            ),
            ticketKey: keep("ticketKey", storedText(held, "ticketKey"), fresh.ticketKey),
            transportHashKey: keep("transportHashKey", storedText(held, "transportHashKey"), fresh.transportHashKey),
            totpSecretPart: keep("totpSecretPart", storedText(held, "totpSecretPart"), fresh.totpSecretPart),
            metricsPassword: keep("metricsPassword", storedText(held, "metricsPassword"), fresh.metricsPassword),
            objectStorage: keep(
                "objectStorage",
                storageAccess !== undefined && storageSecret !== undefined
                    ? { accessKey: storageAccess, secretKey: storageSecret }
                    : undefined,
                fresh.objectStorage,
            ),
            sfu: keep(
                "sfu",
                sfuClient !== undefined && sfuSecret !== undefined
                    ? { clientId: sfuClient, secret: sfuSecret }
                    : undefined,
                fresh.sfu,
            ),
        },
        added,
    };
}

/** The mint as it is stored. 0600, because it is every secret the instance has in one place. */
function mintDocument(secrets: MintedSecrets): GeneratedFile {
    return {
        path: MINT_FILE,
        contents: `${JSON.stringify({ format: MINT_FORMAT, mintedAt: new Date().toISOString(), secrets }, null, 2)}\n`,
        mode: 0o600,
    };
}

/* ------------------------------------------------------------------------------------------------
 * The machine.
 * ---------------------------------------------------------------------------------------------- */

export interface SetupPorts {
    readonly store: ConfigStore;
    readonly imageFor: ImageFor;
    readonly compose: ComposeRunner;

    /** Host paths of whatever TLS material the install script established. See {@link Certificates}. */
    readonly certificates?: Certificates;

    /**
     * How long the readiness wait gets, and how often it asks. Defaults to {@link READY_TIMEOUT_MS}.
     *
     * Overridable for one reason, and it is not tuning: the default is five minutes, so a test that
     * proves "a service that never comes up is named rather than reported as a duration" would take five
     * minutes to prove it — which is a test that gets deleted the first time somebody is in a hurry.
     * Nothing in production passes this.
     */
    readonly readiness?: { readonly timeoutMs?: number; readonly pollMs?: number };

    /**
     * Where a line of a subprocess's output goes, already redacted.
     *
     * Optional in the shape and not in practice. `docker compose pull` is minutes of work, and a
     * process with nowhere to report it is a process that appears to have hung at the exact moment the
     * operator is most inclined to reboot the box. This container's own log is the channel that is
     * there whether or not anyone is still holding the request. The real one is wired in
     * {@link setupFromEnvironment}.
     *
     * It is a port rather than a `console.log` here because everything that leaves this file has to be
     * redactable and testable, and because `main.ts` owns the log's format.
     */
    readonly progress?: (line: string) => void;
}

/**
 * The reversible half of an apply, handed to the half that is not.
 *
 * A type of its own rather than an {@link ApplyOutcome}, because a successful configure is not a
 * successful apply — it is the point at which the interesting part starts, and the second half needs
 * what the first produced: the mint, so the compose project's `.env` carries the same database password
 * that is already in `secrets.json`, and the project itself, so nothing rebuilds it from a second read
 * of the same answers.
 */
type Configured =
    | {
          readonly ok: true;
          readonly written: readonly WrittenFile[];
          readonly secrets: MintedSecrets;
          readonly project: ComposeProject;
      }
    | { readonly ok: false; readonly outcome: ApplyRefusal };

/**
 * One setup, from the first answer to a running instance.
 *
 * One instance per process, held by the server. Not durable, on purpose and with a cost — see the note
 * on restarts at the top of this file.
 */
export class Setup {
    readonly #store: ConfigStore;
    readonly #imageFor: ImageFor;
    readonly #compose: ComposeRunner;
    readonly #certificates: Certificates;
    readonly #report: ((line: string) => void) | undefined;
    readonly #readyTimeoutMs: number;
    readonly #readyPollMs: number;

    #held: Held = { answers: {}, credentials: {} };
    #stage: Stage = "awaiting-configuration";

    #secrets: MintedSecrets | undefined;
    #restarted = false;
    #note: string | undefined;
    #problem: string | undefined;
    #minted: readonly string[] = [];

    /** One-way. Once true, `compose up` has been called and containers may exist on this machine. */
    #started = false;

    #image: SetupState["image"];
    #validation: readonly RoleReport[] | undefined;
    #written: readonly WrittenFile[] | undefined;

    #progress: string[] = [];
    #services: readonly ServiceStatus[] | undefined;

    /** Roles the operator chose that the generated project will not run. Named, never guessed at. */
    #unrun: readonly string[] = [];

    /**
     * Single-flight, and the reason is a container start.
     *
     * Two browser tabs, or one impatient operator, otherwise start two interrogations of the same image
     * at once — two containers reflecting over every assembly, on a box that was sized for one. Keyed by
     * version so that changing the answer starts a new one rather than returning the old image's roles.
     */
    #interrogating: { readonly version: string; readonly work: Promise<InterrogationOutcome> } | undefined;

    /** Per role, for the same reason, keyed by version so a changed image is asked again. */
    readonly #details = new Map<string, Promise<RoleDetail>>();

    #applying: Promise<ApplyOutcome> | undefined;
    #adoption: Promise<void> | undefined;

    constructor(ports: SetupPorts) {
        this.#store = ports.store;
        this.#imageFor = ports.imageFor;
        this.#compose = ports.compose;
        this.#certificates = ports.certificates ?? {};
        this.#report = ports.progress;
        this.#readyTimeoutMs = ports.readiness?.timeoutMs ?? READY_TIMEOUT_MS;
        this.#readyPollMs = ports.readiness?.pollMs ?? READY_POLL_MS;
    }

    /**
     * A function that removes this install's secrets from a piece of text.
     *
     * The function, never the secrets. The panel has to redact things this module has never seen — a
     * container's log, the daemon's complaint about a failed restart — and the alternative was handing
     * those callers the bundle to redact with, which would make "nothing here returns a secret" false
     * for the sake of convenience. What leaves this object is a closure over them.
     *
     * Bound at the moment it is asked for rather than once: the mint happens partway through a setup,
     * so a redactor taken before it would quietly stop covering the database password the moment there
     * was one to cover.
     */
    redactor(): (text: string) => string {
        return (text) => redact(text, this.#secrets ?? {}, this.#held.credentials);
    }

    /** What the wizard renders. Cheap: it never starts a container. */
    async state(): Promise<SetupState> {
        await this.#adopt();

        const { answers, credentials } = this.#held;
        const warnings = [...warningsFor(answers, this.#image?.pairing)];

        if (this.#minted.length > 0)
            warnings.push(
                `new secret material was generated for ${this.#minted.join(", ")}, because the stored mint did not carry it. Everything else was reused.`,
            );

        // A role that was configured and is not run is silent in every other way: `conf.d` has a file
        // for it, the wizard shows it as chosen, and no container exists. `compose.ts` drops the roles
        // §6 refuses, and the wizard only warns about two of them.
        if (this.#unrun.length > 0)
            warnings.push(
                `${this.#unrun.join(", ")} ${this.#unrun.length === 1 ? "was" : "were"} configured but will not be run: the generated compose project does not carry ${this.#unrun.length === 1 ? "that role" : "those roles"}, so nothing starts for ${this.#unrun.length === 1 ? "it" : "them"}.`,
            );

        return {
            stage: this.#stage,
            answers,
            missing: missingAnswers(answers),
            policy: ROLE_POLICY,
            credentials: credentials.objectStorage === undefined ? [] : ["object storage"],
            image: this.#image,
            warnings,
            restarted: this.#restarted,
            note: this.#note,
            written: this.#written,
            validation: this.#validation,
            panel: answers.domain === undefined ? undefined : panelFor(answers.domain),
            progress: this.#progress.length === 0 ? undefined : [...this.#progress],
            services: this.#services,
            problem: this.#problem,
        };
    }

    /**
     * Takes one step of the wizard, or refuses all of it.
     *
     * All or nothing: a step that half-applies leaves the operator looking at a form where some fields
     * took and some did not, with one error message between them.
     */
    async submit(step: Step): Promise<Submission> {
        await this.#adopt();

        const next = takeStep(this.#held, step);

        // What roles exist is a fact about one image, so a step that changes the version is a step whose
        // roles have nothing to be checked against yet — and the answer this process is holding about the
        // old image stops being an answer about anything.
        const reimaged = next.answers.serverVersion !== this.#held.answers.serverVersion;
        const rejections = checkAnswers(next.answers, next.credentials, reimaged ? undefined : this.#image?.roles);

        if (rejections.length > 0) return { ok: false, rejections };

        if (reimaged) this.#image = undefined;

        this.#held = next;

        // The last verdict was about the answers that produced it. Keeping it beside changed answers is
        // how a UI ends up showing a green tick for a configuration that was never checked.
        this.#validation = undefined;

        this.#settle();

        return { ok: true, state: await this.state() };
    }

    /**
     * Asks the image what it offers. Slow — a container start — which is why it is a call of its own and
     * not something `state()` does on the way past.
     */
    async interrogate(): Promise<InterrogationOutcome> {
        await this.#adopt();

        const version = this.#held.answers.serverVersion;

        if (version === undefined)
            return {
                ok: false,
                reason: "no-version",
                problem: "which server version to install has not been answered yet, and the image is what gets asked.",
            };

        const running = this.#interrogating;

        if (running !== undefined && running.version === version) return running.work;

        const work = this.#interrogateOnce(version);

        this.#interrogating = { version, work };

        const outcome = await work;

        // A failure is not cached. Docker not running is a thing an operator fixes and retries, and a
        // memo that held the failure would answer them with it for the life of the process.
        if (!outcome.ok && this.#interrogating?.work === work) this.#interrogating = undefined;

        return outcome;
    }

    /**
     * Generates, has the server judge it, writes it, and starts it.
     *
     * A second call while one is running joins the first rather than starting another. That mattered
     * when this was a validation and a write; it matters more now that it is also an image pull and a
     * `compose up`, because the second call would otherwise reconcile the same project from a second set
     * of staged content while the first was still writing it. The window is now minutes rather than
     * seconds, and the join has to hold across all of it — including across the `compose up`, where a
     * second reconcile of a project that is mid-creation is the worst version of this.
     */
    async apply(): Promise<ApplyOutcome> {
        await this.#adopt();

        const running = this.#applying;

        if (running !== undefined) return running;

        const work = this.#applyOnce();

        this.#applying = work;

        try {
            return await work;
        } finally {
            this.#applying = undefined;
        }
    }

    async #applyOnce(): Promise<ApplyOutcome> {
        if (this.#problem !== undefined) return { ok: false, reason: "blocked", problem: this.#problem };

        const { answers, credentials } = this.#held;
        const rejections = checkAnswers(answers, credentials, this.#image?.roles);

        if (rejections.length > 0) return { ok: false, reason: "incomplete", rejections };

        if (!complete(answers))
            return {
                ok: false,
                reason: "incomplete",
                rejections: missingAnswers(answers).map((field) => ({ field, problem: "it has not been answered." })),
            };

        this.#stage = "applying";

        try {
            const configured = await this.#configure(answers, credentials);

            if (!configured.ok) {
                this.#stage = configured.outcome.reason === "invalid" ? "invalid" : this.#stageFromAnswers();

                return configured.outcome;
            }

            return await this.#start(answers, configured);
        } catch (cause) {
            // Nothing here is expected to throw; every failure it knows about is an outcome. What lands
            // here is a bug or a disk, and both are worth reporting with the cause's own words rather
            // than as an empty 500 the operator can do nothing with. Redacted now that some of those
            // words can have come from a command that read the `.env` beside the compose document.
            const problem = redact(reasonOf(cause), this.#secrets ?? {}, this.#held.credentials);

            // A throw after the start is not a write that failed — it is a bug on a machine that now has
            // containers on it, and reporting it as though nothing had started would send the operator
            // to retry against a running stack.
            if (this.#started) {
                this.#stage = "degraded";

                return {
                    ok: false,
                    reason: "start-failed",
                    problem: `the start did not finish: ${problem}`,
                    output: "",
                    running: true,
                    services: this.#services ?? [],
                    panel: panelFor(answers.domain),
                };
            }

            this.#stage = this.#stageFromAnswers();

            return { ok: false, reason: "write-failed", problem };
        }
    }

    /** Everything up to and including the write: the reversible half of an apply. See {@link Configured}. */
    async #configure(answers: Answers, credentials: OperatorCredentials): Promise<Configured> {
        const interrogation = await this.interrogate();

        const refuse = (outcome: ApplyRefusal): Configured => ({ ok: false, outcome });

        if (!interrogation.ok) return refuse({ ok: false, reason: "image", problem: interrogation.problem });

        const known = new Map(interrogation.roles.map((role) => [role.id, role]));
        const unknown = answers.roles.filter((role) => !known.has(role));

        if (unknown.length > 0)
            return refuse({
                ok: false,
                reason: "incomplete",
                rejections: [{ field: "roles", problem: `this server image has no role called ${unknown.join(", ")}.` }],
            });

        const details: RoleDetail[] = [];

        for (const role of answers.roles) {
            try {
                details.push(await this.#explain(answers.serverVersion, role, known.get(role)));
            } catch (cause) {
                return refuse({ ok: false, reason: "image", problem: describeCliFailure(cause) });
            }
        }

        const secrets = this.#mint();
        const files = generate(answers, details, { secrets, supplied: credentials });

        // Nothing to write into `conf.d` means the interrogation told us nothing about where settings go,
        // and the secrets file alone would be an instance with the shipped defaults for everything else —
        // including the signing key that is published in a public repository.
        if (!files.some((file) => file.path.startsWith(`${DEPLOYMENT.confD}/`)))
            return refuse({
                ok: false,
                reason: "image",
                problem: "the image reported no configuration sections for the chosen roles, so there is nothing to write. That is a server this bootstrapper cannot read rather than an answer that was wrong.",
            });

        // Built here — before anything is staged and long before anything is written — because every way
        // `composeProject` can refuse is a fact about the answers or about what the install script left
        // on this machine, and finding out after the configuration has landed means an install that is
        // configured and unstartable. It throws rather than returning, and the throw is the report: it
        // names the certificate that was not passed, or the pinned reference the panel's image cannot be
        // derived from, in sentences written for an operator.
        let project: ComposeProject;

        try {
            project = composeProject(answers, secrets, this.#composeOptions(answers, interrogation.roles));
        } catch (cause) {
            return refuse({
                ok: false,
                reason: "not-startable",
                problem: redact(reasonOf(cause), secrets, credentials),
            });
        }

        // The document and its sidecars. The `.env` among them is mode 0600 and holds the database
        // password, so these go through the same store, with the modes they carry, as everything else.
        const projectFiles: readonly GeneratedFile[] = [
            { path: COMPOSE_FILENAME, contents: project.document, mode: COMPOSE_MODE },
            ...project.files,
        ];

        const staging = await this.#store.scratch();

        try {
            await this.#store.write(staging, files);

            let reports: RoleReport[];

            try {
                reports = await this.#validate(answers, staging, files, secrets, credentials);
            } catch (cause) {
                // Docker failing to run is not the server saying no. An operator told their configuration
                // is invalid goes and edits files, and the problem is in their daemon.
                //
                // Redacted like the reports are, and for the same reason: this is the one failure path
                // where the text came from a container that had the secrets document mounted.
                return refuse({
                    ok: false,
                    reason: "image",
                    problem: redact(describeCliFailure(cause), secrets, credentials),
                });
            }

            this.#validation = reports;

            if (reports.some((report) => !report.ok)) return refuse({ ok: false, reason: "invalid", reports });

            // Everything above this line is reversible. The mint goes first because it is the one thing
            // that cannot be regenerated: with it, every file below can be rebuilt byte for byte, and a
            // process that dies between these two writes comes back and produces the same install.
            await this.#store.write(this.#store.root, [mintDocument(secrets)]);
            await this.#store.write(this.#store.root, files);

            // Last, and beside the configuration rather than under it: compose looks for its document in
            // the project directory, and the roles' bind mounts in it name `conf.d` and the secrets file
            // by relative path. Written after them so that a crash between the two writes leaves an
            // install with no compose document — nothing starts — rather than a compose document that
            // would mount configuration which is not there yet.
            await this.#store.write(this.#store.root, projectFiles);

            const written = [...files, ...projectFiles].map(({ path, mode }) => ({ path, mode }));

            this.#written = written;
            this.#secrets = secrets;

            // Named rather than assumed equal: `compose.ts` drops the roles §6 refuses, and the wizard
            // only warns about two of them. A role configured and not run is otherwise silent.
            this.#unrun = answers.roles.filter((role) => !project.roles.includes(role));

            return { ok: true, written, secrets, project };
        } finally {
            // A staging directory that will not go away is not worth failing an apply that succeeded, and
            // there is nowhere to report it to from here anyway — this file does not log. The next run
            // sweeps it, which is what the sweep in `scratch` is for.
            await this.#store.discard(staging).catch(() => undefined);
        }
    }

    /**
     * What `compose.ts` needs to know about this machine that it cannot work out for itself.
     */
    #composeOptions(answers: Answers, known: readonly RoleSummary[]): ComposeOptions {
        return {
            // The install root **as the host sees it**. It is only the host's path because the install
            // script bind-mounts the configuration directory into this container at the path it has on
            // the host — the same constraint the validation mounts rest on, for the same reason: the
            // docker daemon resolves every one of these on the host and knows nothing about this
            // container's filesystem. See the note at the top of this file.
            installRoot: this.#store.root,

            // What the image said about itself, which beats `compose.ts`'s fallback table. It decides
            // publishing, and the direction it is wrong in matters: a silo mistaken for a client puts an
            // Orleans gateway on the internet.
            knownRoles: known,

            // The image that was interrogated, and that said yes to this configuration. `compose.ts` has
            // a default repository of its own and it is a *different* one, so leaving this out would run
            // an image nobody validated — and the whole worth of the step before this is that the server
            // itself read these files and accepted them. See the report.
            serverImage: referenceFor(answers.serverVersion),

            tls: needsCertificate(answers.traffic) ? this.#certificates.instance : undefined,
            voiceTls: this.#certificates.media,
        };
    }

    /**
     * The half that cannot be taken back: pull, start everything but this container, and wait.
     *
     * The order is the whole of it. Pulling first means the longest and most failure-prone part happens
     * while nothing is running and a retry is still clean; `up` then runs with every image already
     * local, so the window in which the instance is half-created is seconds rather than the length of a
     * download.
     */
    async #start(answers: Answers, configured: Configured & { ok: true }): Promise<ApplyOutcome> {
        const where: ComposeInvocation = {
            project: COMPOSE_PROJECT,
            directory: this.#store.root,
            file: COMPOSE_FILENAME,
        };

        const panel = panelFor(answers.domain);

        // Everything a compose command prints goes through here. It reads the `.env` beside the document
        // and echoes service definitions it is reconciling, so it is exactly as capable of quoting one of
        // our own secrets back at us as `--validate-config` is.
        const hide = (text: string): string => redact(text, configured.secrets, this.#held.credentials);
        const say = (line: string): void => this.#say(hide(line));

        this.#stage = "starting";
        this.#progress = [];

        const pulled = await this.#compose.pull(where, say);

        if (!pulled.ok) {
            // Back to `configured`, which is exactly true: the files are on disk and nothing is running
            // against them. Nothing has been started, so the operator is still looking at this wizard
            // and the next thing they press can be the same button.
            this.#stage = "configured";

            return {
                ok: false,
                reason: "start-failed",
                problem:
                    "the images could not be pulled, so nothing was started. The configuration is written and this wizard is still here: fix whatever the registry said and apply again.",
                output: hide(pulled.output),
                running: false,
                services: [],
                panel,
            };
        }

        // Every service but this one. `compose up` with no arguments would recreate the panel — which
        // is the container running this code — and the process would be killed part-way through
        // starting everything else, leaving a project nobody was watching finish.
        const starting = configured.project.services.filter((name) => name !== PANEL_SERVICE);

        // Set before the call rather than after it, and that ordering is load-bearing: a `up` that
        // throws part-way through has still created containers, and a failure reporting "the machine is
        // as it was" would be a lie told at the worst possible moment.
        this.#started = true;

        const started = await this.#compose.up(where, starting, say);

        if (!started.ok) {
            // Which side of the line this ended on is a fact about the machine, so it is answered by
            // asking compose what exists rather than by guessing from where the code got to.
            const services = await this.#statusOrNone(where);

            this.#services = services;
            this.#stage = services.length > 0 ? "degraded" : "configured";

            return {
                ok: false,
                reason: "start-failed",
                problem:
                    services.length > 0
                        ? "docker compose refused to bring the project up, and it had already created containers — so there is something running on this machine against the configuration that was just written. Read the output below, then stop or fix the project rather than starting a second one."
                        : "docker compose refused to bring the project up and created nothing, so no containers are running. The configuration is written, so fixing what compose complained about below and applying again reuses everything already generated — including the secrets, which are minted once.",
                output: hide(started.output),
                running: services.length > 0,
                services,
                panel,
            };
        }

        const waited = await this.#waitUntilReady(where, starting);

        this.#services = waited.services;

        if (waited.waiting.length > 0) {
            this.#stage = "degraded";

            return {
                ok: false,
                reason: "not-ready",
                problem: `the project started and did not come up within ${Math.round(this.#readyTimeoutMs / 1000)}s: ${waited.waiting.join("; ")}. The containers are running — 'docker compose -p ${COMPOSE_PROJECT} logs' on the service named above is where the reason is.`,
                running: true,
                services: waited.services,
                panel,
            };
        }

        this.#stage = "running";

        return { ok: true, written: configured.written, services: waited.services, panel };
    }

    /**
     * Waits for every service in the project, and gives up on time rather than on hope.
     *
     * Bounded because the operator's browser is holding the request that started this and it gets one
     * answer; an unbounded wait is a browser that eventually gives up on a question nobody can ask
     * again. What comes back when the bound expires is which services are not ready and what compose
     * calls them — see {@link unreadyServices} for why that sentence is the whole point.
     */
    async #waitUntilReady(
        where: ComposeInvocation,
        expected: readonly string[],
    ): Promise<{ readonly waiting: readonly string[]; readonly services: readonly ServiceStatus[] }> {
        const deadline = Date.now() + this.#readyTimeoutMs;

        let services = await this.#statusOrNone(where);
        let waiting = unreadyServices(expected, services);

        while (waiting.length > 0 && Date.now() < deadline) {
            await new Promise<void>((wake) => setTimeout(wake, this.#readyPollMs));

            services = await this.#statusOrNone(where);
            waiting = unreadyServices(expected, services);

            // Reported every round rather than only at the end: this is the only thing moving during
            // the minutes a cold Postgres takes, and a log that says nothing for five minutes is
            // indistinguishable from a process that has hung.
            if (waiting.length > 0) this.#say(`waiting: ${waiting.join("; ")}`);
        }

        return { waiting, services };
    }

    /**
     * What compose says exists, or nothing.
     *
     * A `ps` that fails is not evidence that the project is empty, but it is evidence that we cannot
     * say what is in it — and the caller is in the middle of reporting a failure. Reporting the `ps`
     * failure instead would replace the operator's actual problem with ours.
     */
    async #statusOrNone(where: ComposeInvocation): Promise<readonly ServiceStatus[]> {
        try {
            return await this.#compose.status(where);
        } catch {
            return [];
        }
    }

    /** One line of progress: kept for the state, and handed to whoever is writing the container log. */
    #say(line: string): void {
        this.#progress.push(line);

        if (this.#progress.length > PROGRESS_LINES) this.#progress.splice(0, this.#progress.length - PROGRESS_LINES);

        this.#report?.(line);
    }

    /**
     * One `--validate-config` per chosen role, against the staged files.
     *
     * Per role because the command validates every role in the catalog when it is not told which one, and
     * a self-hosted instance deliberately does not run all of them — so an unscoped run reports errors
     * for configuration nobody asked for and refuses a perfectly good install.
     *
     * Every role is asked even after one has refused. The alternative sends the operator round the loop
     * once per problem, and each trip is a container start.
     */
    async #validate(
        answers: Answers,
        staging: string,
        files: readonly GeneratedFile[],
        secrets: MintedSecrets,
        credentials: OperatorCredentials,
    ): Promise<RoleReport[]> {
        // The secrets document travels with the directory or validation lies — see {@link Mounts}. It is
        // named only when it was generated, because naming one that is not on disk is its own Error.
        const mounts: Mounts = {
            configDir: join(staging, DEPLOYMENT.confD),
            secretsFile: files.some((file) => file.path === DEPLOYMENT.secretsFile)
                ? join(staging, DEPLOYMENT.secretsFile)
                : undefined,
        };

        const image = this.#imageFor(answers.serverVersion, mounts);
        const reports: RoleReport[] = [];

        for (const role of answers.roles) {
            const outcome = await validateConfig(image, role);

            reports.push({ role, ok: outcome.ok, output: redact(outcome.output, secrets, credentials) });
        }

        return reports;
    }

    async #interrogateOnce(version: string): Promise<InterrogationOutcome> {
        const image = this.#imageFor(version);

        try {
            const { capabilities, version: reported, pairing } = await interrogate(image);

            this.#image = {
                reference: image.reference,
                version: reported,
                pairing,
                roles: capabilities.roles,
                topologies: capabilities.topologies,
            };

            return {
                ok: true,
                reference: image.reference,
                version: reported,
                pairing,
                roles: capabilities.roles,
                topologies: capabilities.topologies,
            };
        } catch (cause) {
            return { ok: false, reason: "image", problem: describeCliFailure(cause) };
        }
    }

    async #explain(version: string, role: string, summary: RoleSummary | undefined): Promise<RoleDetail> {
        // A space is a separator neither half can contain: a role id comes out of the image's own table,
        // which is whitespace-delimited, and a version with a space in it is refused at the step.
        const key = `${version} ${role}`;
        const held = this.#details.get(key);

        if (held !== undefined) return held;

        const work = explainRole(this.#imageFor(version), role, summary);

        this.#details.set(key, work);

        try {
            return await work;
        } catch (cause) {
            // Same reasoning as the interrogation memo: a failure that stays cached is a failure the
            // operator cannot retry past.
            this.#details.delete(key);

            throw cause;
        }
    }

    /** The instance's secrets: whatever was minted before, and a fresh bundle only the first time. */
    #mint(): MintedSecrets {
        this.#secrets ??= mintSecrets();

        return this.#secrets;
    }

    /**
     * Looks for an install that a previous process left behind.
     *
     * Single-flight and awaited by everything public, because two requests arriving together on a fresh
     * process would otherwise both read the mint and the second would overwrite the first's answer.
     */
    async #adopt(): Promise<void> {
        this.#adoption ??= this.#adoptOnce();

        await this.#adoption;
    }

    async #adoptOnce(): Promise<void> {
        let stored: string | undefined;

        try {
            stored = await this.#store.read(MINT_FILE);
        } catch (cause) {
            // Unreadable is not absent. An unreadable mint may still be the mint a running instance is
            // using, and minting a new one over it changes the database password of a database that will
            // not accept the new one.
            this.#block(
                `${MINT_FILE} is there and could not be read (${reasonOf(cause)}). It holds the secrets an existing install is already using, and generating new ones over the top of it would leave this instance unable to reach its own database.`,
            );

            return;
        }

        if (stored === undefined) return;

        this.#restarted = true;

        try {
            const adopted = adoptMint(JSON.parse(stored) as unknown);

            this.#secrets = adopted.secrets;
            this.#minted = adopted.added;
            this.#note =
                "this setup was started before — either the wizard was reloaded after a restart, or the installer is being run again. The answers from that run were not kept, so the questions come round again; the secrets that were generated then were kept, so re-applying will not change the database password of a database that already exists.";
        } catch (cause) {
            this.#block(
                `${MINT_FILE} is there and is not something this bootstrapper can read (${reasonOf(cause)}). It holds the secrets an existing install is already using; generating new ones over the top of it would leave this instance unable to reach its own database. Move it aside only if there is no install to protect.`,
            );
        }
    }

    #block(problem: string): void {
        this.#problem = problem;
        this.#stage = "blocked";
    }

    /** Back to whichever collecting stage the answers put us in, unless something has blocked. */
    #settle(): void {
        if (this.#stage === "blocked") return;

        this.#stage = this.#stageFromAnswers();
    }

    #stageFromAnswers(): Stage {
        if (this.#problem !== undefined) return "blocked";

        return checkAnswers(this.#held.answers, this.#held.credentials, this.#image?.roles).length === 0 &&
            complete(this.#held.answers)
            ? "ready"
            : "awaiting-configuration";
    }
}

function reasonOf(cause: unknown): string {
    return cause instanceof Error ? cause.message : String(cause);
}

/**
 * A failed interrogation, in a sentence.
 *
 * The server's own output is included because it is the only thing that says *what* it did not like, and
 * it is the one place an operator can act on. It is not redacted here: these commands are run against an
 * image with no configuration mounted, so there is nothing of ours in what they print.
 */
function describeCliFailure(cause: unknown): string {
    if (!(cause instanceof ArgonCliError)) return reasonOf(cause);

    const output = cause.output.trim();

    return output.length === 0 ? cause.message : `${cause.message}: ${output}`;
}

/* ------------------------------------------------------------------------------------------------
 * The real world.
 * ---------------------------------------------------------------------------------------------- */

/**
 * The image reference for an answer.
 *
 * A bare version becomes a tag on the published image; anything with a slash or a digest in it is taken
 * as a reference the operator gave in full, which is what somebody installing from their own registry
 * types. `argon.ts` reads the version back off whichever it turns out to be.
 */
export function referenceFor(version: string): string {
    const answer = version.trim();

    return answer.includes("/") || answer.includes("@") ? answer : `${COMPOSE_SERVER_IMAGE}:${answer}`;
}

/** Docker, for real. */
export const dockerImageFor: ImageFor = (version, mounts) =>
    dockerImage(referenceFor(version), mounts === undefined ? {} : { ...mounts });

/**
 * How long each compose command may take.
 *
 * `pull` is the outlier and it is not close: it is a first-run download of a Postgres, a Valkey, a NATS,
 * a SeaweedFS, a Traefik, a LiveKit and however many copies of the server image the roles need, over
 * whatever connection the operator's VPS has. Half an hour is generous and the alternative — a bound
 * that expires mid-download on a slow link — kills an install that was working.
 *
 * `up` is short by comparison because by the time it runs every image is local; what is left is
 * `depends_on` waiting on Postgres's health and the bucket-init loop. It is a bound and not a schedule:
 * the readiness wait after it is what actually watches the project come up.
 */
const COMPOSE_TIMEOUTS = { pull: 30 * 60_000, up: 10 * 60_000, status: 60_000 } as const;

/**
 * The exact argv for one compose command.
 *
 * Extracted from the runner for the reason `argon.ts` extracts `dockerCommandFor`: what a command is
 * pointed at is the part worth asserting, and asserting it should not need a docker daemon.
 *
 * Every invocation names the project, the directory and the file explicitly. The document already
 * carries the project name, so `--project-name` is redundant on the happy path and is not there for the
 * happy path: a document edited by hand to a different name would otherwise become a second stack
 * beside the first, silently, and this makes it a mismatch compose complains about instead.
 *
 * `--ansi never` because this output is read by a log and by {@link SetupState.progress}, and cursor
 * movement in either is noise. There is no `--progress` flag: compose prints plain lines when there is
 * no TTY, and these are spawned with pipes.
 */
export function composeCommandFor(
    where: ComposeInvocation,
    args: readonly string[],
    docker = "docker",
): string[] {
    return [
        docker,
        "compose",
        "--ansi",
        "never",
        "--project-name",
        where.project,
        "--project-directory",
        where.directory,
        "--file",
        join(where.directory, where.file),
        ...args,
    ];
}

/**
 * {@link composeCommandFor}, run — and the daemon, read.
 *
 * Two transports because there are two kinds of operation here and they want different things. Starting
 * and pulling want compose, which is the only thing that knows how to reconcile a file against a
 * machine; asking what is running wants a schema, which the CLI does not promise and the Engine API
 * does. See `docker.ts`.
 */
export function dockerCompose(engine: EngineRequest = dockerEngine(), docker = "docker"): ComposeRunner {
    const command = (where: ComposeInvocation, ...args: readonly string[]): string[] =>
        composeCommandFor(where, args, docker);

    return {
        pull: (where, onOutput) => runStreamed(command(where, "pull"), COMPOSE_TIMEOUTS.pull, onOutput),

        up: (where, services, onOutput) =>
            // `--remove-orphans` because this is the second apply's problem: an operator who drops a role
            // and applies again would otherwise leave its container running against configuration that
            // no longer mentions it. Scoped to this project name, so it can only remove containers this
            // installer created — and the panel is still in the compose file, so naming the others does
            // not make this container an orphan.
            //
            // Not `--wait`. It would do the waiting, and it would answer "some services failed" — while
            // what is owed to the operator is *which* service, which `ps` says directly.
            runStreamed(command(where, "up", "--detach", "--remove-orphans", ...services), COMPOSE_TIMEOUTS.up, onOutput),

        // Read from the daemon, not from the CLI. See `docker.ts`: compose's `ps --format json` changed
        // shape partway through v2 and an installer does not get to pin which compose is installed, so
        // the parser that coped with both was a permanent tax paid to read something the Engine API
        // already returns with a schema. The label compose stamps on its containers is what makes the
        // two views the same view.
        status: (where) => projectStatus(where.project, engine),
    };
}

interface StreamedResult extends ComposeResult {
    /** Just stdout, for the one command whose output is data rather than progress. */
    readonly stdout: string;
}

/**
 * Runs a command, reporting each line as it arrives.
 *
 * Both streams are drained concurrently with the wait and not one after the other, for the reason
 * `argon.ts` gives: a command that fills a pipe nobody is reading blocks forever, and these write to
 * both. What is different here is that the lines are handed out as they arrive rather than at the end —
 * an image pull is minutes of work, and a report that arrives with the exit code is not a report.
 */
async function runStreamed(
    cmd: readonly string[],
    timeoutMs: number,
    onOutput: (line: string) => void,
): Promise<StreamedResult> {
    let proc;

    try {
        proc = Bun.spawn({ cmd: [...cmd], stdout: "pipe", stderr: "pipe", stdin: "ignore" });
    } catch (cause) {
        return {
            ok: false,
            output: `could not run '${cmd[0] ?? ""}': ${reasonOf(cause)}`,
            stdout: "",
        };
    }

    const output: string[] = [];
    let timedOut = false;

    const timer = setTimeout(() => {
        timedOut = true;
        proc.kill("SIGKILL");
    }, timeoutMs);

    try {
        const [stdout, , exitCode] = await Promise.all([
            drain(proc.stdout, (line) => {
                output.push(line);
                onOutput(line);
            }),
            drain(proc.stderr, (line) => {
                output.push(line);
                onOutput(line);
            }),
            proc.exited,
        ]);

        if (timedOut)
            return {
                ok: false,
                output: `${output.join("\n")}\n'${cmd.join(" ")}' did not finish within ${timeoutMs}ms and was killed`,
                stdout,
            };

        return { ok: exitCode === 0, output: output.join("\n"), stdout };
    } finally {
        clearTimeout(timer);
    }
}

/** Reads a stream to the end, calling back once per line and returning the whole of it. */
async function drain(stream: ReadableStream<Uint8Array> | undefined, onLine: (line: string) => void): Promise<string> {
    if (stream === undefined) return "";

    const reader = stream.getReader();
    const decoder = new TextDecoder();

    let whole = "";
    let buffered = "";

    for (;;) {
        const { done, value } = await reader.read();

        if (done) break;

        // `{ stream: true }` because a multi-byte character can straddle two chunks, and decoding each
        // chunk on its own turns one of those into two replacement characters in the operator's log.
        const text = decoder.decode(value, { stream: true });

        whole += text;
        buffered += text;

        for (;;) {
            const newline = buffered.indexOf("\n");

            if (newline < 0) break;

            const line = buffered.slice(0, newline).replace(/\r$/, "");

            buffered = buffered.slice(newline + 1);

            if (line.trim().length > 0) onLine(line);
        }
    }

    const tail = buffered.trim();

    if (tail.length > 0) onLine(tail);

    return whole;
}

function within(directory: string, path: string): boolean {
    const step = relative(directory, path);

    return step.length > 0 && !step.startsWith("..") && !isAbsolute(step);
}

/**
 * The install directory.
 *
 * Every write is a temporary file with the right mode, then a rename over the target. Two reasons, and
 * both have bitten somebody: a reader that opens the file mid-write gets the old one rather than half of
 * the new one, and `writeFile(path, data, { mode })` does not change the mode of a file that already
 * exists — so a secrets file that was once 0644 would stay 0644 forever, with the mode argument sitting
 * there looking like it did something.
 */
export function localStore(root: string): ConfigStore {
    const staging = join(root, STAGING_PREFIX);

    return {
        root,

        async write(directory, files) {
            for (const file of files) {
                const target = resolve(directory, file.path);

                // The generator's paths are constants, and this is a port that will one day be handed a
                // path from somewhere else. A write that climbs out of the directory it was given is not
                // recoverable by the operator, because they cannot see where it went.
                if (!within(directory, target))
                    throw new Error(`'${file.path}' would be written outside ${directory}`);

                await mkdir(dirname(target), { recursive: true });

                const temporary = `${target}.${randomBytes(6).toString("hex")}.partial`;

                await writeFile(temporary, file.contents, { mode: file.mode });

                // Explicit, because the umask this process inherited subtracts from the mode above and
                // never adds to it. 0600 survives any umask; 0644 does not survive 0077, and a conf.d
                // file the argon container cannot read is a boot that fails on a missing setting.
                await chmod(temporary, file.mode);
                await rename(temporary, target);
            }
        },

        async read(path) {
            try {
                return await readFile(resolve(root, path), "utf8");
            } catch (cause) {
                if ((cause as NodeJS.ErrnoException).code === "ENOENT") return undefined;

                throw cause;
            }
        },

        async scratch() {
            // Swept before a new one is made rather than after a crash, because after a crash there is
            // nothing running to do the sweeping — and what is left behind is a staged secrets file.
            for (const entry of await readdir(root, { withFileTypes: true }))
                if (entry.isDirectory() && entry.name.startsWith(STAGING_PREFIX))
                    await rm(join(root, entry.name), { recursive: true, force: true });

            return await mkdtemp(staging);
        },

        async discard(directory) {
            // Only ever a directory this made. `discard` takes a path, and a path is a thing that can
            // arrive wrong; the install root itself is one bad join away.
            if (!directory.startsWith(staging)) throw new Error(`${directory} is not a staging directory`);

            await rm(directory, { recursive: true, force: true });
        },
    };
}

/**
 * The setup machine for this container, from the environment the install script set.
 *
 * A fallback, and it should not survive: the right shape is `main.ts` building this beside the server
 * config it already has, because that file is the one that is allowed to touch the world and `config.ts`
 * has already proved the directory exists by the time it runs. This exists so that the setup routes are
 * not dark while that one line is somebody else's file to write. It reads the variable names from
 * `config.ts` rather than restating them, so there is still only one copy of the contract.
 *
 * **The certificate paths are read here and not from `ServerConfig`** because `config.ts` hands the
 * *contents* on to the listener, and what the generated compose project needs is the two paths — the
 * docker daemon resolves them on the host, and writing the PEM out again beside the install would be a
 * second copy of a private key for nothing. There is no environment variable for the media subdomain's
 * certificate, so a Cloudflare-proxied instance publishing voice directly refuses at the apply with a
 * sentence about it; see the report for the pair `config.ts` owes.
 *
 * **Progress goes to stderr**, which is the one thing about this file that looks like logging and is
 * not: these are a subprocess's own lines, already redacted, passed through rather than swallowed. They
 * go to stderr for the reason `main.ts` sends warnings there — a `docker logs` with two streams keeps
 * them apart — and they exist because the container log is the channel that is there whether or not a
 * browser is still waiting on the request that started this.
 */
/**
 * One certificate and key, or nothing — never one of them.
 *
 * Both pairs the install script can hand over are read through this: the instance's and, for §5's
 * Cloudflare shape, the media subdomain's. Half a pair is a listener that starts and fails every
 * handshake, which reads as a broken network rather than as a missing file. On the media name it is
 * worse than on the main one: voice failing is quieter than the API failing, so it gets found by a user
 * rather than by the operator watching the install.
 *
 * An empty string counts as absent. Compose writes `FOO=` for a variable the host never defined, so a
 * shape that legitimately has no certificate arrives with the variables present and empty; reading that
 * as "a certificate was named" would refuse exactly the installs the design permits.
 */
export function certificatePair(certificatePath: string | undefined, keyPath: string | undefined): TlsMaterial | undefined {
    if (certificatePath === undefined || certificatePath.length === 0) return undefined;
    if (keyPath === undefined || keyPath.length === 0) return undefined;

    return { certificatePath, keyPath };
}

export function setupFromEnvironment(environment: Readonly<Record<string, string | undefined>> = process.env): Setup | undefined {
    const root = environment[ENVIRONMENT.configDirectory]?.trim();

    if (root === undefined || root.length === 0) return undefined;

    // Read from the passed-in environment, not from process.env directly, so a test can hand one in.
    const instance = certificatePair(
        environment[ENVIRONMENT.certificate]?.trim(),
        environment[ENVIRONMENT.certificateKey]?.trim(),
    );

    const media = certificatePair(
        environment[ENVIRONMENT.voiceCertificate]?.trim(),
        environment[ENVIRONMENT.voiceCertificateKey]?.trim(),
    );

    return new Setup({
        store: localStore(root),
        imageFor: dockerImageFor,
        compose: dockerCompose(),
        certificates: { instance, media },
        progress: (line) => void process.stderr.write(`${line}\n`),
    });
}
