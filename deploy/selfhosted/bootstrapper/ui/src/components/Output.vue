<script setup lang="ts">
/**
 * Raw output from something else — a validation report, `docker compose`, a container's log.
 *
 * Never reflowed and never trimmed to fit. This is the text the operator will paste into a bug report
 * or search for a line of, and a version of it that has been tidied is a version that does not match
 * what they will see in a terminal.
 */
defineProps<{ text: string }>();
</script>

<template>
  <pre
    class="mono text-xs text-text-secondary bg-oled-black border border-oled-border-subtle rounded-lg p-3 overflow-auto"
    style="max-height: 18rem; white-space: pre-wrap; word-break: break-word"
  >{{ text }}</pre>
</template>
