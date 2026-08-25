<script setup lang="ts">
/**
 * One question or one section, as a surface.
 *
 * `panel` is the design system's card class and carries the brand's border and elevation; the layout
 * around it is utilities. See ds-bundle/README.md — the stylesheet is prebuilt, so only classes that
 * were compiled ahead of time exist and arbitrary values silently do nothing.
 */
defineProps<{ title: string; description?: string }>();
</script>

<template>
  <section class="panel p-5 flex flex-col gap-4">
    <div class="flex flex-col gap-1">
      <h2 class="text-sm font-bold text-text-primary">{{ title }}</h2>
      <p v-if="description" class="text-xs text-text-muted leading-relaxed">{{ description }}</p>
    </div>
    <slot />
  </section>
</template>
