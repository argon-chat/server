<script setup lang="ts">
import delay from "@/lib/delay"
import { onMounted } from "vue"
import { useRouter } from "vue-router"
import { Loader2 } from "@lucide/vue"

const router = useRouter()

onMounted(async () => {
  await delay(2000)
  await router.push({ name: "MasterPage" })
})
</script>

<template>
  <div class="flex flex-col items-center justify-center h-full gap-3 animate-fade-in">
    <Loader2 class="w-6 h-6 animate-spin text-accent" />
    <p class="text-sm text-text-muted">Exchanging OAuth code...</p>
  </div>
</template>
