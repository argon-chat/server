<script setup lang="ts">
import { computed, type HTMLAttributes } from "vue"
import { Loader2 } from "@lucide/vue"

type ButtonVariant = "default" | "ghost" | "outline" | "danger" | "accent" | "success"
type ButtonSize = "xs" | "sm" | "md" | "lg" | "icon"

const props = withDefaults(defineProps<{
  variant?: ButtonVariant
  size?: ButtonSize
  disabled?: boolean
  loading?: boolean
  class?: HTMLAttributes["class"]
}>(), {
  variant: "default",
  size: "md",
})

const variantClasses: Record<ButtonVariant, string> = {
  default: "glass glass-hover text-text-primary hover:text-white",
  ghost: "bg-transparent hover:bg-white/5 text-text-secondary hover:text-text-primary border-transparent",
  outline: "bg-transparent border border-glass-border hover:border-glass-border-hover text-text-primary hover:bg-white/5",
  danger: "bg-danger-muted border border-danger/30 text-red-300 hover:bg-danger/20 hover:border-danger/50",
  accent: "bg-accent text-white hover:bg-accent-hover accent-glow border-transparent",
  success: "bg-success-muted border border-success/30 text-green-300 hover:bg-success/20",
}

const sizeClasses: Record<ButtonSize, string> = {
  xs: "px-2 py-1 text-xs rounded-md gap-1",
  sm: "px-3 py-1.5 text-sm rounded-lg gap-1.5",
  md: "px-4 py-2 text-sm rounded-lg gap-2",
  lg: "px-6 py-3 text-base rounded-xl gap-2",
  icon: "p-2 rounded-lg",
}

const classes = computed(() => [
  "inline-flex items-center justify-center font-medium transition-all duration-150 cursor-pointer select-none",
  variantClasses[props.variant],
  sizeClasses[props.size],
  (props.disabled || props.loading) && "opacity-50 pointer-events-none",
  props.class,
])
</script>

<template>
  <button :class="classes" :disabled="disabled || loading">
    <Loader2 v-if="loading" class="w-4 h-4 animate-spin" />
    <slot />
  </button>
</template>
