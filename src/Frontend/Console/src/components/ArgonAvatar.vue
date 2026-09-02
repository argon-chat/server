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
    /**
     * A ready address, used as-is instead of resolving `fileId` against file storage.
     *
     * This is how the signed-in user's own avatar arrives: `avatarUrl` from Aegis' userinfo, the
     * address the deployment is configured to publish rather than one this widget assembles from a
     * file id and a hostname it happens to know. Only that avatar has such an address — userinfo
     * describes the caller and nobody else — so teams, bots and other members still go through file
     * storage below.
     */
    src?: string | null
  }>(),
  {
    overridedSize: undefined,
    src: null,
  },
)

// Withheld from the resolver when an address was given, rather than resolved and then ignored:
// `useAvatarBlob` fetches on sight, and handing it a file id it must not fetch would put a request
// to file storage behind every avatar that already has an address.
const fileIdRef = computed(() => (props.src ? null : props.fileId))
const userIdRef = toRef(props, "userId")

const blob = useAvatarBlob(fileIdRef, userIdRef, "user")

const loading = computed(() => (props.src ? false : blob.loading.value))
const loaded = computed(() => (props.src ? true : blob.loaded.value))
const blobSrc = computed(() => props.src ?? blob.blobSrc.value)

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