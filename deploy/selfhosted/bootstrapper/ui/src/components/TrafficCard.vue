<script setup lang="ts">
import { computed } from "vue";
import type { SetupState } from "../api";
import { store, submit } from "../store";
import Card from "./Card.vue";
import Field from "./Field.vue";

/**
 * How traffic reaches this machine — §5's four paths.
 *
 * The install script already asked this in the terminal, and the answer is already in the edge's
 * configuration; this panel simply has not been told. Until it is, the question has to be here, because
 * the generator refuses to build an edge without it and `missing` would name it forever.
 *
 * `voiceHost` hangs off one shape only. Cloudflare's proxy carries HTTP and WebSockets, not the UDP
 * real-time media rides on, so an instance with voice behind it needs a second name that resolves
 * straight here — and an instance without voice needs nothing.
 */
const props = defineProps<{ state: SetupState }>();

const answered = computed(() => props.state.answers.traffic);

const kind = computed(() => store.draft.trafficKind ?? answered.value?.kind);

/**
 * Read from the draft first, so that turning voice on in the card below opens the hostname field here
 * without waiting for the round trip that answers it.
 */
const voiceOn = computed(() => store.draft.voice ?? props.state.answers.voice ?? false);

const voiceHost = computed({
    get: () => store.draft.voiceHost ?? answered.value?.voiceHost ?? "",
    set: (typed: string) => {
        store.draft.voiceHost = typed;
    },
});

const CHOICES = [
    ["lets-encrypt", "Let's Encrypt", "This machine is public and gets its own certificate. Renewal is handled for you."],
    [
        "own-certificate",
        "A certificate on this machine",
        "You supplied the certificate and key. Renewing it before it expires is yours.",
    ],
    [
        "cloudflare-proxied",
        "Behind Cloudflare's proxy",
        "Cloudflare terminates TLS for visitors; an origin certificate covers the hop to here.",
    ],
    [
        "cloudflare-tunnel",
        "Behind a Cloudflare tunnel",
        "Nothing of this machine is exposed. The tunnel makes the outbound connection.",
    ],
] as const;

function choose(next: string): void {
    store.draft.trafficKind = next;

    // Three of the four are the whole answer, so picking one sends it. The proxy has a field under it
    // and sending it half-answered would drop a voice hostname the operator was about to type.
    if (next !== "cloudflare-proxied") void submit({ traffic: { kind: next } });
}

/** As with the bucket: what is sent is what the box says, held answer included, not only what was typed. */
function save(): void {
    void submit({ traffic: { kind: "cloudflare-proxied", voiceHost: voiceHost.value.trim() || undefined } });
}
</script>

<template>
  <Card
    title="How traffic gets here"
    description="Chosen in the terminal when the installer ran. Confirm it here — the edge is built from this answer."
  >
    <div class="flex flex-col gap-2">
      <label
        v-for="[value, label, description] in CHOICES"
        :key="value"
        class="row-link flex items-start gap-3 px-3 py-2.5 rounded-lg border border-transparent cursor-pointer"
      >
        <input
          type="radio"
          name="traffic"
          class="mt-1"
          :checked="kind === value"
          :disabled="store.busy !== undefined"
          @change="choose(value)"
        />
        <div class="flex flex-col gap-0.5">
          <span class="text-sm text-text-primary">{{ label }}</span>
          <span class="text-xs text-text-muted leading-relaxed">{{ description }}</span>
        </div>
      </label>
    </div>

    <div v-if="kind === 'cloudflare-proxied'" class="flex flex-col gap-3 pt-1">
      <Field
        v-if="voiceOn"
        label="Hostname for voice (optional)"
        :error="store.rejections.traffic"
        hint="A DNS-only name pointing straight at this machine. Left empty, calls ride the proxy — which carries HTTP but not the media itself."
      >
        <input v-model="voiceHost" class="s-input mono" placeholder="media.example.org" autocomplete="off" spellcheck="false" />
      </Field>
      <p v-else class="text-xs text-text-muted leading-relaxed">Voice is off, so nothing needs a second hostname.</p>

      <div class="flex justify-end">
        <button class="s-btn s-btn--subtle s-btn--sm" :disabled="store.busy !== undefined" @click="save">Save</button>
      </div>
    </div>

    <!-- Under the shape that has no field of its own; the proxy's rejection is shown against its box. -->
    <p v-if="store.rejections.traffic && kind !== 'cloudflare-proxied'" class="text-xs text-danger">
      {{ store.rejections.traffic }}
    </p>
  </Card>
</template>
