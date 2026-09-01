<script setup lang="ts">
import { GlassInput } from "@/components/base"
import { AlertTriangle } from "@lucide/vue"
import { useVModel } from "@vueuse/core"
import { ref, watch } from "vue"

const props = defineProps<{
  modelValue: string
  placeholder?: string
  type?: string
  disabled?: boolean
  error?: string | null
  id?: string | null
}>()

const emit = defineEmits<{
  (e: "update:modelValue", payload: string | number): void
  (e: "clear-error"): void
}>()

const glitching = ref(false)

const val = useVModel(props, "modelValue", emit, {
  passive: true,
  defaultValue: "",
})

watch(
  () => props.error,
  (val) => {
    if (val) {
      glitching.value = true
      setTimeout(() => { glitching.value = false }, 400)
    }
  }
)

function handleInput() {
  if (props.error) emit("clear-error")
}
</script>

<template>
  <div class="w-full space-y-1">
    <div class="flex items-center justify-between relative">
      <slot name="label" />
      <Transition name="slide-fade">
        <div
          v-if="error"
          class="absolute top-[-6px] right-[-8px] px-2 py-0.5
                 bg-surface-0/90 border border-danger/60
                 text-red-400 text-[11px] font-mono tracking-wider
                 rounded-lg shadow-[0_0_12px_rgba(239,68,68,0.2)]
                 flex items-center gap-1 overflow-hidden z-10
                 backdrop-blur-sm"
          :class="{ 'animate-pulse': glitching }"
        >
          <AlertTriangle class="w-3.5 h-3.5 shrink-0 text-red-400" />
          <span>{{ error }}</span>
        </div>
      </Transition>
    </div>

    <GlassInput
      v-model="val"
      :placeholder="placeholder"
      :type="type || 'text'"
      :disabled="disabled"
      :error="error"
      :id="id"
      @input="handleInput"
    />
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
  transform: translateX(4px);
}
</style>
