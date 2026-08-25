import { describe, expect, test } from "bun:test";
import { COMPOSE_FILENAME, EDGE_DYNAMIC_CONFIG, EDGE_STATIC_CONFIG } from "./compose";
import { bootstrapFiles, parseBootstrapArguments } from "./emit";

/* ------------------------------------------------------------------------------------------------
 * Reading the install script's command line.
 *
 * Everything here is what the operator answered in a terminal, one indirection later. The failures
 * worth catching are the quiet ones: a flag that was ignored, a value the shell left empty, half a
 * certificate pair — each of which produces a working-looking install that serves the wrong thing.
 * ---------------------------------------------------------------------------------------------- */

const REQUIRED = [
    "--domain=chat.example.org",
    "--traffic=lets-encrypt",
    "--panel-image=ghcr.io/argon-chat/bootstrapper:0.4.2",
    "--root=/opt/argon",
];

function parse(...extra: string[]) {
    return parseBootstrapArguments([...REQUIRED, ...extra]);
}

function problem(result: ReturnType<typeof parseBootstrapArguments>): string {
    if (result.ok) throw new Error("expected a refusal, got a phase");

    return result.problem;
}

describe("the install script's arguments", () => {
    test("the four required flags produce a phase", () => {
        const result = parse();

        expect(result.ok).toBe(true);
        expect(result.ok && result.phase.domain).toBe("chat.example.org");
        expect(result.ok && result.phase.traffic.kind).toBe("lets-encrypt");
        expect(result.ok && result.phase.root).toBe("/opt/argon");
    });

    test.each(REQUIRED)("%s is required", (flag) => {
        const name = flag.slice(0, flag.indexOf("="));
        const without = REQUIRED.filter((each) => each !== flag);

        expect(problem(parseBootstrapArguments(without))).toContain(name);
    });

    /**
     * A misspelled flag is refused rather than dropped.
     *
     * Ignoring it is how an operator who typed `--tls-crt` gets an instance on a different TLS path
     * than the one they chose — and finds out when the browser refuses the page, with nothing in any
     * log that mentions the flag they got wrong.
     */
    test("an unknown flag is a refusal, not a default", () => {
        expect(problem(parse("--tls-crt=/etc/argon/tls.crt"))).toContain("--tls-crt");
    });

    /**
     * `--domain=$DOMAIN` with `DOMAIN` unset expands to `--domain=`, and the shell says nothing.
     */
    test("an empty value is an unset shell variable, not an answer", () => {
        expect(problem(parseBootstrapArguments(["--domain=", ...REQUIRED.slice(1)]))).toContain("empty");
    });

    test("a flag given twice is refused rather than silently resolved", () => {
        expect(problem(parse("--acme-email=a@example.org", "--acme-email=b@example.org"))).toContain("twice");
    });

    test("a bare word is not a flag", () => {
        expect(problem(parse("lets-encrypt"))).toContain("--flag=value");
    });

    test("a traffic shape this does not know is refused with the list", () => {
        const result = parseBootstrapArguments(["--traffic=caddy", ...REQUIRED.filter((f) => !f.startsWith("--traffic"))]);

        expect(problem(result)).toContain("cloudflare-tunnel");
    });

    test.each([["--tls-cert=/etc/argon/tls.crt"], ["--tls-key=/etc/argon/tls.key"]])(
        "%s alone is half a pair and refused",
        (half) => {
            expect(problem(parse(half))).toContain("handshake");
        },
    );

    test("both halves become the material", () => {
        const result = parse("--tls-cert=/etc/argon/tls.crt", "--tls-key=/etc/argon/tls.key");

        expect(result.ok && result.phase.tls).toEqual({
            certificatePath: "/etc/argon/tls.crt",
            keyPath: "/etc/argon/tls.key",
        });
    });
});

describe("what gets written", () => {
    test("the compose document and both Traefik files, and nothing else", () => {
        const result = parse();

        if (!result.ok) throw new Error(result.problem);

        expect(bootstrapFiles(result.phase).map((file) => file.path).sort()).toEqual(
            [COMPOSE_FILENAME, EDGE_DYNAMIC_CONFIG, EDGE_STATIC_CONFIG].sort(),
        );
    });

    /**
     * The parser accepts a shape that the project builder then refuses — deliberately.
     *
     * `own-certificate` with no material parses (both halves are absent, which is a consistent pair)
     * and fails when the edge is built, where the message is about what a front door needs. Asserted so
     * that the refusal is known to survive the extra layer rather than being swallowed by it.
     */
    test("a shape that terminates TLS here still refuses to be built without material", () => {
        const result = parseBootstrapArguments(["--traffic=own-certificate", ...REQUIRED.filter((f) => !f.startsWith("--traffic"))]);

        if (!result.ok) throw new Error(result.problem);

        expect(() => bootstrapFiles(result.phase)).toThrow(/certificate/);
    });
});
