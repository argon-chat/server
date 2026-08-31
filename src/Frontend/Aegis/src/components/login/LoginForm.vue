<script setup lang="ts">
import { Button } from "@argon/ui/button";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter, DialogClose } from "@argon/ui/dialog";
import { onMounted, ref, watch } from "vue";
import { LogInIcon, ArrowRightIcon, LockKeyholeIcon, Loader2Icon, EyeIcon, EyeOffIcon, FingerprintIcon } from "lucide-vue-next";
import QRStyled from "./QRStyled.vue";
import { useLocale } from "@/store/localeStore";
import InputWithError from "../InputWithError.vue";

const { t } = useLocale();
const props = defineProps<{ auth: ReturnType<typeof import("@/composables/useSimpleAuthForm").useSimpleAuthForm> }>();
const { email, password, isLoading, onSubmit, authError, authStore } = props.auth;

const titles = [
  { title: t("greetings.good_to_see_you.title"), desc: t("greetings.good_to_see_you.desc") },
  { title: t("greetings.hey_there.title"), desc: t("greetings.hey_there.desc") },
  { title: t("greetings.welcome_back.title"), desc: t("greetings.welcome_back.desc") },
  { title: t("greetings.glad_you_here.title"), desc: t("greetings.glad_you_here.desc") },
  { title: t("greetings.hello_again.title"), desc: t("greetings.hello_again.desc") },
];
const heading = ref(titles[0]);
function pickRandomHeading() {
  heading.value = titles[Math.floor(Math.random() * titles.length)];
}

const qrLoginUrl = ref("https://www.youtube.com/watch?v=HIcSWuKMwOw");

const step = ref<"email" | "password">("email");
const showBetaModal = ref(false);
const showPassword = ref(false);
const entered = ref(false);

onMounted(() => {
  pickRandomHeading();
  requestAnimationFrame(() => (entered.value = true));
});

async function getLoginScenario(email: string): Promise<"pwd" | "otp" | "pwd-otp" | "passkey" | "passkey-otp" | ""> {
  const scenario = await authStore.getScenario(email);

  if (!scenario) {
    authError.value = "Account does not exist";
    return "";
  }

  console.log("Scenario", scenario);

  if (scenario == "EmailPassword") return "pwd";
  if (scenario == "EmailPasswordOtp") return "pwd-otp";
  if (scenario == "EmailOtp") return "otp";
  if (scenario == "PasskeyOnly") return "passkey";
  if (scenario == "PasskeyWithOtp") return "passkey-otp";
  return "pwd";
}

async function handleNext(e?: Event) {
  e?.preventDefault();
  if (!email.value) return;
  
  const scenario = await getLoginScenario(email.value);
  if (!scenario) return;

  if (scenario === "otp") {
    onSubmit();
  } else if (scenario === "passkey" || scenario === "passkey-otp") {
    // Start passkey flow with email (narrows allowedCredentials)
    await authStore.beginPasskeyLogin(email.value);
  } else {
    step.value = "password";
  }
}

async function handlePasskeyLogin() {
  // Discoverable credential flow — no email needed
  await authStore.beginPasskeyLogin();
}

async function handlePasswordSubmit(e?: Event) {
  e?.preventDefault();
  onSubmit();
}

watch(email, (newVal, oldVal) => {
  if (step.value !== "email" && newVal !== oldVal) {
    step.value = "email";
  }
});
</script>

<template>
  <div class="flex justify-center items-center min-h-screen px-4">
    <Transition
      enter-active-class="transition-all duration-500 ease-out"
      enter-from-class="opacity-0 scale-95 translate-y-4"
      enter-to-class="opacity-100 scale-100 translate-y-0"
    >
      <div
        v-if="entered"
        class="flex flex-col md:flex-row w-full max-w-sm md:max-w-none md:w-auto rounded-3xl border border-white/[0.08] bg-black/60 backdrop-blur-2xl shadow-[0_0_80px_-20px_rgba(99,102,241,0.12)] overflow-hidden relative"
      >
        <!-- ── Left: Login form ── -->
        <form @submit.prevent="onSubmit" class="w-full md:w-[380px] p-8 space-y-7 relative">
          <!-- Ambient glow -->
          <div class="absolute -top-20 left-1/2 -translate-x-1/2 w-60 h-60 bg-indigo-500/[0.05] rounded-full blur-3xl pointer-events-none" />

          <!-- Icon + Heading -->
          <div class="flex flex-col items-center space-y-5 pt-2 relative">
            <div class="relative">
              <div class="absolute inset-0 rounded-full border border-indigo-400/20 animate-[pulse_3s_ease-in-out_infinite]" />
              <div class="absolute -inset-3 rounded-full border border-indigo-400/10 animate-[pulse_3s_ease-in-out_infinite_0.5s]" />
              <div class="w-16 h-16 rounded-full bg-gradient-to-br from-indigo-500/15 to-blue-500/15 flex items-center justify-center">
                <LogInIcon :size="26" class="text-indigo-400" />
              </div>
            </div>

            <div class="text-center space-y-1.5">
              <h2 class="text-lg font-semibold text-white tracking-tight">{{ heading.title }}</h2>
              <p class="text-xs text-white/35">{{ heading.desc }}</p>
            </div>
          </div>

          <!-- Fields -->
          <div class="space-y-4 relative">
            <!-- Email -->
            <div class="space-y-1.5">
              <div class="flex items-center gap-2">
                <label for="email" class="text-xs font-medium text-white/50">Email</label>
                <span v-if="step === 'password'" class="text-[10px] text-white/25">{{ t("editing_resets_step") || "editing will reset step" }}</span>
              </div>
              <InputWithError
                v-model="email"
                :error="authError"
                @clear-error="authError = ''"
                type="email"
                placeholder="example@email.com"
                :disabled="isLoading"
                id="email"
              />
            </div>

            <!-- Password (step 2) -->
            <Transition
              enter-active-class="transition-all duration-300 ease-out"
              enter-from-class="opacity-0 -translate-y-2"
              enter-to-class="opacity-100 translate-y-0"
              leave-active-class="transition-all duration-200"
              leave-to-class="opacity-0 translate-y-2"
            >
              <div v-if="step === 'password'" class="space-y-1.5">
                <label for="password" class="text-xs font-medium text-white/50">Password</label>
                <div class="relative">
                  <input
                    id="password"
                    v-model="password"
                    :type="showPassword ? 'text' : 'password'"
                    placeholder="••••••••"
                    :disabled="isLoading"
                    class="w-full h-11 px-4 pr-10 rounded-xl border border-white/[0.08] bg-white/[0.03] text-white text-sm placeholder-white/20 focus:border-indigo-500/50 focus:ring-1 focus:ring-indigo-500/20 focus:bg-white/[0.05] transition-all outline-none"
                  />
                  <button
                    type="button"
                    class="absolute right-3 top-1/2 -translate-y-1/2 text-white/25 hover:text-white/50 transition-colors"
                    @click="showPassword = !showPassword"
                    tabindex="-1"
                  >
                    <EyeOffIcon v-if="showPassword" :size="16" />
                    <EyeIcon v-else :size="16" />
                  </button>
                </div>
              </div>
            </Transition>
          </div>

          <!-- Actions -->
          <div class="space-y-3 relative">
            <Button
              v-if="step === 'email'"
              type="button"
              :disabled="isLoading || !email"
              class="w-full h-11 text-sm font-semibold rounded-xl transition-all duration-300"
              :class="isLoading
                ? 'bg-indigo-500/20 text-indigo-300 cursor-wait'
                : 'bg-indigo-500 hover:bg-indigo-400 text-white hover:shadow-[0_0_24px_-4px_rgba(99,102,241,0.4)]'"
              @click.prevent="handleNext"
            >
              <Loader2Icon v-if="isLoading" :size="16" class="mr-2 animate-spin" />
              <template v-else>
                {{ t("next") }}
                <ArrowRightIcon :size="16" class="ml-2" />
              </template>
            </Button>

            <Button
              v-else
              type="submit"
              :disabled="isLoading || !password"
              class="w-full h-11 text-sm font-semibold rounded-xl transition-all duration-300"
              :class="isLoading
                ? 'bg-indigo-500/20 text-indigo-300 cursor-wait'
                : 'bg-indigo-500 hover:bg-indigo-400 text-white hover:shadow-[0_0_24px_-4px_rgba(99,102,241,0.4)]'"
              @click.prevent="handlePasswordSubmit"
            >
              <Loader2Icon v-if="isLoading" :size="16" class="mr-2 animate-spin" />
              <LogInIcon v-else :size="16" class="mr-2" />
              {{ t("signin") }}
            </Button>

            <p class="text-[11px] text-white/25 text-center">
              {{ t("dont_have_account") }}
              <a
                @click="showBetaModal = true"
                class="cursor-pointer text-indigo-400/70 hover:text-indigo-400 transition font-medium underline underline-offset-2"
              >
                {{ t("create_one") }}
              </a>
            </p>

            <!-- Passkey login button -->
            <div class="pt-1">
              <div class="flex items-center gap-3 mb-3">
                <div class="flex-1 h-px bg-white/[0.06]" />
                <span class="text-[10px] text-white/20 uppercase tracking-widest">or</span>
                <div class="flex-1 h-px bg-white/[0.06]" />
              </div>
              <Button
                type="button"
                variant="outline"
                :disabled="isLoading"
                class="w-full h-11 text-sm font-medium rounded-xl border-white/[0.08] bg-white/[0.03] text-white/70 hover:text-white hover:bg-white/[0.06] hover:border-white/[0.12] transition-all duration-300"
                @click.prevent="handlePasskeyLogin"
              >
                <FingerprintIcon :size="16" class="mr-2" />
                {{ t("login_with_passkey") }}
              </Button>
            </div>
          </div>
        </form>

        <!-- ── Divider ── -->
        <div class="hidden md:block w-px bg-white/[0.06]" />

        <!-- ── Right: QR code ── -->
        <div class="hidden md:flex flex-col justify-center items-center p-8 w-[240px] text-center space-y-4 relative">
          <div class="absolute -top-16 right-0 w-40 h-40 bg-indigo-500/[0.03] rounded-full blur-3xl pointer-events-none" />
          <p class="text-xs font-medium text-white/40 relative">{{ t("qr_code_login") }}</p>
          <div class="relative p-2 rounded-2xl">
            <QRStyled :value="qrLoginUrl" :size="140" level="M" class="rounded-lg" />
          </div>
          <p class="text-[10px] text-white/20 relative">{{ t("scan_with_app") }}</p>
        </div>
      </div>
    </Transition>

    <!-- Beta Registration Modal -->
    <Dialog v-model:open="showBetaModal">
      <DialogContent class="max-w-[calc(100vw-2rem)] sm:max-w-sm rounded-3xl border border-white/[0.08] bg-black/60 backdrop-blur-2xl p-8 space-y-5">
        <!-- Icon -->
        <div class="flex justify-center">
          <div class="relative">
            <div class="absolute inset-0 rounded-full border border-blue-400/20 animate-[pulse_3s_ease-in-out_infinite]" />
            <div class="w-14 h-14 rounded-full bg-gradient-to-br from-blue-500/15 to-indigo-500/15 flex items-center justify-center">
              <LockKeyholeIcon :size="24" class="text-blue-400" />
            </div>
          </div>
        </div>

        <!-- Content -->
        <DialogHeader class="text-center space-y-2">
          <DialogTitle class="text-lg font-semibold text-white text-center">
            Closed Beta
          </DialogTitle>
          <DialogDescription class="text-xs text-white/35 leading-relaxed text-center">
            Registration is currently available as part of the closed beta program. To request access, submit an application at
            <a href="https://argon.gl" target="_blank" class="text-indigo-400 hover:text-indigo-300 underline underline-offset-2 transition">argon.gl</a>.
          </DialogDescription>
        </DialogHeader>

        <!-- Beta portal link -->
        <div class="rounded-xl bg-white/[0.03] border border-white/[0.04] p-4 space-y-2.5">
          <p class="text-[11px] text-white/40 text-center leading-relaxed">
            Already have beta access? Head to the beta portal to complete registration.
          </p>
          <a
            href="https://beta.argon.gl"
            target="_blank"
            class="flex items-center justify-center gap-2 w-full h-10 text-sm font-semibold rounded-xl bg-indigo-500/10 hover:bg-indigo-500/20 text-indigo-400 border border-indigo-500/20 transition-all"
          >
            <ArrowRightIcon :size="14" />
            beta.argon.gl
          </a>
        </div>

        <!-- Status indicator -->
        <div class="flex items-center justify-center gap-2 py-2 px-4 rounded-xl bg-white/[0.02] border border-white/[0.03]">
          <span class="relative flex h-2 w-2">
            <span class="animate-ping absolute inline-flex h-full w-full rounded-full bg-blue-400 opacity-75" />
            <span class="relative inline-flex rounded-full h-2 w-2 bg-blue-500" />
          </span>
          <p class="text-[10px] text-white/25">Public registration coming soon</p>
        </div>

        <!-- Button -->
        <DialogFooter>
          <DialogClose as-child>
            <Button class="w-full h-11 text-sm font-semibold rounded-xl bg-white/[0.06] hover:bg-white/[0.1] text-white border border-white/[0.08] transition-all">
              Got it
            </Button>
          </DialogClose>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </div>
</template>
