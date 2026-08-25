<script setup lang="ts">
import Icon from "./Icon.vue";

/**
 * A warning or a problem, rendered as the paragraph it is.
 *
 * These arrive as whole sentences of prose — two or three lines each — because every one describes a
 * way the instance ends up quietly broken rather than a field being wrong. Truncating one to fit a chip
 * throws away the half that says what to do about it, so this is built to hold the text rather than to
 * fit a shape.
 *
 * The two tones are not decoration. A warning does not block anything and a problem has already stopped
 * something, and an operator scanning a page needs to know which without reading.
 */
defineProps<{ tone?: "warning" | "danger" }>();
</script>

<template>
  <div
    class="flex items-start gap-3 rounded-lg border border-oled-border-subtle px-4 py-3"
    :class="tone === 'danger' ? 'bg-danger-subtle' : 'bg-warning-subtle'"
  >
    <span class="mt-0.5" :class="tone === 'danger' ? 'text-danger' : 'text-warning'">
      <Icon name="alert" />
    </span>
    <p class="text-sm text-text-secondary leading-relaxed"><slot /></p>
  </div>
</template>
