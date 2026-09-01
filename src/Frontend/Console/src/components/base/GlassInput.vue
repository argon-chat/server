<script setup lang="ts">
import { useVModel } from "@vueuse/core"

const props = defineProps<{
  modelValue: string
  placeholder?: string
  type?: string
  disabled?: boolean
  error?: string | null
  id?: string | null
  class?: string
}>()

const emit = defineEmits<{
  (e: "update:modelValue", payload: string): void
}>()

const val = useVModel(props, "modelValue", emit, {
  passive: true,
  defaultValue: "",
})
</script>

<template>
  <input
    v-model="val"
    :type="type || 'text'"
    :placeholder="placeholder"
    :disabled="disabled"
    :id="id ?? undefined"
    :class="[
      'glass-input w-full px-3 py-2 text-sm outline-hidden',
      error ? 'border-danger! shadow-[0_0_0_3px_rgba(239,68,68,0.15)]' : '',
      disabled ? 'opacity-50 cursor-not-allowed' : '',
      $props.class,
    ]"
  />
</template>
