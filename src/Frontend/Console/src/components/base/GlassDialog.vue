<script setup lang="ts">
import { ref, watch, onMounted, onUnmounted } from "vue"

const props = defineProps<{
  open: boolean
}>()

const emit = defineEmits<{
  (e: "update:open", value: boolean): void
}>()

const dialogRef = ref<HTMLDialogElement | null>(null)

function close() {
  emit("update:open", false)
}

function onBackdropClick(e: MouseEvent) {
  if (e.target === dialogRef.value) {
    close()
  }
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === "Escape") close()
}

watch(() => props.open, (val) => {
  if (val) {
    document.body.style.overflow = "hidden"
  } else {
    document.body.style.overflow = ""
  }
})

onMounted(() => {
  document.addEventListener("keydown", onKeydown)
})
onUnmounted(() => {
  document.removeEventListener("keydown", onKeydown)
  document.body.style.overflow = ""
})
</script>

<template>
  <Teleport to="body">
    <Transition name="dialog">
      <div
        v-if="open"
        ref="dialogRef"
        class="fixed inset-0 z-50 flex items-center justify-center p-4"
        @click="onBackdropClick"
      >
        <!-- Backdrop -->
        <div class="absolute inset-0 bg-black/60 backdrop-blur-sm" />

        <!-- Content -->
        <div
          class="relative glass-card w-full max-w-md p-6 animate-slide-in-up"
          @click.stop
        >
          <slot />
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.dialog-enter-active,
.dialog-leave-active {
  transition: opacity 0.2s ease;
}
.dialog-enter-from,
.dialog-leave-to {
  opacity: 0;
}
</style>
