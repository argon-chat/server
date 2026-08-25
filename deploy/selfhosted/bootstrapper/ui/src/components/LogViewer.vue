<script lang="ts">
import { api } from "../api";
import { store } from "../store";

/**
 * Fetching a service's log.
 *
 * Exported rather than kept private because two buttons ask for it: Refresh, inside the viewer, and the
 * Logs button on a service row, which opens the viewer in the first place. The loader lives with the
 * thing that displays what it loads, so the row imports this rather than this importing the row.
 *
 * A refusal leaves `lines` undefined rather than empty, and the difference is the whole point — an empty
 * array is a service that has printed nothing, which is a fact about the service, and undefined is the
 * panel not having managed to ask.
 */
export async function loadLogs(service: string): Promise<void> {
    store.busy = "logs";

    const response = await api.logs(service);

    store.busy = undefined;
    store.logs = {
        service,
        lines: response.ok ? (response.body?.lines ?? []) : undefined,
        problem: response.ok ? undefined : (response.body?.problem ?? "Could not read the log."),
        truncated: response.ok ? response.body?.truncated === true : false,
    };
}
</script>

<script setup lang="ts">
import { computed } from "vue";
import Note from "./Note.vue";

/**
 * One service's output, on the page rather than in a terminal.
 *
 * `store` and `api` are reached from the block above rather than imported again here: the two script
 * blocks share this module's scope, and importing the same binding twice is a syntax error.
 */

const logs = computed(() => store.logs);
const busy = computed(() => store.busy);

function reload(): void {
    const open = logs.value;

    if (open !== undefined) void loadLogs(open.service);
}

function close(): void {
    store.logs = undefined;
}
</script>

<template>
  <section v-if="logs" class="panel p-5 flex flex-col gap-3">
    <div class="flex items-center gap-3">
      <h2 class="text-sm font-bold text-text-primary flex-1 mono">{{ logs?.service }}</h2>
      <button
        class="s-btn s-btn--ghost s-btn--sm"
        :disabled="busy !== undefined"
        @click="reload"
      >{{ busy === "logs" ? "…" : "Refresh" }}</button>
      <button class="s-btn s-btn--ghost s-btn--sm" @click="close">Close</button>
    </div>

    <!-- Said plainly, because a log silently cut at the front is a log that lies about when a problem started. -->
    <p v-if="logs?.truncated" class="text-xs text-text-muted">
      Older lines were dropped to keep this readable. What is below is the end of the log, not all of it.
    </p>

    <Note v-if="logs?.problem" tone="danger">{{ logs?.problem }}</Note>

    <div v-if="logs?.lines === undefined && !logs?.problem" class="skeleton h-32 rounded-lg"></div>
    <p v-else-if="logs?.lines?.length === 0" class="text-xs text-text-muted">This service has printed nothing.</p>

    <!--
      stderr in the danger colour. On a service that is failing to start — which is when anybody opens
      this — the two streams are saying different things, and a wall of one colour hides which is which.
    -->
    <pre
      v-else-if="logs?.lines"
      class="mono text-xs bg-oled-black border border-oled-border-subtle rounded-lg p-3 overflow-auto"
      style="max-height: 24rem; white-space: pre-wrap; word-break: break-word"
    ><div
      v-for="(line, index) in logs?.lines ?? []"
      :key="index"
      :class="line.stream === 'stderr' ? 'text-danger' : 'text-text-secondary'"
    >{{ line.text }}</div></pre>
  </section>
</template>
