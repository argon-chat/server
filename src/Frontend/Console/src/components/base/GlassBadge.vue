<script setup lang="ts">
type BadgeVariant = "default" | "accent" | "danger" | "success" | "warning" | "muted"

const props = withDefaults(defineProps<{
  variant?: BadgeVariant
}>(), {
  variant: "default",
})

const variantClasses: Record<BadgeVariant, string> = {
  default: "bg-white/5 text-text-secondary border-glass-border",
  accent: "bg-accent/15 text-accent border-accent/30",
  danger: "bg-danger/15 text-red-400 border-danger/30",
  success: "bg-success/15 text-green-400 border-success/30",
  warning: "bg-warning/15 text-amber-400 border-warning/30",
  muted: "bg-white/3 text-text-muted border-transparent",
}
</script>

<template>
  <span :class="[
    'inline-flex items-center gap-1 px-2 py-0.5 text-xs font-medium rounded-md border',
    variantClasses[variant],
  ]">
    <slot />
  </span>
</template>
