<script setup lang="ts">
defineProps<{
  orientation?: "horizontal" | "vertical"
}>()
</script>

<template>
  <div
    :class="orientation === 'vertical'
      ? 'w-px h-full bg-glass-border'
      : 'h-px w-full bg-glass-border'"
  />
</template>
