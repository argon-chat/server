import { COMPOSE_FILENAME, bootstrapProject, type BootstrapPhase } from "./compose";
import type { GeneratedFile, TrafficShape } from "./model";

/**
 * `emit-bootstrap`: the same image, asked to describe the front door instead of serving it.
 *
 * The install script cannot write the bootstrap-phase compose itself. It would have to know the project
 * name, the network subnet, the edge's volume, both Traefik paths and how each of §5's traffic shapes
 * becomes TLS — all of it already stated in `compose.ts`, and all of it having to stay identical, since
 * a bootstrap document that disagrees anywhere builds a *second* project beside the real one instead of
 * being replaced by it. So the script pulls this image (which it must do anyway, to run the panel) and
 * asks it. One source of truth, and the shell stays a shell.
 *
 * Arguments rather than environment variables, unlike the server: these are one-shot inputs to a command
 * that runs once, not the standing contract in `config.ts` that a long-lived container is configured by.
 */

/** Recognised flags, in the order the script passes them. Anything else is a mistake, not a default. */
const FLAGS = {
    domain: "--domain",
    traffic: "--traffic",
    panelImage: "--panel-image",
    root: "--root",
    certificate: "--tls-cert",
    key: "--tls-key",
    acmeEmail: "--acme-email",
} as const;

const TRAFFIC_SHAPES = ["own-certificate", "cloudflare-proxied", "lets-encrypt", "cloudflare-tunnel"] as const;

export type Emission =
    | { readonly ok: true; readonly phase: BootstrapPhase }
    | { readonly ok: false; readonly problem: string };

/**
 * Reads the command line into a phase, or says what is wrong with it.
 *
 * Pure, and separate from writing anything, because every interesting decision is here: which traffic
 * shape was named, whether a certificate came with it, whether a flag was misspelled. A misspelling is
 * refused rather than ignored — `--tls-cert` typed as `--tls-crt` would otherwise produce an instance
 * that silently takes a different TLS path than the operator chose, discovered when the browser refuses
 * the page and there is nothing to point at.
 */
export function parseBootstrapArguments(argv: readonly string[]): Emission {
    const values = new Map<string, string>();
    const known = new Set<string>(Object.values(FLAGS));

    for (const argument of argv) {
        const separator = argument.indexOf("=");

        if (!argument.startsWith("--") || separator === -1)
            return { ok: false, problem: `'${argument}' is not a --flag=value; every argument here takes that form` };

        const name = argument.slice(0, separator);
        const value = argument.slice(separator + 1);

        if (!known.has(name))
            return { ok: false, problem: `unknown flag '${name}'; this accepts ${[...known].sort().join(", ")}` };

        if (values.has(name)) return { ok: false, problem: `'${name}' was given twice` };

        // An empty value is the shell having interpolated a variable that was never set, which is how a
        // domain becomes "" and the failure surfaces three steps later as a certificate for no name.
        if (value.length === 0) return { ok: false, problem: `'${name}' was given an empty value` };

        values.set(name, value);
    }

    for (const required of [FLAGS.domain, FLAGS.traffic, FLAGS.panelImage, FLAGS.root])
        if (!values.has(required)) return { ok: false, problem: `'${required}' is required` };

    const traffic = values.get(FLAGS.traffic)!;

    if (!(TRAFFIC_SHAPES as readonly string[]).includes(traffic))
        return { ok: false, problem: `'${traffic}' is not a traffic shape; one of ${TRAFFIC_SHAPES.join(", ")}` };

    const certificate = values.get(FLAGS.certificate);
    const key = values.get(FLAGS.key);

    // Refused here as well as further in, because the message can name the flag the operator typed.
    if ((certificate === undefined) !== (key === undefined))
        return {
            ok: false,
            problem: `${FLAGS.certificate} and ${FLAGS.key} go together; one without the other is a listener that starts and fails every handshake`,
        };

    return {
        ok: true,
        phase: {
            domain: values.get(FLAGS.domain)!,
            traffic: { kind: traffic } as TrafficShape,
            panelImage: values.get(FLAGS.panelImage)!,
            root: values.get(FLAGS.root)!,
            tls: certificate === undefined || key === undefined ? undefined : { certificatePath: certificate, keyPath: key },
            acmeEmail: values.get(FLAGS.acmeEmail),
        },
    };
}

/** The compose document and the two Traefik files, ready for a {@link ConfigStore} to write. */
export function bootstrapFiles(phase: BootstrapPhase): readonly GeneratedFile[] {
    const project = bootstrapProject(phase);

    return [{ path: COMPOSE_FILENAME, contents: project.document, mode: 0o644 }, ...project.files];
}
