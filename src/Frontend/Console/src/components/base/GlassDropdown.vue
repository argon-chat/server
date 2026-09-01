<script setup lang="ts">
import { ref, onMounted, onUnmounted, nextTick, watch } from "vue"

const props = defineProps<{
  open: boolean
  align?: "left" | "right"
}>()

const emit = defineEmits<{
  (e: "update:open", value: boolean): void
}>()

const dropdownRef = ref<HTMLElement | null>(null)

function close() {
  emit("update:open", false)
}

function onClickOutside(e: MouseEvent) {
  if (dropdownRef.value && !dropdownRef.value.contains(e.target as Node)) {
    close()
  }
}

watch(() => props.open, async (val) => {
  if (val) {
    await nextTick()
    document.addEventListener("click", onClickOutside, { capture: true })
  } else {
    document.removeEventListener("click", onClickOutside, { capture: true })
  }
})

onUnmounted(() => {
  document.removeEventListener("click", onClickOutside, { capture: true })
})
</script>

<template>
  <div class="relative inline-block" ref="dropdownRef">
    <div @click="emit('update:open', !open)">
      <slot name="trigger" />
    </div>

    <Transition name="dropdown">
      <div
        v-if="open"
        :class="[
          'absolute z-50 mt-2 min-w-[14rem] glass-card p-1 animate-slide-in-down',
          align === 'right' ? 'right-0' : 'left-0',
        ]"
      >
        <slot />
      </div>
    </Transition>
  </div>
</template>

<style scoped>
.dropdown-enter-active,
.dropdown-leave-active {
  transition: all 0.15s ease;
}
.dropdown-enter-from,
.dropdown-leave-to {
  opacity: 0;
  transform: translateY(-4px);
}
</style>
