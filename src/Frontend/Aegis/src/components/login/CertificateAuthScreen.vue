<script setup lang="ts">
import { Button } from "@argon/ui/button";
import { ShieldCheckIcon, FingerprintIcon, Loader2Icon, XIcon } from "@lucide/vue";
import { ref, onMounted, computed } from "vue";
import { useLocale } from "@/store/localeStore";

const { t } = useLocale();

const steps = computed(() => [
  { title: t('operator_step_connect'), desc: t('operator_step_connect_desc') },
  { title: t('operator_step_select_cert'), desc: t('operator_step_select_cert_desc') },
  { title: t('operator_step_confirm_pin'), desc: t('operator_step_confirm_pin_desc') },
]);

defineProps<{
  isLoading?: boolean;
}>();

const emit = defineEmits<{
  verify: [];
  cancel: [];
}>();

const entered = ref(false);
onMounted(() => {
  requestAnimationFrame(() => (entered.value = true));
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
        class="w-full max-w-sm rounded-3xl border border-white/[0.08] bg-black/60 backdrop-blur-2xl shadow-[0_0_80px_-20px_rgba(251,191,36,0.15)] p-8 space-y-8 relative overflow-hidden"
      >
        <!-- Ambient glow -->
        <div class="absolute -top-20 left-1/2 -translate-x-1/2 w-60 h-60 bg-amber-500/[0.06] rounded-full blur-3xl pointer-events-none" />

        <!-- Close -->
        <button
          class="absolute top-4 right-4 p-1.5 rounded-lg text-white/30 hover:text-white/60 hover:bg-white/5 transition-all"
          :disabled="isLoading"
          @click="emit('cancel')"
        >
          <XIcon :size="16" />
        </button>

        <!-- Icon with pulse animation -->
        <div class="flex flex-col items-center space-y-6 pt-2">
          <div class="relative">
            <!-- Pulse rings -->
            <div
              class="absolute inset-0 rounded-full border border-amber-400/20"
              :class="isLoading ? 'animate-ping' : 'animate-[pulse_3s_ease-in-out_infinite]'"
              style="animation-duration: 3s"
            />
            <div
              class="absolute -inset-3 rounded-full border border-amber-400/10"
              :class="isLoading ? 'animate-ping [animation-delay:150ms]' : 'animate-[pulse_3s_ease-in-out_infinite_0.5s]'"
            />
            <div
              class="absolute -inset-6 rounded-full border border-amber-400/5"
              :class="isLoading ? 'animate-ping [animation-delay:300ms]' : 'animate-[pulse_3s_ease-in-out_infinite_1s]'"
            />

            <!-- Main icon -->
            <div
              class="relative w-20 h-20 rounded-full flex items-center justify-center transition-all duration-500"
              :class="isLoading
                ? 'bg-amber-500/20 shadow-[0_0_40px_8px_rgba(251,191,36,0.15)]'
                : 'bg-gradient-to-br from-amber-500/15 to-orange-600/15 hover:from-amber-500/25 hover:to-orange-600/25'"
            >
              <Loader2Icon v-if="isLoading" :size="32" class="text-amber-400 animate-spin" />
              <FingerprintIcon v-else :size="32" class="text-amber-400" />
            </div>
          </div>

          <!-- Text -->
          <div class="text-center space-y-2">
            <h2 class="text-xl font-semibold text-white tracking-tight">
              {{ isLoading ? t('operator_verifying_identity') : t('operator_verification') }}
            </h2>
            <p class="text-sm text-white/40 leading-relaxed max-w-[260px]">
              <template v-if="isLoading">
                {{ t('operator_confirm_device') }}
              </template>
              <template v-else>
                {{ t('operator_requires_access') }}
              </template>
            </p>
          </div>
        </div>

        <!-- Steps - only show when not loading -->
        <Transition
          enter-active-class="transition-all duration-300"
          leave-active-class="transition-all duration-200"
          enter-from-class="opacity-0 -translate-y-2"
          leave-to-class="opacity-0 translate-y-2"
        >
          <div v-if="!isLoading" class="space-y-2">
            <div
              v-for="(step, i) in steps"
              :key="i"
              class="flex items-center gap-3 px-4 py-3 rounded-xl bg-white/[0.03] border border-white/[0.04] transition-all hover:bg-white/[0.05]"
            >
              <span class="flex-shrink-0 w-5 h-5 rounded-full bg-amber-500/10 border border-amber-500/20 flex items-center justify-center text-[10px] font-bold text-amber-400">
                {{ i + 1 }}
              </span>
              <div class="min-w-0">
                <p class="text-[13px] font-medium text-white/80">{{ step.title }}</p>
                <p class="text-[11px] text-white/30">{{ step.desc }}</p>
              </div>
            </div>
          </div>
        </Transition>

        <!-- Action -->
        <div class="space-y-2">
          <Button
            class="w-full h-12 text-sm font-semibold rounded-xl transition-all duration-300"
            :class="isLoading
              ? 'bg-amber-500/20 text-amber-300 cursor-wait'
              : 'bg-amber-500 hover:bg-amber-400 text-black hover:shadow-[0_0_24px_-4px_rgba(251,191,36,0.4)]'"
            :disabled="isLoading"
            @click="emit('verify')"
          >
            <ShieldCheckIcon :size="16" class="mr-2" />
            {{ isLoading ? t('operator_waiting_device') : t('operator_authenticate') }}
          </Button>

          <button
            v-if="!isLoading"
            class="w-full py-2 text-xs text-white/25 hover:text-white/50 transition-colors"
            @click="emit('cancel')"
          >
            {{ t('cancel') }}
          </button>
        </div>

        <!-- Security badge -->
        <div class="flex items-center justify-center gap-1.5 pt-1">
          <ShieldCheckIcon :size="10" class="text-white/15" />
          <span class="text-[10px] text-white/15 tracking-wider uppercase">{{ t('operator_fips_badge') }}</span>
        </div>
      </div>
    </Transition>
  </div>
</template>
