<script setup lang="ts">
import { computed } from "vue";
import { store } from "../store";
import Icon from "./Icon.vue";
import Note from "./Note.vue";
import Output from "./Output.vue";
import ServiceList from "./ServiceList.vue";

/**
 * Containers are up and the instance is not.
 *
 * The one outcome where trying again is not free, so this screen deliberately offers no way to try
 * again. Everywhere else on this page a button that writes something is standing in front of a machine
 * nothing has happened to yet; here the configuration is on disk and containers are running against it,
 * and whatever the operator does next happens to a live system.
 *
 * This is what the first version of the page got wrong, and it is the worst mistake available to it:
 * an unhandled stage fell through to the wizard, which put an Install button under a machine with live
 * containers on it. What belongs here instead is what is running and what was printed, because the next
 * move is to go and look at the system rather than to press anything.
 */

const problem = computed(() => store.state?.problem);
const services = computed(() => store.state?.services ?? []);
const log = computed(() => (store.state?.progress ?? []).join("\n"));

/**
 * Given as a line to copy rather than as a button that runs it. The panel can read a service's log
 * itself once the instance is up, but this screen is the case where it is not — and a shell on the
 * machine is the thing that still works when the panel's own view of it is the thing in doubt.
 */
const LOGS_COMMAND = "docker compose -p argon logs <service>";
</script>

<template>
  <div class="max-w-3xl mx-auto px-5 py-10 sm:py-14 flex flex-col gap-6">
    <header class="flex flex-col gap-1">
      <div class="section-label">Argon</div>
      <h1 class="text-2xl font-black tracking-tight text-text-primary flex items-center gap-2">
        <span class="text-danger"><Icon name="alert" :size="20" /></span>
        The instance did not come up
      </h1>
      <p class="text-sm text-text-muted leading-relaxed">
        Containers were created and are running against the configuration that was just written. This is
        not a clean retry: whatever happens next happens to a live system.
      </p>
    </header>

    <Note v-if="problem" tone="danger">{{ problem }}</Note>

    <div v-if="services.length > 0" class="panel p-5 flex flex-col gap-2">
      <h2 class="text-sm font-bold text-text-primary">Services</h2>
      <ServiceList :services="services" />
    </div>

    <Output v-if="log.length > 0" :text="log" />

    <div class="panel p-5 flex flex-col gap-2">
      <h2 class="text-sm font-bold text-text-primary">Where to look</h2>
      <p class="text-xs text-text-muted leading-relaxed">
        The services above name which one is unhappy. Its logs are the only thing that says why:
      </p>
      <code class="mono text-xs text-text-secondary">{{ LOGS_COMMAND }}</code>
    </div>
  </div>
</template>
