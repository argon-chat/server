<script setup lang="ts">
import { ref, computed, watch } from "vue"
import { Plus, Bot, AppWindow, Globe, Loader2 } from "@lucide/vue"
import { GlassButton, GlassCard, GlassDialog, GlassInput, GlassSelect } from "@/components/base"
import { useToast } from "@/composables/useToast"
import { useApi } from "@/store/apiStore"
import { ClientAppPlatform, type TeamShortDetails } from "@/lib/glue/accountConsole"
import InputWithError from "./InputWithError.vue"
import { logger } from "@argon/core"

const props = defineProps<{
  selectedTeam: TeamShortDetails | null
}>()

const emit = defineEmits<{
  (e: "app-created"): void
}>()

const showDialog = ref(false)
const appType = ref<"bot" | "client">("bot")
const appName = ref("")
const appUsername = ref("")
const appPlatform = ref("")
const usernameError = ref<string | null>(null)
const isCreating = ref(false)
const api = useApi()
const { toast } = useToast()

const platforms = [
  { value: "windows-desktop", label: "Windows Desktop" },
  { value: "macos-desktop", label: "macOS Desktop" },
  { value: "linux-desktop", label: "Linux Desktop" },
  { value: "android", label: "Android" },
  { value: "ios", label: "iOS" },
  { value: "web-based", label: "Web Based" },
]

const getClientAppPlatformByPlatform = (platform: string) => {
  switch (platform) {
    case "windows-desktop": return ClientAppPlatform.WindowsDesktop
    case "macos-desktop": return ClientAppPlatform.MacOSDesktop
    case "linux-desktop": return ClientAppPlatform.LinuxDesktop
    case "android": return ClientAppPlatform.Android
    case "ios": return ClientAppPlatform.iOS
    case "web-based": return ClientAppPlatform.WebBased
    default: return null
  }
}

const isDisabled = computed(() => !props.selectedTeam)
const isUsernameValid = computed(() => usernameError.value === null)

async function validateUsername() {
  const username = appUsername.value.trim()
  usernameError.value = null
  if (!username) return
  if (!username.toLowerCase().endsWith("bot")) {
    usernameError.value = "Username must end with 'bot'"
    return
  }
  if (!props.selectedTeam) return
  try {
    const result = await api.appsManagement.CheckUsernameForBot(props.selectedTeam.teamId, username)
    switch (result) {
      case 0: usernameError.value = null; break
      case 1: usernameError.value = "This username is already taken"; break
      case 2: usernameError.value = "Username must end with .bot"; break
      default: usernameError.value = "Invalid username"
    }
  } catch {
    usernameError.value = "Failed to validate username"
  }
}

let debounceTimer: ReturnType<typeof setTimeout> | null = null
watch(appUsername, () => {
  if (debounceTimer) clearTimeout(debounceTimer)
  debounceTimer = setTimeout(() => validateUsername(), 500)
})

function clearUsernameError() {
  usernameError.value = null
}

async function createApp() {
  if (!props.selectedTeam) {
    toast({ title: "Team not selected", description: "Please select a team first.", variant: "destructive" })
    return
  }

  if (appType.value === "bot") {
    if (!appName.value.trim() || !appUsername.value.trim()) {
      toast({ title: "Missing data", description: "Please fill in all required fields.", variant: "destructive" })
      return
    }
    if (!isUsernameValid.value) {
      toast({ title: "Invalid username", description: "Please enter a valid and available bot username.", variant: "destructive" })
      return
    }
    try {
      isCreating.value = true
      const bot = await api.appsManagement.CreateBotApp(props.selectedTeam.teamId, appName.value.trim(), appUsername.value.trim())
      toast({ title: "Bot created successfully", description: `New bot "${bot.name}" has been created.` })
      emit("app-created")
      resetForm()
    } catch (err: any) {
      toast({ title: "Failed to create bot", description: err?.message ?? "Unexpected error", variant: "destructive" })
    } finally {
      isCreating.value = false
    }
  } else if (appType.value === "client") {
    if (!appName.value.trim() || !appPlatform.value) {
      toast({ title: "Missing data", description: "Please fill in all required fields.", variant: "destructive" })
      return
    }
    try {
      isCreating.value = true
      logger.info("Creating client app with platform:", appPlatform.value, getClientAppPlatformByPlatform(appPlatform.value)!)
      const app = await api.appsManagement.CreateClientApp(props.selectedTeam.teamId, appName.value.trim(), getClientAppPlatformByPlatform(appPlatform.value)!)
      toast({ title: "Client app created successfully", description: `New client app "${app.name}" has been created.` })
      emit("app-created")
      resetForm()
    } catch (err: any) {
      logger.error("Failed to create client app:", err)
      toast({ title: "Failed to create client app", description: err?.message ?? "Unexpected error", variant: "destructive" })
    } finally {
      isCreating.value = false
    }
  }
}

function resetForm() {
  showDialog.value = false
  appName.value = ""
  appUsername.value = ""
  appPlatform.value = ""
  usernameError.value = null
  appType.value = "bot"
}
</script>

<template>
  <GlassCard class="space-y-3">
    <div class="flex items-center gap-2">
      <Plus class="w-4 h-4 text-accent" />
      <h3 class="text-sm font-semibold text-text-primary">Create a new app</h3>
    </div>
    <p class="text-[11px] text-text-muted">Start building your integration</p>
    <GlassButton class="w-full" variant="accent" :disabled="isDisabled" @click="showDialog = true">
      Create Application
    </GlassButton>
  </GlassCard>

  <GlassDialog :open="showDialog" @update:open="val => { if (!val) resetForm() }">
    <h3 class="text-lg font-semibold text-text-primary mb-1">Create new application</h3>
    <p class="text-sm text-text-muted mb-4">Choose app type and provide required details.</p>

    <div class="space-y-4">
      <!-- App type toggle -->
      <div class="flex items-center gap-2">
        <GlassButton
          size="sm"
          :variant="appType === 'client' ? 'accent' : 'outline'"
          @click="appType = 'client'"
        >
          <AppWindow class="w-4 h-4" /> Client App
        </GlassButton>
        <GlassButton size="sm" variant="ghost" disabled class="opacity-40">
          <Globe class="w-4 h-4" /> Web App
        </GlassButton>
        <GlassButton
          size="sm"
          :variant="appType === 'bot' ? 'accent' : 'outline'"
          @click="appType = 'bot'"
        >
          <Bot class="w-4 h-4" /> Bot App
        </GlassButton>
      </div>

      <!-- App name -->
      <div>
        <label class="text-sm text-text-secondary mb-1 block">App name</label>
        <GlassInput
          v-model="appName"
          :placeholder="appType === 'bot' ? 'My awesome bot' : 'My awesome app'"
        />
      </div>

      <!-- Bot-specific fields -->
      <template v-if="appType === 'bot'">
        <InputWithError
          v-model="appUsername"
          placeholder="argon.bot"
          :error="usernameError"
          @clear-error="clearUsernameError"
        >
          <template #label>
            <label class="text-sm text-text-secondary mb-1 block">Bot username</label>
          </template>
        </InputWithError>
      </template>

      <!-- Client-specific fields -->
      <template v-else-if="appType === 'client'">
        <div>
          <label class="text-sm text-text-secondary mb-1 block">Platform</label>
          <GlassSelect v-model="appPlatform" :options="platforms" placeholder="Select platform" />
        </div>
      </template>
    </div>

    <div class="flex justify-end gap-2 mt-6">
      <GlassButton variant="ghost" @click="resetForm">Cancel</GlassButton>
      <GlassButton variant="accent" :loading="isCreating" @click="createApp">
        {{ appType === "bot" ? "Create Bot" : "Create App" }}
      </GlassButton>
    </div>
  </GlassDialog>
</template>
