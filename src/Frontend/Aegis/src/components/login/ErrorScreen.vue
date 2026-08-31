<script setup lang="ts">
import { Button } from "@argon/ui/button";
import { XIcon, ArrowLeftIcon } from "@lucide/vue";
import { ref, onMounted } from "vue";

defineProps<{
  title: string;
  message: string;
}>();

const emit = defineEmits<{
  dismiss: [];
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
        class="w-full max-w-sm rounded-3xl border border-white/[0.08] bg-black/60 backdrop-blur-2xl shadow-[0_0_80px_-20px_rgba(239,68,68,0.12)] p-8 space-y-7 relative overflow-hidden"
      >
        <!-- Ambient glow -->
        <div class="absolute -top-20 left-1/2 -translate-x-1/2 w-60 h-60 bg-red-500/[0.05] rounded-full blur-3xl pointer-events-none" />

        <!-- Icon with shake -->
        <div class="flex flex-col items-center space-y-5 pt-2">
          <div class="relative">
            <!-- Pulse ring -->
            <div class="absolute inset-0 rounded-full border border-red-400/20 animate-[pulse_3s_ease-in-out_infinite]" />
            <div class="absolute -inset-3 rounded-full border border-red-400/10 animate-[pulse_3s_ease-in-out_infinite_0.5s]" />

            <div class="relative w-16 h-16 rounded-full bg-red-500/[0.12] flex items-center justify-center">
              <XIcon :size="28" class="text-red-400" stroke-width="2.5" />
            </div>
          </div>

          <div class="text-center space-y-2">
            <h2 class="text-lg font-semibold text-white tracking-tight">{{ title }}</h2>
            <p class="text-sm text-white/35 leading-relaxed max-w-[280px]">
              {{ message }}
            </p>
          </div>
        </div>

        <!-- Error details -->
        <div class="px-4 py-3 rounded-xl bg-red-500/[0.04] border border-red-500/[0.08]">
          <p class="text-[11px] text-red-300/50 text-center font-mono">{{ title }}</p>
        </div>

        <!-- Action -->
        <Button
          @click="emit('dismiss')"
          class="w-full h-11 text-sm font-semibold rounded-xl bg-white/[0.06] hover:bg-white/[0.1] text-white/70 hover:text-white border border-white/[0.06] hover:border-white/[0.1] transition-all duration-300"
        >
          <ArrowLeftIcon :size="14" class="mr-2" />
          Go Back
        </Button>
      </div>
    </Transition>
  </div>
</template>
