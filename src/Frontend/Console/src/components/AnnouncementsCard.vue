<script setup lang="ts">
import { Megaphone } from "@lucide/vue"
import { GlassCard } from "@/components/base"
</script>

<template>
  <GlassCard class="space-y-3">
    <div class="flex items-center gap-2">
      <Megaphone class="w-4 h-4 text-accent" />
      <h3 class="text-sm font-semibold text-text-primary">Announcements</h3>
    </div>
    <p class="text-[11px] text-text-muted">Latest updates and info</p>
    <ul class="text-sm text-text-secondary space-y-1.5">
      <li class="flex items-center gap-2">
        <span class="w-1 h-1 rounded-full bg-accent shrink-0" />
        Dashboard UI update
      </li>
      <li class="flex items-center gap-2">
        <span class="w-1 h-1 rounded-full bg-accent shrink-0" />
        Initial support custom clients
      </li>
    </ul>
  </GlassCard>
</template>
