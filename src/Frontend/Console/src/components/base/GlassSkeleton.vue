<script setup lang="ts">
defineProps<{
  class?: string
}>()
</script>

<template>
  <div
    :class="['rounded-lg bg-white/5 animate-pulse', $props.class]"
  />
</template>
