<script setup lang="ts">
import { ref, computed } from "vue"
import {
  AlertTriangle, RefreshCw, KeyRound,
  Eye, EyeOff, Copy, Trash2, Plus, Check,
  Shield, ShieldCheck, ShieldX, Globe,
  MessageSquare, Lock, Unlock, Bot, Link,
  Rocket, Ban, Circle, ToggleLeft, ToggleRight,
} from "@lucide/vue"
import { useToast } from "@/composables/useToast"
import { useApi } from "@/store/apiStore"
import { GlassButton, GlassCard, GlassCheckbox, GlassBadge } from "@/components/base"
import ArgonAvatar from "@/components/ArgonAvatar.vue"
import { BotLifecycleState } from "@/lib/glue/accountConsole"
import type { AppDetails, BotDetails, ScopeKeyValue } from "@/lib/glue/accountConsole"
import {
  groupedEntitlements, entitlementsFromMask, maskFromKeys,
  type EntitlementInfo,
} from "@/lib/entitlements"

const props = defineProps<{
  app: AppDetails
  bot: BotDetails
}>()

const emit = defineEmits<{
  (e: "refresh"): void
}>()

const api = useApi()
const { toast } = useToast()

// Tabs
type Tab = "overview" | "credentials" | "entitlements" | "scopes" | "redirects"
const activeTab = ref<Tab>("overview")
const tabs: { key: Tab; label: string }[] = [
  { key: "overview", label: "Overview" },
  { key: "credentials", label: "Credentials" },
  { key: "entitlements", label: "Entitlements" },
  { key: "scopes", label: "Scopes" },
  { key: "redirects", label: "Redirects" },
]

// Lifecycle
const lifecycleState = ref(props.bot.lifecycleState)
const isLifecycleLoading = ref(false)

const lifecycleLabel = computed(() => {
  switch (lifecycleState.value) {
    case BotLifecycleState.Development: return "Development"
    case BotLifecycleState.Published: return "Published"
    case BotLifecycleState.Suspended: return "Suspended"
  }
})

const lifecycleSteps = computed(() => [
  {
    state: BotLifecycleState.Development,
    label: "Development",
    icon: Circle,
    desc: "Bot is private, only you can test it",
  },
  {
    state: BotLifecycleState.Published,
    label: "Published",
    icon: Rocket,
    desc: "Bot is publicly installable",
  },
  {
    state: BotLifecycleState.Suspended,
    label: "Suspended",
    icon: Ban,
    desc: "Bot is frozen, cannot be installed",
  },
])

function stepStatus(stepState: BotLifecycleState) {
  const current = lifecycleState.value
  if (stepState === current) return "current" as const
  if (stepState < current && current !== BotLifecycleState.Suspended) return "done" as const
  return "upcoming" as const
}

function canTransitionTo(stepState: BotLifecycleState): boolean {
  const current = lifecycleState.value
  if (stepState === current) return false
  // Development → Published
  if (current === BotLifecycleState.Development && stepState === BotLifecycleState.Published) return true
  // Published → Suspended
  if (current === BotLifecycleState.Published && stepState === BotLifecycleState.Suspended) return true
  // Suspended → Published
  if (current === BotLifecycleState.Suspended && stepState === BotLifecycleState.Published) return true
  return false
}

function handleStepClick(stepState: BotLifecycleState) {
  if (!canTransitionTo(stepState)) return
  if (stepState === BotLifecycleState.Published) publishBot()
  else if (stepState === BotLifecycleState.Suspended) suspendBot()
}

// OAuth
const oauthEnabled = ref(props.bot.requiresOAuth2)
const isOAuthLoading = ref(false)

// Credentials
const showBotToken = ref(false)
const isRefreshingToken = ref(false)
const botToken = ref(props.bot.botToken)
const copiedField = ref<string | null>(null)

const fields = ref<{ key: string; label: string; value: string; show: boolean }[]>([
  { key: "clientId", label: "Client ID", value: props.app.clientId, show: true },
  { key: "clientSecret", label: "Client Secret", value: props.app.clientSecret ?? "", show: false },
])

// Entitlements
const entitlementGroups = groupedEntitlements()
const selectedKeys = ref<Set<string>>(
  new Set(entitlementsFromMask(props.bot.requiredEntitlements).map(e => e.key))
)
const isEntitlementsLoading = ref(false)

function isEntitlementSelected(e: EntitlementInfo) {
  return selectedKeys.value.has(e.key)
}

function toggleEntitlement(e: EntitlementInfo) {
  const next = new Set(selectedKeys.value)
  if (next.has(e.key)) next.delete(e.key)
  else next.add(e.key)
  selectedKeys.value = next
}

const hasEntitlementChanges = computed(() => {
  const current = maskFromKeys([...selectedKeys.value])
  return current !== BigInt(props.bot.requiredEntitlements)
})

// Scopes
const scopes = ref<ScopeKeyValue[]>([...props.app.requiredScopes])
const scopeUpdating = ref<string | null>(null)

// Redirects
const redirects = ref<string[]>([...props.app.allowedRedirects])
const newRedirect = ref("")
const isAddingRedirect = ref(false)
const deletingRedirect = ref<number | null>(null)

// Overview computed
const statusItems = computed(() => [
  {
    icon: props.bot.isVerfied ? ShieldCheck : ShieldX,
    label: "Verification",
    value: props.bot.isVerfied ? "Verified" : "Unverified",
    color: props.bot.isVerfied ? "text-green-400" : "text-amber-400",
    bg: props.bot.isVerfied ? "bg-green-400/10" : "bg-amber-400/10",
  },
  {
    icon: oauthEnabled.value ? ToggleRight : ToggleLeft,
    label: "OAuth2",
    value: oauthEnabled.value ? "Enabled" : "Disabled",
    color: oauthEnabled.value ? "text-accent" : "text-text-muted",
    bg: oauthEnabled.value ? "bg-accent/10" : "bg-white/5",
  },
  {
    icon: MessageSquare,
    label: "Direct Messages",
    value: props.bot.allowDMs ? "Allowed" : "Disabled",
    color: props.bot.allowDMs ? "text-green-400" : "text-text-muted",
    bg: props.bot.allowDMs ? "bg-green-400/10" : "bg-white/5",
  },
])

// Lifecycle actions
async function publishBot() {
  try {
    isLifecycleLoading.value = true
    await api.appsManagement.PublishBot(props.app.teamId, props.app.appId)
    lifecycleState.value = BotLifecycleState.Published
    toast({ title: "Bot published", description: "Your bot is now publicly installable." })
  } catch (err: any) {
    toast({ title: "Failed to publish", description: err?.message ?? "Error", variant: "destructive" })
  } finally {
    isLifecycleLoading.value = false
  }
}

async function suspendBot() {
  try {
    isLifecycleLoading.value = true
    await api.appsManagement.SuspendBot(props.app.teamId, props.app.appId)
    lifecycleState.value = BotLifecycleState.Suspended
    toast({ title: "Bot suspended", description: "Your bot has been suspended." })
  } catch (err: any) {
    toast({ title: "Failed to suspend", description: err?.message ?? "Error", variant: "destructive" })
  } finally {
    isLifecycleLoading.value = false
  }
}

async function toggleOAuth() {
  try {
    isOAuthLoading.value = true
    const next = !oauthEnabled.value
    await api.appsManagement.SetBotOAuth(props.app.teamId, props.app.appId, next)
    oauthEnabled.value = next
    toast({ title: next ? "OAuth2 enabled" : "OAuth2 disabled", description: next ? "You can now configure scopes and redirects." : "OAuth flow has been disabled." })
  } catch (err: any) {
    toast({ title: "Failed", description: err?.message ?? "Error", variant: "destructive" })
  } finally {
    isOAuthLoading.value = false
  }
}

async function saveEntitlements() {
  try {
    isEntitlementsLoading.value = true
    const mask = maskFromKeys([...selectedKeys.value])
    await api.appsManagement.UpdateBotEntitlements(props.app.teamId, props.app.appId, mask)
    toast({ title: "Entitlements updated", description: "Bot entitlements have been saved." })
  } catch (err: any) {
    toast({ title: "Failed to update entitlements", description: err?.message ?? "Error", variant: "destructive" })
  } finally {
    isEntitlementsLoading.value = false
  }
}

// Scope actions
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

// Redirect actions
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
    toast({ title: "Added", description: "Redirect URL added." })
    newRedirect.value = ""
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

async function refreshBotToken() {
  try {
    isRefreshingToken.value = true
    const token = await api.appsManagement.RegenerateBotToken(props.app.teamId, props.app.appId)
    botToken.value = token
    showBotToken.value = true
    toast({ title: "Token regenerated", description: "A new bot token has been generated. Make sure to update your integration." })
  } catch (err: any) {
    toast({ title: "Failed to refresh token", description: err?.message ?? "Unexpected error", variant: "destructive" })
  } finally {
    isRefreshingToken.value = false
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
    <!-- Alert banner -->
    <div
      v-if="lifecycleState === BotLifecycleState.Suspended"
      class="flex items-start gap-3 px-4 py-3 rounded-xl border border-danger/30 bg-danger/5"
    >
      <AlertTriangle class="w-5 h-5 shrink-0 mt-0.5 text-red-400" />
      <div>
        <p class="text-sm font-medium text-red-300">Bot Suspended</p>
        <p class="text-sm text-text-secondary mt-0.5">
          This bot is currently suspended and cannot be installed. Contact support or check compliance.
        </p>
      </div>
    </div>

    <div
      v-else-if="!bot.isVerfied"
      class="flex items-start gap-3 px-4 py-3 rounded-xl border border-amber-500/30 bg-amber-500/5"
    >
      <ShieldX class="w-5 h-5 shrink-0 mt-0.5 text-amber-400" />
      <div>
        <p class="text-sm font-medium text-amber-300">Not Verified</p>
        <p class="text-sm text-text-secondary mt-0.5">
          This bot is not verified. Verification unlocks access to Privileged Intents, restricted API endpoints, and higher rate limits.
        </p>
      </div>
    </div>

    <!-- Header card -->
    <GlassCard class="!p-0 overflow-hidden">
      <div class="flex flex-col sm:flex-row items-start sm:items-center gap-4 p-5">
        <ArgonAvatar
          :fallback="app.name.at(0) ?? '?'"
          :file-id="bot.avatarFileId"
          :overrided-size="56"
        />
        <div class="flex-1 min-w-0">
          <div class="flex items-center gap-2.5 flex-wrap">
            <h1 class="text-xl font-semibold text-text-primary">{{ app.name }}</h1>
            <GlassBadge variant="accent">
              <Bot class="w-3 h-3" /> Bot
            </GlassBadge>
            <GlassBadge v-if="bot.isVerfied" variant="success">
              <ShieldCheck class="w-3 h-3" /> Verified
            </GlassBadge>
          </div>
          <p class="text-sm text-text-muted mt-1">{{ app.desc || "No description provided." }}</p>
          <div class="flex items-center gap-3 mt-2 text-xs text-text-muted">
            <span class="font-mono select-all">{{ app.appId }}</span>
            <span>&middot;</span>
            <span>Created {{ app.createdAt.date.toLocaleDateString() }}</span>
            <span>&middot;</span>
            <span>Max {{ bot.maxSpaces }} spaces</span>
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
          :class="activeTab === tab.key
            ? 'text-accent'
            : 'text-text-muted hover:text-text-secondary'"
        >
          {{ tab.label }}
          <span
            v-if="tab.key === 'entitlements' && selectedKeys.size"
            class="ml-1.5 text-[10px] bg-white/5 px-1.5 py-0.5 rounded-full"
          >{{ selectedKeys.size }}</span>
          <span
            v-if="tab.key === 'scopes' && scopes.length"
            class="ml-1.5 text-[10px] bg-white/5 px-1.5 py-0.5 rounded-full">{{ scopes.length }}</span>
          <span
            v-if="tab.key === 'redirects' && redirects.length"
            class="ml-1.5 text-[10px] bg-white/5 px-1.5 py-0.5 rounded-full"
          >{{ redirects.length }}</span>
          <!-- Active indicator -->
          <span
            v-if="activeTab === tab.key"
            class="absolute bottom-0 left-2 right-2 h-0.5 bg-accent rounded-full"
          />
        </button>
      </div>
    </GlassCard>

    <!-- Tab: Overview -->
    <div v-if="activeTab === 'overview'" class="flex flex-col gap-5">
      <!-- Lifecycle Stepper -->
      <GlassCard class="space-y-4">
        <h2 class="text-xs font-semibold uppercase tracking-wider text-text-muted">Lifecycle</h2>
        <div class="flex items-start">
          <template v-for="(step, i) in lifecycleSteps" :key="step.state">
            <!-- Step -->
            <div class="flex flex-col items-center flex-1 relative">
              <button
                class="relative z-10 w-10 h-10 rounded-full flex items-center justify-center border-2 transition-all"
                :class="[
                  stepStatus(step.state) === 'current'
                    ? step.state === BotLifecycleState.Suspended
                      ? 'border-red-400 bg-red-400/20 text-red-400'
                      : step.state === BotLifecycleState.Published
                        ? 'border-green-400 bg-green-400/20 text-green-400'
                        : 'border-accent bg-accent/20 text-accent'
                    : canTransitionTo(step.state)
                      ? 'border-glass-border bg-black/20 text-text-muted hover:border-accent/50 hover:text-accent cursor-pointer'
                      : stepStatus(step.state) === 'done'
                        ? 'border-green-400/40 bg-green-400/10 text-green-400/60 cursor-default'
                        : 'border-glass-border/50 bg-black/10 text-text-muted/40 cursor-not-allowed',
                ]"
                :disabled="isLifecycleLoading || !canTransitionTo(step.state)"
                @click="handleStepClick(step.state)"
              >
                <component :is="stepStatus(step.state) === 'done' ? Check : step.icon" class="w-4.5 h-4.5" />
              </button>
              <p class="text-xs font-medium mt-2" :class="stepStatus(step.state) === 'current' ? 'text-text-primary' : 'text-text-muted'">
                {{ step.label }}
              </p>
              <p class="text-[10px] text-text-muted mt-0.5 text-center max-w-[120px]">{{ step.desc }}</p>
            </div>
            <!-- Connector -->
            <div
              v-if="i < lifecycleSteps.length - 1"
              class="h-0.5 flex-1 mt-5 rounded-full"
              :class="stepStatus(lifecycleSteps[i + 1].state) === 'upcoming' ? 'bg-glass-border' : 'bg-green-400/30'"
            />
          </template>
        </div>
      </GlassCard>

      <!-- Status cards -->
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

      <!-- Bot token — special card -->
      <div class="p-4 rounded-xl border border-accent/20 bg-accent/[0.03] space-y-2">
        <div class="flex items-center justify-between">
          <div class="flex items-center gap-2">
            <KeyRound class="w-3.5 h-3.5 text-accent" />
            <span class="text-xs font-medium text-accent uppercase tracking-wider">Bot Token</span>
          </div>
          <div class="flex gap-1">
            <button
              class="p-1.5 rounded-md hover:bg-white/5 text-text-muted hover:text-text-primary transition-colors cursor-pointer"
              @click="showBotToken = !showBotToken"
            >
              <component :is="showBotToken ? EyeOff : Eye" class="w-3.5 h-3.5" />
            </button>
            <button
              class="p-1.5 rounded-md hover:bg-white/5 transition-colors cursor-pointer"
              :class="copiedField === 'botToken' ? 'text-green-400' : 'text-text-muted hover:text-text-primary'"
              @click="copyToClipboard(botToken, 'botToken')"
            >
              <Check v-if="copiedField === 'botToken'" class="w-3.5 h-3.5" />
              <Copy v-else class="w-3.5 h-3.5" />
            </button>
          </div>
        </div>
        <div class="font-mono text-sm rounded-lg bg-black/30 px-3 py-2 border border-white/[0.04] select-all break-all">
          <span v-if="showBotToken" class="text-text-secondary">{{ botToken }}</span>
          <span v-else class="text-text-muted select-none blur-[3px]">{{ "•".repeat(40) }}</span>
        </div>
        <div class="flex items-center justify-between pt-1">
          <p class="text-[11px] text-text-muted">Regenerating will invalidate the current token immediately.</p>
          <GlassButton size="xs" variant="danger" :loading="isRefreshingToken" @click="refreshBotToken">
            <RefreshCw class="w-3.5 h-3.5" /> Regenerate
          </GlassButton>
        </div>
      </div>
    </div>

    <!-- Tab: Entitlements -->
    <div v-else-if="activeTab === 'entitlements'" class="flex flex-col gap-4">
      <GlassCard class="space-y-5">
        <div class="flex items-center justify-between">
          <p class="text-xs text-text-muted">
            Permissions this bot will receive when installed in a space. A locked archetype will be auto-created with these entitlements.
          </p>
          <GlassButton
            size="sm" variant="accent"
            :loading="isEntitlementsLoading"
            :disabled="!hasEntitlementChanges"
            @click="saveEntitlements"
          >
            <Check class="w-3.5 h-3.5" /> Save
          </GlassButton>
        </div>

        <div v-for="group in entitlementGroups" :key="group.category" class="space-y-2">
          <h3 class="text-xs font-semibold uppercase tracking-wider text-text-muted">{{ group.label }}</h3>
          <div class="grid grid-cols-1 sm:grid-cols-2 gap-2">
            <div
              v-for="ent in group.items"
              :key="ent.key"
              class="flex items-start gap-3 px-3 py-2.5 rounded-lg border transition-colors cursor-pointer"
              :class="[
                isEntitlementSelected(ent)
                  ? ent.dangerous
                    ? 'border-red-400/20 bg-red-400/[0.03]'
                    : 'border-accent/20 bg-accent/[0.03]'
                  : 'border-glass-border bg-black/20 hover:border-glass-border-hover',
              ]"
              @click="toggleEntitlement(ent)"
            >
              <GlassCheckbox
                :model-value="isEntitlementSelected(ent)"
                class="mt-0.5"
                @update:model-value="toggleEntitlement(ent)"
              />
              <div class="flex-1 min-w-0">
                <div class="flex items-center gap-2">
                  <span class="text-sm text-text-primary">{{ ent.label }}</span>
                  <GlassBadge v-if="ent.dangerous" variant="danger" class="text-[9px]">
                    <AlertTriangle class="w-2.5 h-2.5" /> Dangerous
                  </GlassBadge>
                </div>
                <p class="text-[11px] text-text-muted mt-0.5">{{ ent.description }}</p>
              </div>
            </div>
          </div>
        </div>
      </GlassCard>
    </div>

    <!-- Tab: Scopes -->
    <div v-else-if="activeTab === 'scopes'">
      <GlassCard v-if="scopes.length > 0" class="space-y-4">
        <p class="text-xs text-text-muted">
          OAuth scopes this bot requests when authorizing via OAuth2. Locked scopes cannot be toggled.
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
        <p class="text-sm text-text-muted">No OAuth scopes configured for this bot.</p>
      </div>
    </div>

    <!-- Tab: Redirects -->
    <div v-else-if="activeTab === 'redirects'" class="flex flex-col gap-4">
      <!-- OAuth toggle -->
      <GlassCard class="flex items-center justify-between">
        <div>
          <p class="text-sm font-medium text-text-primary">OAuth2 Flow</p>
          <p class="text-xs text-text-muted mt-0.5">
            {{ oauthEnabled ? "OAuth2 is enabled. Configure redirect URIs below." : "Enable OAuth2 to allow authorization flows and configure redirects." }}
          </p>
        </div>
        <GlassButton
          :variant="oauthEnabled ? 'danger' : 'accent'"
          size="sm"
          :loading="isOAuthLoading"
          @click="toggleOAuth"
        >
          <component :is="oauthEnabled ? ToggleRight : ToggleLeft" class="w-4 h-4" />
          {{ oauthEnabled ? "Disable" : "Enable" }}
        </GlassButton>
      </GlassCard>

      <!-- Redirects list (only when OAuth enabled) -->
      <GlassCard v-if="oauthEnabled" class="space-y-4">
        <p class="text-xs text-text-muted">
          OAuth redirect URIs that are allowed for this bot. Only HTTPS URLs are accepted.
        </p>

        <!-- Add new -->
        <div class="flex gap-2">
          <input
            v-model="newRedirect"
            type="text"
            placeholder="https://example.com/callback"
            class="glass-input flex-1 px-3 py-2 text-sm"
            @keydown.enter="addRedirect"
          />
          <GlassButton variant="accent" size="sm" :loading="isAddingRedirect" @click="addRedirect">
            <Plus class="w-4 h-4" /> Add
          </GlassButton>
        </div>

        <!-- List -->
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
