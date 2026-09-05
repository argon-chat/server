<script setup lang="ts">
import { ref, computed } from "vue"
import {
  KeyRound, Eye, EyeOff, Copy, Check, Trash2, Plus,
  Globe, Code, Cookie, Link, Lock, Shield, ShieldCheck, ShieldX,
  AppWindow, Smartphone, Monitor,
} from "@lucide/vue"
import { useToast } from "@/composables/useToast"
import { useApi } from "@/store/apiStore"
import { GlassButton, GlassCard, GlassCheckbox, GlassBadge } from "@/components/base"
import { ClientAppPlatform } from "@/lib/glue/accountConsole"
import type { AppDetails, ClientAppDetails, ScopeKeyValue } from "@/lib/glue/accountConsole"

const props = defineProps<{
  app: AppDetails
  clientApp: ClientAppDetails
}>()

const api = useApi()
const { toast } = useToast()

type Tab = "overview" | "credentials" | "scopes" | "redirects"
const activeTab = ref<Tab>("overview")
const tabs: { key: Tab; label: string }[] = [
  { key: "overview", label: "Overview" },
  { key: "credentials", label: "Credentials" },
  { key: "scopes", label: "Scopes" },
  { key: "redirects", label: "Redirects" },
]

const platformNames = ["Windows Desktop", "macOS Desktop", "Linux Desktop", "Web Based", "iOS", "Android"]

const isWebBased = computed(() => props.clientApp.platform === ClientAppPlatform.WebBased)
const isMobile = computed(() =>
  props.clientApp.platform === ClientAppPlatform.iOS || props.clientApp.platform === ClientAppPlatform.Android)

const platformIcon = computed(() => isWebBased.value ? Globe : isMobile.value ? Smartphone : Monitor)

// Credentials
const copiedField = ref<string | null>(null)
const fields = ref<{ key: string; label: string; value: string; show: boolean }[]>([
  { key: "clientId", label: "Client ID", value: props.app.clientId, show: true },
  { key: "clientSecret", label: "Client Secret", value: props.app.clientSecret ?? "", show: false },
])

// Scopes
const scopes = ref<ScopeKeyValue[]>([...props.app.requiredScopes])
const scopeUpdating = ref<string | null>(null)

// Redirects
const redirects = ref<string[]>([...props.app.allowedRedirects])
const newRedirect = ref("")
const isAddingRedirect = ref(false)
const deletingRedirect = ref<number | null>(null)

// Development cookies
const isGeneratingCookies = ref(false)

// A native client has nowhere on the web to receive a redirect, so it registers a scheme of its own
// or listens on loopback; a web-based one is held to https. The placeholder says which is expected
// rather than leaving the developer to discover it from a rejection.
const redirectPlaceholder = computed(() =>
  isWebBased.value ? "https://example.com/callback" : "gl.example.app://oauth/callback")

const redirectHint = computed(() =>
  isWebBased.value
    ? "Only HTTPS URLs are accepted, plus http:// on localhost for development."
    : "A private-use scheme naming a domain you control in reverse order (gl.example.app://callback), "
    + "an https:// address, or an http://127.0.0.1 loopback address for desktop clients.")

const statusItems = computed(() => [
  {
    icon: props.clientApp.isVerfied ? ShieldCheck : ShieldX,
    label: "Verification",
    value: props.clientApp.isVerfied ? "Verified" : "Unverified",
    color: props.clientApp.isVerfied ? "text-green-400" : "text-amber-400",
    bg: props.clientApp.isVerfied ? "bg-green-400/10" : "bg-amber-400/10",
  },
  {
    icon: platformIcon.value,
    label: "Platform",
    value: platformNames[props.clientApp.platform] ?? "Unknown",
    color: "text-accent",
    bg: "bg-accent/10",
  },
  {
    icon: props.clientApp.isPublic ? Globe : Lock,
    label: "Availability",
    value: props.clientApp.isPublic ? "Public" : "Team only",
    color: props.clientApp.isPublic ? "text-accent" : "text-text-muted",
    bg: props.clientApp.isPublic ? "bg-accent/10" : "bg-white/5",
  },
])

async function toggleScope(scope: ScopeKeyValue) {
  try {
    scopeUpdating.value = scope.key
    await api.appsManagement.UpdateScope(props.app.teamId, props.app.appId, {
      key: scope.key,
      isRequired: scope.isRequired,
      isLocked: scope.isLocked,
    })
    toast({ title: "Updated", description: `Scope "${scope.key}" updated.` })
  } catch (err: any) {
    toast({ title: "Failed", description: err?.message ?? "Error", variant: "destructive" })
  } finally {
    scopeUpdating.value = null
  }
}

async function addRedirect() {
  const value = newRedirect.value.trim()
  if (!value) return
  try {
    isAddingRedirect.value = true
    const result = await api.appsManagement.AddRedirect(props.app.teamId, props.app.appId, value)
    if (!result?.ok) {
      toast({ title: "Validation failed", description: result?.error ?? "Unknown validation error", variant: "destructive" })
      return
    }
    redirects.value.push(value)
    newRedirect.value = ""
    toast({ title: "Added", description: "Redirect URL added." })
  } catch (err: any) {
    toast({ title: "Failed", description: err?.message ?? "Error", variant: "destructive" })
  } finally {
    isAddingRedirect.value = false
  }
}

async function deleteRedirect(index: number) {
  const redirect = redirects.value[index]
  try {
    deletingRedirect.value = index
    await api.appsManagement.RemoveRedirect(props.app.teamId, props.app.appId, redirect)
    redirects.value.splice(index, 1)
    toast({ title: "Removed", description: "Redirect removed." })
  } catch (err: any) {
    toast({ title: "Failed to remove redirect", description: err?.message ?? "Error", variant: "destructive" })
  } finally {
    deletingRedirect.value = null
  }
}

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

async function copyToClipboard(value: string, fieldKey?: string) {
  if (!value) return
  try {
    await navigator.clipboard.writeText(value)
    if (fieldKey) {
      copiedField.value = fieldKey
      setTimeout(() => (copiedField.value = null), 1500)
    }
  } catch {
    /* noop */
  }
}
</script>

<template>
  <div class="flex flex-col gap-6">
    <div
      v-if="!clientApp.isVerfied && !clientApp.isPublic"
      class="flex items-start gap-3 px-4 py-3 rounded-xl border border-amber-500/30 bg-amber-500/5"
    >
      <ShieldX class="w-5 h-5 shrink-0 mt-0.5 text-amber-400" />
      <div>
        <p class="text-sm font-medium text-amber-300">Team access only</p>
        <p class="text-sm text-text-secondary mt-0.5">
          Until this app is published or verified, only members of the owning team can sign into it.
          Everyone else is refused at the consent screen.
        </p>
      </div>
    </div>

    <!-- Header card -->
    <GlassCard class="!p-0 overflow-hidden">
      <div class="flex flex-col sm:flex-row items-start sm:items-center gap-4 p-5">
        <div class="w-14 h-14 rounded-xl bg-gradient-to-br from-accent to-purple-600 flex items-center justify-center text-2xl font-bold text-white shrink-0">
          {{ app.name.at(0) ?? '?' }}
        </div>
        <div class="flex-1 min-w-0">
          <div class="flex items-center gap-2.5 flex-wrap">
            <h1 class="text-xl font-semibold text-text-primary">{{ app.name }}</h1>
            <GlassBadge variant="accent">
              <AppWindow class="w-3 h-3" /> Client App
            </GlassBadge>
            <GlassBadge v-if="clientApp.isVerfied" variant="success">
              <ShieldCheck class="w-3 h-3" /> Verified
            </GlassBadge>
            <GlassBadge v-if="clientApp.isInternalApp" variant="muted">
              <Lock class="w-3 h-3" /> Internal
            </GlassBadge>
          </div>
          <p class="text-sm text-text-muted mt-1">{{ app.desc || "No description provided." }}</p>
          <div class="flex items-center gap-3 mt-2 text-xs text-text-muted flex-wrap">
            <span class="font-mono select-all">{{ app.appId }}</span>
            <span>&middot;</span>
            <span>Created {{ app.createdAt.toDate().toLocaleDateString() }}</span>
            <span>&middot;</span>
            <span>{{ clientApp.rateLimitPerMinute === -1 ? '∞' : clientApp.rateLimitPerMinute }} req/min</span>
          </div>
        </div>
      </div>

      <!-- Tabs -->
      <div class="flex border-t border-glass-border px-2 overflow-x-auto scrollbar-thin">
        <button
          v-for="tab in tabs"
          :key="tab.key"
          @click="activeTab = tab.key"
          class="relative px-4 py-3 text-sm font-medium transition-colors whitespace-nowrap cursor-pointer"
          :class="activeTab === tab.key ? 'text-accent' : 'text-text-muted hover:text-text-secondary'"
        >
          {{ tab.label }}
          <span
            v-if="tab.key === 'scopes' && scopes.length"
            class="ml-1.5 text-[10px] bg-white/5 px-1.5 py-0.5 rounded-full"
          >{{ scopes.length }}</span>
          <span
            v-if="tab.key === 'redirects' && redirects.length"
            class="ml-1.5 text-[10px] bg-white/5 px-1.5 py-0.5 rounded-full"
          >{{ redirects.length }}</span>
          <span
            v-if="activeTab === tab.key"
            class="absolute bottom-0 left-2 right-2 h-0.5 bg-accent rounded-full"
          />
        </button>
      </div>
    </GlassCard>

    <!-- Tab: Overview -->
    <div v-if="activeTab === 'overview'" class="flex flex-col gap-5">
      <div class="grid grid-cols-1 sm:grid-cols-3 gap-3">
        <div
          v-for="item in statusItems"
          :key="item.label"
          class="flex flex-col items-center gap-2 p-4 rounded-xl border border-glass-border bg-black/20 text-center"
        >
          <div class="p-2.5 rounded-xl" :class="item.bg">
            <component :is="item.icon" class="w-5 h-5" :class="item.color" />
          </div>
          <div>
            <p class="text-sm font-medium" :class="item.color">{{ item.value }}</p>
            <p class="text-[11px] text-text-muted mt-0.5">{{ item.label }}</p>
          </div>
        </div>
      </div>

      <GlassCard v-if="clientApp.websiteUrl || clientApp.repositoryUrl" class="space-y-2">
        <h2 class="text-xs font-semibold uppercase tracking-wider text-text-muted">Links</h2>
        <div v-if="clientApp.websiteUrl" class="flex items-center gap-2 text-sm text-text-muted">
          <Globe class="w-4 h-4" />
          Website: <a :href="clientApp.websiteUrl" target="_blank" class="text-accent hover:underline">{{ clientApp.websiteUrl }}</a>
        </div>
        <div v-if="clientApp.repositoryUrl" class="flex items-center gap-2 text-sm text-text-muted">
          <Code class="w-4 h-4" />
          Repository: <a :href="clientApp.repositoryUrl" target="_blank" class="text-accent hover:underline">{{ clientApp.repositoryUrl }}</a>
        </div>
      </GlassCard>

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

    <!-- Tab: Credentials -->
    <div v-else-if="activeTab === 'credentials'" class="flex flex-col gap-4">
      <template v-for="field in fields" :key="field.key">
        <div class="p-4 rounded-xl border border-glass-border bg-black/20 space-y-2">
          <div class="flex items-center justify-between">
            <span class="text-xs font-medium text-text-muted uppercase tracking-wider">{{ field.label }}</span>
            <div class="flex gap-1">
              <button
                class="p-1.5 rounded-md hover:bg-white/5 text-text-muted hover:text-text-primary transition-colors cursor-pointer"
                :title="field.show ? 'Hide' : 'Reveal'"
                @click="field.show = !field.show"
              >
                <component :is="field.show ? EyeOff : Eye" class="w-3.5 h-3.5" />
              </button>
              <button
                class="p-1.5 rounded-md hover:bg-white/5 transition-colors cursor-pointer"
                :class="copiedField === field.key ? 'text-green-400' : 'text-text-muted hover:text-text-primary'"
                title="Copy"
                @click="copyToClipboard(field.value, field.key)"
              >
                <Check v-if="copiedField === field.key" class="w-3.5 h-3.5" />
                <Copy v-else class="w-3.5 h-3.5" />
              </button>
            </div>
          </div>
          <div class="font-mono text-sm rounded-lg bg-black/30 px-3 py-2 border border-white/[0.04] select-all break-all">
            <span v-if="field.show" class="text-text-secondary">{{ field.value || "—" }}</span>
            <span v-else class="text-text-muted select-none blur-[3px]">{{ "•".repeat(20) }}</span>
          </div>
        </div>
      </template>

      <div v-if="!isWebBased" class="flex items-start gap-3 px-4 py-3 rounded-xl border border-glass-border bg-black/20">
        <KeyRound class="w-4 h-4 shrink-0 mt-0.5 text-text-muted" />
        <p class="text-xs text-text-muted">
          An app distributed to users cannot keep a secret: anything shipped inside the binary is
          readable by whoever holds it. Use the authorization code flow with PKCE and treat the
          client secret as an identifier, not a credential.
        </p>
      </div>
    </div>

    <!-- Tab: Scopes -->
    <div v-else-if="activeTab === 'scopes'">
      <GlassCard v-if="scopes.length > 0" class="space-y-4">
        <p class="text-xs text-text-muted">
          OAuth scopes this app requests when authorizing users. Locked scopes cannot be toggled —
          <code class="text-text-secondary">offline_access</code> unlocks once the app is verified.
        </p>
        <div class="grid grid-cols-1 sm:grid-cols-2 gap-2">
          <div
            v-for="scope in scopes"
            :key="scope.key"
            class="flex items-center gap-3 px-3 py-2.5 rounded-lg border transition-colors"
            :class="[
              scope.isLocked
                ? 'border-white/[0.04] bg-black/10 opacity-50'
                : scope.isRequired
                  ? 'border-accent/20 bg-accent/[0.03]'
                  : 'border-glass-border bg-black/20 hover:border-glass-border-hover',
            ]"
          >
            <GlassCheckbox
              :model-value="scope.isRequired"
              :disabled="scope.isLocked || scopeUpdating === scope.key"
              @update:model-value="scope.isRequired = $event; toggleScope(scope)"
            />
            <div class="flex-1 min-w-0">
              <span class="text-sm text-text-primary font-mono">{{ scope.key }}</span>
            </div>
            <GlassBadge v-if="scope.isLocked" variant="muted">
              <Lock class="w-2.5 h-2.5" /> Locked
            </GlassBadge>
          </div>
        </div>
      </GlassCard>

      <div v-else class="flex flex-col items-center justify-center py-16 text-center">
        <Shield class="w-10 h-10 text-text-muted opacity-30 mb-3" />
        <p class="text-sm text-text-muted">No OAuth scopes are available for this app.</p>
      </div>
    </div>

    <!-- Tab: Redirects -->
    <div v-else-if="activeTab === 'redirects'" class="flex flex-col gap-4">
      <GlassCard class="space-y-4">
        <p class="text-xs text-text-muted">
          Redirect URIs allowed for this app. The authorization endpoint compares the incoming
          <code class="text-text-secondary">redirect_uri</code> against this list exactly, so an app
          with no redirect registered cannot complete an authorization.
        </p>

        <div class="flex gap-2">
          <input
            v-model="newRedirect"
            type="text"
            :placeholder="redirectPlaceholder"
            class="glass-input flex-1 px-3 py-2 text-sm"
            @keydown.enter="addRedirect"
          />
          <GlassButton variant="accent" size="sm" :loading="isAddingRedirect" @click="addRedirect">
            <Plus class="w-4 h-4" /> Add
          </GlassButton>
        </div>

        <p class="text-[11px] text-text-muted">{{ redirectHint }}</p>

        <div v-if="redirects.length" class="space-y-1.5">
          <div
            v-for="(url, i) in redirects"
            :key="i"
            class="group flex items-center gap-3 px-3 py-2.5 bg-black/20 border border-glass-border rounded-lg hover:border-glass-border-hover transition-colors"
          >
            <Link class="w-4 h-4 text-text-muted shrink-0" />
            <span class="flex-1 text-sm text-text-secondary font-mono break-all select-all">{{ url }}</span>
            <button
              class="p-1.5 rounded-md text-text-muted opacity-0 group-hover:opacity-100 hover:bg-danger/15 hover:text-red-400 transition-all shrink-0 cursor-pointer"
              title="Remove redirect"
              :disabled="deletingRedirect === i"
              @click="deleteRedirect(i)"
            >
              <Trash2 class="w-3.5 h-3.5" />
            </button>
          </div>
        </div>

        <div v-else class="py-8 text-center">
          <Link class="w-8 h-8 text-text-muted opacity-30 mx-auto mb-2" />
          <p class="text-sm text-text-muted">No redirect URIs configured yet.</p>
        </div>
      </GlassCard>
    </div>
  </div>
</template>
