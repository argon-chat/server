<script setup lang="ts">
import { computed } from "vue";
import { api, type SetupState } from "../api";
import { refresh, store } from "../store";
import Card from "./Card.vue";
import Field from "./Field.vue";
import Icon from "./Icon.vue";

/**
 * The credential that outlives setup.
 *
 * Not one of the answers — it is not Argon's configuration, it is this panel's own door — so it has its
 * own route and shows up in the state as a single boolean. It is required all the same: finishing the
 * install retires the bootstrap code, and retiring it with nothing behind it would leave a panel that
 * starts and stops containers and that nobody can ever sign into again. The apply bar names it
 * alongside the answers for that reason, and the server refuses an install without it regardless.
 */
const props = defineProps<{ state: SetupState }>();

const isSet = computed(() => props.state.panelPassword === true);

/**
 * Never the stored value: the server keeps a hash and cannot return one. This is only ever what is
 * being typed now, which is also why replacing it needs no old password — holding a session already
 * required knowing one.
 */
const password = computed({
    get: () => store.draft.panelPassword ?? "",
    set: (typed: string) => {
        store.draft.panelPassword = typed;
    },
});

const ready = computed(() => store.busy === undefined && password.value.trim().length > 0);

async function save(): Promise<void> {
    store.busy = "password";

    // Sent as typed rather than trimmed. Whitespace at either end is as much of a password as any other
    // character, and quietly removing it would set one thing and tell the operator another.
    const response = await api.setPanelPassword(password.value);

    store.busy = undefined;

    if (response.ok) {
        store.rejections.panelPassword = undefined;
        store.draft.panelPassword = "";

        // Re-read rather than assumed: `panelPassword` in the state is what the apply bar gates on, and
        // it is the server's to say.
        await refresh();

        return;
    }

    store.rejections.panelPassword =
        (response.body as { problem?: string } | undefined)?.problem ?? "That password was not accepted.";
}
</script>

<template>
  <Card
    title="Panel access"
    description="The password you will use here from now on. The code from the terminal stops working once the install finishes — that is the point of it."
  >
    <Field
      :label="isSet ? 'Replace the password' : 'Password'"
      :error="store.rejections.panelPassword"
      hint="At least 12 characters. Length is the only part of a password that reliably costs an attacker anything."
    >
      <input
        v-model="password"
        class="s-input"
        type="password"
        autocomplete="new-password"
        aria-label="New panel password"
        :placeholder="isSet ? 'Leave empty to keep the current one' : ''"
      />
    </Field>

    <div v-if="isSet" class="flex items-center gap-2 text-xs text-text-muted">
      <span class="text-success"><Icon name="check" :size="13" /></span>
      <span>A password is set. This panel will still open after the install.</span>
    </div>

    <div class="flex justify-end">
      <!--
        The visible label changes with whether a password exists; the aria-label does not, so a test can
        find the button without depending on which of the two it is.
      -->
      <button class="s-btn s-btn--subtle s-btn--sm" aria-label="Save panel password" :disabled="!ready" @click="save">
        {{ isSet ? "Replace" : "Set password" }}
      </button>
    </div>
  </Card>
</template>
