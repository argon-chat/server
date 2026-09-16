<script setup lang="ts">
/**
 * Creating an account, from the widget.
 *
 * The fields are the installed client's — `NewUserCredentialsInput` is what both ends up as — so an
 * account made here and one made from the desktop app are the same account, made the same way. What
 * this is not is the invite form under `/register`: that one needs a token out of an invitation
 * email and has nowhere to send someone who has not been invited.
 *
 * It sits inside the sign-in flow rather than on a route of its own, because registering succeeds
 * into a consent screen. A page of its own would have to grow its own copy of consent, OTP and the
 * error screen to carry the user the rest of the way.
 */
import { Button } from "@argon/ui/button";
import { ref } from "vue";
import { ArrowLeftIcon, Loader2Icon, UserPlusIcon, EyeIcon, EyeOffIcon } from "@lucide/vue";
import { useSimpleAuthStore } from "@/store/simpleAuthStore";
import InputWithError from "../InputWithError.vue";

const emit = defineEmits<{ (e: "back"): void }>();

const authStore = useSimpleAuthStore();

const email = ref("");
const username = ref("");
const displayName = ref("");
const password = ref("");
const birthDate = ref("");
const agreeTos = ref(false);
const agreeOptionalEmails = ref(false);
const showPassword = ref(false);

const errorFor = (field: string) => authStore.registerFieldErrors[field] ?? null;

async function submit(e?: Event) {
  e?.preventDefault();
  if (authStore.isLoading) return;

  // Checked here as well as on the server: the server is what enforces it, and this is what stops a
  // round trip that can only come back refused.
  if (!agreeTos.value) {
    authStore.registerFieldErrors = { ...authStore.registerFieldErrors, tos: "You have to accept the terms to continue." };
    return;
  }

  await authStore.register({
    email: email.value.trim(),
    username: username.value.trim(),
    // Blank is not a missing value: someone who did not fill it in is telling us to show their
    // username, and sending an empty string would put one in the database.
    displayName: displayName.value.trim() || username.value.trim(),
    password: password.value,
    birthDate: birthDate.value,
    agreeTos: agreeTos.value,
    agreeOptionalEmails: agreeOptionalEmails.value,
  });
}
</script>

<template>
  <div class="mx-auto w-full max-w-[380px]">
    <form class="rounded-3xl border border-white/[0.08] bg-black/40 backdrop-blur-2xl p-8 space-y-4"
      @submit="submit">
      <div class="text-center space-y-1.5">
        <div class="flex justify-center mb-3">
          <div class="w-12 h-12 rounded-2xl bg-gradient-to-br from-indigo-500/15 to-blue-500/15
                      border border-indigo-500/20 flex items-center justify-center">
            <UserPlusIcon :size="20" class="text-indigo-400" />
          </div>
        </div>
        <h1 class="text-lg font-semibold text-white">Create an account</h1>
        <p class="text-xs text-white/35">It takes a minute, and you keep the same account everywhere.</p>
      </div>

      <p v-if="authStore.registerError" role="alert"
        class="rounded-xl bg-red-500/[0.07] border border-red-500/20 px-3 py-2 text-[11px] text-red-300">
        {{ authStore.registerError }}
      </p>

      <div class="space-y-3">
        <InputWithError v-model="email" type="email" placeholder="Email" :error="errorFor('email')"
          :disabled="authStore.isLoading" />
        <InputWithError v-model="username" placeholder="Username" :error="errorFor('username')"
          :disabled="authStore.isLoading" />
        <InputWithError v-model="displayName" placeholder="Display name (optional)"
          :error="errorFor('displayName')" :disabled="authStore.isLoading" />

        <div class="relative">
          <InputWithError v-model="password" :type="showPassword ? 'text' : 'password'" placeholder="Password"
            :error="errorFor('password')" :disabled="authStore.isLoading" />
          <button type="button" tabindex="-1"
            class="absolute right-3 top-[11px] text-white/25 hover:text-white/50 transition"
            @click="showPassword = !showPassword">
            <EyeOffIcon v-if="showPassword" :size="15" />
            <EyeIcon v-else :size="15" />
          </button>
        </div>

        <div class="space-y-1">
          <label class="block text-[11px] text-white/35 pl-1">Date of birth</label>
          <InputWithError v-model="birthDate" type="date" :error="errorFor('birthDate')"
            :disabled="authStore.isLoading" />
        </div>
      </div>

      <div class="space-y-2 pt-1">
        <label class="flex items-start gap-2.5 cursor-pointer">
          <input v-model="agreeTos" type="checkbox"
            class="mt-0.5 h-3.5 w-3.5 rounded border-white/20 bg-white/[0.04] accent-indigo-500" />
          <span class="text-[11px] leading-relaxed text-white/40">
            I agree to the
            <a href="https://argon.gl/terms" target="_blank"
              class="text-indigo-400/80 hover:text-indigo-400 underline underline-offset-2">terms of service</a>
            and the
            <a href="https://argon.gl/privacy" target="_blank"
              class="text-indigo-400/80 hover:text-indigo-400 underline underline-offset-2">privacy policy</a>.
          </span>
        </label>
        <p v-if="errorFor('tos')" role="alert" class="pl-6 text-[11px] text-red-300">{{ errorFor('tos') }}</p>

        <label class="flex items-start gap-2.5 cursor-pointer">
          <input v-model="agreeOptionalEmails" type="checkbox"
            class="mt-0.5 h-3.5 w-3.5 rounded border-white/20 bg-white/[0.04] accent-indigo-500" />
          <span class="text-[11px] leading-relaxed text-white/40">
            Send me occasional product news. Optional, and not needed to sign in.
          </span>
        </label>
      </div>

      <Button type="submit" :disabled="authStore.isLoading"
        class="w-full h-11 text-sm font-semibold rounded-xl bg-indigo-500 hover:bg-indigo-400 text-white
               hover:shadow-[0_0_24px_-4px_rgba(99,102,241,0.4)] transition-all">
        <Loader2Icon v-if="authStore.isLoading" :size="16" class="mr-2 animate-spin" />
        <UserPlusIcon v-else :size="16" class="mr-2" />
        Create account
      </Button>

      <button type="button"
        class="flex items-center justify-center gap-1.5 w-full text-[11px] text-white/30 hover:text-white/60 transition"
        @click="emit('back')">
        <ArrowLeftIcon :size="12" />
        Back to sign in
      </button>
    </form>
  </div>
</template>
