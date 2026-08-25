<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from "vue";
import { signInWithCode, signInWithPassword, store } from "../store";
import Icon from "./Icon.vue";
import Note from "./Note.vue";

/**
 * The door.
 *
 * Which one is drawn is the server's answer, not this page's guess: the page cannot read the state to
 * find out, because the state is the thing behind the door. `GET /api/auth/mode` is the unauthenticated
 * route that says so, and the store asks it on every 401 rather than once at startup — the answer
 * changes underneath a tab that is left open, because finishing the install retires the code.
 *
 * During setup both are open and the code is what the operator has in front of them, so the code wins
 * whenever it is offered. Afterwards it is gone and only the password remains. Neither open is a state
 * `BootstrapAuth.retire` refuses to create — it will not retire the code before a password exists — so
 * it is drawn as the fault it would be rather than as an empty form. A form that cannot work is worse
 * than a sentence saying why: somebody will keep typing into it.
 */
const door = computed<"code" | "password" | "none">(() => {
    const mode = store.mode;

    // Undefined means the mode route has not answered yet, which on the very first paint is the
    // ordinary case. The code is the right guess: the only way to be unauthenticated and unasked is to
    // be at the start of an install, and a blank panel on the one screen somebody is actually waiting
    // in front of is worse than a field that turns out to be the wrong one.
    if (mode === undefined || mode.code) return "code";

    return mode.password ? "password" : "none";
});

const busy = computed(() => store.busy === "signin");

/**
 * The lockout, counted down.
 *
 * The server is what counts — five wrong answers and it stops replying for thirty seconds — and this
 * number is a courtesy, nothing more. Without it the field simply goes dead with nothing on screen
 * saying why, which reads as the panel having crashed rather than as having been told to wait.
 *
 * `now` is here only so the number moves: `store.lockedUntil` is a fixed instant, and comparing it
 * against a `Date.now()` read during render would never re-run, because nothing reactive changed.
 */
const now = ref(Date.now());
const locked = computed(() => store.lockedUntil > now.value);
const remaining = computed(() => Math.ceil((store.lockedUntil - now.value) / 1000));

let ticking: ReturnType<typeof setInterval> | undefined;

function stopTicking(): void {
    if (ticking !== undefined) clearInterval(ticking);

    ticking = undefined;
}

/**
 * One ticker at a time, and none once this screen is gone.
 *
 * Driven from the lockout itself rather than started by the sign-in that was refused, which is what
 * fixes two things the hand-rolled page got wrong. A second refusal arriving during a lockout pushes
 * the deadline out and lands here again, so the running ticker is replaced instead of a second one
 * being stacked beside it; and `onUnmounted` cancels the last one, which matters now in a way it did
 * not before — a sign-in that lands swaps this component out, and an interval that outlives it goes on
 * writing to reactive state for a screen nobody is looking at.
 *
 * Clearing the error when the lock lifts is deliberate: the sentence says the panel has stopped
 * answering, and leaving it up after it has started answering again describes a machine that no longer
 * exists.
 */
watch(
    () => store.lockedUntil,
    (until) => {
        stopTicking();

        if (until <= Date.now()) return;

        now.value = Date.now();

        ticking = setInterval(() => {
            now.value = Date.now();

            if (store.lockedUntil > now.value) return;

            stopTicking();
            store.signInError = undefined;
        }, 1000);
    },
    { immediate: true },
);

onUnmounted(stopTicking);

/**
 * The code, reshaped into the shape it was printed in.
 *
 * It is copied off another screen — sometimes off a photograph of one — and a code that looks unlike
 * the printed one reads as the wrong code. The alphabet the installer draws from is `A-Z2-9` minus the
 * ambiguous glyphs, so nothing outside it can be part of a real code and dropping it is safe: see
 * `random_code` in bootstrap.sh, which excludes I, L, O, 0 and 1 for exactly the reason this field
 * exists. Pasting the whole thing with its hyphens has to work too — that is how most people will do
 * it, and a field that mangles a correct paste is worse than one that does nothing.
 */
function reshape(event: Event): void {
    const field = event.target as HTMLInputElement;
    const cleaned = field.value
        .toUpperCase()
        .replace(/[^A-Z2-9]/g, "")
        .slice(0, 16);
    const grouped = cleaned.match(/.{1,4}/g)?.join("-") ?? "";

    store.draft.code = grouped;

    // Written back onto the element as well as into the store, because Vue patches the value only when
    // the thing it is bound to changed. Type a character the filter drops and `grouped` comes out
    // identical to what is already bound, so there is no patch to make and the rejected character sits
    // in the field looking accepted.
    field.value = grouped;
}

/**
 * Enter in the field and the button are the same act.
 *
 * Nothing typed is dropped here rather than by disabling the button, because an empty answer is still
 * an answer as far as the server is concerned: it would spend one of the five attempts being counted
 * against this machine and come back refused, which is a worse outcome than the click doing nothing.
 */
async function submit(): Promise<void> {
    if (door.value === "code") {
        const code = (store.draft.code ?? "").trim();

        if (code.length === 0) return;

        await signInWithCode(code);

        return;
    }

    const password = store.draft.signInPassword ?? "";

    if (password.length === 0) return;

    await signInWithPassword(password);
}

const label = computed(() => (busy.value ? "Checking…" : door.value === "code" ? "Continue" : "Sign in"));
</script>

<template>
  <!--
    No way in at all. On the page's own shell rather than in the sign-in card, because this is not a
    sign-in that failed — there is nothing here to type, and a card with a heading and no field is an
    invitation to look for the field.
  -->
  <div v-if="door === 'none'" class="max-w-3xl mx-auto px-5 py-10 sm:py-14 flex flex-col gap-6">
    <header class="flex flex-col gap-1">
      <div class="section-label">Argon</div>
      <h1 class="text-2xl font-black tracking-tight text-text-primary">Set up this instance</h1>
    </header>

    <Note tone="danger">
      This panel has no way to sign in: the bootstrap code has been retired and no password is set. That
      should not be reachable — the code refuses to retire before a password exists.
    </Note>
  </div>

  <div v-else class="min-h-screen flex items-center justify-center px-5 py-10">
    <div class="panel p-7 w-full flex flex-col gap-5" style="max-width: 26rem">
      <div class="flex flex-col gap-1.5">
        <div class="section-label">Argon</div>

        <template v-if="door === 'code'">
          <h1 class="text-xl font-black tracking-tight text-text-primary">Enter the setup code</h1>
          <p class="text-sm text-text-muted leading-relaxed">
            The installer printed it in the terminal on this machine. It is the only thing that tells
            this panel you are the person who started the install.
          </p>
        </template>

        <template v-else>
          <h1 class="text-xl font-black tracking-tight text-text-primary">Sign in</h1>
          <p class="text-sm text-text-muted leading-relaxed">
            The password set while this instance was installed. The code from the terminal stopped
            working when the install finished.
          </p>
        </template>
      </div>

      <!--
        Bound with `:value` and an explicit handler rather than `v-model`, because what is typed and
        what is kept are not the same string — see `reshape`. The label is what the browser test finds
        this field by; the placeholder is a shape and the heading is prose, and neither is a name.
      -->
      <input
        v-if="door === 'code'"
        class="s-input mono text-center"
        style="font-size: 1.05rem; letter-spacing: 0.14em"
        placeholder="XXXX-XXXX-XXXX-XXXX"
        autocomplete="off"
        spellcheck="false"
        aria-label="Bootstrap code"
        :disabled="locked || busy"
        :value="store.draft.code ?? ''"
        @input="reshape"
        @keydown.enter="submit"
      />

      <!--
        `current-password`, unlike every other field on this page: this is the one door that is the same
        door on every visit for the rest of the instance's life, so a browser offering what it saved is
        offering the right thing.
      -->
      <input
        v-else
        v-model="store.draft.signInPassword"
        class="s-input"
        type="password"
        autocomplete="current-password"
        aria-label="Panel password"
        :disabled="locked || busy"
        @keydown.enter="submit"
      />

      <!--
        Four outcomes reach this line — the wrong code, an attempt that sat too long and expired, a
        lockout, and a setup that is already over — and which sentence to show is the store's decision,
        because it is the half that knows which status came back. Interpolated, never markup.
      -->
      <Note v-if="store.signInError" tone="danger">{{ store.signInError }}</Note>

      <p v-if="locked" class="text-xs text-text-muted text-center">
        Locked for {{ remaining }} more second{{ remaining === 1 ? "" : "s" }}.
      </p>

      <button class="s-btn s-btn--primary w-full justify-center" :disabled="locked || busy" @click="submit">
        <Icon name="lock" />
        {{ label }}
      </button>
    </div>
  </div>
</template>
