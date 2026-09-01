<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick, watch } from "vue"
import { ChevronDown } from "@lucide/vue"

const props = defineProps<{
  modelValue: string
  placeholder?: string
  options: { value: string; label: string }[]
  disabled?: boolean
}>()

const emit = defineEmits<{
  (e: "update:modelValue", value: string): void
}>()

const open = ref(false)
const containerRef = ref<HTMLElement | null>(null)

const selectedLabel = computed(() => {
  const opt = props.options.find(o => o.value === props.modelValue)
  return opt?.label ?? props.placeholder ?? "Select..."
})

function select(value: string) {
  emit("update:modelValue", value)
  open.value = false
}

function onClickOutside(e: MouseEvent) {
  if (containerRef.value && !containerRef.value.contains(e.target as Node)) {
    open.value = false
  }
}

watch(open, (val) => {
  if (val) {
    document.addEventListener("click", onClickOutside, { capture: true })
  } else {
    document.removeEventListener("click", onClickOutside, { capture: true })
  }
})

onUnmounted(() => {
  document.removeEventListener("click", onClickOutside, { capture: true })
})
</script>

<template>
  <div ref="containerRef" class="relative">
    <button
      type="button"
      :disabled="disabled"
      class="glass-input flex w-full items-center justify-between px-3 py-2 text-sm cursor-pointer"
      :class="[disabled && 'opacity-50 cursor-not-allowed']"
      @click="open = !open"
    >
      <span :class="modelValue ? 'text-text-primary' : 'text-text-muted'">
        {{ selectedLabel }}
      </span>
      <ChevronDown class="w-4 h-4 text-text-muted transition-transform" :class="open && 'rotate-180'" />
    </button>

    <Transition name="dropdown">
      <div
        v-if="open"
        class="absolute z-50 mt-1 w-full glass-card p-1 max-h-60 overflow-y-auto animate-slide-in-down"
      >
        <button
          v-for="option in options"
          :key="option.value"
          type="button"
          class="w-full text-left px-3 py-2 text-sm rounded-md transition-colors cursor-pointer"
          :class="option.value === modelValue
            ? 'bg-accent/15 text-accent'
            : 'text-text-primary hover:bg-white/5'"
          @click="select(option.value)"
        >
          {{ option.label }}
        </button>
      </div>
    </Transition>
  </div>
</template>

<style scoped>
.dropdown-enter-active,
.dropdown-leave-active {
  transition: all 0.15s ease;
}
.dropdown-enter-from,
.dropdown-leave-to {
  opacity: 0;
  transform: translateY(-4px);
}
</style>
