<script setup lang="ts">
import { computed, ref, watch } from "vue"
import { Users, Plus, Loader2, Shield, UserRoundPlus, AppWindow } from "@lucide/vue"
import { useApi } from "@/store/apiStore"
import { useToast } from "@/composables/useToast"
import { GlassButton, GlassCard, GlassDialog, GlassInput } from "@/components/base"
import {
  type TeamShortDetails,
  type TeamMemberDetails,
  type TeamInviteInfo,
  InviteUserError,
} from "@/lib/glue/accountConsole"
import ArgonAvatar from "./ArgonAvatar.vue"

const props = defineProps<{
  selectedTeam: TeamShortDetails | null
}>()

const api = useApi()
const { toast } = useToast()

const members = ref<TeamMemberDetails[]>([])
const invites = ref<TeamInviteInfo[]>([])
const isLoading = ref(false)
const showDialog = ref(false)
const showInviteDialog = ref(false)
const inviteUsername = ref("")
const inviteLoading = ref(false)

function openInviteDialog() {
  inviteUsername.value = ""
  showInviteDialog.value = true
}

async function sendInvite() {
  if (!props.selectedTeam) return
  try {
    inviteLoading.value = true
    const result = await api.teamsManagement.InviteUserToTeam(props.selectedTeam.teamId, inviteUsername.value.trim())
    switch (result) {
      case InviteUserError.OK:
        toast({ title: "Success", description: `Invitation sent to @${inviteUsername.value}` })
        showInviteDialog.value = false
        await fetchTeamMembersAndInvites()
        break
      case InviteUserError.USER_NOT_FOUND:
        toast({ title: "User not found", description: `User @${inviteUsername.value} does not exist.`, variant: "destructive" })
        break
      case InviteUserError.ALREADY_INVITED:
        toast({ title: "Already invited", description: `User @${inviteUsername.value} already has a pending invite.`, variant: "destructive" })
        break
      case InviteUserError.ALREADY_IN_TEAM:
        toast({ title: "Already a member", description: `User @${inviteUsername.value} is already a team member.`, variant: "destructive" })
        break
      default:
        toast({ title: "Error", description: "Internal server error.", variant: "destructive" })
    }
  } catch (err: any) {
    toast({ title: "Unexpected error", description: err?.message ?? "Something went wrong", variant: "destructive" })
  } finally {
    inviteLoading.value = false
  }
}

const owner = computed(() => members.value.find(m => m.isOwner)?.user ?? null)

async function fetchTeamMembersAndInvites() {
  if (!props.selectedTeam) {
    members.value = []
    invites.value = []
    return
  }
  try {
    isLoading.value = true
    const details = await api.teamsManagement.GetTeamDetails(props.selectedTeam.teamId)
    members.value = details.members ?? []
    invites.value = (await api.teamsManagement.GetTeamInvites(props.selectedTeam.teamId)) ?? []
  } catch (err: any) {
    toast({ title: "Failed to load team info", description: err?.message ?? "Unexpected error", variant: "destructive" })
  } finally {
    isLoading.value = false
  }
}

watch(
  () => props.selectedTeam,
  async (team) => {
    if (team) await fetchTeamMembersAndInvites()
    else { members.value = []; invites.value = [] }
  },
  { immediate: true }
)
</script>

<template>
  <GlassCard v-if="selectedTeam" class="space-y-4">
    <!-- Team header -->
    <div class="flex items-center gap-3">
      <ArgonAvatar
        :fallback="selectedTeam.name?.at(0) ?? '?'"
        :file-id="selectedTeam.avatarFileId"
        :overrided-size="40"
      />
      <div class="min-w-0">
        <p class="font-semibold text-text-primary truncate">{{ selectedTeam.name }}</p>
        <p class="text-xs text-text-muted">Team overview</p>
      </div>
    </div>

    <!-- Stats -->
    <div class="grid grid-cols-2 gap-2 text-sm">
      <div v-if="owner" class="flex items-center gap-2 text-text-secondary">
        <Shield class="w-3.5 h-3.5 text-yellow-500" />
        <span class="truncate">{{ owner.displayName }}</span>
      </div>
      <div class="flex items-center gap-2 text-text-secondary">
        <Users class="w-3.5 h-3.5 text-blue-400" />
        <span>{{ members.length }} members</span>
      </div>
      <div class="flex items-center gap-2 text-text-secondary">
        <UserRoundPlus class="w-3.5 h-3.5 text-purple-400" />
        <span>{{ invites.length }} invites</span>
      </div>
      <div class="flex items-center gap-2 text-text-secondary">
        <AppWindow class="w-3.5 h-3.5 text-green-400" />
        <span>{{ selectedTeam.appsCount }} apps</span>
      </div>
    </div>

    <!-- Actions -->
    <div class="flex gap-2">
      <GlassButton size="sm" variant="outline" @click="openInviteDialog">
        <Plus class="w-3.5 h-3.5" /> Invite
      </GlassButton>
      <GlassButton size="sm" variant="ghost" @click="showDialog = true">
        <Users class="w-3.5 h-3.5" /> {{ members.length }} Members
      </GlassButton>
    </div>
  </GlassCard>

  <!-- Members dialog -->
  <GlassDialog :open="showDialog" @update:open="showDialog = $event">
    <h3 class="text-lg font-semibold text-text-primary mb-4">Team Members</h3>

    <div v-if="isLoading" class="flex justify-center py-8">
      <Loader2 class="w-5 h-5 animate-spin text-text-muted" />
    </div>

    <div v-else class="space-y-4 max-h-80 overflow-y-auto scrollbar-thin">
      <div>
        <h4 class="text-xs uppercase tracking-wider text-text-muted mb-2">Active members</h4>
        <div v-if="members.length" class="space-y-1.5">
          <div
            v-for="m in members"
            :key="m.user.userId"
            class="flex items-center gap-3 p-2 rounded-lg hover:bg-white/5 transition-colors"
          >
            <ArgonAvatar :fallback="m.user.displayName.at(0) ?? '?'" :user-id="m.user.userId" :file-id="m.user.avatarFileId" :overrided-size="32" />
            <div class="flex-1 min-w-0">
              <p class="text-sm font-medium text-text-primary truncate">
                {{ m.user.displayName }}
                <span v-if="m.isOwner" class="ml-1 text-[10px] px-1.5 py-0.5 rounded-full bg-yellow-500/10 text-yellow-400 border border-yellow-500/30">
                  Owner
                </span>
              </p>
              <p class="text-xs text-text-muted">@{{ m.user.username }}</p>
            </div>
          </div>
        </div>
        <p v-else class="text-sm text-text-muted">No members yet.</p>
      </div>

      <div v-if="invites.length">
        <h4 class="text-xs uppercase tracking-wider text-text-muted mb-2">Pending invites</h4>
        <div class="space-y-1.5">
          <div
            v-for="inv in invites"
            :key="`${inv.to.userId}-${inv.date}`"
            class="flex items-center gap-3 p-2 rounded-lg hover:bg-white/5 transition-colors"
          >
            <ArgonAvatar :fallback="inv.to.displayName.at(0) ?? '?'" :user-id="inv.to.userId" :file-id="inv.to.avatarFileId" :overrided-size="32" />
            <div class="flex-1 min-w-0">
              <p class="text-sm font-medium text-text-primary truncate">{{ inv.to.displayName }}</p>
              <p class="text-xs text-text-muted">@{{ inv.to.username }}</p>
            </div>
            <p class="text-xs text-text-muted shrink-0">by {{ inv.from.displayName }}</p>
          </div>
        </div>
      </div>
    </div>
  </GlassDialog>

  <!-- Invite dialog -->
  <GlassDialog :open="showInviteDialog" @update:open="showInviteDialog = $event">
    <h3 class="text-lg font-semibold text-text-primary mb-4">Invite user</h3>

    <div class="space-y-4">
      <div>
        <label class="text-sm text-text-secondary mb-1 block">Username</label>
        <GlassInput v-model="inviteUsername" placeholder="username" />
      </div>

      <div class="flex justify-end gap-2">
        <GlassButton variant="ghost" @click="showInviteDialog = false">Cancel</GlassButton>
        <GlassButton variant="accent" :disabled="inviteLoading || !inviteUsername.trim()" :loading="inviteLoading" @click="sendInvite">
          Invite
        </GlassButton>
      </div>
    </div>
  </GlassDialog>
</template>
