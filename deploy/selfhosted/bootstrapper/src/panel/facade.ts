import { COMPOSE_PROJECT } from "../compose";
import { dockerCommand, dockerEngine, dockerStream, projectStatus } from "../docker";
import type { ServiceStatus } from "../model";
import type { ApplyOutcome, Setup } from "../setup";
import { createBackup, dockerExec, installStore, listBackups, type BackupOutcome, type BackupSummary } from "./backup";
import { edgeProbe, inspectCertificates, type CertificateReport } from "./certificate";
import { ARGON_CONTAINERS, type ControlOutcome, type Lifecycle, type LogOutcome } from "./containers";
import {
    Upgrades,
    currentVersion,
    historyIn,
    instanceIn,
    outcomeOf,
    previousVersion,
    type AppliedVersion,
    type UpgradePlan,
} from "./upgrade";

/**
 * The four panel modules, tied together into the six things the routes actually ask for.
 *
 * ## Why this exists rather than the routes calling the modules
 *
 * `server.ts` says of itself that its handlers do one thing each — map an outcome onto a status code —
 * and that a rule which lives there is a rule that cannot be tested without a socket. Wiring four
 * modules, three docker transports, a redactor and an install root is a rule. It lives here, where a
 * test can hand in fake ports, and the routes stay what they claim to be.
 *
 * ## Why nothing here throws
 *
 * Every part of the overview can be unavailable at once, and the page has to render anyway — an edge
 * that is down, a daemon that will not answer, a history that was never written. That is not the
 * exceptional case: it is precisely the state an instance is in when somebody opens the panel to find
 * out what is wrong. So each part is gathered independently and an absence is reported as itself rather
 * than as the failure of the whole page.
 *
 * ## Where the secrets are
 *
 * Nowhere in this file. The redactor arrives as a closure from {@link Setup.redactor}, which is the only
 * thing that leaves that object — see its comment for why the bundle itself does not.
 */

export interface PanelOverview {
    readonly domain?: string;
    readonly services: readonly ServiceStatus[];

    /**
     * The services the panel may act on, asked of the module rather than decided here.
     *
     * The page draws a stop button from this list and the route refuses independently, so the two agree
     * without either of them restating the rule. What is missing from it is the panel's own container:
     * stopping that is the operator switching off the thing they are using.
     */
    readonly controllable: readonly string[];

    readonly certificates: readonly CertificateReport[];
    readonly backups: readonly BackupSummary[];
    readonly version: { readonly current?: AppliedVersion; readonly previous?: AppliedVersion };
}

/**
 * Whether a version change may go ahead, and if not, whether that is a refusal or a question.
 *
 * The distinction is the whole point of the type. A `settled` refusal is a change that cannot work and
 * no amount of confirming makes it work. An `unproven` one is the panel saying it cannot see far enough
 * — the running version came from a moving tag, or nothing wrote down what was installed — and the
 * operator, who can see further, is allowed to say so.
 */
export type UpgradeVerdict =
    | { readonly ok: true }
    | { readonly ok: false; readonly standing: "settled" | "unproven"; readonly problem: string };

export interface Panel {
    overview(setup: Setup): Promise<PanelOverview>;
    logs(setup: Setup, service: string, tail?: string | number): Promise<LogOutcome>;
    control(setup: Setup, service: string, action: string): Promise<ControlOutcome>;
    /** No `setup`: nothing in a backup passes through a redactor. What it carries, it carries by name. */
    backup(): Promise<BackupOutcome>;

    /**
     * These three take the setup for one reason: the redactor.
     *
     * The history records a note, and a failed upgrade's note is whatever went wrong — which can be a
     * pull failure carrying a registry credential. `upgrade.ts` made its redactor required after a
     * reviewer found a secret written to disk verbatim; handing it an identity function here would put
     * that back while leaving the type happy.
     */
    plan(setup: Setup, version: string): Promise<UpgradePlan>;

    /**
     * Whether a version change may proceed, and on what footing.
     *
     * Not a sentence, and that is the correction: this returned only the text, which threw away the
     * `standing` the module works to produce. `settled` means the change cannot work — a downgrade
     * across a release line against a database a migration has already moved. `unproven` means the
     * panel cannot establish that it is safe, which is a different thing and, as upgrade.ts's own
     * `Standing` doc puts it, "the expected surface is a confirmation rather than a missing button".
     * Collapsing the two made every moving-tag install permanently unupgradable.
     */
    judgeUpgrade(setup: Setup, version: string): Promise<UpgradeVerdict>;

    record(setup: Setup, version: string, outcome: ApplyOutcome): Promise<void>;
}

/**
 * The panel over one install root, wired to the real daemon.
 *
 * The three docker transports are built once. Each opens its own connection per call, so sharing them
 * costs nothing and keeps the socket path in one place — a second copy of it is a panel that reports an
 * empty instance because somebody moved the mount.
 */
export function panelFor(root: string): Panel {
    const request = dockerEngine();
    const command = dockerCommand();
    const stream = dockerStream();

    const upgrades = (redact: (text: string) => string): Upgrades =>
        new Upgrades({
            history: historyIn(root),
            redact,

            // Wired, and it was not: without it `Upgrades` short-circuits every empty history to
            // "unrecorded" without looking, so a genuinely fresh root and a root that has been running
            // Argon since before this file existed got the same pessimistic refusal. The module's own
            // test for the benign path passed `installed: async () => false` — a path production never
            // took, which is why a green suite said nothing about it.
            installed: instanceIn(root),
        });

    return {
        async overview(setup) {
            const state = await setup.state();
            const answers = state.answers;
            const redact = setup.redactor();

            // Gathered together rather than in sequence: the certificate probe is a TLS handshake with a
            // timeout on it, and an edge that is down should not make the operator wait for the rest of
            // the page. `allSettled` because any of them may reject and none of them may take the page
            // down with it.
            const [services, certificates, backups, history] = await Promise.all([
                projectStatus(COMPOSE_PROJECT, request).catch(() => [] as ServiceStatus[]),

                answers.domain === undefined || answers.traffic === undefined || answers.voice === undefined
                    ? Promise.resolve([] as readonly CertificateReport[])
                    : inspectCertificates(
                          { ...answers, domain: answers.domain, traffic: answers.traffic, voice: answers.voice } as never,
                          edgeProbe(),
                      ).catch(() => [] as readonly CertificateReport[]),

                listBackups(installStore(root)).catch(() => [] as readonly BackupSummary[]),
                upgrades(redact).applied().catch(() => [] as readonly AppliedVersion[]),
            ]);

            return {
                domain: answers.domain,
                services,
                controllable: services
                    .map((service) => service.service)
                    .filter((name) => ARGON_CONTAINERS.controllable(name) !== undefined),
                certificates,
                backups,
                version: { current: currentVersion(history), previous: previousVersion(history) },
            };
        },

        logs(setup, service, tail) {
            // `undefined` rather than `NaN` for a tail that was not a number: the module clamps and
            // defaults, and handing it a NaN would make it decide that question from a value nobody
            // meant. See its `asTail`.
            const asked = tail === undefined || tail === "" ? undefined : Number(tail);

            return ARGON_CONTAINERS.readLogs(
                { service, tail: Number.isFinite(asked) ? asked : undefined },
                { request, stream, redact: setup.redactor() },
            );
        },

        control(setup, service, action) {
            // Cast at the boundary and checked immediately inside: `asLifecycle` is what stands between
            // a verb out of an HTTP path and the docker socket, and it runs before anything is resolved.
            return ARGON_CONTAINERS.control({ service, action: action as Lifecycle }, {
                request,
                command,
                redact: setup.redactor(),
            });
        },

        backup() {
            // Without the machine's keys, which is the module's default and its decision: the usual
            // reason to take one is to have the data, and the usual thing done with the file afterwards
            // is to move it off the machine. A panel that silently included them would make every copy
            // of every archive a copy of the instance's identity.
            return createBackup({
                engine: request,
                exec: dockerExec("/var/run/docker.sock"),
                store: installStore(root),
                now: () => new Date(),
                project: COMPOSE_PROJECT,
            });
        },

        plan(setup, version) {
            return upgrades(setup.redactor()).plan(version);
        },

        async judgeUpgrade(setup, version) {
            const plan = await upgrades(setup.redactor()).plan(version);

            // Both are carried: `problem` is the sentence written for the person reading it, `standing`
            // is what decides whether they are being told no or being asked to confirm. Returning only
            // the first is what made the two indistinguishable everywhere downstream.
            return plan.judgement.ok
                ? { ok: true }
                : { ok: false, standing: plan.judgement.standing, problem: plan.judgement.problem };
        },

        async record(setup, version, outcome) {
            await upgrades(setup.redactor()).record(version, outcomeOf(outcome));
        },
    };
}
