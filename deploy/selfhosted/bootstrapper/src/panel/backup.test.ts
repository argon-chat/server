import { describe, expect, test } from "bun:test";
import { mkdtempSync, symlinkSync } from "node:fs";
import { mkdtemp, readFile, stat, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { COMPOSE_FILENAME, COMPOSE_PROJECT, ENV_FILENAME, STORAGE_IDENTITIES } from "../compose";
import { CREDENTIAL_FILE } from "../credential";
import { DEPLOYMENT } from "../generate";
import { MINT_FILE } from "../setup";
import {
    ARCHIVE_EXTENSION,
    ARCHIVE_MODE,
    BACKUP_DIRECTORY,
    BACKUP_FORMAT,
    DATABASE_FILE,
    DUMP_TERMINATOR,
    INSTALL_PREFIX,
    MANIFEST_FILE,
    backupName,
    classify,
    createBackup,
    databaseContainer,
    demultiplex,
    dockerExec,
    dumpCommand,
    dumpDatabase,
    installStore,
    listBackups,
    parseBackupName,
    tar,
    type BackupManifest,
    type BackupPorts,
    type BackupStore,
    type Bytes,
    type ContainerExec,
    type ExecSpec,
    type ExecState,
    type StoredArchive,
    type TarEntry,
} from "./backup";
import type { EngineRequest } from "../docker";

/* ------------------------------------------------------------------------------------------------
 * Backups.
 *
 * Four things can go wrong here and only one of them is loud. A dump can come back framed and be read
 * as if it were not; it can come back truncated and be stored as if it were whole; the archive can be
 * unreadable by `tar`; and the wrong file can be in it. Every one of those is discovered on the day the
 * backup is needed rather than on the day it was taken, so all four are tested against real bytes —
 * real docker frames, a real ustar archive parsed back by an implementation that shares nothing with
 * the writer.
 * ---------------------------------------------------------------------------------------------- */

const encoder = new TextEncoder();
const decoder = new TextDecoder();

function bytes(text: string): Bytes {
    return encoder.encode(text);
}

function joined(...parts: readonly Uint8Array[]): Bytes {
    const whole = new Uint8Array(parts.reduce((total, part) => total + part.length, 0));

    let at = 0;

    for (const part of parts) {
        whole.set(part, at);
        at += part.length;
    }

    return whole;
}

/**
 * One docker stream frame, built the way the daemon builds them.
 *
 * Written out here rather than imported from the module under test, because a test that framed its
 * input with the same code that unframes it would agree with any framing at all, including none.
 */
function frame(stream: number, payload: Uint8Array | string): Bytes {
    const body = typeof payload === "string" ? bytes(payload) : payload;
    const block = new Uint8Array(8 + body.length);

    block[0] = stream;
    new DataView(block.buffer).setUint32(4, body.length);
    block.set(body, 8);

    return block;
}

/**
 * Whether this machine will make a symlink at all.
 *
 * Windows refuses without Developer Mode or an elevated shell, and the tests that want one are about a
 * Linux install root, so they are skipped rather than failed on a laptop that cannot. Probed instead of
 * assumed from the platform, because a Windows box with Developer Mode on runs them exactly as CI does.
 */
const SYMLINKS = ((): boolean => {
    const probe = mkdtempSync(join(tmpdir(), "argon-symlink-"));

    try {
        symlinkSync(join(probe, "nothing"), join(probe, "link"));

        return true;
    } catch {
        return false;
    }
})();

const DUMP_TAIL = `--\n-- ${DUMP_TERMINATOR}\n--\n\n`;

function dumpText(body = 'CREATE TABLE "users" ("id" uuid);\n'): string {
    return `--\n-- PostgreSQL database dump\n--\n\n${body}${DUMP_TAIL}`;
}

/* ------------------------------------------------------------------------------------------------
 * The daemon's framing.
 * ---------------------------------------------------------------------------------------------- */

describe("demultiplexing an exec response", () => {
    /**
     * The failure this whole function exists for. pg_dump writes notices to stderr while it is writing
     * SQL to stdout, and the daemon interleaves them; concatenating the body puts the notice *inside*
     * the SQL, where it restores fine until the moment it does not.
     */
    test("stderr interleaved between stdout chunks stays out of the dump", () => {
        const body = joined(
            frame(1, "CREATE TABLE a;\n"),
            frame(2, "pg_dump: warning: circular foreign-key constraints\n"),
            frame(1, "CREATE TABLE b;\n"),
        );

        const result = demultiplex(body);

        expect(result.ok).toBe(true);

        if (!result.ok) return;

        expect(decoder.decode(result.stdout)).toBe("CREATE TABLE a;\nCREATE TABLE b;\n");
        expect(result.stderr).toBe("pg_dump: warning: circular foreign-key constraints\n");
    });

    /** The daemon frames on buffer boundaries, not on line boundaries. */
    test("a statement split across two frames comes back byte-identical", () => {
        const result = demultiplex(joined(frame(1, "INSERT INTO t VALUES ('half a "), frame(1, "value');\n")));

        expect(result.ok && decoder.decode(result.stdout)).toBe("INSERT INTO t VALUES ('half a value');\n");
    });

    /**
     * Decoding each frame as it arrives corrupts any character the daemon split across the boundary.
     * The messages long enough to be split are the ones that mattered.
     */
    test("a multi-byte character split across two stderr frames survives", () => {
        const ellipsis = encoder.encode("…");
        const body = joined(
            frame(2, ellipsis.subarray(0, 1)),
            frame(2, joined(ellipsis.subarray(1), bytes(" truncated"))),
        );

        const result = demultiplex(body);

        expect(result.ok && result.stderr).toBe("… truncated");
    });

    /** Sizes are a big-endian uint32, so anything past 64 KiB proves the width and the byte order. */
    test("a frame longer than a uint16 is read at its full length", () => {
        const long = "x".repeat(70_000);

        const result = demultiplex(joined(frame(1, long), frame(1, "!")));

        expect(result.ok && result.stdout.length).toBe(70_001);
        expect(result.ok && decoder.decode(result.stdout.subarray(69_998))).toBe("xx!");
    });

    /**
     * Every other fixture here owns its buffer from byte zero, so the offset the sizes are read at was
     * only ever the same number as the offset into the buffer. A caller that hands over a slice of
     * something bigger — which the port's own contract invites, since `start` returns the body "as the
     * daemon sent it" — reads its frame lengths out of whatever was in front of it, which is a dump cut
     * apart at offsets taken from somebody else's bytes.
     */
    test("a body that is a window into a larger buffer is read at its own offset", () => {
        const one = joined(frame(1, "CREATE TABLE a;\n"), frame(2, "a warning\n"));
        const padded = new Uint8Array(16 + one.length);

        padded.set(one, 16);

        const result = demultiplex(padded.subarray(16));

        expect(result.ok && decoder.decode(result.stdout)).toBe("CREATE TABLE a;\n");
        expect(result.ok && result.stderr).toBe("a warning\n");
    });

    test("an empty response is an empty dump rather than an error", () => {
        const result = demultiplex(new Uint8Array(0));

        expect(result.ok && result.stdout.length).toBe(0);
        expect(result.ok && result.stderr).toBe("");
    });

    /**
     * A TTY was attached somewhere and the daemon sent the raw stream. Slicing it as if it were framed
     * would cut the dump apart at offsets read out of its own text, so it is refused instead.
     */
    test("a raw dump is refused rather than parsed", () => {
        const result = demultiplex(bytes(dumpText()));

        expect(result).toEqual({ ok: false, problem: "unframed" });
    });

    /**
     * The three reserved bytes are what separates framed from raw. Without checking them, raw output
     * that happens to begin with 0x01 is read as a frame whose length is the next four bytes of SQL.
     *
     * One case per byte rather than one that perturbs the middle of the three: the argument the module
     * makes is that all three together make the discrimination safe, and checking only `+2` accepts
     * `01 41 00 00 00 00 00 04 'SEL!'` — raw output with a non-zero second byte — as a frame.
     */
    test.each([1, 2, 3])("a stdout identifier with a non-zero reserved byte at +%i is not a frame", (at) => {
        const forged = joined(frame(1, "SELECT 1;\n"));

        forged[at] = 0x41;

        expect(demultiplex(forged)).toEqual({ ok: false, problem: "unframed" });
    });

    /** `0` is stdin, which never appears in a response — so a zero here says the body is not framed. */
    test("a frame claiming to be stdin is not a frame", () => {
        expect(demultiplex(joined(frame(0, "SELECT 1;\n")))).toEqual({ ok: false, problem: "unframed" });
    });

    /**
     * The connection dropped mid-chunk. What arrived is a valid prefix of a dump and nothing else in
     * the response says it is a prefix, which is exactly why the length has to be checked.
     */
    test("a frame declaring more than arrived is truncated, not a short frame", () => {
        const body = joined(frame(1, "CREATE TABLE a;\n"), frame(1, "CREATE TABLE b;\n"));

        expect(demultiplex(body.subarray(0, body.length - 4))).toEqual({ ok: false, problem: "truncated" });
    });

    test("a body ending inside a header is truncated", () => {
        const body = joined(frame(1, "CREATE TABLE a;\n"), frame(1, "b"));

        expect(demultiplex(body.subarray(0, body.length - 5))).toEqual({ ok: false, problem: "truncated" });
    });

    /** A zero-length chunk is legal and means nothing; it must not end the parse early. */
    test("an empty frame is skipped rather than treated as the end", () => {
        const result = demultiplex(joined(frame(1, "a"), frame(1, ""), frame(1, "b")));

        expect(result.ok && decoder.decode(result.stdout)).toBe("ab");
    });
});

/* ------------------------------------------------------------------------------------------------
 * The dump.
 * ---------------------------------------------------------------------------------------------- */

interface FakeExec {
    readonly port: ContainerExec;
    readonly calls: string[];
    spec(): ExecSpec | undefined;
    container(): string | undefined;
}

function fakeExec(body: Bytes, state: ExecState = { running: false, exitCode: 0 }): FakeExec {
    const calls: string[] = [];

    let spec: ExecSpec | undefined;
    let container: string | undefined;

    return {
        calls,
        spec: () => spec,
        container: () => container,
        port: {
            async create(id, given) {
                calls.push("create");
                container = id;
                spec = given;

                return "exec-1";
            },
            async start(exec) {
                calls.push(`start:${exec}`);

                return body;
            },
            async inspect(exec) {
                calls.push(`inspect:${exec}`);

                return state;
            },
        },
    };
}

describe("the pg_dump invocation", () => {
    /**
     * The whole argv, in order, rather than a `toContain` per flag.
     *
     * `arrayContaining` cannot see argv[0] — `pg_dumpall` reads as a pass — and it cannot see an
     * addition either, so appending `--data-only` (a dump with no schema in it) would satisfy it. It
     * also could not see `--quote-all-identifiers` go missing, which is the flag that decides whether
     * this restores into a later major version where a name of ours became a reserved word.
     */
    test("is the exact command a restore two years from now needs", () => {
        expect(dumpCommand()).toEqual([
            "pg_dump",
            `--username=${DEPLOYMENT.database.user}`,
            `--dbname=${DEPLOYMENT.database.name}`,
            "--format=plain",
            "--no-owner",
            "--no-privileges",
            "--clean",
            "--if-exists",
            "--quote-all-identifiers",
        ]);
    });

    test("runs as postgres, so that no password has to be put anywhere", async () => {
        const exec = fakeExec(frame(1, dumpText()));

        await dumpDatabase("abc123", exec.port);

        expect(exec.spec()?.user).toBe("postgres");
        expect(exec.container()).toBe("abc123");
    });
});

describe("taking the dump", () => {
    test("a complete dump comes back with its warnings beside it, not inside it", async () => {
        const sql = dumpText();
        const exec = fakeExec(joined(frame(1, sql), frame(2, "pg_dump: warning: something\n")));

        const outcome = await dumpDatabase("abc", exec.port);

        expect(outcome.ok).toBe(true);

        if (!outcome.ok) return;

        expect(decoder.decode(outcome.sql)).toBe(sql);
        expect(outcome.warnings).toBe("pg_dump: warning: something\n");
    });

    /**
     * The exec is inspected only after its output has been read, because reading the output is what
     * lets it finish. Inspecting first reports `Running` on every dump that has anything to say.
     */
    test("the exit code is read after the output, not before", async () => {
        const exec = fakeExec(frame(1, dumpText()));

        await dumpDatabase("abc", exec.port);

        expect(exec.calls).toEqual(["create", "start:exec-1", "inspect:exec-1"]);
    });

    test("a non-zero exit is a refusal that quotes what pg_dump said", async () => {
        const exec = fakeExec(joined(frame(2, "pg_dump: error: connection to server failed\n")), {
            running: false,
            exitCode: 1,
        });

        const outcome = await dumpDatabase("abc", exec.port);

        expect(outcome.ok).toBe(false);
        expect(!outcome.ok && outcome.reason).toBe("dump-failed");
        expect(!outcome.ok && outcome.detail).toContain("connection to server failed");
    });

    /**
     * The case the exit code cannot see. A connection dropped after the last complete chunk gives a
     * zero exit and a dump that is a valid SQL prefix — it restores most of the way and then stops.
     */
    test("a clean exit with an unterminated dump is still refused", async () => {
        const exec = fakeExec(frame(1, "--\n-- PostgreSQL database dump\n--\n\nCREATE TABLE a;\n"));

        const outcome = await dumpDatabase("abc", exec.port);

        expect(!outcome.ok && outcome.reason).toBe("dump-truncated");
    });

    /**
     * The terminator is looked for at the tail and nowhere else, because it is ordinary text and the
     * dump contains the instance's messages. Somebody who pasted pg_dump's own output into a chat
     * message must not thereby make every truncated backup look complete.
     */
    test("the terminator inside a row does not count as the end of the dump", async () => {
        const forged = `--\n-- PostgreSQL database dump\n--\n\nINSERT INTO m VALUES ('${DUMP_TERMINATOR}');\n${"-".repeat(2000)}\n`;
        const exec = fakeExec(frame(1, forged));

        const outcome = await dumpDatabase("abc", exec.port);

        expect(!outcome.ok && outcome.reason).toBe("dump-truncated");
    });

    test("a truncated frame is refused before the exit code is even consulted", async () => {
        const complete = frame(1, dumpText());
        const exec = fakeExec(complete.subarray(0, complete.length - 3));

        const outcome = await dumpDatabase("abc", exec.port);

        expect(!outcome.ok && outcome.reason).toBe("dump-truncated");
        expect(exec.calls).not.toContain("inspect:exec-1");
    });

    test("an unframed body is named as such, because the fix is elsewhere", async () => {
        const exec = fakeExec(bytes(dumpText()));

        const outcome = await dumpDatabase("abc", exec.port);

        expect(!outcome.ok && outcome.reason).toBe("unframed");
        expect(!outcome.ok && outcome.detail).toContain("TTY");
    });

    test("an exec still running when its output ended is not a dump", async () => {
        const exec = fakeExec(frame(1, dumpText()), { running: true, exitCode: undefined });

        const outcome = await dumpDatabase("abc", exec.port);

        // Named, not merely refused: "still running" and "pg_dump exited undefined" are different things
        // for the operator to do, and without the `reason` this passed on the second one.
        expect(!outcome.ok && outcome.reason).toBe("still-running");
    });

    /**
     * The half of that guard the case above cannot reach. A missing exit code alone already refuses,
     * so the `running` flag was never what decided it — and a daemon answering `Running: true` with an
     * exit code of zero would have been taken for a finished dump.
     */
    test("an exec that says it is running is not finished, whatever exit code came with it", async () => {
        const exec = fakeExec(frame(1, dumpText()), { running: true, exitCode: 0 });

        const outcome = await dumpDatabase("abc", exec.port);

        expect(!outcome.ok && outcome.reason).toBe("still-running");
    });

    /**
     * The cap is the only thing between a large instance and the panel dying with three copies of the
     * dump in it — the exec's buffer, the tar, and the gzip. Reached here with a ceiling of a few bytes
     * rather than by building a gigabyte, which is the whole reason it is a parameter.
     */
    test("a dump past the ceiling is refused rather than archived", async () => {
        const exec = fakeExec(frame(1, dumpText()));

        const outcome = await dumpDatabase("abc", exec.port, 16);

        expect(!outcome.ok && outcome.reason).toBe("too-large");
        expect(!outcome.ok && outcome.detail).toContain("16");
    });

    test("a dump inside the ceiling is not", async () => {
        const exec = fakeExec(frame(1, dumpText()));

        expect((await dumpDatabase("abc", exec.port, dumpText().length)).ok).toBe(true);
    });
});

/* ------------------------------------------------------------------------------------------------
 * Finding the database.
 * ---------------------------------------------------------------------------------------------- */

function fakeEngine(rows: unknown, seen: string[] = []): EngineRequest {
    return async (path) => {
        seen.push(path);

        return rows;
    };
}

describe("finding the database container", () => {
    test("asks for this project's postgres and no other project's", async () => {
        const seen: string[] = [];

        await databaseContainer("someone-elses", fakeEngine([], seen));

        const filters = decodeURIComponent(seen[0] ?? "");

        expect(filters).toContain(`com.docker.compose.project=someone-elses`);
        expect(filters).toContain(`com.docker.compose.service=${DEPLOYMENT.hosts.postgres}`);
    });

    test("a running container is the one to exec into", async () => {
        const rows = [{ Id: "deadbeef", State: "running" }];

        expect(await databaseContainer(COMPOSE_PROJECT, fakeEngine(rows))).toEqual({ found: true, id: "deadbeef" });
    });

    /**
     * The two failures are different sentences to the operator: nothing installed here versus a
     * database that needs starting. Filtering on `status` at the daemon collapses both into an empty
     * list and loses the difference.
     */
    test("a container that exists but is down is stopped, not missing", async () => {
        const rows = [{ Id: "deadbeef", State: "exited" }];

        expect(await databaseContainer(COMPOSE_PROJECT, fakeEngine(rows))).toEqual({
            found: false,
            reason: "stopped",
        });
    });

    test("no container at all is missing", async () => {
        expect(await databaseContainer(COMPOSE_PROJECT, fakeEngine([]))).toEqual({ found: false, reason: "missing" });
    });
});

/* ------------------------------------------------------------------------------------------------
 * What goes in.
 * ---------------------------------------------------------------------------------------------- */

describe("classifying a path in the install root", () => {
    test.each([
        [`${DEPLOYMENT.confD}/api.json`, "configuration"],
        [COMPOSE_FILENAME, "configuration"],
        ["traefik/traefik.yml", "configuration"],
        ["traefik/dynamic.yml", "configuration"],
        // Its first line says the keys arrive through the environment, and they do. Reading "livekit"
        // as "secret" would leave the SFU unconfigured after a restore for nothing.
        ["sfu/livekit.yaml", "configuration"],
        [DEPLOYMENT.secretsFile, "secret"],
        [ENV_FILENAME, "secret"],
        [STORAGE_IDENTITIES, "secret"],
        [MINT_FILE, "secret"],
        [CREDENTIAL_FILE, "excluded"],
        ["bootstrap.code", "excluded"],
        [`${BACKUP_DIRECTORY}/argon-20260101T000000Z.tar.gz`, "excluded"],
        // `setup.ts` stages into these, and a staging directory can hold a half-written secrets file.
        [".staging-a1b2/secrets.json", "excluded"],
        ["notes-to-self.txt", "unknown"],
        ["conf.d", "unknown"],
        ["../etc/shadow", "unknown"],
    ])("%s is %s", (path, expected) => {
        expect(classify(path)).toBe(expected as ReturnType<typeof classify>);
    });

    /**
     * The sweep is by directory, so what an operator puts in one of those directories is what decides
     * whether an archive labelled "no secrets" holds keys. `traefik/acme.json` is the case that will
     * actually happen: it is the canonical name of Traefik's ACME store — the account key and the
     * private key of every certificate — and bind-mounting it into the install root is the ordinary way
     * to get certificates into a backup at all.
     */
    test.each([
        ["traefik/acme.json", "secret"],
        ["traefik/tls.key", "secret"],
        ["traefik/argon.pem", "secret"],
        ["traefik/users.htpasswd", "secret"],
        ["sfu/keys.yaml", "secret"],
        [`${DEPLOYMENT.confD}/${ENV_FILENAME}`, "secret"],
        [`${DEPLOYMENT.confD}/${DEPLOYMENT.secretsFile}`, "secret"],
        // Not everything unusual in there is a key. An unrecognised dotfile — an editor's swap file, a
        // half-written rename — is reported rather than archived and rather than dropped.
        ["traefik/.dynamic.yml.swp", "unknown"],
        // And the ordinary contents still are what they were.
        ["traefik/dynamic.yml", "configuration"],
        ["sfu/livekit.yaml", "configuration"],
    ])("%s is %s", (path, expected) => {
        expect(classify(path)).toBe(expected as ReturnType<typeof classify>);
    });

    /**
     * The near misses, which is where a list of known key names always ends up. Every one of these was
     * `configuration` while `traefik/acme.json` was `secret`: the same store under the name Traefik
     * writes on a case-insensitive filesystem, the same store on the staging endpoint, the same keys
     * under the name an operator chose. The rule that catches them is not a longer list — it is that a
     * file nothing here recognises, in a directory that holds key material, is read as key material.
     */
    test.each([
        ["traefik/ACME.json"],
        ["traefik/Acme.Json"],
        ["traefik/acme-staging.json"],
        ["traefik/certificates.json"],
        ["traefik/tls.crt"],
        ["sfu/keys.json"],
        ["sfu/api-secret.txt"],
    ])("%s is secret, not one letter away from it", (path) => {
        expect(classify(path)).toBe("secret");
    });

    /**
     * The inversion itself, on names with nothing key-shaped about them at all. `traefik/` holds the
     * ACME store and `sfu/` holds LiveKit's keys, so being unrecognised in there is not a reason to
     * treat something as ordinary configuration.
     */
    test.each([["traefik/README.md"], ["traefik/backup-2026.yml"], ["sfu/room-defaults.yaml"]])(
        "%s is secret because nothing here knows what it is",
        (path) => {
            expect(classify(path)).toBe("secret");
        },
    );

    /**
     * And the price of that, paid where it can be paid. `conf.d` is the one swept directory whose
     * contents this file cannot enumerate — `generate.ts` writes one `<feature>.json` per role that owns
     * a section, and the role names come out of the server binary — so the shape stays trusted. A role
     * this bootstrapper has never heard of must still be in a plain backup, or a restore comes up short
     * of the configuration the instance was running.
     */
    test.each([["api"], ["auth"], ["identity"], ["some-future-role"]])(
        "conf.d/%s.json is configuration without being named anywhere in this file",
        (feature) => {
            expect(classify(`${DEPLOYMENT.confD}/${feature}.json`)).toBe("configuration");
        },
    );

    /**
     * That generosity is the one place a key can still hide behind a name, so the word rule guards it —
     * and guards it on words rather than substrings, because `conf.d/monkey.json` is a feature file.
     */
    test("a key-shaped name inside the one generous shape is still a key", () => {
        expect(classify(`${DEPLOYMENT.confD}/certificates.json`)).toBe("secret");
        expect(classify(`${DEPLOYMENT.confD}/private-token.json`)).toBe("secret");
        expect(classify(`${DEPLOYMENT.confD}/monkey.json`)).toBe("configuration");
    });

    /**
     * The rule is about what a directory this sweeps may hold, not about names in general. A stray key
     * at the root is still unrecognised, and archiving it because of its name would be sweeping the
     * install root wholesale by another route.
     */
    test("a key-shaped name outside a swept directory is still unrecognised", () => {
        expect(classify("deploy.key")).toBe("unknown");
        expect(classify("acme.json")).toBe("unknown");
    });

    /** A staging directory can hold a half-written secrets file, and that is why it is excluded. */
    test("a staging directory stays excluded whatever is inside it", () => {
        expect(classify(`.staging-a1b2/${DEPLOYMENT.secretsFile}`)).toBe("excluded");
        expect(classify(".staging-a1b2/tls.key")).toBe("excluded");
    });

    test("a Windows separator does not turn a known path into an unknown one", () => {
        expect(classify("traefik\\traefik.yml")).toBe("configuration");
        expect(classify(`.\\${DEPLOYMENT.secretsFile}`)).toBe("secret");
    });

    /** A `..` anywhere in the path, not only at the front, is a path that leaves the root. */
    test("a path that climbs out part-way through is not configuration", () => {
        expect(classify(`${DEPLOYMENT.confD}/../../etc/shadow`)).toBe("unknown");
    });
});

/* ------------------------------------------------------------------------------------------------
 * Naming.
 * ---------------------------------------------------------------------------------------------- */

describe("naming an archive", () => {
    const at = new Date("2026-08-25T07:46:00.512Z");

    test("a plain backup says nothing extra; one with keys in it says so in the name", () => {
        expect(backupName(at, false)).toBe("argon-20260825T074600Z.tar.gz");
        expect(backupName(at, true)).toBe("argon-20260825T074600Z-with-secrets.tar.gz");
    });

    test("the name round-trips through the parser", () => {
        expect(parseBackupName(backupName(at, true))).toEqual({
            takenAt: "2026-08-25T07:46:00Z",
            containsSecrets: true,
        });

        expect(parseBackupName(backupName(at, false))?.containsSecrets).toBe(false);
    });

    /** Lexicographic order has to be chronological order, or a plain `ls` misleads. */
    test("sorting the names sorts the backups", () => {
        const names = [
            backupName(new Date("2026-01-02T00:00:00Z"), false),
            backupName(new Date("2025-12-31T23:59:59Z"), false),
            backupName(new Date("2026-01-02T00:00:01Z"), true),
        ];

        expect([...names].sort()).toEqual([names[1]!, names[0]!, names[2]!]);
    });

    test.each([
        ["backup.tar.gz"],
        ["argon-20260825T074600Z.tar"],
        ["argon-20260825T074600Z.tar.gz.partial"],
        ["argon-20260825T074600Z-with-keys.tar.gz"],
        // Shaped like a stamp and not a date. A listing sorted on a NaN puts an arbitrary entry first.
        ["argon-20261301T000000Z.tar.gz"],
        ["argon-20260231T000000Z.tar.gz"],
    ])("%s is not a backup", (name) => {
        expect(parseBackupName(name)).toBeUndefined();
    });
});

describe("listing", () => {
    function taken(...archives: readonly StoredArchive[]): BackupStore {
        return {
            list: async () => [],
            read: async () => undefined,
            put: async () => true,
            taken: async () => archives,
        };
    }

    test("newest first, with what each one carries", async () => {
        const store = taken(
            { name: "argon-20260101T000000Z.tar.gz", bytes: 10 },
            { name: "argon-20260303T121212Z-with-secrets.tar.gz", bytes: 20 },
            { name: "argon-20260202T000000Z.tar.gz", bytes: 30 },
        );

        const listed = await listBackups(store);

        expect(listed.map((one) => one.takenAt)).toEqual([
            "2026-03-03T12:12:12Z",
            "2026-02-02T00:00:00Z",
            "2026-01-01T00:00:00Z",
        ]);

        expect(listed.map((one) => one.containsSecrets)).toEqual([true, false, false]);
        expect(listed[0]?.bytes).toBe(20);
    });

    /**
     * A half-finished `scp` and an editor's swap file both land in that directory. Offering them as
     * restorable points is offering something that will be acted on.
     */
    test("anything that is not an archive is not listed as one", async () => {
        const store = taken(
            { name: ".argon-20260101T000000Z.tar.gz.swp", bytes: 1 },
            { name: "argon-20260101T000000Z.tar.gz.partial", bytes: 2 },
            { name: "argon-20260101T000000Z.tar.gz", bytes: 3 },
        );

        expect((await listBackups(store)).map((one) => one.name)).toEqual(["argon-20260101T000000Z.tar.gz"]);
    });
});

/* ------------------------------------------------------------------------------------------------
 * The archive.
 *
 * Parsed back by an implementation that shares no constant and no offset with the writer, because a
 * round trip through the writer's own reader would pass on an archive no `tar` accepts.
 * ---------------------------------------------------------------------------------------------- */

interface Extracted {
    readonly path: string;
    readonly mode: number;
    readonly mtime: number;

    /**
     * The ownership fields, which nothing read until a review pointed out that nothing read them.
     *
     * They are not decoration: `tar -xpf` run as root during a restore chowns each file to the uid the
     * header names, so a uid of 1000 in here hands `install/secrets.json` to whichever local account
     * holds 1000 on the restoring machine. The header checksum cannot catch a wrong-but-consistent
     * value, because it is recomputed from the same bytes it is checking.
     */
    readonly uid: number;
    readonly gid: number;
    readonly uname: string;

    readonly bytes: Uint8Array;
}

function untar(archive: Uint8Array): Extracted[] {
    const field = (block: Uint8Array, from: number, to: number): string =>
        decoder.decode(block.subarray(from, to)).split("\0")[0]!.trim();

    const entries: Extracted[] = [];

    let at = 0;
    let terminated = false;

    while (at + 512 <= archive.length) {
        const block = archive.subarray(at, at + 512);

        if (block.every((byte) => byte === 0)) {
            terminated = true;
            break;
        }

        if (field(block, 257, 263) !== "ustar") throw new Error(`block at ${at} is not a ustar header`);

        const stated = Number.parseInt(field(block, 148, 156), 8);
        const recomputed = block.reduce((sum, byte, index) => sum + (index >= 148 && index < 156 ? 0x20 : byte), 0);

        if (stated !== recomputed) throw new Error(`checksum at ${at}: header says ${stated}, bytes say ${recomputed}`);

        const size = Number.parseInt(field(block, 124, 136), 8);

        at += 512;

        entries.push({
            path: field(block, 0, 100),
            mode: Number.parseInt(field(block, 100, 108), 8),
            uid: Number.parseInt(field(block, 108, 116), 8),
            gid: Number.parseInt(field(block, 116, 124), 8),
            mtime: Number.parseInt(field(block, 136, 148), 8),
            uname: field(block, 265, 297),
            bytes: archive.slice(at, at + size),
        });

        at += Math.ceil(size / 512) * 512;
    }

    if (!terminated) throw new Error("the archive has no end-of-archive marker");

    return entries;
}

describe("the tar writer", () => {
    const at = new Date("2026-08-25T07:46:00Z");

    function entry(path: string, text: string, mode: number): TarEntry {
        return { path, bytes: bytes(text), mode };
    }

    /**
     * Ownership is pinned to root and zero rather than to whatever the panel's process happens to be —
     * both so that two backups of an unchanged instance differ only where the instance differs, and
     * because a restore extracted as root gives every file to the uid written here.
     */
    test("what goes in comes back out, with its mode and owned by nobody in particular", () => {
        const archive = tar(
            [entry("b/manifest.json", "{}\n", 0o600), entry("b/install/conf.d/api.json", '{"a":1}\n', 0o644)],
            at,
        );

        const seconds = Math.floor(at.getTime() / 1000);
        const owner = { uid: 0, gid: 0, uname: "root" };

        expect(untar(archive)).toEqual([
            { path: "b/manifest.json", mode: 0o600, mtime: seconds, ...owner, bytes: bytes("{}\n") },
            { path: "b/install/conf.d/api.json", mode: 0o644, mtime: seconds, ...owner, bytes: bytes('{"a":1}\n') },
        ]);
    });

    test("content of exactly one block is not padded with a second", () => {
        const archive = tar([entry("b/exact", "x".repeat(512), 0o600)], at);

        // One header, one content block, two zero blocks.
        expect(archive.length).toBe(512 * 4);
        expect(untar(archive)[0]?.bytes.length).toBe(512);
    });

    test("content one byte over a block is padded out to the next one", () => {
        const archive = tar([entry("b/spill", "x".repeat(513), 0o600)], at);

        expect(archive.length).toBe(512 * 5);
        expect(untar(archive)[0]?.bytes.length).toBe(513);
    });

    test("bytes that are not text survive unchanged", () => {
        const raw = new Uint8Array(300);

        for (let index = 0; index < raw.length; index += 1) raw[index] = index % 256;

        const extracted = untar(tar([{ path: "b/blob", bytes: raw, mode: 0o600 }], at))[0];

        expect(extracted?.bytes).toEqual(raw);
    });

    /** Truncating the name silently would put a file in the archive under somebody else's path. */
    test("a name too long for the format is refused rather than truncated", () => {
        expect(() => tar([entry(`b/${"n".repeat(120)}`, "x", 0o600)], at)).toThrow(/ustar name/);
    });

    test("an empty archive is still a well-formed one", () => {
        expect(untar(tar([], at))).toEqual([]);
    });
});

/* ------------------------------------------------------------------------------------------------
 * Taking one.
 * ---------------------------------------------------------------------------------------------- */

/** The credential that must never leave the machine, written where the panel really keeps it. */
const PANEL_HASH = "$argon2id$v=19$m=65536,t=2,p=1$THE-PANEL-PASSWORD-HASH";
const SIGNING_KEY = "THE-INSTANCE-SIGNING-KEY";
const DATABASE_PASSWORD = "THE-DATABASE-PASSWORD";

const INSTALL: Readonly<Record<string, { readonly text: string; readonly mode: number }>> = {
    [`${DEPLOYMENT.confD}/api.json`]: { text: '{"Kestrel":{"Port":8080}}\n', mode: 0o644 },
    [`${DEPLOYMENT.confD}/media.json`]: { text: '{"Storage":{"Kind":"s3"}}\n', mode: 0o644 },
    [COMPOSE_FILENAME]: { text: "services:\n  argon-core: {}\n", mode: 0o644 },
    ["traefik/traefik.yml"]: { text: "entryPoints: {}\n", mode: 0o644 },
    [DEPLOYMENT.secretsFile]: { text: `{"Jwt":{"Key":"${SIGNING_KEY}"}}\n`, mode: 0o600 },
    [ENV_FILENAME]: { text: `ARGON_POSTGRES_PASSWORD=${DATABASE_PASSWORD}\n`, mode: 0o600 },
    [STORAGE_IDENTITIES]: { text: '{"identities":[{"secretKey":"S3-SECRET"}]}\n', mode: 0o600 },
    [MINT_FILE]: { text: '{"database":"' + DATABASE_PASSWORD + '"}\n', mode: 0o600 },
    [CREDENTIAL_FILE]: { text: `${PANEL_HASH}\n`, mode: 0o600 },
    ["bootstrap.code"]: { text: "ABCD-2345-EFGH\n", mode: 0o600 },
    ["operator-notes.txt"]: { text: "remember to renew the domain\n", mode: 0o644 },
};

interface Harness {
    readonly ports: BackupPorts;
    readonly written: { name: string; bytes: Bytes; mode: number }[];

    /** Every path asked of the engine, so that which project was looked for is visible. */
    readonly seen: string[];
}

function harness(
    options: {
        readonly files?: Readonly<Record<string, { readonly text: string; readonly mode: number }>>;
        readonly body?: Bytes;
        readonly state?: ExecState;
        readonly rows?: unknown;
        readonly existing?: readonly StoredArchive[];
        readonly at?: Date;
        readonly project?: string;

        /** A store where the name was claimed after `taken()` answered — the second half of a double click. */
        readonly claimed?: boolean;
    } = {},
): Harness {
    const files = options.files ?? INSTALL;
    const written: { name: string; bytes: Bytes; mode: number }[] = [];
    const seen: string[] = [];

    const store: BackupStore = {
        list: async () => Object.keys(files),
        read: async (path) => {
            const file = files[path];

            return file === undefined ? undefined : { bytes: bytes(file.text), mode: file.mode };
        },
        put: async (name, contents, mode) => {
            // Refused the way the real store refuses: nothing written, and the answer says so.
            if (options.claimed === true) return false;

            written.push({ name, bytes: contents, mode });

            return true;
        },
        taken: async () => options.existing ?? [],
    };

    return {
        written,
        seen,
        ports: {
            engine: fakeEngine(options.rows ?? [{ Id: "pg-1", State: "running" }], seen),
            exec: fakeExec(options.body ?? frame(1, dumpText()), options.state ?? { running: false, exitCode: 0 }).port,
            store,
            now: () => options.at ?? new Date("2026-08-25T07:46:00Z"),
            project: options.project,
        },
    };
}

function opened(archive: Bytes): { entries: Extracted[]; manifest: BackupManifest; plain: string } {
    const plainBytes = Bun.gunzipSync(archive);
    const entries = untar(plainBytes);
    const manifest = entries.find((one) => one.path.endsWith(MANIFEST_FILE));

    if (manifest === undefined) throw new Error("the archive has no manifest");

    return {
        entries,
        manifest: JSON.parse(decoder.decode(manifest.bytes)) as BackupManifest,
        plain: decoder.decode(plainBytes),
    };
}

describe("creating a backup", () => {
    test("the archive is a real gzipped tar with the dump and the configuration in it", async () => {
        const { ports, written } = harness();

        const outcome = await createBackup(ports);

        expect(outcome.ok).toBe(true);
        expect(written).toHaveLength(1);

        const { entries, manifest } = opened(written[0]!.bytes);
        const prefix = "argon-20260825T074600Z";

        expect(entries.map((one) => one.path)).toEqual([
            `${prefix}/${MANIFEST_FILE}`,
            `${prefix}/${DATABASE_FILE}`,
            `${prefix}/${INSTALL_PREFIX}/${COMPOSE_FILENAME}`,
            `${prefix}/${INSTALL_PREFIX}/${DEPLOYMENT.confD}/api.json`,
            `${prefix}/${INSTALL_PREFIX}/${DEPLOYMENT.confD}/media.json`,
            `${prefix}/${INSTALL_PREFIX}/traefik/traefik.yml`,
        ]);

        // The same list from the other side. The paths above come out of the tar headers and this one
        // out of the manifest, and the manifest is what somebody reads years from now to decide whether
        // the archive holds what they need — an archive that understates itself is one nobody trusts.
        expect(manifest.contents.configuration).toEqual([
            COMPOSE_FILENAME,
            `${DEPLOYMENT.confD}/api.json`,
            `${DEPLOYMENT.confD}/media.json`,
            "traefik/traefik.yml",
        ]);

        const dump = entries.find((one) => one.path.endsWith(DATABASE_FILE));

        expect(decoder.decode(dump?.bytes)).toBe(dumpText());
    });

    /**
     * The four fields the manifest exists to carry and the summary the panel shows, none of which any
     * assertion looked at. `format` is the one a future reader branches on, so an archive that lies
     * about it is an archive read with the wrong rules; `project` decides which instance was dumped;
     * `databaseWarnings` is where pg_dump's `circular foreign-key constraints` notice — the one that is
     * about the *restore* — ends up, and dropping it drops the only warning anybody gets.
     */
    test("the manifest says what it is, what it came from, and what pg_dump said", async () => {
        const { ports, written, seen } = harness({
            project: "someone-elses",
            body: joined(frame(1, dumpText()), frame(2, "pg_dump: warning: circular foreign-key constraints\n")),
        });

        const outcome = await createBackup(ports);
        const { manifest } = opened(written[0]!.bytes);

        expect(decodeURIComponent(seen[0] ?? "")).toContain("com.docker.compose.project=someone-elses");
        expect(manifest.format).toBe(BACKUP_FORMAT);
        expect(manifest.project).toBe("someone-elses");
        expect(manifest.databaseWarnings).toBe("pg_dump: warning: circular foreign-key constraints\n");
        expect(outcome.ok && outcome.backup.bytes).toBe(written[0]!.bytes.length);
    });

    /**
     * The clock is injected so that a test can pin it, and it lands in four places: the filename, the
     * manifest, the summary and every tar entry's mtime. Only the filename was ever checked, so a
     * manifest stamped `1970` or a tar stamped with the real wall clock passed — and the mtime is what
     * makes two backups of an unchanged instance identical rather than merely similar.
     */
    test("one injected instant reaches the manifest, the summary and the entries", async () => {
        const at = new Date("2026-08-25T07:46:00Z");
        const { ports, written } = harness({ at });

        const outcome = await createBackup(ports);
        const { manifest, entries } = opened(written[0]!.bytes);

        expect(manifest.takenAt).toBe(at.toISOString());
        expect(outcome.ok && outcome.backup.takenAt).toBe(at.toISOString());

        for (const entry of entries) expect(entry.mtime).toBe(Math.floor(at.getTime() / 1000));
    });

    /**
     * The whole point of the module. A backup lands on somebody's laptop; the panel's own password hash
     * must not be on it, at any setting, so this looks in the bytes rather than at the path list.
     */
    test("the panel credential is in no archive, and the manifest says it was left out", async () => {
        for (const includeSecrets of [false, true]) {
            const { ports, written } = harness();

            await createBackup(ports, { includeSecrets });

            const { manifest, plain } = opened(written[0]!.bytes);

            expect(plain).not.toContain(PANEL_HASH);
            expect(plain).not.toContain("ABCD-2345-EFGH");

            // The names are recorded even though the contents are not: a restore has to know what it is
            // going to have to re-establish.
            expect(manifest.contents.excluded).toEqual(["bootstrap.code", CREDENTIAL_FILE]);
        }
    });

    /** Off by default, because the usual next thing done with the file is to move it off the machine. */
    test("the keys to the machine are not in a backup nobody asked for one", async () => {
        const { ports, written } = harness();

        const outcome = await createBackup(ports);

        expect(outcome.ok && outcome.backup.containsSecrets).toBe(false);
        expect(written[0]?.name).toBe("argon-20260825T074600Z.tar.gz");

        const { manifest, plain } = opened(written[0]!.bytes);

        expect(plain).not.toContain(SIGNING_KEY);
        expect(plain).not.toContain(DATABASE_PASSWORD);

        expect(manifest.containsSecrets).toBe(false);
        expect(manifest.contents.secrets).toEqual([]);
        // In sorted order, because the whole listing is: two backups of an unchanged instance have to
        // produce the same archive, and a directory read that came back in a different order must not
        // look like a configuration change.
        expect(manifest.contents.omitted).toEqual([
            MINT_FILE,
            ENV_FILENAME,
            DEPLOYMENT.secretsFile,
            STORAGE_IDENTITIES,
        ]);
    });

    /**
     * Asked for, they go in — and the filename says so, because the moment that matters is the one
     * where somebody copies the file without opening it.
     */
    test("asked for, the keys go in and the filename admits it", async () => {
        const { ports, written } = harness();

        const outcome = await createBackup(ports, { includeSecrets: true });

        expect(outcome.ok && outcome.backup.containsSecrets).toBe(true);
        expect(written[0]?.name).toBe("argon-20260825T074600Z-with-secrets.tar.gz");

        const { manifest, plain, entries } = opened(written[0]!.bytes);

        expect(plain).toContain(SIGNING_KEY);
        expect(manifest.containsSecrets).toBe(true);
        expect(manifest.contents.omitted).toEqual([]);
        expect(manifest.contents.secrets).toContain(DEPLOYMENT.secretsFile);

        // Extracting must not be what widens it. The mode has to survive the round trip.
        const secrets = entries.find((one) => one.path.endsWith(DEPLOYMENT.secretsFile));

        expect(secrets?.mode).toBe(0o600);
    });

    /**
     * The sweep is by directory, so the archive's own account of itself is only as good as what the
     * operator left in one. Traefik's ACME store under its canonical name is the case that will happen,
     * and read as configuration it went into a file called `argon-….tar.gz` — no suffix — whose manifest
     * said `containsSecrets: false` while it carried the private key of every certificate on the box.
     */
    test("a key an operator left in a configuration directory is not in a backup that says it has none", async () => {
        const acme = '{"letsencrypt":{"Account":{"PrivateKey":"THE-ACME-ACCOUNT-KEY"}}}\n';
        const files = { ...INSTALL, ["traefik/acme.json"]: { text: acme, mode: 0o600 } };

        const plain = harness({ files });

        await createBackup(plain.ports);

        const first = opened(plain.written[0]!.bytes);

        expect(first.plain).not.toContain("THE-ACME-ACCOUNT-KEY");
        expect(first.manifest.contents.configuration).not.toContain("traefik/acme.json");
        expect(first.manifest.contents.omitted).toContain("traefik/acme.json");

        // Asked for, it goes in — under a name that admits it, which is the whole arrangement working.
        const withKeys = harness({ files });

        await createBackup(withKeys.ports, { includeSecrets: true });

        expect(withKeys.written[0]?.name).toContain("-with-secrets");
        expect(opened(withKeys.written[0]!.bytes).plain).toContain("THE-ACME-ACCOUNT-KEY");
    });

    /**
     * The same thing again for a name this file has never been told about, which is the case a list of
     * known key names cannot cover. `traefik/certificates.json` is what Traefik's own documentation
     * calls the store when it is not called `acme.json`, and `sfu/keys.json` is a LiveKit key file
     * named by whoever wrote the compose override. Neither is in any list here; both hold private keys;
     * neither may be in an archive that says it holds none.
     */
    test("an unrecognised file in a directory that holds keys is treated as one, end to end", async () => {
        const files = {
            ...INSTALL,
            ["traefik/certificates.json"]: { text: '{"key":"THE-CERTIFICATE-KEY"}\n', mode: 0o600 },
            ["sfu/keys.json"]: { text: '{"APIabc":"THE-LIVEKIT-SECRET"}\n', mode: 0o600 },
        };

        const plain = harness({ files });

        await createBackup(plain.ports);

        const first = opened(plain.written[0]!.bytes);

        expect(plain.written[0]?.name).not.toContain("-with-secrets");
        expect(first.plain).not.toContain("THE-CERTIFICATE-KEY");
        expect(first.plain).not.toContain("THE-LIVEKIT-SECRET");

        // Named rather than silently dropped, so the operator can see the guess and argue with it —
        // which is the difference between this and `skipped`.
        expect(first.manifest.contents.omitted).toContain("traefik/certificates.json");
        expect(first.manifest.contents.omitted).toContain("sfu/keys.json");
        expect(first.manifest.contents.configuration).not.toContain("traefik/certificates.json");

        const withKeys = harness({ files });

        await createBackup(withKeys.ports, { includeSecrets: true });

        expect(opened(withKeys.written[0]!.bytes).plain).toContain("THE-CERTIFICATE-KEY");
    });

    /** Every archive holds every account row on the instance, whatever else it holds. */
    test("the dump and the archive are 0600 even when no secrets were asked for", async () => {
        const { ports, written } = harness();

        await createBackup(ports);

        expect(written[0]?.mode).toBe(ARCHIVE_MODE);

        const { entries } = opened(written[0]!.bytes);

        expect(entries.find((one) => one.path.endsWith(DATABASE_FILE))?.mode).toBe(0o600);
        expect(entries.find((one) => one.path.endsWith("api.json"))?.mode).toBe(0o644);
    });

    /**
     * The install root is a directory somebody administers. A backup that swept up whatever was left
     * there would be a backup whose secret content nobody can state, so unknown paths are reported
     * instead — on the day the backup is taken rather than the day it is needed.
     */
    test("a file nobody recognises is skipped and said out loud", async () => {
        const { ports, written } = harness();

        const outcome = await createBackup(ports);

        expect(outcome.ok && outcome.contents.skipped).toEqual(["operator-notes.txt"]);
        expect(opened(written[0]!.bytes).plain).not.toContain("renew the domain");
    });

    test("the manifest's digest is the dump's own", async () => {
        const { ports, written } = harness();

        await createBackup(ports);

        const { manifest, entries } = opened(written[0]!.bytes);
        const dump = entries.find((one) => one.path.endsWith(DATABASE_FILE))!;
        const recorded = manifest.entries.find((one) => one.path.endsWith(DATABASE_FILE));

        expect(recorded?.bytes).toBe(dump.bytes.length);
        expect(recorded?.sha256).toBe(new Bun.CryptoHasher("sha256").update(dump.bytes).digest("hex"));
    });

    /** Two backups of an unchanged instance should differ only where the instance differs. */
    test("the same instance twice produces the same archive", async () => {
        const first = harness();
        const second = harness();

        await createBackup(first.ports);
        await createBackup(second.ports);

        expect(first.written[0]!.bytes).toEqual(second.written[0]!.bytes);
    });

    /** Everything sits under one directory, so extracting in the wrong place is recoverable. */
    test("nothing extracts into the current directory", async () => {
        const { ports, written } = harness();

        await createBackup(ports);

        for (const entry of opened(written[0]!.bytes).entries)
            expect(entry.path.startsWith("argon-20260825T074600Z/")).toBe(true);
    });
});

describe("what a backup refuses to do", () => {
    test("nothing is written when there is no instance here", async () => {
        const { ports, written } = harness({ rows: [] });

        const outcome = await createBackup(ports);

        expect(!outcome.ok && outcome.reason).toBe("no-database");
        expect(written).toHaveLength(0);
    });

    test("a database that is down is named as down, not as absent", async () => {
        const { ports } = harness({ rows: [{ Id: "pg-1", State: "exited" }] });

        const outcome = await createBackup(ports);

        expect(!outcome.ok && outcome.reason).toBe("database-stopped");
    });

    /** A file that exists and is half an archive is worse than no file: somebody will find it. */
    test("a failed dump leaves nothing behind", async () => {
        const { ports, written } = harness({ state: { running: false, exitCode: 1 } });

        const outcome = await createBackup(ports);

        expect(!outcome.ok && outcome.reason).toBe("dump-failed");
        expect(written).toHaveLength(0);
    });

    test("a truncated dump is not stored under a name that says it is a backup", async () => {
        const { ports, written } = harness({ body: frame(1, "CREATE TABLE a;\n") });

        expect((await createBackup(ports)).ok).toBe(false);
        expect(written).toHaveLength(0);
    });

    /** Overwriting a backup is the one thing this must never do quietly. */
    test("a second backup in the same second refuses rather than replacing the first", async () => {
        const { ports, written } = harness({
            existing: [{ name: "argon-20260825T074600Z.tar.gz", bytes: 10 }],
        });

        const outcome = await createBackup(ports);

        expect(!outcome.ok && outcome.reason).toBe("already-taken");
        expect(written).toHaveLength(0);
    });

    /**
     * The check above ran before a `pg_dump`, which is long enough for a second click to overtake it:
     * both calls saw an empty directory, both computed the same name, and the second write replaced the
     * first archive without a word. So the store answers the same refusal from the far side of the
     * dump, where it is the write itself that either claims the name or does not.
     */
    test("a name claimed while the dump was running refuses instead of replacing it", async () => {
        const { ports, written } = harness({ claimed: true });

        const outcome = await createBackup(ports);

        expect(!outcome.ok && outcome.reason).toBe("already-taken");
        expect(written).toHaveLength(0);
    });

    /**
     * A ustar name field is 100 bytes and the writer throws past it, which out of here would be a
     * rejected promise where every other failure is a sentence the panel can show — and it would be
     * thrown after the dump had already run.
     */
    test("a path too long for the archive is a refusal, not a stack trace", async () => {
        const long = `${DEPLOYMENT.confD}/${"n".repeat(60)}.json`;
        const files = { ...INSTALL, [long]: { text: "{}\n", mode: 0o644 } };

        for (const includeSecrets of [false, true]) {
            const { ports, written } = harness({ files });

            const outcome = await createBackup(ports, { includeSecrets });

            expect(!outcome.ok && outcome.reason).toBe("path-too-long");
            expect(!outcome.ok && outcome.detail).toContain(long);
            expect(written).toHaveLength(0);
        }
    });

    /**
     * The budget itself, on the byte either side of it, because the two settings used to have different
     * ones. `-with-secrets` is 13 bytes of the 100 a ustar name field holds, so a path in that window
     * archived fine for months of ordinary backups and refused the first time somebody deliberately
     * asked for the keys — which is the backup taken before a migration, at the moment it is least
     * welcome. Both fixtures are built from the archive's own name rather than from a copied number, so
     * that a change to the naming moves the test with it.
     */
    test("the budget is the same one whether or not the backup is carrying keys", async () => {
        const at = new Date("2026-08-25T07:46:00Z");
        const inside = `${backupName(at, true).slice(0, -ARCHIVE_EXTENSION.length)}/${INSTALL_PREFIX}/`;

        // The longest install-relative path a `-with-secrets` archive can name, and one byte more.
        const room = 100 - inside.length;
        const named = (length: number): string =>
            `${DEPLOYMENT.confD}/${"n".repeat(length - DEPLOYMENT.confD.length - ".json".length - 1)}.json`;

        for (const includeSecrets of [false, true]) {
            const fits = harness({ files: { ...INSTALL, [named(room)]: { text: "{}\n", mode: 0o644 } } });

            expect((await createBackup(fits.ports, { includeSecrets })).ok).toBe(true);

            const over = harness({ files: { ...INSTALL, [named(room + 1)]: { text: "{}\n", mode: 0o644 } } });
            const outcome = await createBackup(over.ports, { includeSecrets });

            // Including the plain one, which had thirteen bytes of room it could not keep.
            expect(!outcome.ok && outcome.reason).toBe("path-too-long");
            expect(over.written).toHaveLength(0);
        }
    });

    /** And the same file one byte inside the budget is still archived, on both settings. */
    test("a path that fits is still taken, with or without the suffix", async () => {
        const fits = `${DEPLOYMENT.confD}/${"n".repeat(30)}.json`;
        const files = { ...INSTALL, [fits]: { text: "{}\n", mode: 0o644 } };

        for (const includeSecrets of [false, true]) {
            const { ports, written } = harness({ files });

            expect((await createBackup(ports, { includeSecrets })).ok).toBe(true);
            expect(opened(written[0]!.bytes).manifest.contents.configuration).toContain(fits);
        }
    });

    test("an existing backup with secrets does not block a plain one", async () => {
        const { ports, written } = harness({
            existing: [{ name: "argon-20260825T074600Z-with-secrets.tar.gz", bytes: 10 }],
        });

        expect((await createBackup(ports)).ok).toBe(true);
        expect(written).toHaveLength(1);
    });

    /** An apply running underneath this takes files away mid-backup. One file is not the whole backup. */
    test("a file that vanishes between the listing and the read is skipped, not fatal", async () => {
        const { ports } = harness();

        const store = ports.store;
        const patched: BackupStore = {
            ...store,
            list: async () => [...(await store.list()), `${DEPLOYMENT.confD}/gone.json`],
        };

        const outcome = await createBackup({ ...ports, store: patched });

        expect(outcome.ok).toBe(true);
        expect(outcome.ok && outcome.contents.skipped).toContain(`${DEPLOYMENT.confD}/gone.json`);
    });
});

/* ------------------------------------------------------------------------------------------------
 * The real exec port.
 *
 * `dockerExec` is nothing but a request body — three decisions about what to ask the daemon for — and
 * the whole of its job happens on the far side of `fetch`. That used to be the argument for leaving it
 * untested, and the file said so out loud; but "`Tty` is false in both calls and has to stay that way"
 * is the load-bearing sentence in this module, and a comment is not a check. A TTY makes the daemon
 * merge stderr into stdout, which puts pg_dump's warnings inside the SQL of a dump that looks perfectly
 * normal until a restore reaches one.
 *
 * So the global is swapped for the duration, which is the smallest fake that can see a request body.
 * It is restored in a `finally` whatever the test does, because a leaked stub is a whole suite failing
 * somewhere else.
 * ---------------------------------------------------------------------------------------------- */

const SOCKET = "/var/run/docker.sock";

interface Sent {
    readonly url: string;
    readonly method: string;
    readonly unix: unknown;
    readonly body: Record<string, unknown> | undefined;
}

async function overFetch<T>(
    reply: (sent: Sent) => Response,
    use: (exec: ContainerExec, sent: readonly Sent[]) => Promise<T>,
): Promise<T> {
    const original = globalThis.fetch;
    const sent: Sent[] = [];

    globalThis.fetch = (async (input: unknown, init?: Record<string, unknown>): Promise<Response> => {
        const one: Sent = {
            url: String(input),
            method: typeof init?.method === "string" ? init.method : "GET",
            unix: init?.unix,
            body: typeof init?.body === "string" ? (JSON.parse(init.body) as Record<string, unknown>) : undefined,
        };

        sent.push(one);

        return reply(one);
    }) as unknown as typeof fetch;

    try {
        return await use(dockerExec(SOCKET), sent);
    } finally {
        globalThis.fetch = original;
    }
}

/** What the daemon answers each of the three calls with, when the point of the test is the request. */
function daemon(body: Bytes): (sent: Sent) => Response {
    return (sent) => {
        if (sent.url.endsWith("/exec")) return new Response(JSON.stringify({ Id: "exec-1" }));
        if (sent.url.endsWith("/start")) return new Response(body);

        return new Response(JSON.stringify({ Running: false, ExitCode: 0 }));
    };
}

describe("the exec port over the docker socket", () => {
    /**
     * The two flags this module's correctness rests on, asserted on the bytes that go to the daemon.
     *
     * `Tty: true` is what turns the framed response into a raw one — {@link demultiplex} refuses that
     * body rather than parsing it, so the visible result would be every backup failing with "a TTY was
     * attached" and nobody able to see where from. `AttachStderr: false` is the quieter one: the dump
     * still succeeds, and pg_dump's warnings — the `circular foreign-key constraints` notice, which is
     * about the *restore* — are simply never in the manifest.
     */
    test("the exec is created without a TTY and with stderr attached", async () => {
        const sent = await overFetch(daemon(frame(1, dumpText())), async (exec, seen) => {
            await exec.create("pg-1", { command: dumpCommand(), user: "postgres" });

            return seen[0];
        });

        expect(sent?.body).toEqual({
            AttachStdin: false,
            AttachStdout: true,
            AttachStderr: true,
            Tty: false,
            Cmd: dumpCommand(),
            User: "postgres",
        });
    });

    /** And again on `start`, where a TTY would override what the exec was created with. */
    test("the exec is started without a TTY and without detaching", async () => {
        const sent = await overFetch(daemon(frame(1, dumpText())), async (exec, seen) => {
            await exec.start("exec-1");

            return seen[0];
        });

        expect(sent?.body).toEqual({ Detach: false, Tty: false });
        expect(sent?.url).toBe("http://docker/exec/exec-1/start");
    });

    /** Over the socket it was handed, as a POST. An HTTP host called `docker` resolves nowhere. */
    test("every call goes over the unix socket", async () => {
        const seen = await overFetch(daemon(frame(1, dumpText())), async (exec, sent) => {
            await exec.create("pg-1", { command: ["pg_dump"] });
            await exec.start("exec-1");
            await exec.inspect("exec-1");

            return sent;
        });

        expect(seen.map((one) => one.unix)).toEqual([SOCKET, SOCKET, SOCKET]);
        expect(seen.map((one) => one.method)).toEqual(["POST", "POST", "GET"]);
    });

    /** No user named means the image's own default, which is not the same as asking for `""`. */
    test("an exec with no user asks for none rather than for nobody", async () => {
        const sent = await overFetch(daemon(frame(1, dumpText())), async (exec, seen) => {
            await exec.create("pg-1", { command: ["pg_dump"] });

            return seen[0];
        });

        expect(sent?.body).not.toHaveProperty("User");
    });

    /**
     * The body comes back exactly as the daemon sent it, still framed — which is the port's contract
     * and the reason {@link demultiplex} sits above it rather than inside it. Anything that decoded it
     * on the way through would corrupt a dump at the first byte that is not text.
     */
    test("the response body is handed back as bytes, unparsed", async () => {
        const body = joined(frame(1, dumpText()), frame(2, "a warning\n"));

        const returned = await overFetch(daemon(body), async (exec) => await exec.start("exec-1"));

        expect(new Uint8Array(returned)).toEqual(body);
    });

    test("the daemon's own state is what inspect reports", async () => {
        const state = await overFetch(
            () => new Response(JSON.stringify({ Running: true, ExitCode: null })),
            async (exec) => await exec.inspect("exec-1"),
        );

        // `null` is what the daemon sends while it is still running, and it is not an exit code.
        expect(state).toEqual({ running: true, exitCode: undefined });
    });

    /**
     * A refusal from the daemon has to be an error rather than an empty dump. `POST /exec` answering
     * 409 for a container that stopped between the lookup and the exec would otherwise read as a
     * successful dump of nothing at all.
     */
    test("a daemon that refuses is an error naming the status", async () => {
        const failing = overFetch(
            () => new Response("no such container", { status: 409 }),
            async (exec) => await exec.create("pg-1", { command: ["pg_dump"] }),
        );

        await expect(failing).rejects.toThrow(/409/);
    });

    test("an exec created without an id is not an exec", async () => {
        const failing = overFetch(
            () => new Response(JSON.stringify({ Warnings: [] })),
            async (exec) => await exec.create("pg-1", { command: ["pg_dump"] }),
        );

        await expect(failing).rejects.toThrow(/without an id/);
    });
});

/* ------------------------------------------------------------------------------------------------
 * The real store.
 *
 * The only tests here that touch a disk, and they are here for the two properties that cannot be
 * demonstrated against a fake: the mode a file lands with, and the fact that the walk does not descend
 * into the archives it is about to write beside.
 * ---------------------------------------------------------------------------------------------- */

describe("the install directory", () => {
    async function directory(): Promise<string> {
        return await mkdtemp(join(tmpdir(), "argon-backup-"));
    }

    test("lists what is in it, with '/' whatever the host uses", async () => {
        const root = await directory();

        await Bun.write(join(root, DEPLOYMENT.confD, "api.json"), "{}\n");
        await Bun.write(join(root, DEPLOYMENT.secretsFile), "{}\n");

        expect([...(await installStore(root).list())].sort()).toEqual([
            `${DEPLOYMENT.confD}/api.json`,
            DEPLOYMENT.secretsFile,
        ]);
    });

    /** An archive of the archives grows without bound, and it is the largest thing on the box. */
    test("does not walk into the backups it writes", async () => {
        const root = await directory();

        await Bun.write(join(root, BACKUP_DIRECTORY, "argon-20260101T000000Z.tar.gz"), "not really an archive");
        await Bun.write(join(root, COMPOSE_FILENAME), "services: {}\n");

        expect(await installStore(root).list()).toEqual([COMPOSE_FILENAME]);
    });

    /**
     * A symlink's dirent says it is neither a file nor a directory, and listing only files dropped it
     * out of the walk entirely — not archived, and in none of `skipped`, `omitted` or `excluded`, so the
     * backup looked complete and was missing the API's configuration. Linked *within* the install root —
     * one shared file that several feature documents point at — it is still read through, which is the
     * half of that fix that survives.
     */
    test.skipIf(!SYMLINKS)("a symlinked configuration file inside the root is listed and read through", async () => {
        const root = await directory();

        await Bun.write(join(root, "shared", "api.json"), '{"linked":true}\n');
        await Bun.write(join(root, COMPOSE_FILENAME), "services: {}\n");
        await Bun.write(join(root, DEPLOYMENT.confD, ".keep"), "");
        await symlink(join(root, "shared", "api.json"), join(root, DEPLOYMENT.confD, "api.json"));

        const store = installStore(root);

        expect([...(await store.list())].sort()).toContain(`${DEPLOYMENT.confD}/api.json`);
        expect(decoder.decode((await store.read(`${DEPLOYMENT.confD}/api.json`))?.bytes)).toBe('{"linked":true}\n');
    });

    /**
     * And the other half, which is why the containment check had to stop being about the string.
     *
     * The name is inside the root and what it opens is not. `resolve` says `conf.d/api.json` is fine,
     * `stat` and `readFile` follow the link, and the host's file came back as this instance's
     * configuration — see the end-to-end case below for what that produced. Answered as absent, so it
     * lands in `skipped` where the operator is told, rather than thrown, which would fail the backup.
     */
    test.skipIf(!SYMLINKS)("a link whose target is outside the root reads as absent", async () => {
        const root = await directory();
        const elsewhere = await directory();

        await Bun.write(join(elsewhere, "host-file"), "root:x:0:0:HOST-FILE-CONTENT\n");
        await Bun.write(join(root, DEPLOYMENT.confD, ".keep"), "");
        await symlink(join(elsewhere, "host-file"), join(root, DEPLOYMENT.confD, "api.json"));

        const store = installStore(root);

        // Listed, because the walk cannot tell and must not silently drop things.
        expect([...(await store.list())].sort()).toContain(`${DEPLOYMENT.confD}/api.json`);
        expect(await store.read(`${DEPLOYMENT.confD}/api.json`)).toBeUndefined();
    });

    /**
     * The same escape one step short of the root's edge, and the reason the resolved name is classified
     * rather than only measured. `conf.d/api.json` pointing at `../panel.credential` lands *inside* the
     * install root, so containment has nothing to say about it — and the classification only ever saw
     * the name it was handed. The panel's own Argon2id hash is the one thing this module promises is in
     * no archive at any setting, and it is the credential to a container holding the docker socket.
     */
    test.skipIf(!SYMLINKS)("a link to a file that is in no backup does not become one that is", async () => {
        const root = await directory();

        await Bun.write(join(root, CREDENTIAL_FILE), `${PANEL_HASH}\n`);
        await Bun.write(join(root, DEPLOYMENT.confD, ".keep"), "");
        await symlink(join(root, CREDENTIAL_FILE), join(root, DEPLOYMENT.confD, "api.json"));

        const store = installStore(root);

        expect([...(await store.list())].sort()).toContain(`${DEPLOYMENT.confD}/api.json`);
        expect(await store.read(`${DEPLOYMENT.confD}/api.json`)).toBeUndefined();

        // And the file under its own name is still what it always was: excluded, not skipped.
        expect(classify(CREDENTIAL_FILE)).toBe("excluded");
    });

    /**
     * The whole of it, against the real store: the file an operator never meant to copy anywhere does
     * not end up in an archive that calls it configuration.
     *
     * Reproduced as the review reproduced it. Before this, `createBackup` over an install root holding
     * one link wrote `argon-….tar.gz` — no `-with-secrets` suffix, `containsSecrets: false`, the escaped
     * bytes listed under `contents.configuration`. Anyone who can write into the install root can leave
     * that link, and after an upgrade that includes anything the panel itself writes there.
     *
     * `classify` is asserted here too, and it still answers `configuration`: the classification is about
     * the name and cannot be what saves this. The store is.
     */
    test.skipIf(!SYMLINKS)("a link out of the root does not put the host's file in an archive", async () => {
        const root = await directory();
        const elsewhere = await directory();

        await Bun.write(join(elsewhere, "host-file"), "root:x:0:0:HOST-FILE-CONTENT\n");
        await Bun.write(join(root, COMPOSE_FILENAME), "services: {}\n");
        await Bun.write(join(root, DEPLOYMENT.confD, ".keep"), "");
        await symlink(join(elsewhere, "host-file"), join(root, DEPLOYMENT.confD, "api.json"));

        expect(classify(`${DEPLOYMENT.confD}/api.json`)).toBe("configuration");

        const store = installStore(root);
        const outcome = await createBackup({
            engine: fakeEngine([{ Id: "pg-1", State: "running" }]),
            exec: fakeExec(frame(1, dumpText())).port,
            store,
            now: () => new Date("2026-08-25T07:46:00Z"),
        });

        expect(outcome.ok).toBe(true);
        expect(outcome.ok && outcome.contents.skipped).toContain(`${DEPLOYMENT.confD}/api.json`);
        expect(outcome.ok && outcome.contents.configuration).not.toContain(`${DEPLOYMENT.confD}/api.json`);

        const written = await readFile(join(root, BACKUP_DIRECTORY, "argon-20260825T074600Z.tar.gz"));
        const archive = opened(new Uint8Array(written));

        expect(archive.plain).not.toContain("HOST-FILE-CONTENT");
        expect(archive.manifest.contents.configuration).not.toContain(`${DEPLOYMENT.confD}/api.json`);
    });

    /**
     * The other side of that: what the link points at is not always a file. Listed anyway and answered
     * as absent, so it is reported in `skipped` rather than throwing EISDIR out of the middle of an
     * otherwise fine backup, or disappearing without a word.
     */
    test.skipIf(!SYMLINKS)("a link to a directory, and one to nothing, are listed and read as absent", async () => {
        const root = await directory();
        const elsewhere = await directory();

        await symlink(elsewhere, join(root, "linked-directory"));
        await symlink(join(elsewhere, "never-existed"), join(root, "dangling"));

        const store = installStore(root);

        expect([...(await store.list())].sort()).toEqual(["dangling", "linked-directory"]);
        expect(await store.read("linked-directory")).toBeUndefined();
        expect(await store.read("dangling")).toBeUndefined();
    });

    test("a file that is not there reads as absent rather than throwing", async () => {
        expect(await installStore(await directory()).read(DEPLOYMENT.secretsFile)).toBeUndefined();
    });

    test("a read that climbs out of the root is refused", async () => {
        expect(installStore(await directory()).read("../../etc/shadow")).rejects.toThrow();
    });

    /**
     * `read` ran every path through the containment check and `put` ran it through nothing, so
     * `put("../compose.yaml", …)` wrote through the join and replaced the live compose file — atomically
     * and unrecoverably, since it went in by rename. Nothing passes a name like that today, and §10's
     * download, delete and restore all take one out of an HTTP request.
     */
    test.each(["../compose.yaml", "../../etc/cron.d/argon", "a/b.tar.gz", ".", ".."])(
        "put refuses '%s', which is a path and not a name",
        async (name) => {
            const root = await directory();

            await Bun.write(join(root, COMPOSE_FILENAME), "services: {}\n");

            // Refused *as a name*, and the message says so — a couple of these fail on their own by
            // landing somewhere that does not exist, which is luck rather than a guard.
            await expect(installStore(root).put(name, bytes("archive"), ARCHIVE_MODE)).rejects.toThrow(
                /not a backup file name/,
            );

            expect(await readFile(join(root, COMPOSE_FILENAME), "utf8")).toBe("services: {}\n");
        },
    );

    /**
     * The already-taken refusal, held where it can be atomic. Two `createBackup` calls a moment apart
     * both ask `taken()` before either writes — a `pg_dump` is long enough for that — so the name is
     * claimed by the write or it is not claimed at all.
     */
    test("a second put under the same name writes nothing and says so", async () => {
        const root = await directory();
        const store = installStore(root);
        const name = "argon-20260101T000000Z.tar.gz";

        expect(await store.put(name, bytes("the first archive"), ARCHIVE_MODE)).toBe(true);
        expect(await store.put(name, bytes("the second archive"), ARCHIVE_MODE)).toBe(false);

        expect(await readFile(join(root, BACKUP_DIRECTORY, name), "utf8")).toBe("the first archive");

        // And the refused write leaves no `.partial` behind for somebody to find and wonder about.
        expect((await store.taken()).map((one) => one.name)).toEqual([name]);
    });

    test("no backup directory means no backups, not an error", async () => {
        expect(await installStore(await directory()).taken()).toEqual([]);
    });

    /**
     * A `put` finishing underneath a listing renames its `.partial` away, and that is a name `readdir`
     * has already handed over: the `stat` that follows answers ENOENT. Caught around the whole loop, as
     * it was, that ended the walk and returned the archives seen so far as though they were all of them
     * — a listing of 1 where there were 201, which makes `listBackups` lie and lets the already-taken
     * check pass over a name that is on disk. A dangling link is the same thing without the race.
     */
    test.skipIf(!SYMLINKS)("an entry that is gone by its stat removes its own row and no others", async () => {
        const root = await directory();
        const names: string[] = [];

        // Interleaved on the way in and named so that they interleave alphabetically as well, because a
        // directory comes back in whatever order the filesystem keeps it in and both are plausible.
        for (let index = 0; index < 12; index += 1) {
            const name = `argon-2026011${index}T000000Z.tar.gz`;

            names.push(name);

            await Bun.write(join(root, BACKUP_DIRECTORY, name), "x".repeat(index + 1));
            await symlink(join(root, BACKUP_DIRECTORY, "never-existed"), join(root, BACKUP_DIRECTORY, `${name}.gone`));
        }

        const listed = await installStore(root).taken();

        expect(listed.map((one) => one.name).sort()).toEqual([...names].sort());
    });

    /** Followed rather than filtered on `isFile`, so archives kept on another disk still list. */
    test.skipIf(!SYMLINKS)("an archive linked in from elsewhere is one of the backups", async () => {
        const root = await directory();
        const elsewhere = await directory();
        const name = "argon-20260101T000000Z.tar.gz";

        await Bun.write(join(elsewhere, name), "0123456789");
        await Bun.write(join(root, BACKUP_DIRECTORY, ".keep"), "");
        await symlink(join(elsewhere, name), join(root, BACKUP_DIRECTORY, name));

        expect(await installStore(root).taken()).toContainEqual({ name, bytes: 10 });
    });

    test("what is put comes back in the listing at its real size", async () => {
        const root = await directory();
        const store = installStore(root);

        await store.put("argon-20260101T000000Z.tar.gz", bytes("0123456789"), ARCHIVE_MODE);

        expect(await store.taken()).toEqual([{ name: "argon-20260101T000000Z.tar.gz", bytes: 10 }]);
        expect(await readFile(join(root, BACKUP_DIRECTORY, "argon-20260101T000000Z.tar.gz"), "utf8")).toBe("0123456789");
    });

    /**
     * The umask this process inherited subtracts from a mode and never adds to it, so both are set
     * explicitly. Skipped on Windows, which models a read-only bit and nothing about group or world —
     * the panel is a Linux container and this is the laptop.
     */
    test.skipIf(process.platform === "win32")("the archive is 0600 inside a 0700 directory", async () => {
        const root = await directory();

        await installStore(root).put("argon-20260101T000000Z.tar.gz", bytes("x"), ARCHIVE_MODE);

        expect((await stat(join(root, BACKUP_DIRECTORY))).mode & 0o777).toBe(0o700);
        expect((await stat(join(root, BACKUP_DIRECTORY, "argon-20260101T000000Z.tar.gz"))).mode & 0o777).toBe(0o600);
    });

    test.skipIf(process.platform === "win32")("a file's mode comes back with its contents", async () => {
        const root = await directory();

        await writeFile(join(root, DEPLOYMENT.secretsFile), "{}\n", { mode: 0o600 });

        expect((await installStore(root).read(DEPLOYMENT.secretsFile))?.mode).toBe(0o600);
    });
});
