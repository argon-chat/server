<script lang="ts">
/**
 * A timestamp as something a person reads.
 *
 * Absolute, not "3 days ago". Whoever is reading this is about to compare it with a line in
 * `docker compose logs`, and a relative stamp cannot be lined up against one.
 *
 * Exported because the panel's header says the same thing about the version that is running. One
 * formatter rather than two, so the header and this card cannot drift into disagreeing about what a
 * time looks like.
 */
export function when(iso: string | undefined): string {
    if (typeof iso !== "string") return "at an unknown time";

    const parsed = new Date(iso);

    return Number.isNaN(parsed.getTime()) ? "at an unknown time" : parsed.toISOString().replace("T", " ").slice(0, 16);
}
</script>

<script setup lang="ts">
import { computed } from "vue";
import { api, type AppliedVersion } from "../api";
import { refresh, refreshOverview, store } from "../store";
import Card from "./Card.vue";
import Field from "./Field.vue";
import Icon from "./Icon.vue";
import Note from "./Note.vue";

/**
 * What an upgrade would do, said before it does it.
 *
 * The refusals below are the module's and they are not advisory: a downgrade across a release line
 * cannot work against a database a migration has already moved, and the panel is not the place to find
 * that out. What the module cannot know — whether this particular step carries a destructive migration
 * — it says, rather than implying a safety it cannot deliver.
 */

const props = defineProps<{ version: { current?: AppliedVersion; previous?: AppliedVersion } }>();

const previously = computed(() =>
    props.version.previous
        ? `Previously ${props.version.previous.version}, installed ${when(props.version.previous.at)}.`
        : "Nothing else has been installed here.",
);

/**
 * What is typed, which is what the button follows.
 *
 * The page this was ported from had to wire this by hand — it rebuilt the DOM to redraw, and replacing
 * the node under the cursor loses the focus and the selection on every keystroke, so the enabled state
 * was set on the node already there. Getting that wrong was not subtle and it was invisible: every
 * button rendered disabled because the draft was empty, typing updated the draft without redrawing, and
 * so the button stayed disabled no matter what was in the field. Vue patches the input in place instead,
 * which is the whole reason this is allowed to be a computed.
 */
const named = computed(() => (store.draft.upgradeTo ?? "").trim());

const allowed = computed(() => store.plan?.judgement?.ok === true);

/**
 * Two kinds of no, and only one of them is the operator's to overrule.
 *
 * `settled` is a change that cannot work — a downgrade across a release line onto a database a migration
 * has already moved. `unproven` is the panel saying it cannot see far enough: the running version came
 * from a moving tag, or nothing wrote down what is installed. The operator can see further than the
 * panel, and drawing both as a dead button is what locked every `latest`-pinned install out of
 * re-pulling its own tag — the one operation that kind of install exists to do.
 */
const askable = computed(() => !allowed.value && store.plan?.judgement?.standing === "unproven");

async function ask(): Promise<void> {
    store.busy = "plan";

    const response = await api.plan(named.value);

    store.busy = undefined;
    store.plan = response.ok ? response.body : undefined;

    // A refusal answers a sentence where a plan would have been, so it is read out of a body typed as
    // the plan it is not.
    store.planProblem = response.ok
        ? undefined
        : ((response.body as { problem?: string } | undefined)?.problem ?? "Could not work out what that would do.");
}

async function upgrade(): Promise<void> {
    store.busy = "upgrade";

    // `confirm` is sent only when it was asked for. A settled refusal ignores it on the server anyway,
    // so this cannot talk its way past a change that cannot work — it only answers the question the
    // panel actually asked.
    const response = await api.upgrade(store.plan?.to?.value ?? store.draft.upgradeTo ?? "", askable.value);

    store.busy = undefined;
    store.planProblem = response.ok
        ? undefined
        : ((response.body as { problem?: string } | undefined)?.problem ?? "The upgrade did not finish.");
    store.plan = undefined;

    // Both halves, because an upgrade moves both: the stage the wizard reports and everything the panel
    // shows. `refresh` only fetches the overview when there is none held, and by now there is one.
    await refresh();
    await refreshOverview();
}
</script>

<template>
  <Card title="Version" :description="previously">
    <Field label="Version to move to" :error="store.planProblem">
      <input
        v-model="store.draft.upgradeTo"
        class="s-input mono"
        :placeholder="version.current?.version ?? '1.4.0'"
        autocomplete="off"
        spellcheck="false"
        aria-label="Version to move to"
      />
    </Field>

    <div class="flex justify-end">
      <button
        class="s-btn s-btn--subtle s-btn--sm"
        :disabled="store.busy !== undefined || named.length === 0"
        @click="ask"
      >What would that do?</button>
    </div>

    <div
      v-if="store.plan"
      class="flex flex-col gap-3 rounded-lg border border-oled-border-subtle bg-oled-surface p-4"
    >
      <div class="flex items-center gap-2 flex-wrap">
        <span class="s-badge s-badge--closed">{{ store.plan?.direction }}</span>
        <span v-if="store.plan?.crossing === 'yes'" class="s-badge s-badge--pending">crosses a major</span>
        <span v-if="store.plan?.backupFirst" class="s-badge s-badge--pending">back up first</span>
      </div>

      <Note v-for="(caution, index) in store.plan?.warnings ?? []" :key="index">{{ caution }}</Note>

      <!--
        The same sentence in two tones, and the tone is the whole message. An `unproven` refusal is
        something the operator can answer, so it is a warning with a way past it below; a `settled` one
        has already stopped this, so it is a problem and there is nothing under it.
      -->
      <Note
        v-if="!allowed && store.plan?.judgement?.problem"
        :tone="askable ? 'warning' : 'danger'"
      >{{ store.plan?.judgement?.problem }}</Note>

      <label v-if="askable" class="flex items-start gap-3 cursor-pointer">
        <!--
          `accent-accent` rather than the browser's own blue, which is the one colour on this page that
          is not Argon's. Restyling the control properly costs a pseudo-element and gives up the
          platform's focus and keyboard behaviour, which on something somebody tabs through is a bad
          trade.
        -->
        <input v-model="store.draft.upgradeConfirmed" type="checkbox" class="mt-0.5 accent-accent" />
        <span class="text-xs text-text-secondary leading-relaxed">
          I know what is installed here and this change is safe. The panel could not establish that on its own.
        </span>
      </label>

      <div v-if="(store.plan?.images ?? []).length > 0" class="flex flex-col gap-1">
        <div
          v-for="(change, index) in store.plan?.images ?? []"
          :key="index"
          class="flex items-center gap-2 mono text-xs"
        >
          <span class="text-text-muted flex-1 truncate">{{ change.repository ?? change.name ?? "image" }}</span>
          <span class="text-text-faint">{{ change.from ?? "—" }}</span>
          <span class="text-text-muted"><Icon name="arrow" :size="12" /></span>
          <span class="text-text-primary">{{ change.to ?? "—" }}</span>
        </div>
      </div>

      <div class="flex justify-end">
        <button
          class="s-btn s-btn--sm"
          :class="askable ? 's-btn--danger' : 's-btn--primary'"
          :disabled="(!allowed && !(askable && store.draft.upgradeConfirmed === true)) || store.busy !== undefined"
          @click="upgrade"
        >{{ store.busy === "upgrade" ? "Upgrading…" : askable ? "Do it anyway" : "Do it" }}</button>
      </div>
    </div>
  </Card>
</template>
