<script setup lang="ts">
import { BookOpen, ExternalLink } from "@lucide/vue"
import { GlassCard } from "@/components/base"

const links = [
  { label: "Developer Docs", href: "https://docs.argon.gl" },
  { label: "Status Page", href: "https://status.argon.gl" },
  { label: "Bots Docs", href: "https://bots.argon.gl" },
]
</script>

<template>
  <GlassCard class="space-y-3">
    <div class="flex items-center gap-2">
      <BookOpen class="w-4 h-4 text-accent" />
      <h3 class="text-sm font-semibold text-text-primary">Resources</h3>
    </div>
    <p class="text-[11px] text-text-muted">Helpful links and docs</p>
    <div class="flex flex-col gap-1.5">
      <a
        v-for="link in links"
        :key="link.href"
        :href="link.href"
        target="_blank"
        rel="noopener"
        class="flex items-center gap-2 text-sm text-accent hover:text-accent-hover transition-colors group"
      >
        {{ link.label }}
        <ExternalLink class="w-3 h-3 opacity-0 group-hover:opacity-100 transition-opacity" />
      </a>
    </div>
  </GlassCard>
</template>
