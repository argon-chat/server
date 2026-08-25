<script setup lang="ts">
import { computed } from "vue";
import type { SetupState } from "../api";
import { store, submit } from "../store";
import Card from "./Card.vue";
import Field from "./Field.vue";
import Icon from "./Icon.vue";

/**
 * Where avatars and attachments live.
 *
 * The two shapes differ in what else has to be answered, and that is why one of them saves the moment
 * it is picked and the other waits for a button. A store on this machine needs nothing more; a bucket
 * needs four fields, and sending half of them is a rejection rather than an answer.
 */
const props = defineProps<{ state: SetupState }>();

const answered = computed(() => props.state.answers.storage);

const kind = computed(() => store.draft.storageKind ?? answered.value?.kind ?? "local");

/**
 * Whether a key pair is already held.
 *
 * The server answers `credentials: ["object storage"]` and never a value — it cannot, it keeps them
 * where the operator cannot read them back either. So there is nothing to put in the boxes, and an
 * empty box beside a saved bucket reads as "not saved". This is what says otherwise.
 */
const held = computed(() => (props.state.credentials ?? []).includes("object storage"));

const CHOICES = [
    ["local", "On this machine", "A store runs beside Argon. Nothing else to configure, and the disk is yours to watch."],
    ["s3", "An S3 bucket you own", "Anything speaking S3. You provide the endpoint and a key pair."],
] as const;

function choose(value: string): void {
    store.draft.storageKind = value;

    // Picking the local store is the whole answer, so it is sent. Picking S3 only opens the fields.
    if (value === "local") void submit({ storage: { kind: "local" } });
}

const endpoint = computed({
    get: () => store.draft.endpoint ?? answered.value?.endpoint ?? "",
    set: (typed: string) => {
        store.draft.endpoint = typed;
    },
});

const bucket = computed({
    get: () => store.draft.bucket ?? answered.value?.bucket ?? "",
    set: (typed: string) => {
        store.draft.bucket = typed;
    },
});

const region = computed({
    get: () => store.draft.region ?? answered.value?.region ?? "",
    set: (typed: string) => {
        store.draft.region = typed;
    },
});

/**
 * The two write-only ones.
 *
 * Never the stored value, because there is no stored value to have: the answers the server returns
 * cannot carry a credential at all. What is here is only ever what is being typed now, which is also
 * why leaving both empty means "keep what you have" rather than "clear them".
 */
const accessKey = computed({
    get: () => store.draft.accessKey ?? "",
    set: (typed: string) => {
        store.draft.accessKey = typed;
    },
});

const secretKey = computed({
    get: () => store.draft.secretKey ?? "",
    set: (typed: string) => {
        store.draft.secretKey = typed;
    },
});

/**
 * Sent as what the boxes say, not as what has been typed into them this session.
 *
 * The three plain fields fall back to the held answer when the draft is untouched, and the submission
 * has to fall back the same way. Reading the draft alone sent an empty endpoint from a form that was
 * visibly showing one, and the operator got "endpoint is required" under a filled-in box.
 */
function save(): void {
    void submit({
        storage: {
            kind: "s3",
            endpoint: endpoint.value.trim(),
            bucket: bucket.value.trim(),
            region: region.value.trim() || undefined,
            accessKey: accessKey.value.trim() || undefined,
            secretKey: secretKey.value.trim() || undefined,
        },
    });
}
</script>

<template>
  <Card title="File storage" description="Where avatars and attachments live.">
    <div class="flex flex-col gap-2">
      <label
        v-for="[value, label, description] in CHOICES"
        :key="value"
        class="row-link flex items-start gap-3 px-3 py-2.5 rounded-lg border border-transparent cursor-pointer"
      >
        <input
          type="radio"
          name="storage"
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

    <!--
      The server rejects `storage` whole rather than field by field, so the one sentence it answers with
      is shown under each of them. Whichever box the operator is looking at when they read it, the
      complaint is next to it.
    -->
    <div v-if="kind === 's3'" class="flex flex-col gap-3 pt-1">
      <Field label="Endpoint" :error="store.rejections.storage">
        <input
          v-model="endpoint"
          class="s-input mono"
          placeholder="https://s3.eu-central-1.amazonaws.com"
          autocomplete="off"
          spellcheck="false"
        />
      </Field>

      <Field label="Bucket" :error="store.rejections.storage">
        <input v-model="bucket" class="s-input mono" placeholder="argon-content" autocomplete="off" spellcheck="false" />
      </Field>

      <Field label="Region (optional)" :error="store.rejections.storage">
        <input v-model="region" class="s-input mono" placeholder="eu-central-1" autocomplete="off" spellcheck="false" />
      </Field>

      <div v-if="held" class="flex items-center gap-2 text-xs text-text-muted">
        <span class="text-success"><Icon name="check" :size="13" /></span>
        <span>Access key and secret are held. Enter them again to replace.</span>
      </div>

      <Field label="Access key" :error="store.rejections.storage">
        <input v-model="accessKey" class="s-input mono" autocomplete="off" spellcheck="false" />
      </Field>

      <!--
        `new-password` rather than `off`: a browser offering to fill an object-storage secret with
        somebody's saved login is worse than no help at all.
      -->
      <Field label="Secret key" :error="store.rejections.storage">
        <input v-model="secretKey" class="s-input mono" type="password" autocomplete="new-password" spellcheck="false" />
      </Field>

      <div class="flex justify-end">
        <button class="s-btn s-btn--subtle s-btn--sm" :disabled="store.busy !== undefined" @click="save">Save</button>
      </div>
    </div>
  </Card>
</template>
