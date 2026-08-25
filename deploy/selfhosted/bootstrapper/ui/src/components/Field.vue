<script setup lang="ts">
/**
 * A labelled control with room for what the server said about it.
 *
 * The error is a whole sentence from the server, not a code — `Setup.submit` answers with one per
 * rejected field — so this gives it two lines rather than a chip. The hint is shown only when there is
 * no error: replacing guidance with a complaint is the useful order, and stacking both is noise at the
 * moment somebody is trying to fix something.
 */
defineProps<{ label: string; error?: string; hint?: string }>();
</script>

<template>
  <div class="flex flex-col gap-1.5">
    <label class="s-label">{{ label }}</label>
    <slot />
    <p v-if="hint && !error" class="text-xs text-text-muted leading-relaxed">{{ hint }}</p>
    <p v-if="error" class="text-xs text-danger leading-relaxed">{{ error }}</p>
  </div>
</template>
