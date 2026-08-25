<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { poll, stopPolling, store } from "../store";
import Icon from "./Icon.vue";
import Output from "./Output.vue";
import ServiceList from "./ServiceList.vue";

/**
 * The install, while it runs.
 *
 * Nothing on this screen is this browser's own idea of how far along things are. The stage arrives from
 * the server on every poll, which is why closing the tab and coming back — or opening a second one on
 * another machine — lands on the same line lit rather than at the beginning.
 */

/**
 * The four stages, in the order the server moves through them.
 *
 * A sequence with the current one marked, and no percentage anywhere. There are four real transitions
 * and no fifth number anybody can honestly produce: a bar sitting at 60% through a five-minute image
 * pull is a guess presented as a measurement, and the thing an operator watching an install most needs
 * is to be able to believe what is in front of them.
 */
const STEPS = [
    { stage: "applying", label: "Writing and checking the configuration" },
    { stage: "configured", label: "Configuration on disk" },
    { stage: "starting", label: "Pulling images and starting containers" },
    { stage: "running", label: "Up" },
] as const;

const reached = computed(() => STEPS.findIndex((step) => step.stage === store.state?.stage));

/**
 * The address of this panel, shown while the install is still running rather than only at the end.
 *
 * The server sends it early for exactly this reason: a browser that gives up waiting takes the response
 * with it but not the install, and an operator whose tab timed out still has to know where this page
 * moves to once `/` becomes Argon. Saving it for the finished screen means the one case that needs it
 * is the one case that never sees it.
 */
const address = computed(() => store.state?.panel);

const services = computed(() => store.state?.services ?? []);
const log = computed(() => (store.state?.progress ?? []).join("\n"));

/**
 * The poll belongs to this screen, and so does stopping it.
 *
 * While the apply runs the request that started it is still open — for minutes — so polling beside it
 * is what moves the log and what lets a second tab see the same thing. Tied to the screen rather than
 * to that request, because the operator who reopens a tab they closed never made the request, and the
 * install they are looking at is somebody else's in-flight call.
 *
 * Stopping on unmount is the other half: this screen goes away the moment the stage stops being one of
 * the three it draws, and a timer left running after that is a request every two seconds, forever, for
 * a page nobody is on.
 */
onMounted(poll);
onUnmounted(stopPolling);

const tail = ref<InstanceType<typeof Output> | null>(null);

/**
 * Keeping the end of the log in view without taking the page away from somebody reading it.
 *
 * Measured before Vue writes the new lines and applied after, so the rule is "follow if they were
 * already at the end". An operator who scrolled up to read a line that went past is reading it, and
 * yanking them back every two seconds is worse than not following at all.
 *
 * The margin is for fractional scroll heights, which a zoomed page or a fractional device pixel ratio
 * produces and which otherwise make the exact bottom a position it is not possible to be at.
 */
watch(log, async () => {
    const view = tail.value?.$el as HTMLElement | undefined;

    if (view === undefined) return;

    const wasAtEnd = view.scrollHeight - view.scrollTop - view.clientHeight < 24;

    await nextTick();

    if (wasAtEnd) view.scrollTop = view.scrollHeight;
});
</script>

<template>
  <div class="max-w-3xl mx-auto px-5 py-10 sm:py-14 flex flex-col gap-6">
    <header class="flex flex-col gap-1">
      <div class="section-label">Argon</div>
      <h1 class="text-2xl font-black tracking-tight text-text-primary">Set up this instance</h1>
      <p class="text-sm text-text-muted">
        This takes a few minutes. You can close this page — it carries on without you.
      </p>
    </header>

    <div class="panel p-5 flex flex-col gap-4">
      <ol class="flex flex-col gap-2.5">
        <li v-for="(step, index) in STEPS" :key="step.stage" class="flex items-center gap-3">
          <span v-if="index < reached" class="text-success"><Icon name="check" :size="14" /></span>
          <span v-else-if="index === reached" class="signal signal--pending"><span class="signal-dot" /></span>
          <span v-else class="text-text-faint" style="width: 14px">·</span>
          <span
            class="text-sm"
            :class="index < reached ? 'text-text-muted' : index === reached ? 'text-text-primary' : 'text-text-faint'"
          >{{ step.label }}</span>
        </li>
      </ol>

      <div
        v-if="address"
        class="flex items-start gap-3 rounded-lg border border-oled-border-subtle bg-oled-surface px-4 py-3"
      >
        <div class="flex flex-col gap-0.5 min-w-0">
          <p class="text-xs text-text-muted">This panel will live here once the instance is up</p>
          <a class="mono text-sm text-accent hover:text-accent-hover" :href="address.url">{{ address.url }}</a>
        </div>
      </div>
    </div>

    <div v-if="services.length > 0" class="panel p-5 flex flex-col gap-2">
      <h2 class="text-sm font-bold text-text-primary">Services</h2>
      <ServiceList :services="services" />
    </div>

    <Output v-if="log.length > 0" ref="tail" :text="log" />
  </div>
</template>
