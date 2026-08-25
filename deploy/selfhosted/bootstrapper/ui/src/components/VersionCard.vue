<script setup lang="ts">
import { computed } from "vue";
import { api, type SetupState } from "../api";
import { store, submit } from "../store";
import Card from "./Card.vue";
import Field from "./Field.vue";

/**
 * The version, and the minute that follows it.
 *
 * Naming a version is not answering a question. It starts a container and asks the Argon image what it
 * can run, and nothing after this card can be drawn until that answer comes back, because the roles are
 * the image's to list rather than this file's. Hence the wait being a state on screen with an
 * explanation under it rather than a spinner on a button: a button that has been thinking for fifty
 * seconds reads as a page that has died.
 */
const props = defineProps<{ state: SetupState }>();

const asking = computed(() => store.busy === "interrogate");

const version = computed({
    get: () => store.draft.serverVersion ?? props.state.answers.serverVersion ?? "",
    set: (typed: string) => {
        store.draft.serverVersion = typed;
    },
});

const ready = computed(() => store.busy === undefined && version.value.trim().length > 0);

/**
 * Answer, then interrogate — in that order, and the second only if the first was accepted.
 *
 * Interrogating a reference the server has already refused would spend a minute starting a container
 * for a string that is not an image reference, and end with a failure that says nothing the rejection
 * under the field did not already say.
 */
async function askTheImage(): Promise<void> {
    await submit({ serverVersion: version.value.trim() });

    if (store.rejections.serverVersion !== undefined) return;

    store.busy = "interrogate";

    const response = await api.interrogate();

    store.busy = undefined;

    // A refusal here is not a rejected field — the answer was accepted, the image was not reachable —
    // so it goes where the page shows things that are wrong with the machine rather than under the box.
    if (response.ok && response.body !== undefined) store.state = response.body;
    else if (store.state !== undefined)
        store.state = { ...store.state, problem: response.body?.problem ?? "The image could not be reached." };
}
</script>

<template>
  <Card title="Version" description="Which build of Argon to install. A pinned reference — a tag or a digest — works too.">
    <Field label="Version or image reference" :error="store.rejections.serverVersion">
      <input
        v-model="version"
        class="s-input mono"
        placeholder="1.4.0"
        autocomplete="off"
        spellcheck="false"
        :disabled="asking"
        aria-label="Version or image reference"
      />
    </Field>

    <div v-if="state.image" class="flex flex-wrap items-center gap-2 text-xs">
      <span class="s-badge s-badge--resolved">{{ state.image.version?.value ?? "unknown" }}</span>
      <span class="text-text-muted mono">{{ state.image.reference }}</span>
      <span class="text-text-muted">{{ state.image.roles.length }} roles</span>
    </div>

    <div
      v-if="asking"
      class="flex items-start gap-3 rounded-lg border border-oled-border-subtle bg-oled-surface px-4 py-3"
    >
      <span class="signal signal--pending"><span class="signal-dot" /></span>
      <div class="flex flex-col gap-0.5">
        <p class="text-sm text-text-secondary">Asking the image what it can run</p>
        <p class="text-xs text-text-muted">It is started once and interrogated. Up to a minute, and only this once.</p>
      </div>
    </div>
    <div v-else class="flex justify-end">
      <button class="s-btn s-btn--subtle s-btn--sm" :disabled="!ready" @click="askTheImage">
        {{ state.image ? "Ask again" : "Continue" }}
      </button>
    </div>
  </Card>
</template>
