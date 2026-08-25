<script setup lang="ts">
import type { ServiceStatus } from "../api";

/**
 * What the daemon says about each container, and nothing else.
 *
 * Deliberately without a surface or a heading of its own. The same list is drawn while the install is
 * running, on the screen that says it worked, on the screen that says it did not, and in the panel —
 * and the panel's section carries buttons and a problem line around it. A component that brought its
 * own card would put a card inside a card there.
 *
 * Read-only, and that is the point rather than an omission: three of the four places this appears are
 * watching something that is mid-flight, where a Stop button next to a container that is still starting
 * is an invitation to break the thing being watched. Acting on a service is the panel's alone, and the
 * panel draws its own controls beside this list.
 */
defineProps<{ services: readonly ServiceStatus[] }>();

/**
 * Settled, in the only sense this screen cares about.
 *
 * Two shapes count. Running while either passing its health check or not having one is the obvious one.
 * The other is a container that has exited cleanly — the migration and seed steps are one-shots that do
 * their job and leave, and drawing a finished one as pending leaves every install looking permanently
 * half-done with nothing wrong with it.
 */
function settled(service: ServiceStatus): boolean {
    if (service.state === "running") return (service.health ?? "healthy") === "healthy";

    return service.state === "exited" && service.exitCode === 0;
}
</script>

<template>
  <div class="flex flex-col gap-1">
    <div v-for="service in services" :key="service.service" class="flex items-center gap-3 py-1">
      <span class="signal" :class="settled(service) ? 'signal--resolved' : 'signal--pending'">
        <span class="signal-dot" />
      </span>
      <span class="mono text-xs text-text-secondary flex-1 truncate">{{ service.service }}</span>
      <span class="mono tnum text-xs text-text-muted">
        {{ service.health ? `${service.state} · ${service.health}` : service.state }}
      </span>
    </div>
  </div>
</template>
