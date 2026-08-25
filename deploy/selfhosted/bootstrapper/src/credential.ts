import { chmod, readFile, rename, writeFile } from "node:fs/promises";
import { randomBytes } from "node:crypto";
import { join } from "node:path";

/**
 * The panel's own password — the credential that outlives setup.
 *
 * ## Why this exists at all
 *
 * The bootstrap code is printed in a terminal and left in a file in the install root, and it opens a
 * panel holding the docker socket. §4 says it has to stop working once setup finishes, and it is right:
 * a permanently valid code on a public box is worse than the problem it solved.
 *
 * But retiring it with nothing behind it locks the operator out of their own machine. So the code's end
 * and this credential's beginning are one change, and the order is fixed: a password is required before
 * the install runs, and the code is retired the moment the install succeeds.
 *
 * ## What is stored
 *
 * The Argon2id hash, and nothing else. Not the password, not a reversible form of it, not a hint. The
 * file is mode 0600 in the install root beside the secrets — a machine whose root filesystem is readable
 * by somebody else has already lost, and this is the boundary that is actually defensible.
 *
 * Separate from `config.ts` because that module only reads and only at startup, and separate from
 * `auth.ts` because that one never touches a disk: it is handed a hash and checks against it.
 */

export const CREDENTIAL_FILE = "panel.credential";

/**
 * The shortest password this accepts.
 *
 * Twelve, and no rules about which characters. Composition rules produce `Passw0rd!` and stop there;
 * length is the only property that reliably costs an attacker anything, and this credential is already
 * behind Argon2id and a five-attempt lockout. What the operator needs from this number is that it is
 * long enough to be worth a password manager.
 */
export const MINIMUM_LENGTH = 12;

/**
 * Why a password was not taken, or nothing.
 *
 * Sentences rather than codes, because they are shown beside the field. Each says what is wrong and
 * what would be right — a refusal the operator has to guess at is a refusal they retry blindly.
 */
export function refuseCredential(password: string, bootstrapCode: string): string | undefined {
    const given = password.normalize("NFKC");

    if (given.trim().length === 0) return "A password is required. It is what gets you back into this panel after setup.";

    if (given.length < MINIMUM_LENGTH)
        return `At least ${MINIMUM_LENGTH} characters. This one is ${given.length}, and it is the only lock on a panel that can start and stop containers.`;

    // The code is on screen in the terminal right now, which makes it the easiest thing to paste in —
    // and pasting it would mean retiring a credential and replacing it with itself.
    if (equalish(given, bootstrapCode))
        return "That is the bootstrap code. It is about to stop working, which is the point of setting a password — this needs to be something else.";

    return undefined;
}

/** Compared loosely on purpose: the code is printed with hyphens and typed back without them. */
function equalish(password: string, code: string): boolean {
    const strip = (value: string): string => value.replace(/[^a-z0-9]/gi, "").toLowerCase();

    return strip(password).length > 0 && strip(password) === strip(code);
}

/**
 * The stored hash, or nothing when none was ever set.
 *
 * A missing file is the ordinary case — every install starts without one — so it answers `undefined`
 * rather than rejecting. Anything else rejects: a credential file that exists and cannot be read is a
 * broken install, and treating it as absent would silently offer the operator a fresh start on a panel
 * that already has a password.
 */
export async function readCredential(directory: string): Promise<string | undefined> {
    try {
        const contents = await readFile(join(directory, CREDENTIAL_FILE), "utf8");

        return contents.trim().length === 0 ? undefined : contents.trim();
    } catch (cause) {
        if ((cause as NodeJS.ErrnoException).code === "ENOENT") return undefined;

        throw cause;
    }
}

/**
 * Hashes a password and writes it, returning the hash so the caller can start accepting it.
 *
 * Written to a temporary name and renamed, with the mode set before anything is in it. A half-written
 * credential file is a panel nobody can sign into, and the window for that is the length of a disk
 * write — small, and not zero.
 */
export async function writeCredential(directory: string, password: string): Promise<string> {
    const hash = await Bun.password.hash(password.normalize("NFKC"), { algorithm: "argon2id" });

    const target = join(directory, CREDENTIAL_FILE);
    const temporary = `${target}.${randomBytes(6).toString("hex")}.partial`;

    await writeFile(temporary, `${hash}\n`, { mode: 0o600 });

    // Explicit, because the umask this process inherited subtracts from the mode above and never adds
    // to it. 0600 survives any umask; anything wider is a credential the rest of the box can read.
    await chmod(temporary, 0o600);
    await rename(temporary, target);

    return hash;
}
