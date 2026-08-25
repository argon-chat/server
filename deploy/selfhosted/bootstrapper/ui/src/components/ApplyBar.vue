<script setup lang="ts">
import { computed } from "vue";
import { api, type SetupState } from "../api";
import { poll, refresh, stopPolling, store } from "../store";
import Icon from "./Icon.vue";

/**
 * The line the install crosses.
 *
 * Everything above this is reversible — the machine has not been touched. Below it, files are written
 * and containers start. The button says so rather than saying "Next".
 */
const props = defineProps<{ state: SetupState }>();

/**
 * Two gates, and the second is not one of the answers. The server refuses an install with no panel
 * password for the same reason this does; this is so the button says why before it is pressed rather
 * than after.
 */
const ready = computed(() => props.state.stage === "ready" && props.state.panelPassword === true);

const outstanding = computed(() => [
    ...(props.state.missing ?? []),
    ...(props.state.panelPassword === true ? [] : ["a panel password"]),
]);

const explanation = computed(() => {
    if (ready.value)
        return "This writes the configuration, pulls the images and starts the instance. Until now nothing on this machine has changed.";

    if (outstanding.value.length > 0) return `Still to answer: ${outstanding.value.join(", ")}.`;

    // Every answer is in and the stage is still not `ready`, which means something else is refusing — a
    // validation report or a `problem` above says what. Naming an empty list here printed
    // "Still to answer: ." and sent the operator looking for a field that did not exist.
    return "Something above has to be settled before this can run.";
});

async function install(): Promise<void> {
    store.busy = "apply";

    // Moved here rather than waited for. `api.apply()` does not answer until the whole install has
    // finished — minutes — and the operator has to see the press do something; this is also what hands
    // the screen over to the progress view, since App.vue routes on the stage.
    if (store.state !== undefined) store.state = { ...store.state, stage: "applying" };

    // Polled beside the request that is still open, so the log moves, a second tab sees the same thing,
    // and a browser that gives up waiting takes the response with it but not the install.
    poll();

    const response = await api.apply();

    store.busy = undefined;
    stopPolling();

    // Every outcome, good or bad, is a whole state — so there is nothing to merge and nothing to decide
    // here. The failure screens read `stage` and `problem` like every other screen does.
    if (response.body !== undefined) store.state = response.body;

    await refresh();
}
</script>

<template>
  <div class="panel p-5 flex flex-col gap-3">
    <p class="text-xs text-text-muted leading-relaxed">{{ explanation }}</p>

    <div class="flex justify-end">
      <button class="s-btn s-btn--primary" :disabled="!ready || store.busy !== undefined" @click="install">
        Install
        <Icon name="arrow" />
      </button>
    </div>
  </div>
</template>
