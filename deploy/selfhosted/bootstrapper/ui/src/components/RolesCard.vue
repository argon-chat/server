<script setup lang="ts">
import { computed } from "vue";
import type { SetupState } from "../api";
import { store, submit } from "../store";
import Card from "./Card.vue";

/**
 * Roles, with the required ones shown as required rather than as pre-ticked choices.
 *
 * Which is which comes from the server (`state.policy`) rather than from a list in this file. The
 * generator decides it, and a second copy here would eventually offer a role it refuses — the operator
 * would tick it, the save would come back rejected, and nothing on screen would explain why a box that
 * was offered was not allowed.
 *
 * Drawn whether or not there is an image. The card used to be conditional on one, which meant that in
 * the ordinary case — a fresh install, before the version has been settled — the apply bar said `roles`
 * was outstanding and the page offered nowhere to answer it.
 */
const props = defineProps<{ state: SetupState }>();

const policy = computed(() => props.state.policy ?? { required: [], optional: [], refused: [] });

const required = computed(() => new Set(policy.value.required));

/**
 * What is ticked: the draft if the operator has touched anything, otherwise what the server holds,
 * otherwise the required set — which is what an instance is with nothing added to it.
 */
const chosen = computed(() => new Set(store.draft.roles ?? props.state.answers.roles ?? policy.value.required));

/**
 * Refused roles are not offered at all rather than offered and rejected on save. A box that cannot be
 * ticked without being told no is a question the page had no business asking.
 */
const offered = computed(() => {
    const refused = new Set(policy.value.refused);

    return (props.state.image?.roles ?? []).filter((role) => !refused.has(role.id));
});

function toggle(id: string, on: boolean): void {
    const next = new Set(chosen.value);

    if (on) next.add(id);
    else next.delete(id);

    store.draft.roles = [...next];
    void submit({ roles: [...next] });
}
</script>

<template>
  <Card title="Roles" description="Which parts of the server run. Five are what an instance is; the rest are yours to choose.">
    <p v-if="!state.image" class="text-xs text-text-muted leading-relaxed">
      Nothing to choose here yet. Which roles exist is the image's to say rather than this panel's, and no
      image has been asked — settle the version above and they appear here.
    </p>

    <div v-else class="flex flex-col gap-1.5">
      <label
        v-for="role in offered"
        :key="role.id"
        class="row-link flex items-start gap-3 px-3 py-2.5 rounded-lg border border-transparent"
        :class="required.has(role.id) ? '' : 'cursor-pointer'"
      >
        <input
          type="checkbox"
          class="mt-1"
          :checked="required.has(role.id) || chosen.has(role.id)"
          :disabled="required.has(role.id) || store.busy !== undefined"
          @change="toggle(role.id, ($event.target as HTMLInputElement).checked)"
        />
        <div class="flex flex-col gap-0.5 min-w-0">
          <div class="flex items-center gap-2">
            <span class="mono text-sm text-text-primary">{{ role.id }}</span>
            <span v-if="required.has(role.id)" class="s-badge s-badge--closed">required</span>
            <span class="text-xs text-text-muted">{{ role.kind }}</span>
          </div>
          <span class="text-xs text-text-muted leading-relaxed">{{ role.description }}</span>
        </div>
      </label>
    </div>

    <p v-if="store.rejections.roles" class="text-xs text-danger">{{ store.rejections.roles }}</p>
  </Card>
</template>
