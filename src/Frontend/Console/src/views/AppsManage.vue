<script setup lang="ts">
import { ref, watch, onMounted } from "vue"
import { storeToRefs } from "pinia"
import { useTeamsStore } from "@/store/useTeamsStore"
import { AppWindow, Bot, Globe, ArrowLeft } from "@lucide/vue"
import { GlassButton, GlassCard } from "@/components/base"
import AnnouncementsCard from "@/components/AnnouncementsCard.vue"
import CreateNewAppCard from "@/components/CreateNewAppCard.vue"
import ResourcesCard from "@/components/ResourcesCard.vue"
import TeamInfoCard from "@/components/TeamInfoCard.vue"
import { useToast } from "@/composables/useToast"
import { useApi } from "@/store/apiStore"
import router from "@/router"

const api = useApi()
const { toast } = useToast()
const teamsStore = useTeamsStore()
const { selectedTeam } = storeToRefs(teamsStore)

interface AppRow {
  appId: string
  name: string
  kind: string
  createdAt: string
}

const apps = ref<AppRow[]>([])
const isLoading = ref(false)
const errorMessage = ref<string | null>(null)

async function fetchApps() {
  if (!selectedTeam.value) {
    apps.value = []
    return
  }
  try {
    isLoading.value = true
    errorMessage.value = null
    const details = await api.teamsManagement.GetTeamDetails(selectedTeam.value.teamId)
    apps.value = (details.apps ?? []).map((app: any) => ({
      appId: app.appId,
      name: app.name,
      kind: appKindLabel(app.kind),
      createdAt: app.createdAt.toDate().toLocaleDateString(),
    }))
  } catch (err: any) {
    errorMessage.value = err?.message ?? "Failed to load apps"
  } finally {
    isLoading.value = false
  }
}

function appKindLabel(kind: number) {
  switch (kind) {
    case 0: return "Client App"
    case 1: return "Bot App"
    case 2: return "Web App"
    default: return "Unknown"
  }
}

function kindIcon(kind: string) {
  if (kind === "Bot App") return Bot
  if (kind === "Web App") return Globe
  return AppWindow
}

function manageApp(appId: string) {
  if (!selectedTeam.value) {
    toast({ title: "No team selected", description: "Please select a team first.", variant: "destructive" })
    return
  }
  router.push({ name: "AppDetails", params: { teamId: selectedTeam.value.teamId, appId } })
}

watch(selectedTeam, async (newTeam) => {
  if (!newTeam) { apps.value = []; return }
  await fetchApps()
})

onMounted(async () => {
  if (selectedTeam.value) await fetchApps()
})
</script>

<template>
  <div class="max-w-7xl mx-auto px-6 py-8 animate-fade-in">
    <!-- Header -->
    <div class="flex items-center justify-between mb-8">
      <div class="flex items-center gap-3">
        <button
          @click="router.back()"
          class="p-1.5 rounded-lg hover:bg-white/5 text-text-muted hover:text-text-primary transition-colors cursor-pointer"
        >
          <ArrowLeft class="w-5 h-5" />
        </button>
        <div>
          <h1 class="text-2xl font-semibold text-text-primary">Your Applications</h1>
          <p class="text-sm text-text-muted mt-0.5">Manage your published and pending applications.</p>
        </div>
      </div>
    </div>

    <div class="flex gap-8">
      <!-- Main content -->
      <div class="flex-1 min-w-0">
        <!-- Table card -->
        <GlassCard no-padding class="overflow-hidden">
          <!-- Table header -->
          <div class="px-5 py-3 border-b border-glass-border flex items-center justify-between">
            <span class="text-sm font-medium text-text-secondary">
              {{ apps.length }} application{{ apps.length !== 1 ? 's' : '' }}
            </span>
          </div>

          <!-- Table -->
          <div class="overflow-x-auto">
            <table class="w-full">
              <thead>
                <tr class="text-left text-xs uppercase tracking-wider text-text-muted border-b border-glass-border">
                  <th class="px-5 py-3 font-medium">App Name</th>
                  <th class="px-5 py-3 font-medium">Type</th>
                  <th class="px-5 py-3 font-medium text-center">Created</th>
                  <th class="px-5 py-3 font-medium text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                <tr
                  v-for="app in apps"
                  :key="app.appId"
                  class="border-b border-glass-border/50 hover:bg-white/[0.02] transition-colors"
                >
                  <td class="px-5 py-3.5 text-sm text-text-primary font-medium">{{ app.name }}</td>
                  <td class="px-5 py-3.5">
                    <span class="flex items-center gap-2 text-sm text-text-secondary">
                      <component :is="kindIcon(app.kind)" class="w-4 h-4 text-text-muted" />
                      {{ app.kind }}
                    </span>
                  </td>
                  <td class="px-5 py-3.5 text-sm text-text-muted text-center">{{ app.createdAt }}</td>
                  <td class="px-5 py-3.5 text-right">
                    <GlassButton size="xs" variant="outline" @click="manageApp(app.appId)">
                      Manage
                    </GlassButton>
                  </td>
                </tr>
              </tbody>
            </table>

            <!-- Empty state -->
            <div v-if="!apps.length && !isLoading" class="py-16 text-center">
              <AppWindow class="w-10 h-10 text-text-muted mx-auto mb-3 opacity-40" />
              <p class="text-sm text-text-muted">
                {{ selectedTeam ? 'No applications yet.' : 'Select a team first.' }}
              </p>
            </div>

            <!-- Loading -->
            <div v-if="isLoading" class="py-16 flex items-center justify-center">
              <div class="w-5 h-5 border-2 border-accent/30 border-t-accent rounded-full animate-spin" />
            </div>
          </div>

          <!-- Error footer -->
          <div v-if="errorMessage" class="px-5 py-3 border-t border-danger/20">
            <p class="text-sm text-red-400">{{ errorMessage }}</p>
          </div>
        </GlassCard>
      </div>

      <!-- Sidebar -->
      <div class="w-72 shrink-0 flex flex-col gap-4 hidden lg:flex">
        <TeamInfoCard :selected-team="selectedTeam" />
        <AnnouncementsCard />
        <CreateNewAppCard :selected-team="selectedTeam" @app-created="fetchApps" />
        <ResourcesCard />
      </div>
    </div>
  </div>
</template>
