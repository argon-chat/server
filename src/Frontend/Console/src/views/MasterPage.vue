<script setup lang="ts">
import { ref, computed } from "vue"
import { AppWindow, Trash2, FileDown, Loader2, LayoutDashboard, Shield, XCircle } from "@lucide/vue"
import { GlassButton, GlassCard, GlassDialog } from "@/components/base"
import router from "@/router"
import { useApi } from "@/store/apiStore"
import { DeleteAccountError, CancelDeleteError, RequestExportGDRPStatus, DeletionStatusKind } from "@/lib/glue/accountConsole"
import { useUserStore } from "@/store/useUserStore"
import { useTeamsStore } from "@/store/useTeamsStore"
import { useToast } from "@/composables/useToast"
import { storeToRefs } from "pinia"

const api = useApi()
const { toast } = useToast()
const user = useUserStore()
const teamsStore = useTeamsStore()
const { user: userData } = storeToRefs(user)

const isDeletionScheduled = computed(() =>
  userData.value?.deletionStatus === DeletionStatusKind.Scheduled ||
  userData.value?.deletionStatus === DeletionStatusKind.Executing
)
const deletionScheduledAt = computed(() => userData.value?.deletionScheduledAt)
const deletionExecutionAt = computed(() => userData.value?.deletionExecutionAt)
const isExportProgress = ref(user.user?.gdrpExportInProgress)
const showDeleteDialog = ref(false)
const showExportDialog = ref(false)
const deletePassword = ref("")
const deleteLoading = ref(false)
const cancelLoading = ref(false)

const goToAppsManage = () => {
  if (!teamsStore.selectedTeam) {
    toast({
      title: "Team not selected",
      description: "Please select a team before managing applications.",
      variant: "destructive",
    })
    return
  }
  router.push({ name: "AppsManage" })
}

const requestDeleteAccount = () => {
  if (isDeletionScheduled.value) return
  deletePassword.value = ""
  showDeleteDialog.value = true
}

const confirmDeleteAccount = async () => {
  if (!deletePassword.value) {
    toast({ title: "Password required", description: "Please enter your password to confirm account deletion.", variant: "destructive" })
    return
  }
  deleteLoading.value = true
  try {
    const result = await api.consoleInteraction.RequestDeleteAccount(deletePassword.value)
    if (result.success) {
      toast({ title: "Deletion request sent", description: "Your account will be permanently deleted within one month unless canceled." })
      await user.fetchUser(true)
    } else {
      switch (result.error) {
        case DeleteAccountError.InvalidPassword:
          toast({ title: "Invalid password", description: "The password you entered is incorrect.", variant: "destructive" })
          break
        case DeleteAccountError.AlreadyScheduled:
          toast({ title: "Already scheduled", description: "A deletion request for your account already exists.", variant: "destructive" })
          break
        case DeleteAccountError.HasActiveSubscription:
          toast({ title: "Active subscription", description: "Please cancel your subscription before deleting your account.", variant: "destructive" })
          break
        case DeleteAccountError.OwnsSpaces:
          toast({ title: "Owns spaces", description: "Please transfer or delete your spaces before deleting your account.", variant: "destructive" })
          break
        case DeleteAccountError.AccountLocked:
          toast({ title: "Account locked", description: "Your account is locked. Please contact support.", variant: "destructive" })
          break
        default:
          toast({ title: "Error", description: "Could not process deletion request. Try again later.", variant: "destructive" })
      }
    }
  } catch {
    toast({ title: "Network error", description: "Could not reach the server.", variant: "destructive" })
  } finally {
    deleteLoading.value = false
    deletePassword.value = ""
    showDeleteDialog.value = false
  }
}

const cancelDeletion = async () => {
  cancelLoading.value = true
  try {
    const result = await api.consoleInteraction.CancelDeleteAccount()
    if (result.success) {
      toast({ title: "Deletion canceled", description: "Your account deletion has been canceled." })
      await user.fetchUser(true)
    } else {
      switch (result.error) {
        case CancelDeleteError.NotScheduled:
          toast({ title: "Not scheduled", description: "No deletion request found to cancel.", variant: "destructive" })
          break
        case CancelDeleteError.AlreadyExecuting:
          toast({ title: "Already executing", description: "Deletion is already in progress and cannot be canceled.", variant: "destructive" })
          break
        case CancelDeleteError.AlreadyCompleted:
          toast({ title: "Already completed", description: "Account deletion has already been completed.", variant: "destructive" })
          break
        default:
          toast({ title: "Error", description: "Could not cancel deletion. Try again later.", variant: "destructive" })
      }
    }
  } catch {
    toast({ title: "Network error", description: "Could not reach the server.", variant: "destructive" })
  } finally {
    cancelLoading.value = false
  }
}

const requestGDRPExport = () => {
  if (isExportProgress.value) return
  showExportDialog.value = true
}

const confirmGDRPExport = async () => {
  isExportProgress.value = true
  try {
    const result = await api.consoleInteraction.RequestExportGDRP()
    switch (result) {
      case RequestExportGDRPStatus.Ok:
        isExportProgress.value = true
        toast({ title: "Export request accepted", description: "You'll receive your data archive by email within two weeks." })
        break
      case RequestExportGDRPStatus.Already:
        toast({ title: "Request already sent", description: "You already have an active GDPR export request.", variant: "destructive" })
        isExportProgress.value = true
        break
      case RequestExportGDRPStatus.RateLimit:
        toast({ title: "Rate limit reached", description: "Please wait before requesting another export.", variant: "destructive" })
        isExportProgress.value = false
        break
      default:
        toast({ title: "Unexpected error", description: "Could not process export request. Try again later.", variant: "destructive" })
        isExportProgress.value = false
    }
  } catch {
    toast({ title: "Network error", description: "Could not reach the server.", variant: "destructive" })
    isExportProgress.value = false
  } finally {
    showExportDialog.value = false
  }
}
</script>

<template>
  <div class="max-w-4xl mx-auto px-6 py-10 space-y-8 animate-fade-in">
    <!-- Welcome header -->
    <div>
      <h1 class="text-2xl font-semibold text-text-primary">
        Welcome back<template v-if="userData">, {{ userData.displayName }}</template>
      </h1>
      <p class="text-sm text-text-muted mt-1">Manage your applications, teams, and account settings.</p>
    </div>

    <!-- Quick Actions -->
    <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
      <!-- Manage Apps -->
      <button
        @click="goToAppsManage"
        class="glass-card group p-6 text-left cursor-pointer transition-all hover:border-accent/30 hover:shadow-[0_0_30px_rgba(99,102,241,0.08)]"
      >
        <div class="w-10 h-10 rounded-xl bg-accent/10 flex items-center justify-center mb-4 group-hover:bg-accent/20 transition-colors">
          <AppWindow class="w-5 h-5 text-accent" />
        </div>
        <h3 class="text-sm font-semibold text-text-primary mb-1">Manage Applications</h3>
        <p class="text-xs text-text-muted">Create, edit and manage your bot and client apps.</p>
      </button>

      <!-- GDPR Export -->
      <button
        @click="requestGDRPExport"
        :disabled="isExportProgress"
        class="glass-card group p-6 text-left cursor-pointer transition-all hover:border-glass-border-hover disabled:opacity-50 disabled:cursor-not-allowed relative overflow-hidden"
      >
        <div class="w-10 h-10 rounded-xl bg-blue-500/10 flex items-center justify-center mb-4 group-hover:bg-blue-500/20 transition-colors">
          <FileDown class="w-5 h-5 text-blue-400" />
        </div>
        <h3 class="text-sm font-semibold text-text-primary mb-1">Request Data Export</h3>
        <p class="text-xs text-text-muted">Download a copy of your personal data (GDPR).</p>

        <Transition name="fade">
          <div v-if="isExportProgress" class="absolute inset-0 flex items-center justify-center bg-black/60 rounded-xl backdrop-blur-sm">
            <Loader2 class="w-6 h-6 animate-spin text-blue-400" />
          </div>
        </Transition>
      </button>

      <!-- Account Deletion -->
      <button
        @click="requestDeleteAccount"
        :disabled="isDeletionScheduled"
        class="glass-card group p-6 text-left cursor-pointer transition-all hover:border-danger/30 disabled:opacity-50 disabled:cursor-not-allowed relative overflow-hidden"
      >
        <div class="w-10 h-10 rounded-xl bg-danger/10 flex items-center justify-center mb-4 group-hover:bg-danger/20 transition-colors">
          <Trash2 class="w-5 h-5 text-red-400" />
        </div>
        <h3 class="text-sm font-semibold text-text-primary mb-1">Delete Account</h3>
        <p class="text-xs text-text-muted">Permanently remove your account and all data.</p>

        <Transition name="fade">
          <div v-if="isDeletionScheduled" class="absolute inset-0 flex items-center justify-center bg-black/60 rounded-xl backdrop-blur-sm">
            <Loader2 class="w-6 h-6 animate-spin text-red-400" />
          </div>
        </Transition>
      </button>
    </div>

    <!-- Deletion Scheduled Banner -->
    <GlassCard v-if="isDeletionScheduled" class="flex items-start gap-3 !p-4 !border-red-500/30">
      <Trash2 class="w-5 h-5 text-red-400 shrink-0 mt-0.5" />
      <div class="flex-1">
        <p class="text-sm text-text-primary font-semibold mb-1">Account deletion scheduled</p>
        <p class="text-xs text-text-secondary">
          Scheduled at: <span class="text-text-primary">{{ deletionScheduledAt?.toLocaleString() }}</span><br />
          Will be deleted at: <span class="text-red-400 font-medium">{{ deletionExecutionAt?.toLocaleString() }}</span>
        </p>
        <GlassButton variant="ghost" class="mt-2" :loading="cancelLoading" @click="cancelDeletion">
          <XCircle class="w-4 h-4 mr-1" /> Cancel Deletion
        </GlassButton>
      </div>
    </GlassCard>

    <!-- Info Section -->
    <GlassCard class="flex items-start gap-3 !p-4">
      <Shield class="w-5 h-5 text-accent shrink-0 mt-0.5" />
      <div>
        <p class="text-sm text-text-secondary">
          Your data is protected under GDPR. Export and deletion requests are processed within the timeframes required by regulation.
        </p>
      </div>
    </GlassCard>
  </div>

  <!-- Delete Confirmation -->
  <GlassDialog :open="showDeleteDialog" @update:open="showDeleteDialog = $event">
    <h3 class="text-lg font-semibold text-text-primary mb-2">Confirm Account Deletion</h3>
    <p class="text-sm text-text-secondary mb-4">
      Once you confirm, a deletion request will be submitted. Your account will be
      <span class="text-red-400 font-medium">permanently deleted</span> within
      <span class="text-accent font-medium">one month</span> unless canceled.
    </p>
    <div class="mb-4">
      <label class="block text-sm text-text-secondary mb-1">Enter your password to confirm</label>
      <input
        v-model="deletePassword"
        type="password"
        placeholder="Password"
        class="w-full px-3 py-2 rounded-lg bg-white/5 border border-glass-border text-text-primary text-sm placeholder-text-muted focus:outline-none focus:border-accent/50 transition-colors"
        @keyup.enter="confirmDeleteAccount"
      />
    </div>
    <div class="flex justify-end gap-2">
      <GlassButton variant="ghost" @click="showDeleteDialog = false">Cancel</GlassButton>
      <GlassButton variant="danger" :loading="deleteLoading" :disabled="!deletePassword" @click="confirmDeleteAccount">
        Confirm Deletion
      </GlassButton>
    </div>
  </GlassDialog>

  <!-- GDPR Export Confirmation -->
  <GlassDialog :open="showExportDialog" @update:open="showExportDialog = $event">
    <h3 class="text-lg font-semibold text-text-primary mb-2">Request Data Export (GDPR)</h3>
    <p class="text-sm text-text-secondary mb-4">
      After confirming, a data export will be queued. Within
      <span class="text-accent font-medium">two weeks</span>, you'll receive an email containing
      an archive of your personal data.
    </p>
    <div class="flex justify-end gap-2">
      <GlassButton variant="ghost" @click="showExportDialog = false">Cancel</GlassButton>
      <GlassButton variant="accent" :loading="isExportProgress" @click="confirmGDRPExport">
        Confirm Export
      </GlassButton>
    </div>
  </GlassDialog>
</template>

<style scoped>
.fade-enter-active,
.fade-leave-active {
  transition: opacity 0.2s ease;
}
.fade-enter-from,
.fade-leave-to {
  opacity: 0;
}
</style>
