<script setup lang="ts">
import { Button } from "@argon/ui/button";
import { PinInput, PinInputGroup, PinInputInput, PinInputSeparator } from "@argon/ui/pin-input";
import { ArrowLeftIcon, MailIcon, Loader2Icon, ShieldCheckIcon } from "lucide-vue-next";
import { ref, onMounted } from "vue";
import { useLocale } from "@/store/localeStore";

const { t } = useLocale();
const props = defineProps<{ auth: ReturnType<typeof import("@/composables/useSimpleAuthForm").useSimpleAuthForm> }>();
const { otpCode, isLoading, onSubmit, goBackToLogin } = props.auth;

const model = ref([] as string[]);
const entered = ref(false);

onMounted(() => {
  requestAnimationFrame(() => (entered.value = true));
});

function handleComplete(e: string[]) {
    otpCode.value = e.join("");
    onSubmit();
}
function onReturn() {
    otpCode.value = "";
    goBackToLogin();
}
</script>

<template>
    <div class="flex justify-center items-center min-h-screen px-4 sm:px-0">
        <Transition
            enter-active-class="transition-all duration-500 ease-out"
            enter-from-class="opacity-0 scale-95 translate-y-4"
            enter-to-class="opacity-100 scale-100 translate-y-0"
        >
            <div
                v-if="entered"
                class="w-full max-w-sm rounded-3xl border border-white/[0.08] bg-black/60 backdrop-blur-2xl shadow-[0_0_80px_-20px_rgba(34,197,94,0.1)] p-8 space-y-7 relative overflow-hidden"
            >
                <!-- Ambient glow -->
                <div class="absolute -top-20 left-1/2 -translate-x-1/2 w-60 h-60 bg-emerald-500/[0.04] rounded-full blur-3xl pointer-events-none" />

                <!-- Back button -->
                <button
                    class="absolute top-4 left-4 p-1.5 rounded-lg text-white/30 hover:text-white/60 hover:bg-white/5 transition-all"
                    @click="onReturn"
                    aria-label="Go back to login"
                >
                    <ArrowLeftIcon :size="16" />
                </button>

                <!-- Header -->
                <div class="flex flex-col items-center space-y-5 pt-2">
                    <div class="relative">
                        <!-- Pulse rings -->
                        <div class="absolute inset-0 rounded-full border border-emerald-400/20 animate-[pulse_3s_ease-in-out_infinite]" />
                        <div class="absolute -inset-3 rounded-full border border-emerald-400/10 animate-[pulse_3s_ease-in-out_infinite_0.5s]" />

                        <div class="w-16 h-16 rounded-full bg-gradient-to-br from-emerald-500/15 to-teal-500/15 flex items-center justify-center">
                            <MailIcon :size="26" class="text-emerald-400" />
                        </div>
                    </div>

                    <div class="text-center space-y-1.5">
                        <h2 class="text-lg font-semibold text-white tracking-tight">{{ t("enter_your_otp") }}</h2>
                        <p class="text-xs text-white/35">{{ t("we_sent_it_on_email") }}</p>
                    </div>
                </div>

                <!-- PIN Input -->
                <form @submit.prevent="onSubmit" class="space-y-7">
                    <div class="flex justify-center">
                        <PinInput
                            id="pin-input"
                            class="justify-center"
                            v-model="model"
                            placeholder="·"
                            @complete="handleComplete"
                            aria-label="Enter verification code"
                        >
                            <PinInputGroup class="gap-2">
                                <template v-for="(id, index) in 6" :key="id">
                                    <PinInputInput
                                        class="w-11 h-12 rounded-xl border border-white/[0.08] bg-white/[0.03] text-white text-center text-lg font-medium focus:border-emerald-500/50 focus:ring-1 focus:ring-emerald-500/20 focus:bg-white/[0.05] transition-all"
                                        :index="index"
                                    />
                                    <PinInputSeparator v-if="index === 2" class="text-white/10" />
                                </template>
                            </PinInputGroup>
                        </PinInput>
                    </div>

                    <!-- Submit -->
                    <Button
                        :disabled="isLoading"
                        class="w-full h-11 text-sm font-semibold rounded-xl transition-all duration-300"
                        :class="isLoading
                            ? 'bg-emerald-500/20 text-emerald-300 cursor-wait'
                            : 'bg-emerald-500 hover:bg-emerald-400 text-black hover:shadow-[0_0_24px_-4px_rgba(34,197,94,0.4)]'"
                    >
                        <Loader2Icon v-if="isLoading" :size="16" class="mr-2 animate-spin" />
                        <ShieldCheckIcon v-else :size="16" class="mr-2" />
                        {{ t("verify_and_sigin") }}
                    </Button>
                </form>

                <!-- Footer -->
                <p class="text-[10px] text-center text-white/15 pt-1">
                    Didn't receive the code? Check your spam folder
                </p>
            </div>
        </Transition>
    </div>
</template>
