<script setup lang="ts">
import { computed } from "vue";
import { store } from "../store";
import Icon from "./Icon.vue";
import ServiceList from "./ServiceList.vue";

/**
 * The install worked.
 *
 * Shown for the moment the overview takes to arrive and no longer. An earlier version of this page kept
 * it as the terminal screen of an install, which meant it was still there the next day: an operator who
 * came back found the last frame of a film that had ended, with no way through to the panel that
 * administers the instance. `App.vue` hands over to `Panel` the moment the overview lands.
 */

const address = computed(() => store.state?.panel);
const services = computed(() => store.state?.services ?? []);
const written = computed(() => store.state?.written ?? []);

/**
 * The permission bits, as the four octal digits `chmod` takes.
 *
 * Shown at all because several of these files are the reason the install had to ask for secrets — the
 * environment files carry the database password and the object-storage key — and `0600` is the whole
 * of what stands between them and every other account on this machine. An operator who wants to check
 * that has to be able to compare it against something, and `rw-------` is not what they would type.
 */
function mode(bits: number): string {
    return bits.toString(8).padStart(4, "0");
}
</script>

<template>
  <div class="max-w-3xl mx-auto px-5 py-10 sm:py-14 flex flex-col gap-6">
    <header class="flex flex-col gap-1">
      <div class="section-label">Argon</div>
      <h1 class="text-2xl font-black tracking-tight text-text-primary flex items-center gap-2">
        <span class="text-success"><Icon name="check" :size="22" /></span>
        This instance is up
      </h1>
    </header>

    <div v-if="address" class="panel p-5 flex flex-col gap-2">
      <p class="text-sm text-text-secondary leading-relaxed">{{ address.note }}</p>
      <a class="s-btn s-btn--primary self-start" :href="address.url">
        Open the panel
        <Icon name="external" :size="14" />
      </a>
    </div>

    <div v-if="services.length > 0" class="panel p-5 flex flex-col gap-2">
      <h2 class="text-sm font-bold text-text-primary">Services</h2>
      <ServiceList :services="services" />
    </div>

    <div v-if="written.length > 0" class="panel p-5 flex flex-col gap-2">
      <h2 class="text-sm font-bold text-text-primary">Written</h2>
      <div class="flex flex-col gap-1">
        <div v-for="file in written" :key="file.path" class="flex items-center gap-3">
          <span class="mono text-xs text-text-secondary flex-1 truncate">{{ file.path }}</span>
          <span class="mono tnum text-xs text-text-faint">{{ mode(file.mode) }}</span>
        </div>
      </div>
    </div>
  </div>
</template>
