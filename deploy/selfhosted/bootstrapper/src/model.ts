/**
 * What the bootstrapper knows about the server it is installing, and what the operator told it.
 *
 * These types are the seam between three things that are otherwise independent: the part that asks the
 * Argon image what it needs, the part that asks the operator, and the part that writes files. Keeping the
 * shape here rather than letting each grow its own means the wizard can be rearranged without any of them
 * learning about the others.
 *
 * Nothing here encodes what a feature reads. That knowledge lives in the server binary and is asked for —
 * see the design document's §7 — because a second copy of it would drift, and drift silently, into
 * configuration that validates against a schema of sections that has moved.
 */

/** A role, as `--roles` reports it. */
export interface RoleSummary {
    readonly id: string;
    readonly kind: "silo" | "client";
    readonly grains: number;
    readonly features: number;
    readonly description: string;
}

/** A feature a role enables, and the configuration sections it reads. */
export interface FeatureSummary {
    readonly name: string;
    readonly sections: readonly string[];
}

/** A role in detail, as `--explain <role>` reports it. */
export interface RoleDetail {
    readonly id: string;
    readonly features: readonly FeatureSummary[];
}

/** A deployment topology, as `--roles` reports it. */
export interface TopologySummary {
    readonly name: string;
    readonly roles: readonly string[];
}

/** Everything one interrogation of the server image produced. */
export interface ServerCapabilities {
    readonly version: string;
    readonly roles: readonly RoleSummary[];
    readonly topologies: readonly TopologySummary[];
}

/** Where the operator's storage lives. */
export type StorageChoice =
    | { readonly kind: "local" }
    | { readonly kind: "s3"; readonly endpoint: string; readonly bucket: string; readonly region?: string };

/** How traffic reaches the instance, decided by the install script before this process started. */
export type TrafficShape =
    | { readonly kind: "own-certificate" }
    | { readonly kind: "cloudflare-proxied"; readonly voiceHost?: string }
    | { readonly kind: "cloudflare-tunnel" }
    | { readonly kind: "lets-encrypt" };

/** What the operator answered. */
export interface Answers {
    readonly domain: string;
    readonly serverVersion: string;
    readonly roles: readonly string[];
    readonly storage: StorageChoice;
    readonly traffic: TrafficShape;
    readonly voice: boolean;
}

/**
 * One file the generator produces.
 *
 * `mode` is carried rather than assumed because the difference between the two kinds of file this writes
 * is exactly the mode: settings are readable, the secrets file is `0o600`. A generator that returned
 * paths and contents and let the caller decide would put that decision somewhere it can be forgotten.
 */
export interface GeneratedFile {
    readonly path: string;
    readonly contents: string;
    readonly mode: number;
}

/**
 * What docker says about one service's container.
 *
 * Here rather than in `setup.ts` because two modules need it and neither should import the other:
 * `setup.ts` decides what "ready" means from it, and `docker.ts` is where it comes from.
 */
export interface ServiceStatus {
    readonly service: string;

    /** `running`, `exited`, `restarting`, `created`, `paused`, `dead`. */
    readonly state: string;

    /** Docker's health, for the services that declare a healthcheck. Empty or absent for the rest. */
    readonly health?: string;

    /** Set when the container has exited. `argon-storage-init` is meant to, with a zero. */
    readonly exitCode?: number;
}
