<script setup lang="ts">
import { toasts, dismiss } from "@/composables/useToast"
import GlassToast from "./GlassToast.vue"
</script>

<template>
  <Teleport to="body">
    <div
      v-if="toasts.length"
      class="fixed bottom-4 right-4 z-[9999] flex flex-col-reverse gap-2 pointer-events-none"
    >
      <div v-for="t in toasts" :key="t.id" class="pointer-events-auto">
        <GlassToast :toast="t" @dismiss="dismiss" />
      </div>
    </div>
  </Teleport>
</template>
