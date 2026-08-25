import { appendFile, readFile, stat } from "node:fs/promises";
import { join } from "node:path";
import {
    UNDERSTOOD_SERVER_VERSIONS,
    checkPairing,
    parseVersionNumbers,
    resolveVersion,
    type ServerVersion,
    type VersionRange,
} from "../argon";
import { BOOTSTRAPPER_IMAGE_REPOSITORY, COMPOSE_FILENAME, INFRASTRUCTURE_IMAGES, serverImageFor } from "../compose";
import type { ApplyOutcome } from "../setup";
import type { Redactor } from "./containers";

/**
 * What an upgrade is, decided before `apply()` makes it one.
 *
 * `setup.ts` already generates, validates, writes, pulls and starts, and none of that is repeated here:
 * an upgrade in this installer *is* an apply with a different `serverVersion` answer. What is missing
 * around that apply is the pair of things it cannot supply for itself — a record of what ran before it,
 * and a judgement about whether the change being proposed is one the schema can follow.
 *
 * ## Why there has to be a history at all
 *
 * §9 makes upgrade the operation that costs data, and the case that costs it is a *successful* migration
 * followed by rolling the image back. An install with no record of what it ran cannot offer a rollback
 * target, because it does not know one — and when an operator says "it worked last week and it does not
 * now", a shrug is the only answer available without one. So every apply is appended here with the
 * version it was aiming at and how it ended.
 *
 * Append-only, and that is the file format rather than a convention somebody is asked to respect:
 * {@link HISTORY_FILE} is one JSON object per line, written with `O_APPEND`. A JSON array would need
 * read-modify-write on every record — a rewritten history — and a crash part-way through one of those
 * rewrites loses everything that came before it, which is precisely the material a rollback needs.
 *
 * ## What this cannot know, and says rather than implies
 *
 * Whether a particular release carries a `DropColumn` is not visible from a version number, and version
 * numbers are all this reads. So every judgement below is about the *policy* in §9 — destructive
 * migrations land on majors, and operators step through them — and never about the migrations
 * themselves. §9 is candid that nothing enforces that policy yet ("something has to *hold* to it"), so
 * the warnings say what the policy claims and stop there. A plan that said "this is safe" would be
 * stating a fact this module has no way of establishing, and the operator would believe it.
 *
 * The same sentence read backwards is what {@link Standing} is for. Answering `ok: true` for a change
 * whose ends cannot be placed on a release line is also a fact this module cannot establish — it is the
 * green tick over the one operation §9 says costs data — so those cases are refused, and marked as
 * refusals for want of evidence rather than as findings.
 *
 * ## What it deliberately does not do
 *
 * It does not run anything. `plan` describes; the caller answers `serverVersion` into the wizard in
 * `setup.ts`, calls `apply()`, and then calls {@link Upgrades.record} with the outcome. Splitting it that way is
 * what keeps the record honest: the thing that writes the history is the thing that watched the apply
 * finish, rather than a button that was pressed before it started.
 *
 * ## Ports
 *
 * A {@link HistoryStore} and a {@link Redactor}, and then two optional ones: a {@link Clock} so a test
 * can prove ordering, and an {@link InstalledCheck} for the question the history cannot answer about
 * itself — whether a root with no records in it is an empty machine or merely an undocumented one.
 * Everything else is a pure function over version strings, so the parts worth arguing with — a
 * downgrade across a major, a target this bootstrapper cannot configure, what the rollback target
 * actually is after two failed applies — are provable with no disk, no daemon and no network. The real
 * store is at the bottom.
 */

/* ------------------------------------------------------------------------------------------------
 * The record.
 * ---------------------------------------------------------------------------------------------- */

/**
 * Where the history lives, relative to the install root.
 *
 * A dotfile beside the mint, and `.jsonl` rather than `.json` because the extension is the
 * promise: whatever reads this must read it a line at a time, and whatever writes it must only ever add
 * a line. Outside `conf.d/` for the same reason the mint is — the server scans that directory for
 * `<feature>.json` files and this is not one of those.
 */
export const HISTORY_FILE = ".argon-bootstrap-history.jsonl";

/**
 * 0600, the same as the mint and the credential beside it.
 *
 * It was 0644, argued for as "there is no secret in a version number and a timestamp" — and that
 * enumerates two of the record's three fields. {@link AppliedVersion.note} is the third, it is a
 * sentence lifted out of a failed apply, and the sentences `setup.ts` builds for its two
 * `reason: "image"` failures are raw `docker` output that nothing redacted. {@link UpgradePorts.redact}
 * now makes that a seam rather than a claim, and this mode is what stands behind the seam on the day
 * the redactor is handed a secret it was never told about — `setup.ts` only replaces strings of eight
 * characters or more, so an operator-typed key shorter than that passes through every redactor here.
 *
 * The cost is small and worth naming: an operator whose host account is not the one the panel container
 * writes as needs `sudo` to read this file by hand, exactly as they already do for the mint beside it.
 * Reading it through the panel is what {@link Upgrades.applied} is for. Note that the mode only applies
 * when the file is created; an append to an existing file inherits whatever mode it already has.
 */
const HISTORY_MODE = 0o600;

/**
 * How an apply ended, in the only three shapes that change what the operator should do next.
 *
 * The split that matters is between the two failures, and it is the same one {@link ApplyOutcome} draws
 * with `running`: a failure that started nothing left the instance on the version it was already on,
 * while a failure that created containers moved it somewhere nobody can characterise. Flattening those
 * into one "failed" would make {@link currentVersion} answer with the old version for an install whose
 * containers are running the new one — and the rollback offered from there would be a no-op presented
 * as a fix.
 */
export type UpgradeOutcome =
    /** Every service came up on this version. */
    | "succeeded"
    /** It did not, and nothing was started; the instance is still on whatever it was on. */
    | "failed"
    /** It did not, and containers exist against it. What is running is this version, part-way. */
    | "failed-running";

/**
 * One apply, as it is written down.
 *
 * There is no `from` field, and its absence is deliberate: the line before this one already says what
 * the previous version was, and a stored copy of it is a second statement of the same fact that can
 * disagree with the first. The order of the file is the relationship.
 */
export interface AppliedVersion {
    /** ISO 8601 with an offset, from {@link Clock}. */
    readonly at: string;

    /** Exactly the answer that was applied — a bare version, or the full reference an operator pinned. */
    readonly version: string;

    readonly outcome: UpgradeOutcome;

    /**
     * One sentence for whoever reads this file after an upgrade went wrong.
     *
     * This used to say that everything `setup.ts` puts in an outcome's `problem` has been through
     * `redact`, so a note could be written down exactly as it arrived. That was not true: the two
     * `reason: "image"` failures build their sentence out of raw `docker` output, while the neighbouring
     * path redacts the identical expression — and the difference is one comment in another module away
     * from being a secret in a file on disk. So a note is taken through {@link UpgradePorts.redact} and
     * truncated by {@link Upgrades.record}, and this field holds what came out of that rather than what
     * a caller handed in.
     *
     * A plain `string` here and not a branded one, because the fence belongs on the way *in*: see
     * {@link RecordedVersion}, which is what {@link HistoryStore.append} accepts. What comes back off
     * disk is whatever was written there, and typing it as though this module had guaranteed the text
     * would be a claim about a file somebody may have edited by hand.
     */
    readonly note?: string;
}

/**
 * The mark of a record that came from {@link Upgrades.record}. Erased; see {@link RecordedVersion}.
 *
 * `declare` rather than a real symbol: nothing constructs it, nothing reads it, and it does not reach
 * runtime — a record on disk is the same three or four string fields it always was.
 */
declare const minted: unique symbol;

/**
 * A record that has been through the caller's {@link Redactor}, and the only kind `append` takes.
 *
 * The brand is load-bearing rather than decorative. {@link HistoryStore.append} is exported, and while
 * the redaction lived in {@link Upgrades.record} alone, `historyIn(root).append({… note})` wrote a
 * `docker` failure to disk verbatim — the suite's own round-trip test went that way. A note is the one
 * field of a record that ever holds a secret, and a comment asking the next caller to redact first is
 * exactly what this file refuses to rely on for the append-only property (see {@link HistoryStore}, which
 * has no `write`). So the port takes a value only {@link recordedVersion} can make, and that function
 * applies the redactor itself: the claim is checkable by reading one function rather than every caller.
 *
 * The cost is that a record read back out of one history cannot be appended to another without going
 * through `record` again. That is the right way round — a redactor is not promised to be idempotent, so
 * text should pass through one exactly once, at the moment it is minted.
 */
export type RecordedVersion = AppliedVersion & { readonly [minted]: unknown };

/**
 * An apply's outcome, as this file records it.
 *
 * The type is imported and the module is not — `import type` is erased, so this costs nothing at
 * runtime and buys the one thing worth having: if `running` is ever renamed or dropped from
 * {@link ApplyOutcome}, this stops compiling instead of quietly recording every started failure as one
 * that started nothing.
 */
export function outcomeOf(outcome: ApplyOutcome): UpgradeOutcome {
    if (outcome.ok) return "succeeded";

    return "running" in outcome && outcome.running ? "failed-running" : "failed";
}

/* ------------------------------------------------------------------------------------------------
 * Reading the history back.
 * ---------------------------------------------------------------------------------------------- */

/** Where the last record that is not a clean failure sits, or -1. See {@link UpgradeOutcome}. */
function currentIndex(history: readonly AppliedVersion[]): number {
    for (let index = history.length - 1; index >= 0; index--) {
        const entry = history[index];

        // A `failed` apply never reached `compose up`, so it did not change what is running. Skipping it
        // is what makes "the version this install is on" survive a run of failed attempts.
        if (entry !== undefined && entry.outcome !== "failed") return index;
    }

    return -1;
}

/** The version this install is on: the last apply that got as far as creating containers. */
export function currentVersion(history: readonly AppliedVersion[]): AppliedVersion | undefined {
    const index = currentIndex(history);

    return index < 0 ? undefined : history[index];
}

/**
 * The version before the current one, which is what a rollback is offered against.
 *
 * Only a `succeeded` record qualifies. Going back to a version that itself never came up is not a
 * rollback, it is a second failure with more steps — and the operator would read the offer as a
 * statement that it worked once.
 *
 * It is the *previous* version and not "the last version known good", and after a rollback those differ:
 * roll 0.4.2 back to 0.4.1 and the previous version becomes 0.4.2 again, so the offer points forwards.
 * That is honest rather than clever — {@link UpgradePlan.direction} on the offer reads `upgrade`, which
 * is what going there would be, and the alternative is a panel that hides the only other version it has
 * ever successfully run.
 */
export function previousVersion(history: readonly AppliedVersion[]): AppliedVersion | undefined {
    const index = currentIndex(history);
    const current = index < 0 ? undefined : history[index];

    if (current === undefined) return undefined;

    // The comparison is on the resolved reference and not on the answer text. One apply answered `0.4.1`
    // and the next answered `ghcr.io/argon-chat/orleans:0.4.1` are two spellings of one image, and
    // comparing the strings offered the running image back as somewhere to roll to — a button whose
    // target is what is already running, which is the no-op-presented-as-a-fix this function exists to
    // prevent. `directionOf` de-duplicates on the reference, so comparing text here made the two
    // disagree about what "the same version" is.
    const running = versionOf(current.version).reference;

    for (let earlier = index - 1; earlier >= 0; earlier--) {
        const entry = history[earlier];

        if (entry?.outcome === "succeeded" && versionOf(entry.version).reference !== running) return entry;
    }

    return undefined;
}

const OUTCOMES: readonly UpgradeOutcome[] = ["succeeded", "failed", "failed-running"];

/**
 * One line, or nothing.
 *
 * A record missing any of the three fields that make it a record is dropped, including one whose
 * outcome this build has never heard of — which is a real cost worth naming: an older panel reading a
 * history a newer one wrote would lose those lines and could offer a rollback to the wrong version. The
 * alternative, keeping a record whose outcome cannot be interpreted, is worse in the same direction and
 * harder to see, because it would have to be given a default and every default here is a lie about
 * whether containers were created.
 */
function entryOf(value: unknown): AppliedVersion | undefined {
    if (typeof value !== "object" || value === null) return undefined;

    const row = value as Record<string, unknown>;
    const at = typeof row.at === "string" && row.at.length > 0 ? row.at : undefined;
    const version = typeof row.version === "string" ? row.version.trim() : "";
    const outcome = OUTCOMES.find((known) => known === row.outcome);

    if (at === undefined || version.length === 0 || outcome === undefined) return undefined;

    const note = typeof row.note === "string" && row.note.length > 0 ? row.note : undefined;

    return note === undefined ? { at, version, outcome } : { at, version, outcome, note };
}

/**
 * Every record in a history file, oldest first.
 *
 * A line that will not parse is skipped rather than raised, and this is the one place that tolerance is
 * bought rather than assumed: a process killed mid-append leaves a torn last line, and a reader that
 * threw on it would leave the panel unable to read its own history — and so unable to offer a rollback
 * — until somebody edited the file by hand on a box where the panel is how you get in. Losing the torn
 * line is bounded damage; losing the file is not. Being one object per line is what bounds it.
 */
export function readHistory(contents: string): AppliedVersion[] {
    const entries: AppliedVersion[] = [];

    for (const line of contents.split(/\r?\n/)) {
        const trimmed = line.trim();

        if (trimmed.length === 0) continue;

        let parsed: unknown;

        try {
            parsed = JSON.parse(trimmed);
        } catch {
            continue;
        }

        const entry = entryOf(parsed);

        if (entry !== undefined) entries.push(entry);
    }

    return entries;
}

/* ------------------------------------------------------------------------------------------------
 * Versions. The parsing is `argon.ts`'s; only the ordering is here.
 * ---------------------------------------------------------------------------------------------- */

/**
 * The version answer, read the way the installer reads it.
 *
 * Not a second parser: `serverImageFor` turns the answer into the reference the project would actually
 * run, and `resolveVersion` reads a version back off it with the same precedence the interrogation
 * uses. The label is `undefined` because nothing has been asked yet — this is a *proposed* version, and
 * the image may not even be on the machine. That is exactly why a moving tag comes back
 * `source: "unknown"` here: `:latest` names no version until something has run it.
 *
 * `setup.ts` has `referenceFor`, which is the same function; `compose.ts` is where the repository
 * constant lives, so this reaches for that one. See the report.
 */
export function versionOf(answer: string): ServerVersion {
    return resolveVersion(serverImageFor(answer), undefined);
}

/**
 * The release line a version belongs to — the number that is allowed to break things.
 *
 * For `1.7.3` that is the major, and for `0.4.1` it is the *minor*, because a leading zero means the
 * project has not promised anything about the major yet and the second number is carrying the breaking
 * changes. `UNDERSTOOD_SERVER_VERSIONS` says the same thing from the other side: this bootstrapper's own
 * compatibility window is `0.4.0..<0.5.0`, one minor wide, because 0.5 is where the CLI output it parses
 * is allowed to move.
 *
 * Reading the major literally instead would put every 0.x release on one line, and §9's whole rule —
 * dumps on major upgrades, destructive migrations land on majors — would then apply to nothing at all
 * for as long as Argon stays below 1.0, which is now.
 *
 * `undefined` when the version says nothing, gated on `source` the way `checkPairing` gates it: a value
 * that came from a digest or a moving tag is not a claim about a version, whatever digits it contains.
 */
export function releaseLine(version: ServerVersion): string | undefined {
    const numbers = version.source === "unknown" ? undefined : parseVersionNumbers(version.value);

    if (numbers === undefined) return undefined;

    const [major, minor] = numbers;

    return major === 0 ? `0.${minor}` : String(major);
}

/**
 * Orders two parsed versions.
 *
 * The numbers come from `argon.ts`; only this three-element comparison is local, because `argon.ts`
 * keeps its own comparison private and re-deriving the numbers from the strings is what would produce a
 * second parser that can disagree with the first. Two tuples of numbers cannot disagree with anything.
 */
function compare(left: readonly [number, number, number], right: readonly [number, number, number]): number {
    for (let index = 0; index < 3; index++) {
        const a = left[index] ?? 0;
        const b = right[index] ?? 0;

        if (a !== b) return a - b;
    }

    return 0;
}

function numbersOf(version: ServerVersion): [number, number, number] | undefined {
    return version.source === "unknown" ? undefined : parseVersionNumbers(version.value);
}

export type Direction =
    /** Nothing was applied here before, and nothing is installed here either. See {@link Provenance}. */
    | "install"
    /** Nothing was *recorded* here before, and something is installed anyway. See {@link Provenance}. */
    | "unrecorded"
    /** The same pinned reference again — configuration changed, the image did not. */
    | "reapply"
    | "upgrade"
    | "downgrade"
    /**
     * Which way this goes cannot be read: a digest or a moving tag on one side, or two references that
     * agree on every number this parses and differ in the part it does not.
     */
    | "unknown";

/**
 * Whether a reference names one image rather than a stream of them.
 *
 * A digest is the strong form and a release tag is the weaker one this project relies on; `:latest` is
 * neither, and the distinction exists only for the equality shortcut in {@link directionOf}. `source`
 * carries it already: `resolveVersion` reports `unknown` for exactly the tags that move, and a digest
 * reference is the one shape that says `unknown` while still naming a single immutable image.
 */
function pinsOneImage(version: ServerVersion): boolean {
    return version.reference.includes("@") || version.source !== "unknown";
}

function directionOf(from: ServerVersion | undefined, to: ServerVersion): Direction {
    if (from === undefined) return "install";

    // Checked before the numbers, because two identical references are the same image whether or not
    // anything can read a version out of them. An operator re-applying a digest-pinned install should
    // not be told the direction is unknown when nothing is moving.
    //
    // Gated on the reference actually pinning an image, which it was not: `latest` to `latest` took this
    // shortcut and came back `reapply`, meaning "the image did not move", for the one answer whose jump
    // is unbounded — re-pulling a moving tag a month later can land on a different release line
    // entirely. That plan also lost its migrations warning, because the warning is gated on `reapply`.
    // One side is enough to ask: the two references are the same string by the time this is reached.
    if (from.reference === to.reference && pinsOneImage(to)) return "reapply";

    const before = numbersOf(from);
    const after = numbersOf(to);

    if (before === undefined || after === undefined) return "unknown";

    const order = compare(before, after);

    if (order !== 0) return order < 0 ? "upgrade" : "downgrade";

    // Equal numbers and a different reference, which is this project's own tag scheme rather than a
    // corner case: GitVersion stamps four components and `parseVersionNumbers` reads three, so `0.4.1`
    // and `0.4.1.1763-development+e2ed453` compare equal here. Calling that a reapply planned "nothing
    // moves" for an apply that pulls a different image and runs whatever migrations it carries — and
    // planned the reverse, a genuine rollback between two builds of one version, the same way. What
    // separates them is the component this does not read, so the honest answer is that it cannot tell.
    return "unknown";
}

/** Whether the two versions sit on different release lines. Three-valued because it is often unknown. */
export type MajorCrossing = "yes" | "no" | "unknown";

function crossingOf(from: ServerVersion | undefined, to: ServerVersion): MajorCrossing {
    if (from === undefined) return "no";

    const before = releaseLine(from);
    const after = releaseLine(to);

    // Not `false`. A boolean here would make "nobody could tell" indistinguishable from "checked, and it
    // does not cross" — and it is the first of those that wants a dump taken.
    if (before === undefined || after === undefined) return "unknown";

    return before === after ? "no" : "yes";
}

/**
 * What is underneath when the history has no record of anything.
 *
 * The two are not the same machine and the difference is a data-loss case: one that has never run Argon
 * has nothing to lose, and one that has been running it since before this file existed has a database
 * whose schema no plan here can see. A missing history file looks identical in both — and reading it as
 * the first of them is what a review caught, with a path that is not hypothetical: no install in
 * existence has this file yet, so the first operator to open this panel on a year-old 0.5.x install
 * would have been shown "install", "nothing to change from", no dump wanted and no warnings, applied
 * 0.4.7 over it, and met a 42703 with no dump to go back to. So the caller answers instead, from
 * evidence in the directory this module already owns — {@link instanceIn}.
 *
 * It sits up here beside the versions rather than down with the plan because it is an input to the
 * direction: {@link directionFor} is where the two meet, and {@link judge} needs the result as much as
 * {@link planFor} does.
 */
export type Provenance =
    /** The root holds no instance. This really is the first apply here. */
    | "first-install"
    /** Something is installed here and nothing wrote down what. The safe answer when nobody can say. */
    | "unrecorded";

/**
 * The direction, with what is underneath an unwritten-down root taken into account.
 *
 * {@link directionOf} sees two versions and answers `install` for anything with no `from`, because two
 * versions are all it sees; whether that is a first install or an install nobody wrote down is a fact
 * about the disk, and it arrives from the caller. Both {@link judge} and {@link planFor} need the
 * combined answer, and they take it from one function rather than each doing the step — two copies is
 * how a plan that reads `unrecorded` ends up carrying a judgement that decided `install`.
 */
function directionFor(from: ServerVersion | undefined, to: ServerVersion, underneath: Provenance): Direction {
    const recorded = directionOf(from, to);

    return recorded === "install" && underneath === "unrecorded" ? "unrecorded" : recorded;
}

/**
 * The crossing, with the same accounting, and for the same reason it cannot be done twice.
 *
 * `crossingOf` answers "no" for anything with no `from`, which is true of a first install — there is
 * nothing to cross — and false of an install nobody wrote down, where the line it is on is exactly what
 * cannot be established. "no" means "checked, and it does not", so it is not what that install gets to
 * say, and {@link judge} refuses on this value.
 */
function crossingFor(from: ServerVersion | undefined, to: ServerVersion, direction: Direction): MajorCrossing {
    return direction === "unrecorded" ? "unknown" : crossingOf(from, to);
}

/* ------------------------------------------------------------------------------------------------
 * The judgement.
 * ---------------------------------------------------------------------------------------------- */

export type Refusal =
    /** `checkPairing` says this bootstrapper cannot configure that server, in either direction. */
    | "unsupported"
    /** Backwards over a release line. §9's destructive migrations live exactly on that boundary. */
    | "across-major-downgrade"
    /** One end of the change names no version, so the comparison above cannot be made at all. */
    | "unreadable-direction"
    /** Nothing was written down here and the root is not empty, so there is no near end to compare. */
    | "unrecorded-install";

/**
 * Whether a refusal is an answer or the absence of one.
 *
 * `settled` was computed from something this module read: the pairing window, or two release lines it
 * could place. Nothing an operator knows changes it, and a panel should have no button.
 *
 * `unproven` is a refusal for want of evidence — nothing here can rule the data-loss case in *or* out,
 * so it says no, which is the direction that fails safe. An operator who holds the evidence this module
 * lacks may still be right, so the expected surface is a confirmation rather than a missing button, and
 * every `problem` for one of these ends by naming what would establish it.
 *
 * A field rather than a lookup from {@link Refusal}, and the objection to that is real and lives in this
 * file already: {@link AppliedVersion} has no `from` because two statements of one fact can disagree.
 * What buys it here is the other direction. A fifth refusal added later would have to fall to one side
 * of a hardcoded list in a caller, silently — and the side an `else` lands on is the side that shows the
 * button. A field does not compile until somebody chooses.
 */
export type Standing = "settled" | "unproven";

export type Judgement =
    | { readonly ok: true }
    | {
          readonly ok: false;
          readonly reason: Refusal;
          readonly problem: string;
          readonly standing: Standing;
      };

/**
 * Whether a version change may be attempted, and the sentence to show when it may not.
 *
 * Everything below is about §9's policy over release lines. It is still true that this module has not
 * read a migration and cannot say whether a particular release carries a `DropColumn`, and no judgement
 * here claims otherwise — {@link UpgradePlan.warnings} carries that sentence on every plan that moves an
 * image.
 *
 * ## Two refusals that were checked, and two that could not be
 *
 * `unsupported` and `across-major-downgrade` are computed from versions this module placed on release
 * lines. The other two are the same refusal arriving where the comparison cannot be made at all, and
 * {@link Standing} is what keeps them distinguishable from the first pair.
 *
 * That distinction replaces an earlier answer, and the earlier one was wrong in the way that costs data.
 * When the install was pinned to a moving tag — `latest`, `development`, both of which the wizard
 * accepts — `from` names no version, so the across-line comparison is unmakeable, and this used to
 * answer `{ok: true}` with a warning beside it. The reasoning was that refusing would lock the only
 * button an operator has on the day their `latest` install stopped working. The input that broke it was
 * `rollback()` out of a `latest` install to 0.4.9: the exact case the refusal exists for, offered with a
 * green tick, because `ok: true` is what a button reads and a warning is not. A refusal that cannot fire
 * is not a lenient policy, it is an absent one, and nothing in the answer tells the operator which they
 * are looking at. So an unreadable change is refused, and `standing: "unproven"` carries what the
 * warning used to: this is a confirmation to put in front of an operator, not a button to take away.
 *
 * A `reapply` is exempt, and provably rather than charitably — the same reference on both sides, pinning
 * one image. Without that carve-out a digest-pinned install could never be re-applied to change its
 * configuration, because a digest names no line either.
 *
 * What would remove the guess is evidence this module has no port for on purpose: the version *label*
 * off the running container, which `resolveVersion` already prefers over a tag. That lives behind the
 * docker socket, `containers.ts` holds it, and the seam to add is a caller that passes a `from` it read
 * from the running image — not a socket down here.
 */
export function judge(
    from: ServerVersion | undefined,
    to: ServerVersion,
    range: VersionRange = UNDERSTOOD_SERVER_VERSIONS,
    underneath: Provenance = "unrecorded",
): Judgement {
    const pairing = checkPairing(to, range);

    // `too-old` and `too-new` are facts about this bootstrapper rather than about the operator's
    // intentions: it cannot write configuration that server will accept, so the apply would fail at
    // validation after pulling an image. Better to say so before the pull.
    if (!pairing.ok && pairing.reason !== "unreadable")
        return { ok: false, reason: "unsupported", problem: pairing.detail, standing: "settled" };

    const direction = directionFor(from, to, underneath);
    const crossing = crossingFor(from, to, direction);

    if (direction === "downgrade" && crossing === "yes" && from !== undefined)
        return {
            ok: false,
            reason: "across-major-downgrade",
            standing: "settled",
            problem:
                `going from ${from.value} back to ${to.value} crosses a release line, and that is the one ` +
                "direction the database cannot follow. §9 puts the destructive migrations on majors, so the " +
                `${releaseLine(from)} line may have dropped a column ${releaseLine(to)} still selects — and the ` +
                "old build gets a 42703 on the first query that touches that table, not at startup. The dump " +
                "taken before the upgrade is what undoes this; a different image is not.",
        };

    // Before the two unreadable cases, because it is the one thing that can be established about a
    // reference nothing can read a version out of: the same image again moves nothing, so there is no
    // change here for the comparison below to fail to make.
    if (direction === "reapply") return { ok: true };

    if (direction === "unrecorded")
        return {
            ok: false,
            reason: "unrecorded-install",
            standing: "unproven",
            problem:
                "nothing here has a record of what was applied to this install, and the install root is not " +
                `empty — so what is running now cannot be named, and ${to.value} cannot be shown to be on its ` +
                "release line or a later one. If what is underneath is newer, this is the across-line " +
                "downgrade §9 says the database cannot follow, and it lands as a 42703 on the first query " +
                "that touches a dropped column rather than at startup. A root with containers on it and no " +
                "history is not a root with nothing to lose, which is why this is a refusal and not a " +
                "warning. Take the dump; what lifts it is establishing what is running — the " +
                "`org.opencontainers.image.version` label off the server container, written down here.",
        };

    // Everything below compares two ends and a first install has one: `underneath` said the root holds no
    // instance, so there is no schema here to be older than the image. The other reading of an empty
    // history was refused two lines up.
    if (from === undefined) return { ok: true };

    if (crossing === "unknown")
        return {
            ok: false,
            reason: "unreadable-direction",
            standing: "unproven",
            problem:
                `${unnamed(from, to)} names no version, so nothing here can say which release line it is on ` +
                "— and the check that matters is a comparison of lines: backwards across one is where §9 " +
                "puts the destructive migrations, and it is the change the database cannot follow. That " +
                "comparison cannot be made for this pair, so the change is refused for want of evidence " +
                "rather than allowed for want of an objection. An unknown direction is not a safe one. What " +
                "settles it is a version at both ends — a release tag rather than a moving one, or the " +
                "`org.opencontainers.image.version` label read off the image that is running.",
        };

    return { ok: true };
}

/** Whichever ends of a change cannot be placed on a release line, quoted for the sentence above. */
function unnamed(from: ServerVersion, to: ServerVersion): string {
    const references = [from, to].filter((side) => releaseLine(side) === undefined).map((side) => `'${side.reference}'`);

    // Never empty where it is called: `crossing` is `unknown` only when `releaseLine` refused one of
    // them. The fallback is here so that a future caller cannot produce a sentence starting with a space.
    return references.length === 0 ? "one end of this change" : references.join(" and ");
}

/* ------------------------------------------------------------------------------------------------
 * The plan.
 * ---------------------------------------------------------------------------------------------- */

export interface ImageChange {
    /** Which of the two images this project tags with the server's version. */
    readonly what: "server" | "panel";

    readonly from: string;
    readonly to: string;

    /** What runs it, and what replacing it costs. The operator is about to authorise this. */
    readonly why: string;
}

export interface UpgradePlan {
    /** What is running now. Absent on an install with no history. */
    readonly from?: ServerVersion;
    readonly to: ServerVersion;

    readonly direction: Direction;
    readonly majorCrossing: MajorCrossing;

    /** Whether §9 wants a dump taken first. See {@link planFor} for when this is true and why. */
    readonly backupFirst: boolean;

    /**
     * Whether this may be attempted, and when it may not, whether anything could change that.
     *
     * `ok: false` is not always the end of it: two of the four refusals are `unproven` rather than
     * `settled`, meaning nothing here could establish the dangerous case either way. See
     * {@link Standing}, and read `ok` as "nothing objected" rather than as "this was checked and is
     * fine" — the sentence that says what was *not* checked is the migrations warning below.
     */
    readonly judgement: Judgement;

    /** The images whose tag moves. Empty when nothing moves, which is itself worth showing. */
    readonly images: readonly ImageChange[];

    /**
     * The images that do not move, named.
     *
     * Half of "what will this do" is what it will not do, and an operator about to upgrade Argon
     * reasonably wonders whether their Postgres is about to be replaced under a live database. It is
     * not: `compose.ts` pins the infrastructure images to the *bootstrapper's* release, not the
     * server's, so a server upgrade leaves every one of them exactly where it is.
     */
    readonly pinned: readonly string[];

    /**
     * True, unwelcome, and not a refusal.
     *
     * What this module cannot determine is *not* all here: the two undeterminable cases that can cost
     * data — an install with no record, a change with an end that names no version — are refusals, and
     * their sentences live on {@link Judgement.problem}. What stays here is what is true whatever the
     * judgement says, headed by the one that has to appear on every plan that moves an image: nothing
     * here has read the migrations.
     */
    readonly warnings: readonly string[];
}

/**
 * Whether §9 wants a dump before this, in the four states that want one and the one that does not.
 *
 * The carve-out is first because it used to be missing: a `reapply` provably moves no image — the same
 * reference on both sides, pinning one image — and demanding a dump for it produced a plan that said
 * `images: []` and "take a dump" in the same breath. A tax on an operation that changes nothing is how
 * the habit of taking dumps gets worn away, which is the same argument that keeps same-line upgrades
 * free of one.
 */
function wantsDump(direction: Direction, crossing: MajorCrossing): boolean {
    if (direction === "reapply") return false;

    if (crossing !== "no") return true;

    // `downgrade` is the case the dump does not fix but does survive. `unknown` reaches here as the pair
    // this project's own tag scheme produces — `0.4.1` against `0.4.1.1763-development+e2ed453`, two
    // builds on one line, ordered by the component `parseVersionNumbers` does not read — and not knowing
    // which way that goes is the state that most wants a dump.
    //
    // `unrecorded` used to be a third term here and could never be reached: {@link crossingFor} forces
    // its crossing to `unknown`, so the line above has already returned. A branch that cannot run is a
    // protection nobody has, which is worse than not having written it.
    return direction === "downgrade" || direction === "unknown";
}

/** A bare version answer, or nothing when the operator pinned a whole reference. */
function bareVersion(answer: string): string | undefined {
    const trimmed = answer.trim();

    return trimmed.includes("/") || trimmed.includes("@") ? undefined : trimmed;
}

/**
 * What an apply of `to` would change, given what was applied last.
 *
 * `from` and `to` are the raw answers — whatever went into `Answers.serverVersion` — rather than
 * resolved versions, because that is what the history holds and what the wizard collects.
 *
 * When a dump is wanted, and the four cases are not the same case:
 *
 *  - **Crossing a release line**, which is §9's own rule, and the reason it is a rule is that this is
 *    where a `DropColumn` is allowed to land.
 *  - **Not being able to tell**, because a version that cannot be read cannot be shown not to cross one.
 *  - **Any downgrade**, including one inside a line — not because the dump makes the downgrade work,
 *    since the dump that does that was taken before the upgrade, but because a downgrade that wedges
 *    leaves an operator with nothing at all to go back to.
 *  - **Not knowing what is underneath**, which is {@link Provenance}: no record of an apply, and an
 *    install root that is not empty.
 *
 * A same-line upgrade wants no dump, and that is §9 deciding rather than this being lax: a dump before
 * every patch release is a tax nobody pays attention to, and one nobody pays attention to is one that
 * is not there on the day it is needed. A reapply wants none either, for the stronger reason that it
 * moves no image at all.
 *
 * `underneath` is only read when `from` is absent, and it defaults to `unrecorded` rather than to
 * `first-install` so that the pessimistic plan is the one a caller gets for free. Being wrong that way
 * costs a refusal an operator has to confirm past on a machine that was empty; being wrong the other way
 * is the 42703. It is handed to {@link judge} as well as read here, because "there are containers here
 * and no history" is a fact about what can be compared, and comparing is all the judgement does.
 */
export function planFor(
    from: string | undefined,
    to: string,
    range: VersionRange = UNDERSTOOD_SERVER_VERSIONS,
    underneath: Provenance = "unrecorded",
): UpgradePlan {
    const before = from === undefined ? undefined : versionOf(from);
    const after = versionOf(to);

    // Both from the shared helpers, so the plan and the judgement it carries cannot disagree about which
    // change is being described — {@link judge} derives the same two from the same inputs.
    const direction = directionFor(before, after, underneath);
    const majorCrossing = crossingFor(before, after, direction);
    const judgement = judge(before, after, range, underneath);

    const warnings: string[] = [];
    const pairing = checkPairing(after, range);

    if (!pairing.ok && pairing.reason === "unreadable") warnings.push(pairing.detail);

    // The two cases nobody can read — an install with no record, and a change with an unreadable end —
    // used to be warnings here, in the plan that answered `judgement.ok: true` for them. They are
    // refusals now, and the sentences moved into {@link Judgement.problem} with them rather than being
    // said twice in two voices: a warning beside a refusal that says the same thing reads as a second,
    // milder finding. See {@link Standing} for what an operator is expected to be able to do about them.

    if (before !== undefined && majorCrossing === "yes")
        warnings.push(
            `this crosses a release line, from ${releaseLine(before)} to ${releaseLine(after)}. §9 is ` +
                "explicit that this is where the destructive migrations land, which is why it is also where the " +
                "dumps are taken.",
        );

    if (direction === "downgrade" && majorCrossing === "no")
        warnings.push(
            "this goes backwards inside one release line, which §9's policy says should survive — EF selects " +
                "named columns, so a column the old build never heard of does not disturb it. Nothing here has " +
                "read the migrations between these two versions, and §9 says out loud that no check enforces " +
                "the policy yet, so a patch release that happened to drop a column would break this silently.",
        );

    if (direction !== "install" && direction !== "reapply")
        warnings.push(
            "whether this particular release carries a destructive migration is not something this can see. It " +
                "reads version numbers; it has not read the migrations. Everything above is what the policy " +
                "claims, not what the release was checked to contain.",
        );

    return {
        ...(before === undefined ? {} : { from: before }),
        to: after,
        direction,
        majorCrossing,
        backupFirst: wantsDump(direction, majorCrossing),
        judgement,
        images: imageChanges(from, to, before, after, warnings),
        pinned: direction === "install" ? [] : [...Object.values(INFRASTRUCTURE_IMAGES)],
        warnings,
    };
}

/**
 * The two images an Argon version change moves, and the second one is the surprise.
 *
 * `compose.ts` tags the panel's own image with the *server's* version, on the reasoning that a panel
 * running ahead of the server it manages is a combination nobody tested. But `setup.ts` excludes the
 * panel from `compose up`, because it is the container issuing the command — so an upgrade writes a
 * document naming a new panel image and then does not start it. The panel goes on running the old one
 * until something recreates it, and that something kills the process doing the recreating.
 *
 * That is worth a sentence in the plan rather than a surprise afterwards, and it is why this returns the
 * panel's change even though the apply will not perform it.
 */
function imageChanges(
    fromAnswer: string | undefined,
    toAnswer: string,
    from: ServerVersion | undefined,
    to: ServerVersion,
    warnings: string[],
): ImageChange[] {
    // Reference equality rather than `direction === "reapply"`, which is what it used to be: two
    // GitVersion builds of one version compare equal on the three numbers `directionOf` reads, so the
    // direction said "reapply" and this list said "no image moves" for an apply that replaces the server
    // image and runs its migrations. What this list promises is that no *reference* changes, so that is
    // what it asks. The other side of the same test: a moving tag against itself yields no row either,
    // because there is no reference to name — `direction` and `backupFirst` are where that plan says the
    // image behind the tag may have moved anyway.
    if (from === undefined || fromAnswer === undefined || from.reference === to.reference) return [];

    const changes: ImageChange[] = [
        {
            what: "server",
            from: from.reference,
            to: to.reference,
            why: "every role container runs this one image; `--role` is what makes them different. Replacing it is what runs the migrations, under the lease §9 describes.",
        },
    ];

    const beforeTag = bareVersion(fromAnswer);
    const afterTag = bareVersion(toAnswer);

    // The same condition `compose.ts` refuses on: a server pinned to a whole reference gives it no tag to
    // derive the panel's image from, and the caller has to name one. Which one they named is not visible
    // from here, so this says it cannot tell rather than guessing at a reference.
    if (beforeTag === undefined || afterTag === undefined) {
        warnings.push(
            "the server is pinned to a full reference rather than a version, so the panel's own image is " +
                "whatever the install named explicitly — this cannot say whether it moves with the server.",
        );

        return changes;
    }

    changes.push({
        what: "panel",
        from: `${BOOTSTRAPPER_IMAGE_REPOSITORY}:${beforeTag}`,
        to: `${BOOTSTRAPPER_IMAGE_REPOSITORY}:${afterTag}`,
        why: "the panel is tagged with the server's version, and the apply deliberately does not start it — it is the container running the apply. Until it is recreated by hand, the panel stays a version behind the server it manages, and recreating it ends this process.",
    });

    return changes;
}

/* ------------------------------------------------------------------------------------------------
 * The machine.
 * ---------------------------------------------------------------------------------------------- */

/**
 * The history, as the two operations anything needs from it.
 *
 * A port for the reason `setup.ts` has three: the decisions worth testing are about what the records
 * mean — which version is current after a failure that started containers, which is the rollback target
 * after a rollback — and a test that needs a real install root to reach them is a test that gets skipped
 * the first time CI has no writable disk. There is no `write`, and that is the interface holding the
 * append-only property rather than a comment asking for it.
 */
export interface HistoryStore {
    /** Every record, oldest first. Empty when nothing has been applied here yet. */
    read(): Promise<readonly AppliedVersion[]>;

    /**
     * Adds one record after the last. Never rewrites what is there.
     *
     * {@link RecordedVersion} rather than {@link AppliedVersion}, and that is the redaction seam rather
     * than a comment asking for one: this port is exported, so anything that can be spelled here is
     * something a caller can write to disk without {@link Upgrades.record} ever seeing it.
     */
    append(entry: RecordedVersion): Promise<void>;
}

/** Where `at` comes from. Overridable so a test can prove ordering; nothing in production passes it. */
export type Clock = () => Date;

/**
 * Whether an instance already exists under this install root.
 *
 * A port rather than a `stat` written into {@link Upgrades}, for the reason {@link HistoryStore} is one:
 * the decision worth testing is what an empty history *means*, and a test that needs a real install root
 * to reach it is the test that gets skipped the first time CI has no writable disk. {@link instanceIn}
 * is the real one, and a caller holding the docker socket can pass better evidence than it has.
 */
export type InstalledCheck = () => Promise<boolean>;

export interface UpgradePorts {
    readonly history: HistoryStore;

    /**
     * What to take out of a note before it is written down.
     *
     * Required rather than optional, and `containers.ts`'s `LogPorts.redact` is the precedent: this
     * module holds no secrets and could not decide what to strip if it wanted to, so the awkwardness of
     * passing an identity function is the moment somebody notices they never chose. It replaces a
     * comment on {@link AppliedVersion.note} claiming `setup.ts` had already redacted everything it
     * hands over — which was false for the two `reason: "image"` failures, and a comment in another
     * module is not a thing this one can hold to.
     */
    readonly redact: Redactor;

    readonly now?: Clock;

    /**
     * The server versions this bootstrapper understands. Defaults to `UNDERSTOOD_SERVER_VERSIONS`.
     *
     * Overridable for the reason `SetupPorts.readiness` is: a test proving that a target outside the
     * window is refused should not have to be rewritten every time that window moves, because the test
     * that gets rewritten on every release is the test that eventually gets deleted instead.
     */
    readonly range?: VersionRange;

    /**
     * Whether this root already holds an instance. Asked only when the history has nothing in it.
     *
     * Absent means `unrecorded` rather than "first install": a caller that has not wired this gets the
     * pessimistic plan — a dump wanted, and a warning saying nothing here knows what is running — which
     * is wrong in the harmless direction on a machine that is genuinely empty, and right on the one that
     * has been running Argon since before this file existed. See {@link Provenance}.
     */
    readonly installed?: InstalledCheck;
}

/**
 * The panel's upgrade decisions, over one install's history.
 *
 * It holds no state of its own: every answer is read from the store at the moment it is asked for. Two
 * browser tabs looking at the same panel therefore agree, and a record appended by one is visible to the
 * other — which a cached history would not be, and the case where that matters is the one where somebody
 * is watching an upgrade from a second tab because the first has stopped responding.
 */
export class Upgrades {
    readonly #history: HistoryStore;
    readonly #redact: Redactor;
    readonly #now: Clock;
    readonly #range: VersionRange;
    readonly #installed: InstalledCheck | undefined;

    constructor(ports: UpgradePorts) {
        this.#history = ports.history;
        this.#redact = ports.redact;
        this.#now = ports.now ?? (() => new Date());
        this.#range = ports.range ?? UNDERSTOOD_SERVER_VERSIONS;
        this.#installed = ports.installed;
    }

    /** Everything ever applied here, oldest first. */
    async applied(): Promise<readonly AppliedVersion[]> {
        return await this.#history.read();
    }

    /**
     * Writes down one apply, and hands back what was written.
     *
     * Called after the apply has finished rather than before it starts, because the outcome is half of
     * the record and a record written in advance would have to be corrected — which is the rewrite this
     * file format exists to make impossible.
     */
    async record(version: string, outcome: UpgradeOutcome, note?: string): Promise<AppliedVersion> {
        const entry = recordedVersion(this.#redact, this.#now().toISOString(), version, outcome, note);

        await this.#history.append(entry);

        return entry;
    }

    /** What applying `version` would do to this install, judged against what is running now. */
    async plan(version: string): Promise<UpgradePlan> {
        const current = currentVersion(await this.#history.read())?.version;

        if (current !== undefined) return planFor(current, version, this.#range);

        // An empty history is not an empty machine. Reading it as one produced a first-install plan — no
        // dump, no warnings, and nothing refused — for every install that predates this file, which today
        // is all of them. Asking is the whole of the fix, and {@link Provenance} is what is being asked:
        // `unrecorded` is what makes the answer a refusal rather than a plan with a caveat on it.
        return planFor(undefined, version, this.#range, await this.#underneath());
    }

    /** What is under an install root that recorded nothing. `unrecorded` whenever nobody can say. */
    async #underneath(): Promise<Provenance> {
        if (this.#installed === undefined) return "unrecorded";

        return (await this.#installed()) ? "unrecorded" : "first-install";
    }

    /**
     * The previous version, planned as though the operator had answered it.
     *
     * `undefined` when there is nothing to go back to — a first install, or an install where every apply
     * before this one failed. The plan that comes back may well be a refusal, in either of the two ways
     * {@link Standing} separates: a rollback across a release line is exactly the case §9 says the
     * database cannot follow, and a rollback out of an install pinned to a moving tag is that same case
     * with nothing able to recognise it. Offering either without the judgement attached is offering the
     * operator the data loss as a button, which is what this used to do for the second one.
     */
    async rollback(): Promise<UpgradePlan | undefined> {
        const history = await this.#history.read();
        const target = previousVersion(history);

        if (target === undefined) return undefined;

        return planFor(currentVersion(history)?.version, target.version, this.#range);
    }
}

/**
 * How much of a note is kept: one sentence for a person, not a container's whole stdout.
 *
 * `describeCliFailure` in `setup.ts` builds its sentence out of `${cause.message}: ${cause.output}`, and
 * nothing bounds the output half. The history is append-only and read in full on every plan and every
 * rollback, so one failed apply with a megabyte attached is paid for on every call after it, for the
 * life of the install.
 */
const LONGEST_NOTE = 500;

/** A note as it goes to disk: through the caller's redactor, trimmed, bounded, or absent. */
function noteOf(redact: Redactor, note: string | undefined): string | undefined {
    if (note === undefined) return undefined;

    const said = redact(note).trim();

    if (said.length === 0) return undefined;

    // The marker is part of the record rather than a silent cut: somebody reading this file after an
    // upgrade went wrong needs to know that the sentence stops before the failure does.
    return said.length <= LONGEST_NOTE ? said : `${said.slice(0, LONGEST_NOTE)}… (truncated)`;
}

/**
 * The only producer of a {@link RecordedVersion}, and so the only way into {@link HistoryStore.append}.
 *
 * It takes the raw note and the redactor rather than an already-redacted string, which is the whole
 * point: a signature that accepted a redacted note would be one more place to forget, and the brand
 * would then vouch for something nothing had checked. The single cast is on the line below the call to
 * {@link noteOf}, so whether the claim holds is a three-line read.
 */
function recordedVersion(
    redact: Redactor,
    at: string,
    version: string,
    outcome: UpgradeOutcome,
    note: string | undefined,
): RecordedVersion {
    const said = noteOf(redact, note);
    const entry: AppliedVersion =
        said === undefined
            ? { at, version: version.trim(), outcome }
            : { at, version: version.trim(), outcome, note: said };

    return entry as RecordedVersion;
}

/* ------------------------------------------------------------------------------------------------
 * The real world.
 * ---------------------------------------------------------------------------------------------- */

/**
 * The history in an install root, as a file that is only ever appended to.
 *
 * One record is one `appendFile` of one line, which is one `write` against a descriptor opened
 * `O_APPEND` — so two panels recording at the same moment interleave whole lines rather than halves of
 * two. That is the property `.jsonl` buys, and the reason the alternative shapes were not used: a JSON
 * array needs the whole file read, edited and written back, and the window in which that file is
 * truncated is a window in which every previous version an operator could roll back to is gone.
 *
 * A missing file is the ordinary case — every install starts without one — so it reads as empty. Any
 * other failure rejects, following `credential.ts`: a history that exists and cannot be read is a broken
 * install, and treating it as absent would offer a first-install plan for a machine that has been
 * running for a year.
 */
export function historyIn(root: string): HistoryStore {
    const path = join(root, HISTORY_FILE);

    return {
        async read(): Promise<readonly AppliedVersion[]> {
            try {
                return readHistory(await readFile(path, "utf8"));
            } catch (cause) {
                if ((cause as NodeJS.ErrnoException).code === "ENOENT") return [];

                throw cause;
            }
        },

        async append(entry: RecordedVersion): Promise<void> {
            // One line, one call, and the newline belongs to this write rather than to the next one.
            // Building up several and flushing them together would reintroduce the window this format
            // exists to close; leaving the newline off would concatenate two records onto one line, and
            // the whole file after that point stops parsing.
            await appendFile(path, `${JSON.stringify(entry)}\n`, { mode: HISTORY_MODE });
        },
    };
}

/**
 * Evidence that this root already holds an install, for the case where the history does not.
 *
 * The compose document rather than the mint or a running container: `setup.ts` writes it on every apply
 * that got as far as writing anything, it sits in the directory this module already owns, and reading it
 * needs no docker socket — which keeps this module's ports to a file and a clock. Better evidence
 * exists behind the socket, and a caller holding one can pass its own {@link InstalledCheck}.
 *
 * Anything other than "it is not there" answers `true`. A permission error on the compose document is
 * not evidence of an empty machine, and being wrong in that direction costs a refusal the operator has
 * to confirm past — the other direction is what {@link Provenance} exists to stop. That sentence went
 * untested long enough for `return false` to be a mutation nothing caught, so it is pinned now with the
 * one failure this suite can raise on every machine it runs on: a path `stat` refuses outright.
 */
export function instanceIn(root: string): InstalledCheck {
    const path = join(root, COMPOSE_FILENAME);

    return async () => {
        try {
            await stat(path);

            return true;
        } catch (cause) {
            return (cause as NodeJS.ErrnoException).code !== "ENOENT";
        }
    };
}
