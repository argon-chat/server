<script setup lang="ts">
import { ref, onMounted, computed } from "vue"
import { X, CheckCircle2, AlertCircle } from "@lucide/vue"

export interface ToastItem {
  id: number
  title: string
  description?: string
  variant: "default" | "destructive"
  duration: number
  leaving: boolean
}

const props = defineProps<{ toast: ToastItem }>()
const emit = defineEmits<{ (e: "dismiss", id: number): void }>()

const progress = ref(100)
const paused = ref(false)
let startTime = 0
let remaining = 0
let raf = 0

const accentColor = computed(() =>
  props.toast.variant === "destructive" ? "red" : "green"
)

function tick(now: number) {
  if (paused.value) { raf = requestAnimationFrame(tick); return }
  const elapsed = now - startTime
  const pct = Math.max(0, 100 - (elapsed / props.toast.duration) * 100)
  progress.value = pct
  if (pct <= 0) { dismiss(); return }
  raf = requestAnimationFrame(tick)
}

function pause() {
  paused.value = true
  remaining = (progress.value / 100) * props.toast.duration
}

function resume() {
  paused.value = false
  startTime = performance.now() - (props.toast.duration - remaining)
}

function dismiss() {
  cancelAnimationFrame(raf)
  emit("dismiss", props.toast.id)
}

onMounted(() => {
  remaining = props.toast.duration
  startTime = performance.now()
  raf = requestAnimationFrame(tick)
})
</script>

<template>
  <div
    class="group relative w-80 overflow-hidden rounded-xl border backdrop-blur-2xl shadow-2xl transition-all duration-300"
    :class="[
      toast.leaving ? 'opacity-0 translate-x-8' : 'opacity-100 translate-x-0',
      toast.variant === 'destructive'
        ? 'border-red-500/20 bg-[rgba(25,10,10,0.92)]'
        : 'border-green-500/20 bg-[rgba(10,20,15,0.92)]',
    ]"
    @mouseenter="pause"
    @mouseleave="resume"
  >
    <div class="flex gap-3 p-3.5">
      <div class="mt-0.5 shrink-0">
        <AlertCircle v-if="toast.variant === 'destructive'" class="w-4.5 h-4.5 text-red-400" />
        <CheckCircle2 v-else class="w-4.5 h-4.5 text-green-400" />
      </div>
      <div class="flex-1 min-w-0">
        <p class="text-[13px] font-medium leading-tight"
           :class="toast.variant === 'destructive' ? 'text-red-300' : 'text-green-300'">
          {{ toast.title }}
        </p>
        <p v-if="toast.description" class="text-xs text-white/50 mt-1 leading-relaxed">
          {{ toast.description }}
        </p>
      </div>
      <button
        class="shrink-0 p-0.5 rounded-md text-white/30 hover:text-white/70 transition-colors cursor-pointer opacity-0 group-hover:opacity-100"
        @click="dismiss"
      >
        <X class="w-3.5 h-3.5" />
      </button>
    </div>
    <!-- Progress bar -->
    <div class="h-[2px] w-full bg-black/20">
      <div
        class="h-full transition-none"
        :class="toast.variant === 'destructive' ? 'bg-red-500/40' : 'bg-green-500/40'"
        :style="{ width: `${progress}%` }"
      />
    </div>
  </div>
</template>
