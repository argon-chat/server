<script setup lang="ts">
import { Input } from "@argon/ui/input";
import { ExclamationTriangleIcon } from "@radix-icons/vue";
import { useVModel } from "@vueuse/core";
import { ref, watch } from "vue";

const props = defineProps<{
  modelValue: string;
  placeholder?: string;
  type?: string;
  disabled?: boolean;
  error?: string | null;
  id?: string | null
}>();

const emit = defineEmits<{
  (e: "update:modelValue", payload: string | number): void;
  (e: "clear-error"): void;
}>();

const glitching = ref(false);

const val = useVModel(props, "modelValue", emit, {
  passive: true,
  defaultValue: "",
});

watch(
  () => props.error,
  (val) => {
    if (val) {
      glitching.value = true;
      setTimeout(() => {
        glitching.value = false;
      }, 400);
    }
  }
);

function handleInput() {
  if (props.error) {
    emit("clear-error");
  }
}
</script>

<template>
  <div class="w-full space-y-1">
    <div class="flex items-center justify-between relative">
      <slot name="label" />
      <transition name="slide-fade">
        <div v-if="error" :id="props.id ? `${props.id}-error` : undefined" role="alert" aria-live="assertive" class="absolute top-[-6px] right-[-8px] px-2 py-0.5
             bg-black/70 border border-red-500
             text-red-400 text-[12px] font-mono tracking-wider
             rounded-md shadow-[0_0_8px_rgba(255,0,80,0.6)]
             flex items-center gap-1 overflow-hidden
             translate-x-2 -translate-y-1" :class="{ 'animate-glitch': glitching }">
          <ExclamationTriangleIcon class="w-4 h-4 shrink-0 text-red-500" />
          <span>{{ error }}</span>
        </div>
      </transition>
    </div>

    <Input v-model="val" :placeholder="placeholder" :type="type || 'text'" :disabled="disabled"
           :aria-invalid="!!error" :aria-describedby="error && props.id ? `${props.id}-error` : undefined"
           class="h-11 rounded-xl bg-black/50 border  z-10
             text-white placeholder-gray-500
             focus:ring-2 transition
             w-full cyber-input" :class="error
              ? 'border-red-500 focus:border-red-500 focus:ring-red-500/50'
              : 'border-gray-700 focus:border-blue-500 focus:ring-blue-500/30'
              " @input="handleInput" :id="props.id" />
  </div>
</template>

<style scoped>
.slide-fade-enter-active,
.slide-fade-leave-active {
  transition: all 0.2s ease;
}

.slide-fade-enter-from,
.slide-fade-leave-to {
  opacity: 0;
  transform: translateY(-2px);
}

@keyframes glitch {
  0% {
    transform: translate(0);
    text-shadow: 0 0 2px #ff005e, 0 0 4px #00f7ff;
  }

  20% {
    transform: translate(-1px, 1px);
    text-shadow: 1px 0 red, -1px 0 cyan;
  }

  40% {
    transform: translate(1px, -1px);
    text-shadow: -1px 0 red, 1px 0 cyan;
  }

  60% {
    transform: translate(-1px, -1px);
    text-shadow: 1px 1px red, -1px -1px cyan;
  }

  80% {
    transform: translate(1px, 1px);
    text-shadow: -1px 0 red, 1px 0 cyan;
  }

  100% {
    transform: translate(0);
    text-shadow: 0 0 2px #ff005e, 0 0 4px #00f7ff;
  }
}

.animate-glitch {
  animation: glitch 0.2s infinite;
}
</style>
