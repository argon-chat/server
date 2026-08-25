<script setup lang="ts">
import { computed } from "vue";
import type { SetupState } from "../api";
import { store, submit } from "../store";
import Card from "./Card.vue";

/**
 * Whether this instance carries calls.
 *
 * One answer with one box, and it is here rather than folded into the roles because it decides more
 * than a role: the media server, the ports the edge opens, and whether the traffic card has to ask for
 * a second hostname. Turning it on later works, but not without a restart, which is why the card says
 * so instead of letting it look like a preference.
 */
const props = defineProps<{ state: SetupState }>();

const on = computed(() => store.draft.voice ?? props.state.answers.voice ?? false);

function set(next: boolean): void {
    // Into the draft as well as onto the wire: the traffic card reads it to decide whether to ask for a
    // voice hostname, and it should not have to wait for the round trip to do that.
    store.draft.voice = next;
    void submit({ voice: next });
}
</script>

<template>
  <Card title="Voice" description="Runs the media server for calls. It can be turned on later, but not without a restart.">
    <label class="row-link flex items-center gap-3 px-3 py-2.5 rounded-lg border border-transparent cursor-pointer">
      <input
        type="checkbox"
        :checked="on"
        :disabled="store.busy !== undefined"
        @change="set(($event.target as HTMLInputElement).checked)"
      />
      <span class="text-sm text-text-primary">Carry voice and video calls</span>
    </label>
  </Card>
</template>
