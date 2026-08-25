import { describe, expect, test } from "bun:test";
import { mkdir, mkdtemp, readFile, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { UNDERSTOOD_SERVER_VERSIONS } from "../argon";
import { COMPOSE_FILENAME, INFRASTRUCTURE_IMAGES } from "../compose";
import type { ApplyOutcome } from "../setup";
import {
    HISTORY_FILE,
    Upgrades,
    currentVersion,
    historyIn,
    instanceIn,
    judge,
    outcomeOf,
    planFor,
    previousVersion,
    readHistory,
    releaseLine,
    versionOf,
    type AppliedVersion,
    type HistoryStore,
    type Judgement,
    type UpgradeOutcome,
} from "./upgrade";

/* ------------------------------------------------------------------------------------------------
 * Deciding what an upgrade is.
 *
 * Nothing here touches a disk. The history is a list of strings that the fake store parses with the
 * same `readHistory` the real file goes through, so the fake cannot accept a record the file would
 * reject — which is the failure a hand-rolled fake would hide.
 * ---------------------------------------------------------------------------------------------- */

/** Wide enough that the pairing check never fires, so the policy rules below are what is being read. */
const ANY_VERSION = { atLeast: "0.1.0", below: "9.0.0" } as const;

/**
 * Stands in for `setup.ts`'s `redact`, which knows the mint and every credential the operator typed.
 * This one knows a single fixture secret, which is all that is needed to prove a note goes through it
 * rather than around it. Nothing here passes an identity function, deliberately: the port is required
 * so that a caller has to choose, and a test that shrugged would be the first place that stopped being
 * true.
 */
const SECRET = "sup3rsecret-database-password";

const redact = (text: string) => text.split(SECRET).join("<redacted>");

interface Recording extends HistoryStore {
    /** Exactly what was written, in order. Asserting on this is how append-only is checked. */
    readonly lines: string[];
}

function memory(...entries: readonly AppliedVersion[]): Recording {
    const lines = entries.map((entry) => JSON.stringify(entry));

    return {
        lines,
        async read() {
            return readHistory(lines.join("\n"));
        },
        async append(entry) {
            lines.push(JSON.stringify(entry));
        },
    };
}

function applied(version: string, outcome: UpgradeOutcome, at = "2026-08-25T00:00:00.000Z"): AppliedVersion {
    return { at, version, outcome };
}

/**
 * The refusal on a judgement, or nothing when there is none.
 *
 * Narrowing once and handing back the record, rather than spelling `judgement.ok === false &&
 * judgement.reason` at every assertion: with four refusals and a {@link Standing} on each, those
 * expressions are where a test stops reading as the sentence it is checking.
 */
function refusal(judgement: Judgement | undefined) {
    return judgement === undefined || judgement.ok ? undefined : judgement;
}

describe("what an apply's outcome means for the record", () => {
    const panel = { url: "https://chat.example.org/panel", note: "the panel moved" };

    test("a success is a success", () => {
        const outcome: ApplyOutcome = { ok: true, written: [], services: [], panel };

        expect(outcomeOf(outcome)).toBe("succeeded");
    });

    /**
     * The distinction the whole history rests on. A failure that started nothing left the instance where
     * it was; a failure that created containers moved it. Collapse the two and `currentVersion` answers
     * with the old version for a machine whose containers are running the new one, and the rollback
     * offered from there does nothing while claiming to fix something.
     */
    test("a failure that created containers is not the same record as one that did not", () => {
        const created: ApplyOutcome = {
            ok: false,
            reason: "start-failed",
            problem: "compose refused",
            output: "",
            running: true,
            services: [],
            panel,
        };
        const nothing: ApplyOutcome = { ...created, running: false };

        expect(outcomeOf(created)).toBe("failed-running");
        expect(outcomeOf(nothing)).toBe("failed");
    });

    test("a project that came up and never became ready is running", () => {
        const outcome: ApplyOutcome = {
            ok: false,
            reason: "not-ready",
            problem: "argon-core is 'restarting'",
            running: true,
            services: [],
            panel,
        };

        expect(outcomeOf(outcome)).toBe("failed-running");
    });

    test.each([
        [{ ok: false, reason: "invalid", reports: [] } as ApplyOutcome],
        [{ ok: false, reason: "write-failed", problem: "read-only filesystem" } as ApplyOutcome],
        [{ ok: false, reason: "blocked", problem: "the mint cannot be read" } as ApplyOutcome],
    ])("a refusal from before the start never started anything (%o)", (outcome) => {
        expect(outcomeOf(outcome)).toBe("failed");
    });
});

describe("reading the file back", () => {
    test("what was written comes back in order", () => {
        const contents = [
            JSON.stringify(applied("0.4.1", "succeeded")),
            JSON.stringify(applied("0.4.2", "failed")),
        ].join("\n");

        expect(readHistory(contents).map((entry) => entry.version)).toEqual(["0.4.1", "0.4.2"]);
    });

    /**
     * The reason the format is one object per line. A process killed mid-append leaves the last line
     * half-written; a reader that raised on it would leave the panel unable to say what it had ever run,
     * on a box where the panel is how the operator gets in.
     */
    test("a torn last line costs that line and nothing before it", () => {
        const contents = `${JSON.stringify(applied("0.4.1", "succeeded"))}\n{"at":"2026-08-25T00:00`;

        expect(readHistory(contents).map((entry) => entry.version)).toEqual(["0.4.1"]);
    });

    test("a record missing a field is not half a record", () => {
        const contents = [
            '{"at":"2026-08-25T00:00:00.000Z","version":"0.4.1"}',
            '{"at":"2026-08-25T00:00:00.000Z","outcome":"succeeded"}',
            '{"version":"0.4.3","outcome":"succeeded"}',
            '{"at":"2026-08-25T00:00:00.000Z","version":"0.4.4","outcome":"whatever-comes-next"}',
            JSON.stringify(applied("0.4.5", "succeeded")),
        ].join("\n");

        expect(readHistory(contents).map((entry) => entry.version)).toEqual(["0.4.5"]);
    });

    /**
     * The record itself rather than a count of them, under a name that no longer claims what this cannot
     * check. Neither the `\r?\n` split nor the blank-line guard is falsifiable from here, and that is a
     * property of JSON rather than of this fixture: `JSON.parse` skips whitespace around a value, so a
     * line left holding a trailing `\r` parses anyway, and an empty line raises inside the `try` and is
     * skipped there instead. Both mutations were run; both leave the suite green. What this does pin is
     * the outcome — a file that came off a machine with CRLF endings reads back as exactly its one
     * record, nothing padded onto its fields and no second entry conjured out of the blank lines.
     */
    test("a file written on another machine reads back as exactly its records", () => {
        const contents = `\r\n${JSON.stringify(applied("0.4.1", "succeeded"))}\r\n\r\n`;

        expect(readHistory(contents)).toEqual([applied("0.4.1", "succeeded")]);
    });

    test("a note survives and an empty one is not kept", () => {
        const contents = [
            '{"at":"2026-08-25T00:00:00.000Z","version":"0.4.1","outcome":"failed","note":"the registry said no"}',
            '{"at":"2026-08-25T00:00:00.000Z","version":"0.4.2","outcome":"failed","note":""}',
        ].join("\n");

        const [first, second] = readHistory(contents);

        expect(first?.note).toBe("the registry said no");
        expect(second?.note).toBeUndefined();
    });
});

describe("the file in an install root", () => {
    async function directory(): Promise<string> {
        return await mkdtemp(join(tmpdir(), "argon-upgrade-"));
    }

    /**
     * Records reach the disk the way the panel puts them there, which is the only way left: handing
     * `append` an object literal is what `RecordedVersion` now refuses to compile. It used to be how
     * every test in here wrote, and the round trip below is why that mattered — the one path in this
     * file that touched a real disk was also the one path that went around the redactor.
     */
    function writer(root: string, at: string): Upgrades {
        return new Upgrades({ history: historyIn(root), redact, now: () => new Date(at) });
    }

    /**
     * The line framing, which the fake in this file cannot test because it holds records as an array and
     * supplies the newline itself. Drop the trailing newline from the real `append` and the second
     * record lands on the first one's line: JSON.parse gives up on the pair, `readHistory` returns
     * nothing, and the panel loses the entire history it exists to keep — silently, and for good, since
     * the file is only ever appended to.
     */
    test("two records are two lines, in the order they were appended", async () => {
        const root = await directory();

        await writer(root, "2026-08-25T09:00:00.000Z").record("0.4.1", "succeeded");
        await writer(root, "2026-08-25T10:00:00.000Z").record("0.4.2", "failed-running");

        const lines = (await readFile(join(root, HISTORY_FILE), "utf8")).split("\n").filter((line) => line !== "");

        expect(lines).toHaveLength(2);
        expect((await historyIn(root).read()).map((entry) => entry.version)).toEqual(["0.4.1", "0.4.2"]);
    });

    /**
     * The round trip with a secret in it, which is the shape this test should always have had. It used
     * to build the record by hand and hand it to `append` — the bypass itself: the redaction lived on
     * `Upgrades.record`, this path did not go through it, and a `docker` failure carrying a password
     * landed in a file on disk verbatim. The assertion on the file rather than on the parsed record is
     * the point; what is being checked is the bytes.
     */
    test("a note survives the round trip to disk and the secret in it does not", async () => {
        const root = await directory();

        await writer(root, "2026-08-25T09:00:00.000Z").record("0.4.2", "failed", `the registry said no: ${SECRET}`);

        expect(await readFile(join(root, HISTORY_FILE), "utf8")).not.toContain(SECRET);
        expect((await historyIn(root).read())[0]?.note).toBe("the registry said no: <redacted>");
    });

    /** Every install starts without one, so this is the ordinary case rather than a failure. */
    test("a missing file is a history with nothing in it", async () => {
        expect(await historyIn(await directory()).read()).toEqual([]);
    });

    /**
     * The other half of that, and the half that matters: a history that is *there* and cannot be read is
     * a broken install, not an empty one. Answering with `[]` would offer a first-install plan for a
     * machine that has been running for a year.
     */
    test("a history that cannot be read is not an empty one", async () => {
        const root = await directory();

        await mkdir(join(root, HISTORY_FILE));

        await expect(historyIn(root).read()).rejects.toThrow();
    });

    /**
     * 0600, like the mint and the credential beside it, because {@link AppliedVersion.note} carries a
     * sentence out of a failed apply and `setup.ts` does not redact all of them. Skipped on Windows,
     * which models a read-only bit and nothing about group or world — the container is Linux and this
     * is the laptop.
     */
    test.skipIf(process.platform === "win32")("only the owner can read it", async () => {
        const root = await directory();

        await writer(root, "2026-08-25T09:00:00.000Z").record("0.4.1", "succeeded");

        expect((await stat(join(root, HISTORY_FILE))).mode & 0o777).toBe(0o600);
    });

    test("a root with a compose document in it is holding an install", async () => {
        const root = await directory();

        await writeFile(join(root, COMPOSE_FILENAME), "name: argon\n");

        expect(await instanceIn(root)()).toBe(true);
    });

    test("a root with nothing in it is not", async () => {
        expect(await instanceIn(await directory())()).toBe(false);
    });

    /**
     * The sentence on `instanceIn` — anything other than "it is not there" answers true — with a test
     * behind it at last. Mutating the catch to `return false` left the whole suite green, so a panel
     * that could not read the compose document would have called the root empty and planned a first
     * install over a running one, which is the case {@link Provenance} exists to prevent.
     *
     * The witness is a path `stat` will not even attempt: every platform this runs on rejects a NUL in a
     * filename, and it rejects it with something that is not ENOENT, which is the only property the
     * branch reads. It stands in for the permission error the sentence names, because that one cannot be
     * produced on the Windows box this was written on — and a test that only runs on the CI machine is
     * how the mode assertion two tests up ended up unverifiable here.
     */
    test("a compose document that cannot be looked at is not an absent one", async () => {
        expect(await instanceIn(join(await directory(), "\0"))()).toBe(true);
    });
});

describe("which version this install is on", () => {
    test("no history is not a version", () => {
        expect(currentVersion([])).toBeUndefined();
        expect(previousVersion([])).toBeUndefined();
    });

    /**
     * Three attempts that never reached `compose up` did not move the instance. If they counted, the
     * panel would plan the next upgrade from a version that has never run here.
     */
    test("a run of clean failures leaves the install where it was", () => {
        const history = [
            applied("0.4.1", "succeeded"),
            applied("0.4.2", "failed"),
            applied("0.4.2", "failed"),
            applied("0.4.3", "failed"),
        ];

        expect(currentVersion(history)?.version).toBe("0.4.1");
        expect(previousVersion(history)).toBeUndefined();
    });

    /** The case rollback exists for: something is running, it is the new version, and it did not work. */
    test("a failure that created containers is where the install is now", () => {
        const history = [applied("0.4.1", "succeeded"), applied("0.4.2", "failed-running")];

        expect(currentVersion(history)?.version).toBe("0.4.2");
        expect(previousVersion(history)?.version).toBe("0.4.1");
    });

    /**
     * The fixture has to put a failed record *between* the current entry and the succeeded one, or the
     * backwards scan stops on the succeeded record before the filter can reject anything: with the
     * failure first, `outcome === "succeeded"` can be relaxed to "anything that is not a clean failure"
     * with the suite still green. Here 0.4.2 never came up, so offering it would be the second failure
     * with more steps that this filter exists to refuse — and the operator would read the offer as a
     * statement that it worked once.
     */
    test("a version that never came up is not offered as somewhere to go back to", () => {
        const history = [
            applied("0.4.1", "succeeded"),
            applied("0.4.2", "failed-running"),
            applied("0.4.3", "failed-running"),
        ];

        expect(previousVersion(history)?.version).toBe("0.4.1");
    });

    /**
     * One image answered two ways — a bare version once, the whole reference the next time. The guard
     * compares the resolved reference for this reason: comparing the answer text let the running image
     * through as a rollback target, which is a button whose effect is nothing and whose label says it
     * fixes something.
     */
    test("the same image answered two ways does not become its own rollback target", () => {
        const history = [applied("0.4.1", "succeeded"), applied("ghcr.io/argon-chat/orleans:0.4.1", "failed-running")];

        expect(previousVersion(history)).toBeUndefined();
    });

    test("re-applying the same version does not become its own rollback target", () => {
        const history = [applied("0.4.2", "succeeded"), applied("0.4.2", "succeeded")];

        expect(currentVersion(history)?.version).toBe("0.4.2");
        expect(previousVersion(history)).toBeUndefined();
    });

    /**
     * After a rollback the previous version is the one that was rolled back *from*, so the offer points
     * forwards. That is deliberate: it is the only other version this install has ever run, and the plan
     * attached to the offer reads `upgrade`, which is what going there would be.
     */
    test("after a rollback the previous version is the one that was left behind", () => {
        const history = [
            applied("0.4.1", "succeeded"),
            applied("0.4.2", "succeeded"),
            applied("0.4.1", "succeeded"),
        ];

        expect(previousVersion(history)?.version).toBe("0.4.2");
    });
});

describe("which number is allowed to break things", () => {
    /**
     * The rule that everything else in this file rests on. Below 1.0 the minor carries the breaking
     * change, which is also what `UNDERSTOOD_SERVER_VERSIONS` says from the other side — its own window
     * is one minor wide. Read the major literally and every 0.x release lands on one line, at which
     * point §9's whole policy applies to nothing at all for as long as Argon stays below 1.0.
     */
    test.each([
        ["0.4.1", "0.4"],
        ["0.4.99", "0.4"],
        ["0.5.0", "0.5"],
        ["1.0.0", "1"],
        ["1.7.3", "1"],
        ["2.0.0", "2"],
    ])("%s belongs to release line %s", (version, line) => {
        expect(releaseLine(versionOf(version))).toBe(line);
    });

    test("0.4.1 and 0.5.0 are one apart, not the same line", () => {
        expect(releaseLine(versionOf("0.4.1"))).not.toBe(releaseLine(versionOf("0.5.0")));
    });

    test("a version with a build suffix is still on its line", () => {
        expect(releaseLine(versionOf("0.4.1.1763-development+e2ed453"))).toBe("0.4");
    });

    /**
     * The last two rows are the ones that exercise the `source === "unknown"` gate rather than
     * `parseVersionNumbers` refusing a string that starts with a letter: delete the gate and the first
     * three still pass, while a registry addressed by an IP parses as release line 10 out of its host.
     */
    test.each([
        ["latest"],
        ["development"],
        ["ghcr.io/argon-chat/orleans@sha256:abcdef"],
        ["10.0.0.5:5000/argon/orleans@sha256:abcdef"],
        ["10.0.0.5:5000/argon/orleans:latest"],
    ])("'%s' names no release line", (answer) => {
        expect(releaseLine(versionOf(answer))).toBeUndefined();
    });

    /**
     * What that mutant costs, at the level an operator sees it: with the gate gone the install sits on
     * release line "10", every version this bootstrapper can offer is an across-major downgrade from it,
     * and the panel refuses that install permanently on a line it invented out of a host address. The
     * honest refusal is the other one — nobody can place this install on a line at all — and it is
     * `unproven`, which is the kind an operator can answer.
     */
    test("a registry addressed by an IP is not refused on a release line read out of its host", () => {
        const plan = planFor("10.0.0.5:5000/argon/orleans@sha256:abcdef", "0.4.2", ANY_VERSION);

        // The same gate in `numbersOf`, which is the half that decides the direction: without it the
        // host's octets order against the target and this reads as a downgrade out of release 10, which
        // is `across-major-downgrade` — settled, permanent, and invented.
        expect(plan.direction).toBe("unknown");
        expect(plan.majorCrossing).toBe("unknown");
        expect(refusal(plan.judgement)?.reason).toBe("unreadable-direction");
        expect(refusal(plan.judgement)?.standing).toBe("unproven");
        expect(refusal(plan.judgement)?.problem).toContain("10.0.0.5:5000/argon/orleans@sha256:abcdef");
    });
});

describe("which way a change goes", () => {
    test.each([
        ["0.4.1", "0.4.2", "upgrade"],
        ["0.4.2", "0.4.1", "downgrade"],
        ["0.4.2", "0.4.2", "reapply"],
        ["0.4.9", "0.5.0", "upgrade"],
        ["0.5.0", "0.4.9", "downgrade"],
        ["1.0.0", "0.9.9", "downgrade"],
        // Two answers that differ as text and agree on every number this parses. Every other reapply row
        // above passes the same string twice, so it is answered by the reference shortcut and never
        // reaches the comparison — which is how "equal numbers means reapply" survived being wrong.
        ["0.4.1", "v0.4.1", "unknown"],
        ["0.4.1", "0.4.1.1763-development+e2ed453", "unknown"],
    ] as const)("%s to %s is a %s", (from, to, direction) => {
        expect(planFor(from, to, ANY_VERSION).direction).toBe(direction);
    });

    test("nothing applied before, on a root with nothing in it, is an install", () => {
        const plan = planFor(undefined, "0.4.1", ANY_VERSION, "first-install");

        expect(plan.direction).toBe("install");
        expect(plan.from).toBeUndefined();
        expect(plan.backupFirst).toBe(false);
    });

    /**
     * The same absent history on a machine that is not empty, which is every install that predates this
     * file. Read as a first install it produced "nothing to change from", no dump and no warnings for an
     * apply that can land a 0.4 build on a 0.5 schema — and the refusal that would stop that has no
     * version to compare against, so the plan has to say so instead.
     */
    test("nothing applied before, on a root that is not empty, is not an install", () => {
        const plan = planFor(undefined, "0.4.7", ANY_VERSION);

        expect(plan.direction).toBe("unrecorded");
        expect(plan.backupFirst).toBe(true);
        expect(refusal(plan.judgement)?.problem).toContain("record of what was applied");

        // Not "no". Nothing here has established which line this install is on, and "no" is the value
        // that says somebody checked — the same distinction the three-valued type was introduced for.
        expect(plan.majorCrossing).toBe("unknown");
    });

    test("a moving tag on either side leaves the direction unreadable", () => {
        expect(planFor("latest", "0.4.2", ANY_VERSION).direction).toBe("unknown");
        expect(planFor("0.4.2", "latest", ANY_VERSION).direction).toBe("unknown");
    });

    /**
     * The shortcut is for a reference that pins an image, and `latest` against itself is the one input
     * where the jump is unbounded: what the tag resolves to a month later can be a different release
     * line. Calling it a re-apply reported "the image did not move" and, because the migrations warning
     * is gated on re-applies, withheld the sentence saying nobody read them.
     */
    test("re-applying a moving tag is not a re-apply", () => {
        const plan = planFor("latest", "latest", ANY_VERSION);

        expect(plan.direction).toBe("unknown");
        expect(plan.backupFirst).toBe(true);
        expect(plan.warnings.join(" ")).toContain("has not read the migrations");

        // And not waved through as a re-apply is: the `reapply` carve-out in `judge` is what lets a
        // digest be re-applied onto itself, and it is gated on the same `pinsOneImage` this is not.
        expect(refusal(plan.judgement)?.reason).toBe("unreadable-direction");

        // And still no image row, because no *reference* moves: a row reading
        // `orleans:latest → orleans:latest` states a change nobody can act on. What moved is behind the
        // tag, and the direction and the dump above are where this plan says so.
        expect(plan.images).toEqual([]);
    });

    /** The same digest twice is the same image, whether or not a version can be read out of it. */
    test("the same pinned reference twice is a re-apply and not an unknown", () => {
        const pinned = "ghcr.io/argon-chat/orleans@sha256:abcdef";
        const plan = planFor(pinned, pinned, ANY_VERSION);

        expect(plan.direction).toBe("reapply");
        expect(plan.images).toEqual([]);

        // Not refused for naming no release line, which every other unreadable change now is: the same
        // reference on both sides pins one image, so there is no change for the comparison to fail on.
        // Without this carve-out a digest-pinned install could never be re-applied to change its
        // configuration — the panel would have refused the one operation that provably moves nothing.
        expect(plan.judgement.ok).toBe(true);

        // And nothing else in the plan may contradict that. It used to say in the same breath that
        // nobody could tell which way this goes and that a dump should be taken first — for an
        // operation it had just established replaces no image at all. The sentence that said it lives
        // on the refusal now, so the assertion that used to look for its absence in `warnings` would
        // pass whatever this function did; what cannot contradict a `reapply` is the dump.
        expect(plan.backupFirst).toBe(false);
    });
});

describe("what is refused", () => {
    /**
     * §9's case that costs data, in the one direction it costs it. A release line is where a `DropColumn`
     * is allowed to land, and the old build meets the new schema and gets a 42703 on the first query
     * rather than at startup — which is why the refusal names the dump instead of suggesting a retry.
     */
    test("going back across a release line is refused, and says why an image cannot fix it", () => {
        const refused = refusal(planFor("0.5.2", "0.4.7", ANY_VERSION).judgement);

        expect(refused?.reason).toBe("across-major-downgrade");
        expect(refused?.problem).toContain("0.5.2");
        expect(refused?.problem).toContain("0.4.7");
        expect(refused?.problem).toContain("42703");
    });

    /**
     * The same change, out of an install that cannot be placed on a release line — which is what `latest`
     * is, and what the wizard hands an operator who takes its default. This is the finding: the refusal
     * above is a comparison of two lines, one of them does not exist here, and the answer used to be
     * `{ok: true}` with a warning beside it. `ok: true` is what a button reads. An unknown direction is
     * not a safe direction, so it is refused — and `unproven` is what says so honestly, because what is
     * missing is evidence rather than permission.
     */
    test("the same change out of a moving-tag install is refused too, for not being readable", () => {
        const refused = refusal(planFor("latest", "0.4.7", ANY_VERSION).judgement);

        expect(refused?.reason).toBe("unreadable-direction");
        expect(refused?.standing).toBe("unproven");
        expect(refused?.problem).toContain("ghcr.io/argon-chat/orleans:latest");
        expect(refused?.problem).toContain("refused for want of evidence");
    });

    /**
     * The two kinds of no, and the field that separates them. A `settled` refusal was computed from two
     * release lines this module placed, and nothing an operator knows moves it. An `unproven` one is the
     * absence of that computation: the operator who can read the version off their running container may
     * be right where this is only careful. Flatten them and a panel either greys out both — leaving a
     * `latest` install with no way forward at all — or offers both, which hands over the 42703.
     */
    test.each([
        ["0.5.2", "0.4.7", "across-major-downgrade", "settled"],
        ["latest", "0.4.7", "unreadable-direction", "unproven"],
        ["0.4.7", "latest", "unreadable-direction", "unproven"],
        ["0.4.1", "ghcr.io/argon-chat/orleans@sha256:abcdef", "unreadable-direction", "unproven"],
    ] as const)("%s to %s is refused as %s, which is %s", (from, to, reason, standing) => {
        const refused = refusal(planFor(from, to, ANY_VERSION).judgement);

        expect(refused?.reason).toBe(reason);
        expect(refused?.standing).toBe(standing);
    });

    /**
     * The narrowing that keeps the unreadable refusal about what it is about. These two answers sit on
     * one release line — both parse to 0.4 — while the direction between them is unreadable, because
     * they differ in the fourth component `parseVersionNumbers` does not read. Refusing on an unreadable
     * *direction* would take out every build-stamp move this project makes, including its own rollbacks;
     * what is refused is an unreadable *crossing*, which is the comparison §9's rule is made of. The dump
     * is still asked for, because not knowing which way this goes is still not knowing.
     */
    test("two builds of one release line are not refused for being unorderable", () => {
        const plan = planFor("0.4.1", "0.4.1.1763-development+e2ed453", ANY_VERSION);

        expect(plan.direction).toBe("unknown");
        expect(plan.majorCrossing).toBe("no");
        expect(plan.judgement.ok).toBe(true);
        expect(plan.backupFirst).toBe(true);
    });

    /**
     * The other end of the same rule, and the reason the check is on the crossing rather than on whether
     * both versions can be read: a root with no instance on it has no schema for an unreadable target to
     * be dangerous *to*. Refusing here would be the panel declining the one apply that cannot lose data,
     * and it would refuse it for the wizard's own default answer.
     */
    test("a first install is not refused for a target nobody can place", () => {
        expect(planFor(undefined, "latest", ANY_VERSION, "first-install").judgement.ok).toBe(true);
    });

    /**
     * And the same absent history on a root that is not empty, which is the state every install in
     * existence is in today. `instanceIn` is evidence enough to know that something is running and not
     * enough to say what — so the panel holds containers it cannot name and a target it can, and a
     * warning about that used to be the whole protection. A warning is not a refusal: this could put a
     * 0.4 build on a 0.5 schema and answer `ok: true` while doing it.
     */
    test("an install nobody wrote down cannot approve a change to it", () => {
        const refused = refusal(planFor(undefined, "0.4.7", ANY_VERSION).judgement);

        expect(refused?.reason).toBe("unrecorded-install");
        expect(refused?.standing).toBe("unproven");
        expect(refused?.problem).toContain("0.4.7");
        expect(refused?.problem).toContain("42703");
    });

    test("going forward across a release line is allowed", () => {
        expect(planFor("0.4.7", "0.5.2", ANY_VERSION).judgement.ok).toBe(true);
    });

    /**
     * Allowed, and said out loud. This is the plan's headline sentence for the case §9 calls dangerous,
     * and nothing asserted it: the whole push could be deleted and the suite stayed green, leaving a
     * plan that asks for a dump and never says why. The two line names are interpolated, so they are
     * checked too — "from undefined to undefined" would otherwise render unnoticed.
     */
    test("crossing a release line is said, and both lines are named", () => {
        const warnings = planFor("0.4.9", "0.5.0", ANY_VERSION).warnings.join(" ");

        expect(warnings).toContain("crosses a release line");
        expect(warnings).toContain("from 0.4 to 0.5");
    });

    /**
     * Inside one line the policy says this should survive, and the warning has to say that it is the
     * policy talking. §9 states plainly that nothing enforces it yet, so a plan that read as reassurance
     * would be reassurance nobody checked.
     */
    test("going back inside one release line is allowed, and does not promise it is safe", () => {
        const plan = planFor("0.4.7", "0.4.2", ANY_VERSION);

        expect(plan.judgement.ok).toBe(true);
        expect(plan.warnings.join(" ")).toContain("no check enforces");
        expect(plan.backupFirst).toBe(true);
    });

    test("a target this bootstrapper cannot configure is refused before anything is pulled", () => {
        const tooNew = refusal(planFor("0.4.1", UNDERSTOOD_SERVER_VERSIONS.below, UNDERSTOOD_SERVER_VERSIONS).judgement);
        const tooOld = refusal(planFor("0.4.1", "0.1.0", UNDERSTOOD_SERVER_VERSIONS).judgement);

        expect(tooNew?.reason).toBe("unsupported");
        expect(tooNew?.problem).toContain(UNDERSTOOD_SERVER_VERSIONS.below);
        expect(tooOld?.reason).toBe("unsupported");

        // Settled, and it is the one refusal where that is a fact about this bootstrapper rather than
        // about the operator's install: no evidence they could produce makes it able to write
        // configuration that server accepts. An operator cannot confirm past this one.
        expect(tooNew?.standing).toBe("settled");
    });

    test("a version inside the window this bootstrapper understands is not refused for being one", () => {
        expect(planFor("0.4.0", UNDERSTOOD_SERVER_VERSIONS.atLeast, UNDERSTOOD_SERVER_VERSIONS).judgement.ok).toBe(true);
    });

    /**
     * `setup.ts` puts `checkPairing`'s `unreadable` verdict into `warningsFor` and installs anyway, so a
     * digest-pinned install can be created — and this used to answer `ok: true` on the reasoning that
     * the panel which created an install must not be the one that cannot change it. `unproven` is where
     * that reasoning lives now: the change is refused, because a digest names no release line and the
     * comparison cannot be made, and the refusal is the kind an operator who knows what they pinned can
     * confirm past. The wizard's own sentence still rides along as a warning, which is what makes the
     * two readable together.
     */
    test("a digest target is refused for want of a version, not as a matter of policy", () => {
        const plan = planFor("0.4.1", "ghcr.io/argon-chat/orleans@sha256:abcdef", ANY_VERSION);

        expect(refusal(plan.judgement)?.standing).toBe("unproven");
        expect(plan.warnings.join(" ")).toContain("does not say which version of Argon it holds");
    });

    /**
     * `judge` and `planFor` must not be able to disagree about which change is in front of them, and the
     * last row is where they could: what is underneath an install root with no history is a fact neither
     * can derive from two version strings, so it is passed in — and a `planFor` that forgot to hand it
     * on would carry a refusal for an empty machine, judged against the pessimistic default while the
     * plan beside it reads `install`.
     */
    test.each([
        ["0.5.2", "0.4.7", "unrecorded"],
        ["0.4.1", "0.4.2", "unrecorded"],
        [undefined, "0.4.7", "unrecorded"],
        [undefined, "0.4.7", "first-install"],
    ] as const)("judge answers what the plan for %s to %s (%s) carries", (from, to, underneath) => {
        const before = from === undefined ? undefined : versionOf(from);

        expect(judge(before, versionOf(to), ANY_VERSION, underneath)).toEqual(
            planFor(from, to, ANY_VERSION, underneath).judgement,
        );
    });
});

describe("when a dump is wanted first", () => {
    test.each([
        ["0.4.1", "0.4.2", false],
        ["0.4.1", "0.4.1", false],
        ["0.4.9", "0.5.0", true],
        ["0.4.7", "0.4.2", true],
        ["0.5.0", "0.4.9", true],
    ])("%s to %s wants a backup first: %s", (from, to, wanted) => {
        expect(planFor(from, to, ANY_VERSION).backupFirst).toBe(wanted);
    });

    /**
     * A version that cannot be read cannot be shown *not* to cross a release line, and "nobody could
     * tell" is the state that most wants a dump. A boolean here would have flattened it into "checked,
     * and it does not".
     */
    test("not being able to tell is treated like crossing one", () => {
        const plan = planFor("latest", "0.5.0", ANY_VERSION);

        expect(plan.majorCrossing).toBe("unknown");
        expect(plan.backupFirst).toBe(true);

        // The sentence that used to be a warning here, now that the case is a refusal: saying it twice
        // in two voices would read as a warning and a separate, milder finding beside it.
        expect(refusal(plan.judgement)?.problem).toContain("which release line it is on");
    });

    test("a same-line upgrade is not taxed with one", () => {
        const plan = planFor("0.4.1", "0.4.9", ANY_VERSION);

        expect(plan.majorCrossing).toBe("no");
        expect(plan.backupFirst).toBe(false);
    });
});

describe("what an upgrade will actually change", () => {
    test("the server image moves, named as the reference that will be pulled", () => {
        const server = planFor("0.4.1", "0.4.2", ANY_VERSION).images.find((change) => change.what === "server");

        expect(server?.from).toBe("ghcr.io/argon-chat/orleans:0.4.1");
        expect(server?.to).toBe("ghcr.io/argon-chat/orleans:0.4.2");
    });

    /**
     * The one an operator would not predict. `compose.ts` tags the panel with the *server's* version, and
     * `setup.ts` excludes the panel from `compose up` because it is the container issuing it — so the
     * document names a new panel image and the apply does not start it. Saying so beforehand is the
     * difference between a known follow-up step and a panel silently a version behind its server.
     */
    test("the panel's own image moves too, and the plan says the apply will not start it", () => {
        const panel = planFor("0.4.1", "0.4.2", ANY_VERSION).images.find((change) => change.what === "panel");

        expect(panel?.from).toBe("ghcr.io/argon-chat/bootstrapper:0.4.1");
        expect(panel?.to).toBe("ghcr.io/argon-chat/bootstrapper:0.4.2");
        expect(panel?.why).toContain("does not start it");
    });

    test("a server pinned to a whole reference gives no panel tag, and that is said rather than guessed", () => {
        const plan = planFor("0.4.1", "ghcr.io/argon-chat/orleans@sha256:abcdef", ANY_VERSION);

        expect(plan.images.map((change) => change.what)).toEqual(["server"]);
        expect(plan.warnings.join(" ")).toContain("cannot say whether it moves");
    });

    test("re-applying the same version changes no image at all", () => {
        expect(planFor("0.4.2", "0.4.2", ANY_VERSION).images).toEqual([]);
    });

    /**
     * This project's own tag scheme, and the reason the empty list is decided by the reference rather
     * than by the direction. GitVersion stamps four components and `parseVersionNumbers` reads three, so
     * these two answers compare equal — while the apply they authorise pulls a different image and runs
     * whatever migrations that build carries.
     */
    test("two builds of one version move the image, and the plan names both", () => {
        const plan = planFor("0.4.1", "0.4.1.1763-development+e2ed453", ANY_VERSION);
        const server = plan.images.find((change) => change.what === "server");

        expect(server?.from).toBe("ghcr.io/argon-chat/orleans:0.4.1");
        expect(server?.to).toBe("ghcr.io/argon-chat/orleans:0.4.1.1763-development+e2ed453");
        expect(plan.warnings.join(" ")).toContain("has not read the migrations");
    });

    /** The same pair the other way round is a rollback between two builds, and it wants the dump. */
    test("going back to an earlier build of one version is not a no-op", () => {
        const plan = planFor("0.4.1.1763-development+e2ed453", "0.4.1.1500-development+aaaaaaa", ANY_VERSION);

        expect(plan.images.map((change) => change.what)).toEqual(["server", "panel"]);
        expect(plan.backupFirst).toBe(true);
    });

    test("an install has nothing to change from", () => {
        expect(planFor(undefined, "0.4.2", ANY_VERSION, "first-install").images).toEqual([]);
    });

    /**
     * Half of "what will this do" is what it will not. The infrastructure images are pinned to the
     * bootstrapper's release rather than the server's, so an operator upgrading Argon is not about to
     * have Postgres replaced under a live database — and that is worth stating, because they would
     * reasonably assume otherwise.
     */
    test("the database and the rest of the infrastructure are named as not moving", () => {
        const plan = planFor("0.4.1", "0.4.2", ANY_VERSION);

        expect(plan.pinned).toContain(INFRASTRUCTURE_IMAGES.database);
        expect(plan.pinned).toContain(INFRASTRUCTURE_IMAGES.edge);
        expect(plan.pinned).not.toContain("ghcr.io/argon-chat/orleans:0.4.2");
    });

    test("an install is not told what it leaves untouched, because it touches everything", () => {
        expect(planFor(undefined, "0.4.2", ANY_VERSION, "first-install").pinned).toEqual([]);
    });
});

describe("what it says it cannot know", () => {
    /**
     * The line this module must never cross. It reads version numbers; it has not read the migrations,
     * and a plan that omitted this would leave the operator believing the judgement above it was a check
     * of the release rather than of the policy.
     */
    test.each([
        ["0.4.1", "0.4.2"],
        ["0.4.9", "0.5.0"],
        ["0.5.0", "0.4.9"],
        ["latest", "0.4.2"],
    ])("a change from %s to %s admits the migrations were not read", (from, to) => {
        expect(planFor(from, to, ANY_VERSION).warnings.join(" ")).toContain("has not read the migrations");
    });

    test.each([[undefined, "0.4.2"], ["0.4.2", "0.4.2"]] as const)(
        "%s to %s changes no image, so it makes no claim about migrations",
        (from, to) => {
            expect(
                planFor(from, to, ANY_VERSION, "first-install").warnings.join(" "),
            ).not.toContain("has not read the migrations");
        },
    );
});

describe("the panel's own view", () => {
    const ports = (store: HistoryStore, at = "2026-08-25T09:00:00.000Z") => ({
        history: store,
        redact,
        now: () => new Date(at),
        range: ANY_VERSION,
    });

    test("a record is added after the ones already there, and none of them changes", async () => {
        const store = memory(applied("0.4.1", "succeeded"));
        const first = store.lines[0];

        await new Upgrades(ports(store)).record("0.4.2", "failed-running", "compose refused");

        expect(store.lines).toHaveLength(2);
        expect(store.lines[0]).toBe(first);
        expect(JSON.parse(store.lines[1] ?? "{}")).toEqual({
            at: "2026-08-25T09:00:00.000Z",
            version: "0.4.2",
            outcome: "failed-running",
            note: "compose refused",
        });
    });

    test("two records keep the order they were made in", async () => {
        const store = memory();
        const upgrades = new Upgrades(ports(store));

        await upgrades.record("0.4.1", "succeeded");
        await upgrades.record("0.4.2", "succeeded");

        expect((await upgrades.applied()).map((entry) => entry.version)).toEqual(["0.4.1", "0.4.2"]);
    });

    test("a blank note is not written down as one", async () => {
        const store = memory();

        await new Upgrades(ports(store)).record("0.4.1", "succeeded", "   ");

        expect(JSON.parse(store.lines[0] ?? "{}")).not.toHaveProperty("note");
    });

    /**
     * The note is the only field of a record that did not come from this module, and the sentence
     * `setup.ts` builds for a failed `docker` run is that container's raw output. It used to be written
     * down on the strength of a comment saying `setup.ts` had already redacted it — which was untrue for
     * two of its failure paths. The port is what replaces the comment, so this proves it is applied
     * rather than merely required.
     */
    test("a note goes through the redactor before it reaches the file", async () => {
        const store = memory();

        await new Upgrades(ports(store)).record("0.4.2", "failed", `pull failed: authenticating with ${SECRET}`);

        expect(store.lines[0]).not.toContain(SECRET);
        expect(store.lines[0]).toContain("<redacted>");
    });

    /**
     * And bounded, because nothing else bounds it: the file is append-only and read in full on every
     * plan and every rollback, so one failed apply with a container's whole stdout attached is paid for
     * on every call after it for the life of the install.
     */
    test("a note is cut to a sentence, and says that it was", async () => {
        const store = memory();

        const entry = await new Upgrades(ports(store)).record("0.4.2", "failed", "x".repeat(20_000));

        expect(entry.note?.length).toBeLessThan(600);
        expect(entry.note).toContain("truncated");
    });

    test("a plan is judged against what the history says is running", async () => {
        const store = memory(applied("0.4.1", "succeeded"), applied("0.5.0", "failed-running"));

        const plan = await new Upgrades(ports(store)).plan("0.4.1");

        expect(plan.from?.value).toBe("0.5.0");
        expect(plan.direction).toBe("downgrade");
        expect(plan.judgement.ok).toBe(false);
    });

    test("an install with no history and nothing in the root plans its first version as an install", async () => {
        const plan = await new Upgrades({ ...ports(memory()), installed: async () => false }).plan("0.4.1");

        expect(plan.direction).toBe("install");
        expect(plan.backupFirst).toBe(false);
    });

    /**
     * The same empty history on a machine that is already running something. Nothing in existence has
     * this file yet, so this is the state every install is in on the day the panel first opens on it —
     * and read as a first install it produced a plan with no dump, no warning and no refusal for an
     * apply that can put a 0.4 build on a 0.5 schema.
     */
    test("an install with no history and a compose document is not a first install", async () => {
        const plan = await new Upgrades({ ...ports(memory()), installed: async () => true }).plan("0.4.7");

        expect(plan.direction).toBe("unrecorded");
        expect(plan.backupFirst).toBe(true);
        expect(refusal(plan.judgement)?.reason).toBe("unrecorded-install");
        expect(refusal(plan.judgement)?.problem).toContain("record of what was applied");
    });

    /** A caller that never wired the check gets the pessimistic reading, which is the point of it. */
    test("a caller that cannot say what is underneath gets the careful answer", async () => {
        const plan = await new Upgrades(ports(memory())).plan("0.4.1");

        expect(plan.direction).toBe("unrecorded");
        expect(refusal(plan.judgement)?.reason).toBe("unrecorded-install");
    });

    test("there is nothing to roll back to before anything has worked", async () => {
        const store = memory(applied("0.4.1", "failed"), applied("0.4.1", "failed-running"));

        expect(await new Upgrades(ports(store)).rollback()).toBeUndefined();
    });

    /** The whole point of the history: the upgrade broke, and the panel knows where to go back to. */
    test("a broken upgrade offers the last version that came up", async () => {
        const store = memory(applied("0.4.1", "succeeded"), applied("0.4.2", "failed-running"));

        const offer = await new Upgrades(ports(store)).rollback();

        expect(offer?.to.value).toBe("0.4.1");
        expect(offer?.from?.value).toBe("0.4.2");
        expect(offer?.direction).toBe("downgrade");
        expect(offer?.judgement.ok).toBe(true);
    });

    /**
     * The offer is still made when it cannot be taken, with the refusal attached. Hiding it would leave
     * an operator hunting for a rollback button; making it without the judgement would hand them the
     * data loss as a button.
     */
    test("a rollback across a release line is offered and refused in the same answer", async () => {
        const store = memory(applied("0.4.9", "succeeded"), applied("0.5.1", "failed-running"));

        const offer = await new Upgrades(ports(store)).rollback();

        expect(offer?.to.value).toBe("0.4.9");
        expect(refusal(offer?.judgement)?.reason).toBe("across-major-downgrade");
        expect(refusal(offer?.judgement)?.standing).toBe("settled");
    });

    /**
     * The offer after an upgrade to one of this project's own build stamps. Both records parse to the
     * same three numbers, so the offer used to come back `reapply` with `images: []` — an offer to swap
     * the server image back that stated it changed no image.
     */
    test("a rollback to a different build of the same version still moves the image", async () => {
        const store = memory(
            applied("0.4.1", "succeeded"),
            applied("0.4.1.1763-development+e2ed453", "failed-running"),
        );

        const offer = await new Upgrades(ports(store)).rollback();

        expect(offer?.direction).toBe("unknown");
        expect(offer?.images.find((change) => change.what === "server")?.to).toBe("ghcr.io/argon-chat/orleans:0.4.1");
        expect(offer?.backupFirst).toBe(true);

        // And offered rather than refused: both builds are on release line 0.4, so the comparison §9's
        // rule is made of can be made and it comes back "same line". This is the offer the unreadable
        // refusal must not swallow — it is the ordinary shape of a rollback in this project.
        expect(offer?.judgement.ok).toBe(true);
    });

    /**
     * The finding, at the level an operator meets it. An install pinned to `latest`, an upgrade that
     * broke, and the rollback button: `previousVersion` offers 0.4.9, which may be a release line behind
     * whatever `latest` resolved to — the one change §9 says the database cannot follow — and nothing
     * here can tell. It used to arrive with `judgement.ok: true`, which is a green tick over the exact
     * data-loss case the refusal exists for.
     *
     * The offer is still made, because hiding it leaves an operator hunting for a button on the day
     * their install stopped working. What changed is that it arrives refused, and `unproven` says the
     * refusal is one they can answer — with the dump the same plan is asking for.
     */
    test("a rollback out of a moving-tag install is offered refused, not approved", async () => {
        const store = memory(applied("0.4.9", "succeeded"), applied("latest", "failed-running"));

        const offer = await new Upgrades(ports(store)).rollback();

        expect(offer?.to.value).toBe("0.4.9");
        expect(refusal(offer?.judgement)?.reason).toBe("unreadable-direction");
        expect(refusal(offer?.judgement)?.standing).toBe("unproven");
        expect(offer?.backupFirst).toBe(true);
    });

    test("with no range given, the window this bootstrapper understands is the one that applies", async () => {
        const store = memory(applied(UNDERSTOOD_SERVER_VERSIONS.atLeast, "succeeded"));

        const plan = await new Upgrades({ history: store, redact }).plan(UNDERSTOOD_SERVER_VERSIONS.below);

        expect(refusal(plan.judgement)?.reason).toBe("unsupported");
    });
});
