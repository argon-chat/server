<script setup lang="ts">
import { computed } from "vue";
import type { SetupState } from "../api";
import { store, submit } from "../store";
import Card from "./Card.vue";
import Field from "./Field.vue";

/**
 * The name this instance answers to.
 *
 * The installer already asked for it in the terminal and the edge is already configured from it; this
 * card is where the panel is told, because until it is, `missing` names `domain` forever.
 */
const props = defineProps<{ state: SetupState }>();

/**
 * What is in the box: what is being typed if anything is, otherwise the answer the server holds.
 *
 * The draft wins even when it is the empty string, and that is the point of having one — clearing the
 * field has to leave it cleared rather than snapping the accepted answer back under the cursor, and a
 * value the server rejected has to stay put so the operator can see what they wrote.
 */
const domain = computed({
    get: () => store.draft.domain ?? props.state.answers.domain ?? "",
    set: (typed: string) => {
        store.draft.domain = typed;
    },
});

/** First time through this is the answer; afterwards one is already held and this changes it. */
const label = computed(() => (props.state.missing.includes("domain") ? "Save" : "Update"));

const ready = computed(() => store.busy === undefined && domain.value.trim().length > 0);
</script>

<template>
  <Card
    title="Domain"
    description="The name this instance answers to. The installer already asked for it in the terminal; this is where it is confirmed."
  >
    <Field label="Hostname" :error="store.rejections.domain">
      <input
        v-model="domain"
        class="s-input mono"
        placeholder="chat.example.org"
        autocomplete="off"
        spellcheck="false"
        aria-label="Hostname"
      />
    </Field>

    <div class="flex justify-end">
      <button class="s-btn s-btn--subtle s-btn--sm" :disabled="!ready" @click="submit({ domain: domain.trim() })">
        {{ label }}
      </button>
    </div>
  </Card>
</template>
