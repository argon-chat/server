<script setup lang="ts">
import { api } from "../api";
import { refreshOverview, store } from "../store";
import Note from "./Note.vue";

/**
 * The archives, and the sentence about where they are.
 *
 * The heading carries a button, which is why this is written out rather than built from `Card`: the
 * action belongs on the same line as the thing it acts on, and a card whose title is a title has
 * nowhere to put it.
 */

defineProps<{ backups: readonly { name: string; bytes: number }[] }>();

/**
 * Bytes as a size somebody can hold in their head.
 *
 * The guard is a runtime one on purpose. The shapes in api.ts are declarations about what the API
 * answers, not checks that it did — and "NaN GB" beside an archive name reads as a corrupt archive
 * rather than as a field that did not arrive.
 */
function size(bytes: number): string {
    if (typeof bytes !== "number") return "—";

    const units = ["B", "KB", "MB", "GB"];
    let value = bytes;
    let unit = 0;

    while (value >= 1024 && unit < units.length - 1) {
        value /= 1024;
        unit += 1;
    }

    return `${value < 10 && unit > 0 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}

async function take(): Promise<void> {
    store.busy = "backup";
    store.backupProblem = undefined;

    const response = await api.backup();

    store.busy = undefined;

    if (!response.ok)
        store.backupProblem = (response.body as { problem?: string } | undefined)?.problem ?? "The backup did not finish.";

    await refreshOverview();
}
</script>

<template>
  <section class="panel p-5 flex flex-col gap-3">
    <div class="flex items-center gap-3">
      <h2 class="text-sm font-bold text-text-primary flex-1">Backups</h2>
      <button
        class="s-btn s-btn--subtle s-btn--sm"
        :disabled="store.busy !== undefined"
        @click="take"
      >{{ store.busy === "backup" ? "Taking…" : "Take one now" }}</button>
    </div>

    <p v-if="backups.length === 0" class="text-xs text-text-muted">None taken yet.</p>
    <div v-else class="flex flex-col">
      <div
        v-for="archive in backups"
        :key="archive.name"
        class="flex items-center gap-3 py-1.5 border-b border-oled-border-subtle"
      >
        <span class="mono text-xs text-text-secondary flex-1 truncate">{{ archive.name }}</span>
        <span class="mono tnum text-xs text-text-muted">{{ size(archive.bytes) }}</span>
      </div>
    </div>

    <Note v-if="store.backupProblem" tone="danger">{{ store.backupProblem }}</Note>

    <!--
      Said on the page rather than in a document nobody opens: an archive of this instance carries what
      the instance runs on. Where it is copied to is a decision, not a detail.
    -->
    <p v-if="backups.length > 0" class="text-xs text-text-muted leading-relaxed">
      These live on this machine, which is the machine they are a backup of. Copy them somewhere else, and treat a copy as carrying whatever the archive carries.
    </p>
  </section>
</template>
