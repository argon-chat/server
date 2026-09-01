<script setup lang="ts">
import { storeToRefs } from "pinia"
import { useUserStore } from "@/store/useUserStore"
import { Loader2 } from "@lucide/vue"
import TopBar from "@/components/layout/TopBar.vue"
import { GlassToaster } from "@/components/base"

const userStore = useUserStore()
const { user, isLoading, isLoaded, errorMessage } = storeToRefs(userStore)
</script>

<template>
  <div class="flex flex-col h-screen overflow-hidden">
    <!-- Top bar -->
    <TopBar />

    <!-- Content -->
    <main class="flex-1 overflow-y-auto scrollbar-thin">
      <!-- Loading -->
      <div
        v-if="isLoading && !isLoaded && !errorMessage"
        class="flex flex-1 h-full flex-col items-center justify-center gap-3 text-text-secondary"
      >
        <Loader2 class="w-8 h-8 animate-spin text-accent" />
        <p class="text-sm text-text-muted">Loading your workspace...</p>
      </div>

      <!-- Error -->
      <div
        v-else-if="errorMessage && !isLoaded"
        class="flex flex-1 h-full flex-col items-center justify-center gap-4 text-text-secondary"
      >
        <p class="text-lg">{{ errorMessage }}</p>
        <button
          @click="userStore.retryFetch"
          class="px-5 py-2.5 bg-accent hover:bg-accent-hover rounded-lg text-white text-sm transition-colors accent-glow cursor-pointer"
        >
          Retry
        </button>
      </div>

      <!-- Loaded -->
      <template v-else-if="isLoaded && user">
        <slot />
      </template>

      <!-- Fallback -->
      <div v-else class="flex flex-1 h-full flex-col items-center justify-center gap-3 text-text-muted">
        <Loader2 class="w-8 h-8 animate-spin" />
        <p class="text-sm">Connecting...</p>
      </div>
    </main>

    <GlassToaster />
  </div>
</template>
