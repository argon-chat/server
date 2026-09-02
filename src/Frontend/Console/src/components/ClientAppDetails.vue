<script setup lang="ts">
import { ref, computed } from "vue"
import {
  Loader2, KeyRound, Eye, EyeOff, Copy,
  Globe, Code, Cookie,
} from "@lucide/vue"
import { useToast } from "@/composables/useToast"
import { useApi } from "@/store/apiStore"
import { GlassButton, GlassCard } from "@/components/base"
import type { AppDetails, ClientAppDetails } from "@/lib/glue/accountConsole"

const props = defineProps<{
  app: AppDetails
  clientApp: ClientAppDetails
}>()

const api = useApi()
const { toast } = useToast()

const isGeneratingCookies = ref(false)
const showFullAppId = ref(false)

const isWebBased = computed(() => props.clientApp.platform === 3)

const fields = ref<{ key: string; label: string; value: string; show: boolean }[]>([
  { key: "clientId", label: "Client ID", value: props.app.clientId, show: true },
  { key: "clientSecret", label: "Client Secret", value: props.app.clientSecret ?? '', show: false },
])

const platformNames = ['Windows Desktop', 'macOS Desktop', 'Linux Desktop', 'Web Based', 'iOS', 'Android']

async function generateDevelopmentCookies() {
  if (!props.clientApp.allowedDevelopmentRegenerateCoockies) {
    toast({ title: "Not allowed", description: "Development cookie generation is not enabled for this app.", variant: "destructive" })
    return
  }
  try {
    isGeneratingCookies.value = true
    await api.appsManagement.EnsureCoockiesForApp(props.app.teamId, props.app.appId)
    toast({ title: "Cookies generated", description: "Development cookies have been set in your browser. Expired in 7 days." })
  } catch (err: any) {
    toast({ title: "Failed to generate cookies", description: err?.message ?? "Error", variant: "destructive" })
  } finally {
    isGeneratingCookies.value = false
  }
}

const copyToClipboard = async (value: string) => {
  if (!value) return
  try { await navigator.clipboard.writeText(value) } catch { /* noop */ }
}

const displayedAppId = computed(() => {
  const id = props.app.appId
  if (showFullAppId.value) return id
  return `${id.substring(0, 8)}...${id.substring(id.length - 4)}`
})
</script>

<template>
  <div class="flex flex-col gap-6">
    <div class="flex flex-col lg:flex-row gap-6">
      <!-- App Info -->
      <GlassCard class="flex-1 space-y-4">
        <div class="flex items-center gap-3">
          <div class="w-12 h-12 rounded-xl bg-gradient-to-br from-accent to-purple-600 flex items-center justify-center text-xl font-bold text-white">
            {{ app.name.at(0) ?? '?' }}
          </div>
          <div>
            <h2 class="text-lg font-semibold text-text-primary">{{ app.name }}</h2>
            <p
              class="text-xs text-text-muted font-mono cursor-pointer hover:text-text-secondary transition-colors select-none"
              @click="showFullAppId = !showFullAppId"
              :title="showFullAppId ? 'Click to hide' : 'Click to show full App ID'"
            >
              App ID:
              <span class="inline-block transition-all duration-200">
                <span v-if="!showFullAppId" class="blur-[2px]">{{ displayedAppId }}</span>
                <span v-else>{{ displayedAppId }}</span>
              </span>
            </p>
          </div>
        </div>
        <p class="text-xs text-text-muted">Created at {{ app.createdAt.toDate().toLocaleDateString() }}</p>
        <p class="text-sm text-text-secondary">{{ app.desc || "No description provided." }}</p>

        <div class="grid grid-cols-2 sm:grid-cols-3 gap-y-2 text-sm">
          <div class="text-text-muted">Platform: <span class="text-accent">{{ platformNames[clientApp.platform] }}</span></div>
          <div class="text-text-muted">Verified: <span :class="clientApp.isVerfied ? 'text-green-400' : 'text-text-muted'">{{ clientApp.isVerfied ? 'Yes' : 'No' }}</span></div>
          <div class="text-text-muted">Public: <span :class="clientApp.isPublic ? 'text-accent' : 'text-red-400'">{{ clientApp.isPublic ? 'Yes' : 'No' }}</span></div>
          <div class="text-text-muted">Internal: <span :class="clientApp.isInternalApp ? 'text-accent' : 'text-text-muted'">{{ clientApp.isInternalApp ? 'Yes' : 'No' }}</span></div>
          <div class="text-text-muted">Rate Limit: <span class="text-text-secondary">{{ clientApp.rateLimitPerMinute === -1 ? '∞/min' : `${clientApp.rateLimitPerMinute}/min` }}</span></div>
        </div>

        <div v-if="clientApp.websiteUrl || clientApp.repositoryUrl" class="pt-3 border-t border-glass-border space-y-2">
          <div v-if="clientApp.websiteUrl" class="flex items-center gap-2 text-sm text-text-muted">
            <Globe class="w-4 h-4" />
            Website: <a :href="clientApp.websiteUrl" target="_blank" class="text-accent hover:underline">{{ clientApp.websiteUrl }}</a>
          </div>
          <div v-if="clientApp.repositoryUrl" class="flex items-center gap-2 text-sm text-text-muted">
            <Code class="w-4 h-4" />
            Repository: <a :href="clientApp.repositoryUrl" target="_blank" class="text-accent hover:underline">{{ clientApp.repositoryUrl }}</a>
          </div>
        </div>
      </GlassCard>

      <!-- Credentials -->
      <GlassCard class="flex-1 lg:max-w-[40%] space-y-4">
        <div class="flex items-center gap-2 mb-2">
          <KeyRound class="w-4 h-4 text-text-muted" />
          <h3 class="text-sm font-semibold text-text-primary">Authentication</h3>
        </div>

        <template v-for="field in fields" :key="field.key">
          <div>
            <span class="text-xs text-text-muted">{{ field.label }}</span>
            <div class="mt-1 flex items-center gap-1.5">
              <div class="flex-1 relative bg-black/30 rounded-lg px-3 py-2 border border-glass-border font-mono text-xs overflow-hidden">
                <span v-if="field.show" class="text-text-secondary break-all select-text">{{ field.value || "—" }}</span>
                <span v-else class="text-text-muted select-none blur-[3px]">{{ "•".repeat(16) }}</span>
              </div>
              <button class="p-1.5 rounded-lg hover:bg-white/5 text-text-muted hover:text-text-primary transition-colors cursor-pointer" @click="field.show = !field.show">
                <component :is="field.show ? EyeOff : Eye" class="w-4 h-4" />
              </button>
              <button class="p-1.5 rounded-lg hover:bg-white/5 text-text-muted hover:text-text-primary transition-colors cursor-pointer" @click="copyToClipboard(field.value)">
                <Copy class="w-4 h-4" />
              </button>
            </div>
          </div>
        </template>
      </GlassCard>
    </div>

    <!-- Development cookies -->
    <GlassCard v-if="isWebBased && clientApp.allowedDevelopmentRegenerateCoockies" class="text-center space-y-4">
      <div class="flex items-center justify-center gap-2">
        <Cookie class="w-5 h-5 text-orange-400" />
        <h3 class="text-sm font-semibold text-text-primary">Development Access</h3>
      </div>
      <p class="text-xs text-text-muted">For simplified development without using a native client</p>

      <GlassButton variant="accent" size="lg" class="w-full" :loading="isGeneratingCookies" @click="generateDevelopmentCookies">
        <Cookie class="w-5 h-5" /> Generate browser access cookies
      </GlassButton>

      <div class="text-xs text-text-muted space-y-2">
        <p>
          This allows your web-based app to use the production
          <code class="text-text-secondary bg-black/30 px-1 py-0.5 rounded">api.argon.gl</code>
          endpoint directly from the browser for testing purposes.
        </p>
        <p class="text-amber-500/80">
          <strong>Development only:</strong> In production, authentication cookies must be generated by the host client.
        </p>
      </div>
    </GlassCard>
  </div>
</template>
