<script setup lang="ts">
import { computed } from "vue";
import { store } from "../store";
import Note from "./Note.vue";

/**
 * Nothing was written and nothing started.
 *
 * Something on disk makes going on unsafe — a half-written install from another tool, a compose project
 * already using the name, a directory the panel cannot write to — and the server refused before it
 * touched anything. There is no button here for the same reason there is none on the degraded screen,
 * but for the opposite cause: the obstacle is off this page entirely, and a retry that changes nothing
 * about the machine would refuse again for the same reason.
 *
 * Three arrivals, not one. `App.vue` routes an unavailable panel and a state that has not landed yet
 * here as well, because both are "there is no wizard to draw" — but they are not the same sentence, and
 * telling an operator that their install cannot continue while the first request is still in flight
 * would be a lie that sends them looking for a fault that does not exist.
 */

const state = computed(() => store.state);
</script>

<template>
  <div class="max-w-3xl mx-auto px-5 py-10 sm:py-14 flex flex-col gap-6">
    <header class="flex flex-col gap-1">
      <div class="section-label">Argon</div>
      <h1 class="text-2xl font-black tracking-tight text-text-primary">Set up this instance</h1>
    </header>

    <div v-if="state === undefined" class="skeleton h-40 rounded-xl" />

    <Note v-else-if="state.stage === 'unavailable'" tone="danger">
      {{ state.problem ?? "This panel has no setup to run." }}
    </Note>

    <Note v-else tone="danger">
      {{ state.problem ?? "This install cannot continue, and the panel did not say why." }}
    </Note>
  </div>
</template>
