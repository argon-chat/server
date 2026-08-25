<script setup lang="ts">
import { computed } from "vue";
import { api, type ServiceStatus } from "../api";
import { refreshOverview, store } from "../store";
import Backups from "./Backups.vue";
import Card from "./Card.vue";
import Certificates from "./Certificates.vue";
import LogViewer, { loadLogs } from "./LogViewer.vue";
import Note from "./Note.vue";
import Versions, { when } from "./Versions.vue";

/**
 * What this page is for the rest of the instance's life.
 *
 * Everything on it comes from `overview`, a second state object rather than more fields on the wizard's.
 * That split is not tidiness: each of these costs the server real work — a TLS handshake to read the
 * certificate, a directory walk for the backups, the daemon for the services — and folding them into
 * `/api/state`, which is polled every two seconds during an install, would make the install pay for all
 * of it.
 *
 * The service rows are written here rather than taken from `ServiceList.vue` because these carry
 * buttons. That component is the read-only list the install screens show, where there is nothing yet to
 * start or stop; teaching it lifecycle actions would put a Stop button on a screen where stopping is not
 * a thing anybody can mean.
 */

const services = computed<readonly ServiceStatus[]>(() => store.overview?.services ?? []);

/**
 * Which services may be acted on, decided by the module rather than guessed at here.
 *
 * The panel's own container is not in this set: stopping it is the operator switching off the thing they
 * are using, with no way back short of ssh. `controllable()` in panel/containers.ts refuses it and the
 * route enforces that refusal — a name compared in this file would be a second copy of that decision,
 * and the copy is the one that goes stale.
 */
const controllable = computed(() => new Set(store.overview?.controllable ?? []));

const domain = computed(() => store.overview?.domain ?? store.state?.answers?.domain ?? "This instance");

const running = computed(() => {
    const current = store.overview?.version?.current;

    return current === undefined ? undefined : `Running ${current.version}, installed ${when(current.at)}.`;
});

/**
 * Nothing to worry about, which is two different shapes.
 *
 * A long-running service is settled when it is up and its health check agrees. A one-shot — a migration,
 * a seeding job — is settled when it has *left*, with status zero. Reading only the first would leave a
 * finished migration sitting under a pending dot forever, which reads as a stuck install.
 */
function settled(service: ServiceStatus): boolean {
    return (
        (service.state === "running" && (service.health ?? "healthy") === "healthy") ||
        (service.state === "exited" && service.exitCode === 0)
    );
}

function status(service: ServiceStatus): string {
    return service.health ? `${service.state} · ${service.health}` : service.state;
}

/** Which row's button is waiting on the daemon. `busy` is global, so the row has to ask about itself. */
function acting(service: string): boolean {
    return store.busy === `service:${service}`;
}

function openLogs(service: string): void {
    // Set to the bare service first so the viewer opens on a skeleton rather than on the previous
    // service's output while this one is fetched. Two services' logs under one heading is worse than a
    // moment of nothing, because nobody re-reads a heading they have already read.
    store.logs = { service };
    void loadLogs(service);
}

async function control(service: string, action: "restart" | "stop" | "start"): Promise<void> {
    store.busy = `service:${service}`;
    store.serviceProblem = undefined;

    // Stopping waits for the container to leave of its own accord before it is killed, and the daemon
    // does not answer until it has — so this can sit for half a minute. The button says so by staying
    // busy rather than by pretending it is done.
    const response = await api.control(service, action);

    store.busy = undefined;

    // The control routes answer nothing on success and a `problem` sentence on refusal, so the body is
    // typed as nothing and the sentence is read out of it by hand.
    if (!response.ok)
        store.serviceProblem =
            (response.body as { problem?: string } | undefined)?.problem ?? `Could not ${action} ${service}.`;

    await refreshOverview();
}
</script>

<template>
  <div class="max-w-3xl mx-auto px-5 py-10 sm:py-14 flex flex-col gap-6">
    <header class="flex flex-col gap-1">
      <div class="section-label">Argon</div>
      <h1 class="text-2xl font-black tracking-tight text-text-primary flex items-center gap-2">
        <span class="signal signal--resolved"><span class="signal-dot"></span></span>
        {{ domain }}
      </h1>
      <p v-if="running" class="text-sm text-text-muted">{{ running }}</p>
    </header>

    <Card title="Services">
      <div class="flex flex-col">
        <!--
          Named, so a row can be found by which service it is rather than by the text it happens to
          contain. Selecting by text matches every ancestor that contains it too, which is how a test
          ends up asserting about the whole page.
        -->
        <div
          v-for="service in services"
          :key="service.service"
          :data-service="service.service"
          class="flex items-center gap-3 py-2 border-b border-oled-border-subtle"
        >
          <span class="signal" :class="settled(service) ? 'signal--resolved' : 'signal--pending'">
            <span class="signal-dot"></span>
          </span>
          <span class="mono text-sm text-text-primary flex-1 truncate">{{ service.service }}</span>
          <span class="mono tnum text-xs text-text-muted">{{ status(service) }}</span>

          <!--
            Offered on every row, the panel's own included. What that row is refused is lifecycle, not
            looking: an operator whose panel is misbehaving still needs to read why.
          -->
          <button class="s-btn s-btn--ghost s-btn--sm" @click="openLogs(service.service)">Logs</button>

          <!-- Drawn only where acting is possible; see `controllable` above for whose decision that is. -->
          <template v-if="controllable.has(service.service)">
            <button
              class="s-btn s-btn--subtle s-btn--sm"
              :disabled="store.busy !== undefined"
              @click="control(service.service, 'restart')"
            >{{ acting(service.service) ? "…" : "Restart" }}</button>
            <button
              class="s-btn s-btn--ghost s-btn--sm"
              :disabled="store.busy !== undefined"
              @click="control(service.service, service.state === 'running' ? 'stop' : 'start')"
            >{{ service.state === "running" ? "Stop" : "Start" }}</button>
          </template>
        </div>
      </div>

      <Note v-if="store.serviceProblem" tone="danger">{{ store.serviceProblem }}</Note>
    </Card>

    <Certificates :reports="store.overview?.certificates ?? []" />
    <Versions :version="store.overview?.version ?? {}" />
    <Backups :backups="store.overview?.backups ?? []" />
    <LogViewer v-if="store.logs" />
  </div>
</template>
