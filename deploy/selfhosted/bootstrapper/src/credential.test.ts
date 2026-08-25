import { describe, expect, test } from "bun:test";
import { mkdtemp, readFile, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { CREDENTIAL_FILE, MINIMUM_LENGTH, readCredential, refuseCredential, writeCredential } from "./credential";

/* ------------------------------------------------------------------------------------------------
 * The password that outlives setup.
 *
 * Two halves worth testing separately: the rule, which is what the operator argues with, and the file,
 * which is what an attacker would go looking for.
 * ---------------------------------------------------------------------------------------------- */

const CODE = "TEST-CODE-ABCD-2345";

async function directory(): Promise<string> {
    return await mkdtemp(join(tmpdir(), "argon-credential-"));
}

describe("what is refused", () => {
    test("a password long enough is taken", () => {
        expect(refuseCredential("a-perfectly-ordinary-password", CODE)).toBeUndefined();
    });

    test("nothing at all says what it is for", () => {
        expect(refuseCredential("   ", CODE)).toContain("back into this panel");
    });

    test("too short says how short, and how long is enough", () => {
        const refusal = refuseCredential("short", CODE);

        expect(refusal).toContain(String(MINIMUM_LENGTH));
        expect(refusal).toContain("5");
    });

    /**
     * The code is on screen in the terminal at that exact moment, which makes it the easiest thing in
     * the world to paste in — and pasting it would mean retiring a credential and replacing it with
     * itself. The comparison ignores the hyphens because that is how it is printed and not how it is
     * typed.
     */
    test.each([[CODE], [CODE.replaceAll("-", "")], [CODE.toLowerCase()]])(
        "the bootstrap code is not a password, however it is written (%s)",
        (attempt) => {
            expect(refuseCredential(attempt, CODE)).toContain("bootstrap code");
        },
    );

    test("something that merely contains the code is fine", () => {
        expect(refuseCredential(`${CODE}-and-more-besides`, CODE)).toBeUndefined();
    });
});

describe("the file", () => {
    test("nothing is there before anything is set", async () => {
        expect(await readCredential(await directory())).toBeUndefined();
    });

    test("what is written comes back, and verifies the password that made it", async () => {
        const root = await directory();

        await writeCredential(root, "a-perfectly-ordinary-password");

        const hash = await readCredential(root);

        expect(hash).toBeDefined();
        expect(await Bun.password.verify("a-perfectly-ordinary-password", hash!)).toBe(true);
        expect(await Bun.password.verify("something-else-entirely", hash!)).toBe(false);
    });

    /**
     * The password itself must not survive anywhere on disk. Argon2id is the whole point of the module
     * — a file that held the password, or anything reversible into it, would make the mode below the
     * only thing standing between an attacker and the panel.
     */
    test("the password is not in the file", async () => {
        const root = await directory();
        const password = "a-perfectly-ordinary-password";

        await writeCredential(root, password);

        const contents = await readFile(join(root, CREDENTIAL_FILE), "utf8");

        expect(contents).not.toContain(password);
        expect(contents).toContain("argon2id");
    });

    /**
     * 0600, explicitly, because the umask this process inherited subtracts from a mode and never adds
     * to it. Skipped on Windows, which models a read-only bit and nothing about group or world — the
     * container is Linux and this is the laptop.
     */
    test.skipIf(process.platform === "win32")("only the owner can read it", async () => {
        const root = await directory();

        await writeCredential(root, "a-perfectly-ordinary-password");

        const facts = await stat(join(root, CREDENTIAL_FILE));

        expect(facts.mode & 0o777).toBe(0o600);
    });

    test("setting a second one replaces the first", async () => {
        const root = await directory();

        await writeCredential(root, "the-first-password-set");
        await writeCredential(root, "the-second-password-set");

        const hash = (await readCredential(root))!;

        expect(await Bun.password.verify("the-second-password-set", hash)).toBe(true);
        expect(await Bun.password.verify("the-first-password-set", hash)).toBe(false);
    });

    /**
     * An empty file is not a password. It would otherwise be adopted as a hash, every verification
     * against it would throw, and the panel would refuse a correct password with no way to tell why.
     */
    test("an empty file reads as no password rather than as an empty one", async () => {
        const root = await directory();

        await writeFile(join(root, CREDENTIAL_FILE), "\n");

        expect(await readCredential(root)).toBeUndefined();
    });

    /**
     * Anything other than absence rejects. A credential file that exists and cannot be read is a broken
     * install, and reporting it as absent would offer the operator a fresh start on a panel that
     * already has a password — which is a way to take one over rather than to recover it.
     */
    test("a directory where the file should be is an error, not an absence", async () => {
        const root = await directory();

        await Bun.$`mkdir ${join(root, CREDENTIAL_FILE)}`.quiet();

        expect(readCredential(root)).rejects.toThrow();
    });
});
