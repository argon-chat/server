import { randomBytes } from "node:crypto";
import type {
    FeatureSummary,
    RoleDetail,
    RoleSummary,
    ServerCapabilities,
    TopologySummary,
} from "./model";

/**
 * Asking the Argon image what it needs, instead of knowing.
 *
 * The bootstrapper writes `conf.d/<feature>.json`, and the set of sections a feature reads is declared
 * in the server's own code. Carrying a copy of that here would be a second source of truth that drifts
 * the first time somebody adds an option — and drifts *silently*, into configuration that validates
 * against a schema of sections which has moved. So the server binary is asked instead. Three commands,
 * all of which run without starting anything:
 *
 *   --roles                       roles, their shape, and the declared topologies
 *   --explain <role>              what a role hosts and calls, every feature it enables, and the
 *                                 configuration sections each of those features reads
 *   --validate-config [--role X]  check a configuration; exit 0 when it holds
 *
 * The parsers below are pure — text in, model types out — because that is what makes this testable
 * without a container. Everything that touches a process lives at the bottom of the file, behind
 * {@link ServerImage}, so a test never needs docker and a parse never needs a network.
 *
 * ## Reading the output at all
 *
 * The server logs through Serilog's JSON formatter, so anything that logs during these commands writes
 * a JSON object onto the same stdout as the human table. The invocations in this repository pipe
 * through `grep -v '"Level":"Warning"'` for exactly that reason. We cannot grep, so every parser here
 * skips lines it does not recognise instead of failing on them — with one exception, described on each
 * parser: a result that is empty, or that contradicts the command's own counts, is raised. A wizard
 * offering no roles, or writing configuration for a role whose features went missing, is worse than a
 * message saying the output could not be read.
 */

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// Failures
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/**
 * Why an interrogation did not produce an answer.
 *
 * The kinds are kept apart because the operator's next move differs entirely between them, and one
 * flat "something went wrong" would put "your configuration is invalid" and "docker is not installed"
 * behind the same sentence.
 */
export type CliFailure =
    | "unreadable-output" // it answered, in a shape this bootstrapper does not read
    | "command-failed" // it ran and reported a failure of its own
    | "timeout" // it never answered; see the note on DEFAULT_TIMEOUT_MS
    | "runner-failed"; // docker itself could not run it

export class ArgonCliError extends Error {
    readonly kind: CliFailure;

    /** What the command printed, kept so the operator sees the server's own words and not only ours. */
    readonly output: string;

    constructor(kind: CliFailure, message: string, output = "") {
        super(message);
        this.name = "ArgonCliError";
        this.kind = kind;
        this.output = output;
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// Noise
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/**
 * Whether a line is a structured log event rather than command output.
 *
 * The rule is narrow on purpose: every line these commands print begins with a letter, a digit or an
 * indent, and every Serilog event begins with `{`. Matching the brace rather than `"Level":` catches
 * an event of any level, and also catches one cut short by two writers sharing the descriptor — which
 * parsing the line as JSON would not.
 *
 * If somebody replaces this with a JSON.parse, note what breaks: a truncated event stops counting as
 * noise and starts counting as content, and in {@link parseExplain} an unrecognised line at column 0
 * ends the feature block early.
 */
function isNoise(line: string): boolean {
    return line.trimStart().startsWith("{");
}

/**
 * A capture group that matched.
 *
 * Every group the patterns in this file capture is mandatory, so `undefined` means the pattern and the
 * call reading it have drifted apart — a bug here, not bad input. It throws rather than defaulting to
 * an empty string, because an empty role id becomes a blank row in the wizard rather than a failure.
 */
function captured(match: RegExpMatchArray, index: number): string {
    const value = match[index];

    if (value === undefined) throw new Error(`capture group ${index} is missing from the pattern`);

    return value;
}

function lines(stdout: string): string[] {
    return stdout.split(/\r?\n/);
}

/** `a, b, c` — the one list shape both commands use. Empty entries dropped so `[]` reads as none. */
function splitList(value: string): string[] {
    return value
        .split(",")
        .map((entry) => entry.trim())
        .filter((entry) => entry.length > 0);
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// --roles
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/**
 * One row of the table.
 *
 * Anchored on `silo|client` in the second column rather than on the first column or on the header,
 * because that token is what separates a role row from everything else the command prints: the
 * summary, the topologies, and the `loaded:` lines `ARGON_DUMP_LOADED` adds.
 */
const ROLE_ROW = /^(\S+)[ \t]+(silo|client)[ \t]+(\d+)[ \t]+(\d+)[ \t]+(.*)$/;

/** `12 role(s), 42 grain class(es) discovered in 2 assembly(ies)` — the command counting itself. */
const ROLE_COUNT = /^(\d+) role\(s\)/;

/** `topology distributed [entrypoint, botapi, admin]` */
const TOPOLOGY = /^topology[ \t]+(\S+)[ \t]+\[(.*)\]$/;

/**
 * Reads `--roles`.
 *
 * The version is a parameter and not something lifted out of the text, because the command does not
 * print one — and making it look like it did would hide that from the next reader. See
 * {@link ServerVersion} for where it actually comes from.
 *
 * Two ways this refuses rather than returning something:
 *
 *  - **Nothing parsed.** An empty role list is a wizard with no roles to offer, which reads as a UI
 *    bug and gets reported as one. It is a parse failure and says so.
 *  - **A count that disagrees.** The command prints how many roles it found. If fewer were read, the
 *    table's shape has moved and we are holding a truncated list — which would quietly install an
 *    instance missing a role the operator needed. That count is the cheapest available check that the
 *    format is still what we think it is, and it is worth more than the tolerance it costs.
 */
export function parseRoles(stdout: string, version: string): ServerCapabilities {
    const roles: RoleSummary[] = [];
    const topologies: TopologySummary[] = [];

    let declared: number | undefined;

    for (const line of lines(stdout)) {
        if (isNoise(line)) continue;

        const row = ROLE_ROW.exec(line);

        if (row !== null) {
            roles.push({
                id: captured(row, 1),
                kind: captured(row, 2) === "client" ? "client" : "silo",
                grains: Number(captured(row, 3)),
                features: Number(captured(row, 4)),
                description: captured(row, 5).trim(),
            });
            continue;
        }

        const topology = TOPOLOGY.exec(line);

        if (topology !== null) {
            topologies.push({ name: captured(topology, 1), roles: splitList(captured(topology, 2)) });
            continue;
        }

        const count = ROLE_COUNT.exec(line);

        if (count !== null) declared = Number(captured(count, 1));
    }

    if (roles.length === 0)
        throw new ArgonCliError(
            "unreadable-output",
            "--roles listed no roles; the image answered in a shape this bootstrapper does not read",
            stdout,
        );

    if (declared !== undefined && declared !== roles.length)
        throw new ArgonCliError(
            "unreadable-output",
            `--roles reported ${declared} role(s) but ${roles.length} could be read; ` +
                "the table's shape has moved and the list would be incomplete",
            stdout,
        );

    return { version, roles, topologies };
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// --explain
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/**
 * `role 'entrypoint' — Orleans client`
 *
 * Anchored on the quotes rather than on the em dash. The dash is a non-ASCII character crossing a
 * container boundary and a console encoding; the quotes are not, and the id is what we came for.
 */
const EXPLAIN_HEADER = /^role[ \t]+'([^']+)'/;

/** The one line after which indented entries are features and not something else. */
const FEATURES_HEADER = /^features, in configure order:/;

/** `reads 10 configuration section(s); each may also come from conf.d/<feature>.json` */
const READS_COUNT = /^reads[ \t]+(\d+)[ \t]+configuration section/;

/**
 * `  argon-authorization  [auth, auth:passwordHashing]`
 *
 * The name may not contain a space, which is what keeps a diagnostic line out of the feature list if
 * somebody ever merges stderr into stdout before handing it here.
 */
const FEATURE_ROW = /^[ \t]{2}([A-Za-z0-9][\w.-]*)(?:[ \t]+\[(.*)\])?[ \t]*$/;

/**
 * Reads `--explain <role>`.
 *
 * ## The trap this is shaped around
 *
 * The features are not the only indented, bracketed list the command prints. `hosts N grain(s):` emits
 * lines of exactly the same shape:
 *
 *     UserChatGrain  [stateless-worker, storage:default]
 *     websockets  [WebSockets]
 *
 * A parser that took every indented bracketed line would report `UserChatGrain` as a feature and
 * `storage:default` as a configuration section, and the generator would then write a `conf.d` full of
 * sections no feature declared — which the server reports as C3 and refuses if you are lucky, and
 * which does nothing at all if you are not. So this walks the output as sections and reads features
 * only after the features header. Do not simplify it into a single line-wise match.
 *
 * ## Where it refuses
 *
 * The command prints its own total of configuration sections. If the block read here does not add up
 * to that total, something in the middle was skipped and configuration written from it would be short
 * a section — so the disagreement is raised rather than returned. A role with no features at all is
 * legal (the command prints no block for one) and comes back with an empty list.
 */
export function parseExplain(stdout: string): RoleDetail {
    let id: string | undefined;
    let declaredSections: number | undefined;
    let inFeatures = false;

    const features: FeatureSummary[] = [];

    for (const line of lines(stdout)) {
        if (isNoise(line)) continue;

        if (id === undefined) {
            const header = EXPLAIN_HEADER.exec(line);

            if (header !== null) {
                id = captured(header, 1);
                continue;
            }
        }

        const reads = READS_COUNT.exec(line);

        if (reads !== null) {
            declaredSections = Number(captured(reads, 1));
            inFeatures = false;
            continue;
        }

        if (FEATURES_HEADER.test(line)) {
            inFeatures = true;
            continue;
        }

        if (!inFeatures) continue;

        const feature = FEATURE_ROW.exec(line);

        if (feature !== null) {
            const sections = feature[2];

            features.push({ name: captured(feature, 1), sections: sections === undefined ? [] : splitList(sections) });
            continue;
        }

        // Anything else inside the block: a blank line is the spacing before the next section and is
        // skipped, an indented line that did not parse is left alone, and anything at column 0 is the
        // next section and ends the block. That last case is where a plain-text log line would cut the
        // block short — which the section count below turns into a failure rather than a short list.
        if (line.trim().length > 0 && !/^[ \t]/.test(line)) inFeatures = false;
    }

    if (id === undefined)
        throw new ArgonCliError(
            "unreadable-output",
            "--explain did not name a role; this is not output this bootstrapper can read",
            stdout,
        );

    const read = features.reduce((total, feature) => total + feature.sections.length, 0);

    if (declaredSections !== undefined && declaredSections !== read)
        throw new ArgonCliError(
            "unreadable-output",
            `--explain ${id} reported ${declaredSections} configuration section(s) but ${read} could be ` +
                "read; configuration generated from this would be missing sections",
            stdout,
        );

    if (declaredSections === undefined && read > 0)
        throw new ArgonCliError(
            "unreadable-output",
            `--explain ${id} listed sections without the total that confirms them; the output's shape has moved`,
            stdout,
        );

    return { id, features };
}

/**
 * Checks one command's answer against the other's.
 *
 * `--roles` prints a feature count per role and `--explain` prints the features themselves. They come
 * from the same field of the same descriptor, so they cannot legitimately disagree — a disagreement
 * means one of the two was read wrong, and the one that matters is the one the generator is about to
 * use. This is what catches a feature block truncated by something in the middle of it.
 */
export function assertDetailMatchesSummary(summary: RoleSummary, detail: RoleDetail): void {
    if (summary.id !== detail.id)
        throw new ArgonCliError(
            "unreadable-output",
            `asked the image about '${summary.id}' and it described '${detail.id}'`,
        );

    if (summary.features !== detail.features.length)
        throw new ArgonCliError(
            "unreadable-output",
            `--roles says '${summary.id}' has ${summary.features} feature(s), --explain listed ` +
                `${detail.features.length}; one of the two was read wrong`,
        );
}

/**
 * Every configuration section a set of roles reads, once each.
 *
 * This is the reason the interrogation happens at all: it is the list of sections the generator is
 * allowed to write, and the server is the only thing that knows it. Sorted, so a diff between two runs
 * shows what changed rather than what moved.
 */
export function sectionsOf(details: readonly RoleDetail[]): string[] {
    const sections = new Set<string>();

    for (const detail of details)
        for (const feature of detail.features) for (const section of feature.sections) sections.add(section);

    return [...sections].sort();
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// Which server is this?
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/**
 * Where a version came from, which matters as much as the version does.
 *
 * There is no `--version` on the server's command line, and that is not an omission to route around:
 * `ArgonClusterArgs.Parse` treats an unrecognised argument as *not a command*, so a process started
 * with an invented flag falls through and **boots a server**. An interrogation that boots a server is
 * a container that never exits and a wizard that hangs behind it. So the version is read from outside
 * the process, off the image, and the three ways that can go are named rather than flattened into one
 * string — "0.4.1" read from a label and "0.4.1" read from whatever the operator typed after the colon
 * are not the same claim.
 */
export type VersionSource =
    /** `org.opencontainers.image.version`, set by the image build. The one to trust. */
    | "image-label"
    /** The tag in the reference. The operator chose it; nothing verified it. */
    | "image-tag"
    /** A digest, a moving tag, or a label the build did not fill in. Identifies no version. */
    | "unknown";

export interface ServerVersion {
    /** The version, or the reference itself when the source is `unknown`. Never empty. */
    readonly value: string;
    readonly source: VersionSource;
    /** The image it was read from, so a report can say what it interrogated. */
    readonly reference: string;
}

/** A range of server versions, upper bound exclusive — `below`, not `max`, so nobody reads it as inclusive. */
export interface VersionRange {
    readonly atLeast: string;
    readonly below: string;
}

/**
 * The server versions this bootstrapper knows how to read.
 *
 * The design made the bootstrapper and the server separate images that version independently, which is
 * right — the panel outlives any one server release — and the cost of it is that the two can skew.
 * What actually couples them is the *shape of the CLI output* parsed above, so this range is a claim
 * about that shape: widen it when a new server release is confirmed to still print it, and narrow it
 * the moment one does not.
 */
export const UNDERSTOOD_SERVER_VERSIONS: VersionRange = { atLeast: "0.4.0", below: "0.5.0" };

export type Pairing =
    | { readonly ok: true; readonly version: ServerVersion }
    | {
          readonly ok: false;
          readonly reason: "too-old" | "too-new" | "unreadable";
          readonly version: ServerVersion;
          /** A sentence for the operator. They chose the image; they can choose another one. */
          readonly detail: string;
      };

/**
 * The numbers from a version string, ignoring everything after them.
 *
 * GitVersion gives the server four components and a suffix — `0.4.1.1763-development+e2ed453` — so
 * this reads the leading three and stops. A `v` prefix is stripped because image tags carry one about
 * half the time.
 */
export function parseVersionNumbers(value: string): [number, number, number] | undefined {
    const match = /^v?(\d+)\.(\d+)(?:\.(\d+))?/.exec(value.trim());

    if (match === null) return undefined;

    return [Number(captured(match, 1)), Number(captured(match, 2)), Number(match[3] ?? 0)];
}

function compareVersions(left: readonly number[], right: readonly number[]): number {
    for (let index = 0; index < 3; index++) {
        const a = left[index] ?? 0;
        const b = right[index] ?? 0;

        if (a !== b) return a - b;
    }

    return 0;
}

/**
 * Whether this bootstrapper understands this server.
 *
 * Returns a verdict instead of throwing, because refusing is a policy and this is a fact. An operator
 * running a nightly should be able to proceed past a warning; an operator whose image is a major ahead
 * should not be able to proceed without seeing one. The wizard owns that difference and needs the
 * verdict to make it.
 *
 * A prerelease is deliberately **not** given semver's ordering, where `0.5.0-rc1` sorts below `0.5.0`.
 * The question here is not which release is newer, it is which output format the binary prints — and an
 * rc of 0.5 prints 0.5's format. Sorting it under 0.5.0 would wave through exactly the build most
 * likely to have changed that format.
 */
export function checkPairing(version: ServerVersion, range: VersionRange = UNDERSTOOD_SERVER_VERSIONS): Pairing {
    const numbers = version.source === "unknown" ? undefined : parseVersionNumbers(version.value);

    if (numbers === undefined)
        return {
            ok: false,
            reason: "unreadable",
            version,
            detail:
                `'${version.reference}' does not say which version of Argon it holds, so this bootstrapper ` +
                "cannot tell whether it understands it. Naming a version instead of a moving tag makes " +
                "this checkable.",
        };

    const atLeast = parseVersionNumbers(range.atLeast);
    const below = parseVersionNumbers(range.below);

    if (atLeast === undefined || below === undefined)
        throw new Error(`the understood version range is not readable: ${range.atLeast}..<${range.below}`);

    if (compareVersions(numbers, atLeast) < 0)
        return {
            ok: false,
            reason: "too-old",
            version,
            detail:
                `server ${version.value} is older than ${range.atLeast}, which is the oldest this ` +
                "bootstrapper knows how to configure.",
        };

    if (compareVersions(numbers, below) >= 0)
        return {
            ok: false,
            reason: "too-new",
            version,
            detail:
                `server ${version.value} is newer than this bootstrapper, which understands versions below ` +
                `${range.below}. Update the bootstrapper image before installing it.`,
        };

    return { ok: true, version };
}

/**
 * The tag out of an image reference, if it has one.
 *
 * Awkward enough to be worth its own function: the colon in `localhost:5000/argon/server` is a port and
 * not a tag, so only the last path segment may be looked at, and `...@sha256:...` is a digest — which
 * pins exactly one image while saying nothing about which version is inside it.
 */
export function parseImageTag(reference: string): string | undefined {
    if (reference.includes("@")) return undefined;

    const segments = reference.split("/");
    const last = segments[segments.length - 1] ?? "";
    const colon = last.lastIndexOf(":");

    if (colon < 0) return undefined;

    const tag = last.slice(colon + 1);

    return tag.length > 0 ? tag : undefined;
}

/** Tags that name a stream rather than a release, and so identify nothing. */
const MOVING_TAGS = new Set(["latest", "main", "master", "edge", "nightly", "development", "dev"]);

/** What docker prints for a template that resolved to nothing. Not a version. */
const NO_VALUE = "<no value>";

/**
 * Turns what an image says about itself into a {@link ServerVersion}.
 *
 * Pure, so the precedence is testable: the label is the build's own statement and wins; the tag is the
 * operator's and is taken only when it names something specific.
 */
export function resolveVersion(reference: string, label: string | undefined): ServerVersion {
    const declared = label?.trim();

    if (declared !== undefined && declared.length > 0 && declared !== NO_VALUE)
        return { value: declared, source: "image-label", reference };

    const tag = parseImageTag(reference);

    if (tag !== undefined && !MOVING_TAGS.has(tag.toLowerCase()))
        return { value: tag, source: "image-tag", reference };

    return { value: reference, source: "unknown", reference };
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// Running the image
// ─────────────────────────────────────────────────────────────────────────────────────────────────

export interface CommandResult {
    readonly stdout: string;
    readonly stderr: string;
    readonly exitCode: number;
}

/**
 * The server image, as the parsers need it: something that answers arguments with text.
 *
 * An interface rather than a class, so the tests are fixture strings rather than containers — and so
 * the panel can later interrogate a *running* instance instead of a fresh one without anything above
 * this line changing.
 */
export interface ServerImage {
    readonly reference: string;

    /** Runs the server's command line and returns what it printed, whatever it exited with. */
    run(args: readonly string[]): Promise<CommandResult>;

    /** What this image says its version is. See {@link VersionSource}. */
    version(): Promise<ServerVersion>;
}

/**
 * How long a command may take before it is assumed not to have been a command.
 *
 * This is a correctness measure, not a courtesy. An argument the server does not recognise is not an
 * error there — it falls through to *starting a server*, which listens and never exits. So a
 * bootstrapper interrogating an image too old to know `--roles` gets a container that runs forever
 * instead of a message, and without this it would wait behind that container forever too. Generous,
 * because these commands do reflect over every assembly on a cold container.
 */
export const DEFAULT_TIMEOUT_MS = 60_000;

/** Docker's own exit codes for "I could not run this", as distinct from what the program returned. */
const RUNNER_EXIT_CODES = new Set([125, 126, 127]);

export interface DockerOptions {
    /** The docker binary. Named so a podman-shaped install can be pointed at without a new code path. */
    readonly docker?: string;

    /**
     * Host directory holding the generated `conf.d`.
     *
     * Without it, `--validate-config` checks the image's own defaults and passes without ever seeing
     * what we wrote — validation present, green and meaningless. Mounted read-only: a check must not be
     * able to alter the thing it is checking.
     */
    readonly configDir?: string;

    /**
     * Host path of the one unscoped document — the file `ARGON_CONFIG_FILE` names.
     *
     * Mounting `conf.d` alone makes validation lie. The generator deliberately keeps every generated
     * secret out of the per-feature files and puts them in this document instead, and a required setting
     * with no value is an Error rather than a warning — so a container that can see only `conf.d` reports
     * a perfectly good configuration as invalid, and does it confidently.
     *
     * The env var and the mount travel together and cannot be separated: the server reports naming a file
     * that is not there as an Error too, so setting one without the other trades a false red for a
     * different false red.
     */
    readonly secretsFile?: string;

    /**
     * Docker network. `none` by default, because none of these three commands needs one, and an image
     * that fell through to booting a server should not be able to reach the operator's database.
     */
    readonly network?: string;

    readonly env?: Readonly<Record<string, string>>;
    readonly timeoutMs?: number;
}

/** Where {@link DockerOptions.configDir} lands inside the container. */
const CONTAINER_CONFIG_DIR = "/conf.d";

/** Where {@link DockerOptions.secretsFile} lands inside the container. */
const CONTAINER_CONFIG_FILE = "/argon.secrets.json";

/**
 * The exact argv used to run the server image.
 *
 * Extracted from the runner so what the container is permitted to see can be asserted without a docker
 * daemon. The mounts here are the whole of it: get one wrong and the server is asked to judge a
 * configuration it cannot fully read, which it does — confidently, and wrongly.
 */
export function dockerCommandFor(
    reference: string,
    options: DockerOptions = {},
    args: readonly string[] = [],
    name = `argon-bootstrap-${randomBytes(4).toString("hex")}`,
): string[] {
    const docker = options.docker ?? "docker";

    const cmd = [docker, "run", "--rm", "--name", name, "--network", options.network ?? "none"];

    if (options.configDir !== undefined) {
        cmd.push("--volume", `${options.configDir}:${CONTAINER_CONFIG_DIR}:ro`);
        cmd.push("--env", `ARGON_CONFIG_DIR=${CONTAINER_CONFIG_DIR}`);
    }

    if (options.secretsFile !== undefined) {
        cmd.push("--volume", `${options.secretsFile}:${CONTAINER_CONFIG_FILE}:ro`);
        cmd.push("--env", `ARGON_CONFIG_FILE=${CONTAINER_CONFIG_FILE}`);
    }

    for (const [key, value] of Object.entries(options.env ?? {})) cmd.push("--env", `${key}=${value}`);

    cmd.push(reference, ...args);

    return cmd;
}

export function dockerImage(reference: string, options: DockerOptions = {}): ServerImage {
    const docker = options.docker ?? "docker";
    const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;

    return {
        reference,

        async run(args) {
            // Named, because `docker run --rm` in the foreground does not stop the container when the
            // client process is killed — it detaches. Killing the CLI on a timeout without this leaves
            // a server running on the operator's machine with their conf.d mounted into it: both the
            // thing the timeout exists to prevent, and invisible.
            const name = `argon-bootstrap-${randomBytes(4).toString("hex")}`;

            const cmd = dockerCommandFor(reference, options, args, name);

            return runProcess(cmd, timeoutMs, () => {
                // Deliberately not awaited and deliberately not checked: the run has already failed and
                // the caller is owed that answer now. Removing a container that is already gone fails
                // harmlessly.
                void runProcess([docker, "rm", "--force", name], timeoutMs).catch(() => undefined);
            });
        },

        async version() {
            return resolveVersion(reference, await readImageLabel(docker, reference, timeoutMs));
        },
    };
}

/**
 * Reads the version label without running anything.
 *
 * `docker image inspect` reads the local image record, so this costs nothing and — unlike every other
 * way of asking a program its version — cannot start a server. An image without the label answers
 * empty, which {@link resolveVersion} then treats as the absence it is rather than as a version called
 * `<no value>`.
 */
async function readImageLabel(docker: string, reference: string, timeoutMs: number): Promise<string | undefined> {
    const format = '{{index .Config.Labels "org.opencontainers.image.version"}}';

    try {
        const result = await runProcess([docker, "image", "inspect", "--format", format, reference], timeoutMs);

        return result.exitCode === 0 ? result.stdout.trim() : undefined;
    } catch {
        // An unreadable label is not a reason to stop. It downgrades the pairing verdict to
        // "unreadable", which the wizard has to be able to show anyway, and an image that is genuinely
        // not there fails at the first real command with a message about that instead.
        return undefined;
    }
}

async function runProcess(
    cmd: readonly string[],
    timeoutMs: number,
    onTimeout?: () => void,
): Promise<CommandResult> {
    let proc;

    try {
        proc = Bun.spawn({ cmd: [...cmd], stdout: "pipe", stderr: "pipe", stdin: "ignore" });
    } catch (cause) {
        throw new ArgonCliError(
            "runner-failed",
            `could not run '${cmd[0] ?? ""}': ${cause instanceof Error ? cause.message : String(cause)}`,
        );
    }

    let timedOut = false;

    const timer = setTimeout(() => {
        timedOut = true;
        proc.kill("SIGKILL");
        onTimeout?.();
    }, timeoutMs);

    try {
        // Both streams are drained concurrently with the wait, and not one after the other: a command
        // that fills the stderr pipe while nobody is reading it blocks forever, and these commands do
        // write to both.
        const [stdout, stderr, exitCode] = await Promise.all([
            new Response(proc.stdout).text(),
            new Response(proc.stderr).text(),
            proc.exited,
        ]);

        if (timedOut)
            throw new ArgonCliError(
                "timeout",
                `'${cmd.join(" ")}' did not answer within ${timeoutMs}ms and was killed`,
                stdout + stderr,
            );

        return { stdout, stderr, exitCode };
    } finally {
        clearTimeout(timer);
    }
}

/** The failures that read the same for every command: docker could not run it, or it said no. */
function demandSuccess(result: CommandResult, description: string): CommandResult {
    if (RUNNER_EXIT_CODES.has(result.exitCode))
        throw new ArgonCliError(
            "runner-failed",
            `docker could not run ${description} (exit ${result.exitCode})`,
            result.stderr || result.stdout,
        );

    if (result.exitCode !== 0)
        throw new ArgonCliError(
            "command-failed",
            `${description} exited ${result.exitCode}`,
            result.stderr || result.stdout,
        );

    return result;
}

/** One interrogation of one image: what it offers, which build it was, and whether we understand it. */
export interface Interrogation {
    readonly capabilities: ServerCapabilities;
    readonly version: ServerVersion;
    readonly pairing: Pairing;
}

/**
 * Asks an image what it is and what it offers.
 *
 * The pairing verdict is carried, not enforced. Throwing here would make "I could not read a version
 * off this tag" fatal, and `:latest` is something operators legitimately run. Refusing is the wizard's
 * call, and {@link Pairing} is what it makes it with.
 */
export async function interrogate(
    image: ServerImage,
    range: VersionRange = UNDERSTOOD_SERVER_VERSIONS,
): Promise<Interrogation> {
    const version = await image.version();
    const result = demandSuccess(await image.run(["--roles"]), `${image.reference} --roles`);

    return {
        capabilities: parseRoles(result.stdout, version.value),
        version,
        pairing: checkPairing(version, range),
    };
}

/**
 * Asks an image about one role.
 *
 * `summary` is optional and is the cross-check from {@link assertDetailMatchesSummary}: pass it
 * whenever you have one, which is any time the roles came from the same interrogation.
 */
export async function explainRole(image: ServerImage, role: string, summary?: RoleSummary): Promise<RoleDetail> {
    const result = demandSuccess(await image.run(["--explain", role]), `${image.reference} --explain ${role}`);
    const detail = parseExplain(result.stdout);

    if (detail.id !== role)
        throw new ArgonCliError(
            "unreadable-output",
            `asked the image about '${role}' and it described '${detail.id}'`,
            result.stdout,
        );

    if (summary !== undefined) assertDetailMatchesSummary(summary, detail);

    return detail;
}

/** The answer to `--validate-config`: an exit code, and text for the operator. */
export interface ValidationOutcome {
    readonly ok: boolean;
    readonly exitCode: number;
    /** Everything the command printed, both streams, in the order an operator would want to read it. */
    readonly output: string;
}

/**
 * Checks a generated configuration against the server that will read it.
 *
 * `ok` comes from the exit code and from nothing else. The command also prints `=> N error(s)` per
 * role, and deriving the verdict from that text would be a second implementation of an aggregation the
 * process already did — one that would disagree the first time another role joins the run. The text is
 * returned for the operator to read, not for us to decide on.
 *
 * A non-zero exit is an outcome here and not an exception, because an invalid configuration is a normal
 * thing for a wizard to find and show. Docker failing to run at all is not, and still throws.
 */
export async function validateConfig(image: ServerImage, role?: string): Promise<ValidationOutcome> {
    const args = role === undefined ? ["--validate-config"] : ["--validate-config", "--role", role];
    const result = await image.run(args);

    if (RUNNER_EXIT_CODES.has(result.exitCode))
        throw new ArgonCliError(
            "runner-failed",
            `docker could not run ${image.reference} --validate-config (exit ${result.exitCode})`,
            result.stderr || result.stdout,
        );

    return {
        ok: result.exitCode === 0,
        exitCode: result.exitCode,
        output: [result.stdout, result.stderr].filter((stream) => stream.trim().length > 0).join("\n"),
    };
}
