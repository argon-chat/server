<script setup lang="ts">
import { computed, toRef, type HTMLAttributes } from "vue"
import { useAvatarBlob } from "@/lib/useAvatarBlob"

const props = withDefaults(
  defineProps<{
    class?: HTMLAttributes["class"]
    fileId: string | null
    fallback: string
    serverId?: string
    userId?: string
    overridedSize?: number
  }>(),
  {
    overridedSize: undefined,
  },
)

const fileIdRef = toRef(props, "fileId")
const userIdRef = toRef(props, "userId")

const { loaded, loading, blobSrc } = useAvatarBlob(
  fileIdRef,
  userIdRef,
  "user",
)

const size = computed(() =>
  props.overridedSize ? `${props.overridedSize}px` : "40px",
)
</script>

<template>
  <keep-alive :max="10" :key="props.fileId!">
    <div
      :class="['relative rounded-full overflow-hidden shrink-0 bg-surface-2 border border-glass-border', props.class]"
      :style="{ width: size, height: size }"
    >
      <!-- Loading -->
      <div v-if="loading" class="w-full h-full bg-white/5 animate-pulse" />

      <!-- Loaded -->
      <video
        v-else-if="loaded"
        playsinline autoplay muted loop
        :poster="blobSrc"
        :src="blobSrc"
        disablePictureInPicture
        controlslist="nodownload nofullscreen noremoteplayback"
        class="w-full h-full object-cover"
      />

      <!-- Fallback -->
      <div
        v-else
        class="w-full h-full flex items-center justify-center text-text-secondary font-medium bg-surface-2"
        :style="{ fontSize: `${(props.overridedSize ?? 40) * 0.4}px` }"
      >
        {{ props.fallback.at(0)?.toUpperCase() }}
      </div>
    </div>
  </keep-alive>
</template>