<script setup lang="ts">
import { computed } from "vue";
import type { SetupState } from "../api";
import { store } from "../store";
import Note from "./Note.vue";
import Output from "./Output.vue";
import DomainCard from "./DomainCard.vue";
import VersionCard from "./VersionCard.vue";
import RolesCard from "./RolesCard.vue";
import StorageCard from "./StorageCard.vue";
import TrafficCard from "./TrafficCard.vue";
import VoiceCard from "./VoiceCard.vue";
import PanelPasswordCard from "./PanelPasswordCard.vue";
import ApplyBar from "./ApplyBar.vue";

/**
 * Every question the install needs, on one page.
 *
 * ## Six answers, six controls
 *
 * `missingAnswers` in setup.ts lists what the server is still waiting for — domain, serverVersion,
 * traffic, roles, voice, storage — and the apply bar prints that list back verbatim. So an answer with
 * no control here is a wizard that names something the operator cannot act on and a stage that never
 * reaches `ready`. Roles was that answer: its card was drawn only once the image had been interrogated,
 * which meant that before that the bar said "roles" and the page offered nothing. It is drawn
 * unconditionally now and says for itself why it is empty when there is no image to list them from.
 *
 * ## What replaced the manual DOM poke
 *
 * The renderer this was ported from could not redraw a button without destroying the field the cursor
 * was in, so it had a `gated()` helper that reached into the live input and set `disabled` on every
 * keystroke. That helper existed because of a bug worth remembering: every Save first rendered disabled,
 * because the draft was empty at first paint, and typing updated the draft without redrawing — so the
 * button stayed disabled no matter what was in the field, with nothing on screen suggesting why. The
 * wizard did not work at all. A browser test is what found it; the screenshots looked right because
 * their fixtures already had answers in them.
 *
 * Here that is a `:disabled` binding against the draft, which is most of the reason for moving. Nothing
 * on this page may go back to poking a node it did not render: reactivity is the fix, and a manual poke
 * beside it is a second source of truth that will disagree.
 */

/**
 * The wizard is only mounted with a state — App.vue routes an absent one to `Blocked` — so the
 * narrowing happens once here rather than in each of the eight components below.
 */
const state = computed(() => store.state as SetupState);
</script>

<template>
  <div class="max-w-3xl mx-auto px-5 py-10 sm:py-14 flex flex-col gap-6">
    <header class="flex flex-col gap-1">
      <div class="section-label">Argon</div>
      <h1 class="text-2xl font-black tracking-tight text-text-primary">Set up this instance</h1>
      <p class="text-sm text-text-muted">
        Answer these and this machine becomes an Argon instance. Nothing changes until you say so.
      </p>
    </header>

    <Note v-if="state.restarted">
      {{ state.note ?? "This panel found an install it did not write. The answers from that run are gone." }}
    </Note>

    <div class="flex flex-col gap-4">
      <DomainCard :state="state" />
      <VersionCard :state="state" />
      <RolesCard :state="state" />
      <StorageCard :state="state" />
      <TrafficCard :state="state" />
      <VoiceCard :state="state" />
      <PanelPasswordCard :state="state" />
    </div>

    <Note v-for="(text, index) in state.warnings ?? []" :key="index">{{ text }}</Note>

    <Note v-if="state.problem" tone="danger">{{ state.problem }}</Note>

    <!--
      What the server said, shown with the questions still under it on purpose: a refusal wrote nothing,
      so the fix is to change an answer and the answers should be right there rather than a screen back.
      The refused reports open themselves; the ones that passed stay folded, because a wall of output
      from six roles that all agreed is what hides the one that did not.
    -->
    <section v-if="state.validation" class="panel p-5 flex flex-col gap-3">
      <h2 class="text-sm font-bold text-text-primary">What the server said</h2>
      <div class="flex flex-col gap-2">
        <details v-for="(report, index) in state.validation" :key="index" :open="!report.ok">
          <summary class="flex items-center gap-2 cursor-pointer py-1">
            <span class="s-badge" :class="report.ok ? 's-badge--resolved' : 's-badge--new'">
              {{ report.ok ? "ok" : "refused" }}
            </span>
            <span class="mono text-sm text-text-primary">{{ report.role }}</span>
          </summary>
          <div class="pt-2"><Output :text="report.output" /></div>
        </details>
      </div>
    </section>

    <ApplyBar :state="state" />
  </div>
</template>

<style>
/*
 * The one rule the design system does not carry.
 *
 * It styles inputs, buttons and surfaces, not the native checkbox and radio this page asks its choices
 * with — so without this they render in the browser's own blue, the single colour on screen that is not
 * Argon's. `accent-color` is the whole fix: restyling them properly costs a pseudo-element and gives up
 * the platform's own focus and keyboard behaviour, which on a form somebody tabs through is a bad trade.
 *
 * Deliberately not `scoped`. The controls it is for belong to the cards this page mounts rather than to
 * this component's own markup, and a scoped rule would not reach a single one of them.
 */
input[type="checkbox"],
input[type="radio"] {
    accent-color: var(--color-accent);
    width: 15px;
    height: 15px;
}
</style>
