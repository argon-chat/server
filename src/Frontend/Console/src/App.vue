<script setup lang="ts">
import { onMounted } from "vue"
import { useUserStore } from "@/store/useUserStore"
import { useFileStorage } from "@/store/fileStorage"
import { ensureAuthenticated } from "./composables/useOAuth"
import ConsoleLayout from "@/layouts/ConsoleLayout.vue"

const userStore = useUserStore()
const cache = useFileStorage()

onMounted(async () => {
  await ensureAuthenticated()
  await cache.initStorages()
  await userStore.fetchUser()
})
</script>

<template>
  <ConsoleLayout>
    <RouterView />
  </ConsoleLayout>
</template>
