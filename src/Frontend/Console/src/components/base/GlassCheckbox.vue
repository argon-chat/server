<script setup lang="ts">
const props = defineProps<{
  modelValue: boolean
  disabled?: boolean
  label?: string
}>()

const emit = defineEmits<{
  (e: "update:modelValue", value: boolean): void
}>()

function toggle() {
  if (!props.disabled) {
    emit("update:modelValue", !props.modelValue)
  }
}
</script>

<template>
  <label
    class="inline-flex items-center gap-2 cursor-pointer select-none"
    :class="disabled && 'opacity-50 cursor-not-allowed'"
    @click.prevent="toggle"
  >
    <div
      class="w-4 h-4 rounded border transition-all duration-150 flex items-center justify-center"
      :class="modelValue
        ? 'bg-accent border-accent'
        : 'border-glass-border-hover bg-transparent hover:border-text-muted'"
    >
      <svg
        v-if="modelValue"
        class="w-3 h-3 text-white"
        fill="none"
        viewBox="0 0 24 24"
        stroke="currentColor"
        stroke-width="3"
      >
        <path stroke-linecap="round" stroke-linejoin="round" d="M5 13l4 4L19 7" />
      </svg>
    </div>
    <span v-if="label" class="text-sm text-text-primary">{{ label }}</span>
    <slot />
  </label>
</template>
