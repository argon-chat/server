<script setup lang="ts">
import { ref, onMounted, computed } from "vue"
import { useRoute, useRouter } from "vue-router"
import { Loader2, ArrowLeft } from "@lucide/vue"
import { useApi } from "@/store/apiStore"
import { useToast } from "@/composables/useToast"
import { GlassButton } from "@/components/base"
import BotAppDetails from "./BotAppDetails.vue"
import ClientAppDetails from "./ClientAppDetails.vue"
import type { AppDetails, BotDetails, ClientAppDetails as ClientApp } from "@/lib/glue/accountConsole"

const route = useRoute()
const router = useRouter()
const api = useApi()
const { toast } = useToast()

const app = ref<AppDetails | null>(null)
const bot = ref<BotDetails | null>(null)
const clientApp = ref<ClientApp | null>(null)
const isLoading = ref(true)

const teamId = computed(() => route.params.teamId as string)
const appId = computed(() => route.params.appId as string)
const isBotApp = computed(() => app.value?.kind === 1)
const isClientApp = computed(() => app.value?.kind === 0)

async function fetchAppDetails() {
  try {
    isLoading.value = true
    const details = await api.appsManagement.GetAppDetails(teamId.value, appId.value)
    app.value = details
    bot.value = details.botDetails
    clientApp.value = details.clientAppDetails
  } catch (err: any) {
    toast({ title: "Failed to load app", description: err?.message ?? "Unexpected error occurred", variant: "destructive" })
  } finally {
    isLoading.value = false
  }
}

function goBack() {
  router.push({ name: "AppsManage", params: { teamId: teamId.value } })
}

onMounted(fetchAppDetails)
</script>

<template>
  <div class="max-w-5xl mx-auto px-6 py-8 animate-fade-in">
    <div class="flex items-center gap-3 mb-8">
      <button
        @click="goBack"
        class="p-1.5 rounded-lg hover:bg-white/5 text-text-muted hover:text-text-primary transition-colors cursor-pointer"
      >
        <ArrowLeft class="w-5 h-5" />
      </button>
      <span class="text-sm text-text-muted">Back to Apps</span>
    </div>

    <div v-if="isLoading" class="flex justify-center py-20">
      <Loader2 class="w-6 h-6 animate-spin text-accent" />
    </div>

    <BotAppDetails v-else-if="isBotApp && app && bot" :app="app" :bot="bot" />
    <ClientAppDetails v-else-if="isClientApp && app && clientApp" :app="app" :client-app="clientApp" />
  </div>
</template>
