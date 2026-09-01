<script setup lang="ts">
import type { HTMLAttributes } from "vue"

defineProps<{
  class?: HTMLAttributes["class"]
  noPadding?: boolean
}>()
</script>

<template>
  <div :class="['glass-card', noPadding ? '' : 'p-6', $props.class]">
    <slot />
  </div>
</template>
