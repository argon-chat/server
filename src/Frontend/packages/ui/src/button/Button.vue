<script setup lang="ts">
import type { HTMLAttributes } from "vue";
import { Primitive, type PrimitiveProps } from "reka-ui";
import { type ButtonVariants, buttonVariants } from ".";
import { cn } from "@argon/core";

interface Props extends PrimitiveProps {
  variant?: ButtonVariants["variant"];
  size?: ButtonVariants["size"];
  class?: HTMLAttributes["class"];
  disabled?: boolean;
  clearCss?: boolean;
}

const props = withDefaults(defineProps<Props>(), {
  as: "button",
  disabled: false,
});
</script>

<template>
  <Primitive
    :as="as"
    :as-child="asChild"
    :class="props.clearCss ? props.class : cn(
      buttonVariants({ variant, size }),
      props.class,
      props.disabled ? 'opacity-50 cursor-not-allowed relative' : ''
    )"
    :disabled="props.disabled"
  >
    <slot />
  </Primitive>
</template>
