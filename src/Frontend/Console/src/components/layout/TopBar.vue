<script setup lang="ts">
import { ref, onMounted, onBeforeUnmount } from "vue"
import { storeToRefs } from "pinia"
import { useUserStore } from "@/store/useUserStore"
import { useTeamsStore } from "@/store/useTeamsStore"
import { useApi } from "@/store/apiStore"
import {
  Home,
  AppWindow,
  ChevronDown,
  Plus,
  Loader2,
  Bell,
  Check,
  X,
} from "@lucide/vue"
import ArgonAvatar from "@/components/ArgonAvatar.vue"
import { GlassButton, GlassInput, GlassDialog } from "@/components/base"
import IconSw from "@argon/assets/icons/icon_cat.svg"
import router from "@/router"
import { type MyInvitesInfo } from "@/lib/glue/accountConsole"

const api = useApi()
const userStore = useUserStore()
const teamsStore = useTeamsStore()
const { user, isLoaded } = storeToRefs(userStore)
const { teams, selectedTeam, isLoading: isTeamsLoading } = storeToRefs(teamsStore)

// Team selector
const teamDropdownOpen = ref(false)
const teamDropdownRef = ref<HTMLElement | null>(null)

// Invites
const invitesDropdownOpen = ref(false)
const invitesDropdownRef = ref<HTMLElement | null>(null)
const myInvites = ref<MyInvitesInfo[]>([])
const myInvitesLoading = ref(false)

// Create team
const showCreateDialog = ref(false)
const newTeamName = ref("")

onMounted(async () => {
  document.addEventListener("click", handleClickOutside)
  await fetchMyInvites()
})

onBeforeUnmount(() => {
  document.removeEventListener("click", handleClickOutside)
})

function handleClickOutside(e: MouseEvent) {
  if (teamDropdownRef.value && !teamDropdownRef.value.contains(e.target as Node)) {
    teamDropdownOpen.value = false
  }
  if (invitesDropdownRef.value && !invitesDropdownRef.value.contains(e.target as Node)) {
    invitesDropdownOpen.value = false
  }
}

async function fetchMyInvites() {
  try {
    myInvitesLoading.value = true
    myInvites.value = await api.teamsManagement.GetMyInvites()
  } catch (err) {
    console.error(err)
  } finally {
    myInvitesLoading.value = false
  }
}

async function acceptInvite(teamId: string) {
  try {
    await api.teamsManagement.AcceptTeamInvite(teamId)
    await fetchMyInvites()
    await teamsStore.fetchTeams()
  } catch (err) {
    console.error(err)
  }
}

async function declineInvite(teamId: string) {
  try {
    await api.teamsManagement.DeclineTeamInvite(teamId)
    await fetchMyInvites()
  } catch (err) {
    console.error(err)
  }
}

async function handleCreateTeam() {
  if (!newTeamName.value.trim()) return
  await teamsStore.createTeam(newTeamName.value.trim())
  newTeamName.value = ""
  showCreateDialog.value = false
}

function goHome() {
  router.push({ name: "MasterPage" })
}
</script>

<template>
  <header class="glass-topbar h-14 flex items-center justify-between px-4 shrink-0 z-40">
    <!-- Left: logo + nav -->
    <div class="flex items-center gap-3">
      <div class="flex items-center gap-2 cursor-pointer" @click="goHome">
        <IconSw class="w-7 h-7 fill-accent" />
        <span class="font-semibold text-text-primary hidden sm:block">Argon Console</span>
      </div>

      <div class="h-5 w-px bg-glass-border mx-1 hidden sm:block" />

      <!-- Nav links -->
      <nav class="hidden sm:flex items-center gap-1">
        <router-link
          :to="{ name: 'MasterPage' }"
          class="flex items-center gap-1.5 px-3 py-1.5 text-sm rounded-lg text-text-secondary hover:text-text-primary hover:bg-white/5 transition-colors"
          active-class="!text-text-primary bg-white/5"
          exact
        >
          <Home class="w-4 h-4" /> Home
        </router-link>
        <router-link
          :to="{ name: 'AppsManage' }"
          class="flex items-center gap-1.5 px-3 py-1.5 text-sm rounded-lg text-text-secondary hover:text-text-primary hover:bg-white/5 transition-colors"
          active-class="!text-text-primary bg-white/5"
        >
          <AppWindow class="w-4 h-4" /> Apps
        </router-link>
      </nav>
    </div>

    <!-- Right: team selector + invites + user -->
    <div class="flex items-center gap-2">
      <!-- Team selector -->
      <div ref="teamDropdownRef" class="relative">
        <button
          class="flex items-center gap-2 px-2.5 py-1.5 rounded-lg glass glass-hover cursor-pointer transition-all max-w-[200px]"
          @click="teamDropdownOpen = !teamDropdownOpen"
        >
          <template v-if="selectedTeam">
            <ArgonAvatar
              :user-id="selectedTeam.teamId"
              :file-id="selectedTeam.avatarFileId"
              :fallback="selectedTeam.name[0]"
              :overrided-size="22"
            />
            <span class="text-sm text-text-primary truncate hidden md:block">{{ selectedTeam.name }}</span>
          </template>
          <template v-else>
            <span class="text-sm text-text-muted">Team</span>
          </template>
          <ChevronDown
            class="w-3.5 h-3.5 text-text-muted shrink-0 transition-transform duration-150"
            :class="teamDropdownOpen && 'rotate-180'"
          />
        </button>

        <!-- Team dropdown -->
        <Transition name="dropdown">
          <div
            v-if="teamDropdownOpen"
            class="absolute z-50 right-0 mt-2 w-72 glass-card p-1.5 max-h-80 overflow-y-auto scrollbar-thin shadow-xl shadow-black/30"
          >
            <p class="text-[11px] uppercase tracking-wider text-text-muted font-medium px-2 py-1.5">Switch team</p>

            <div v-if="isTeamsLoading" class="flex items-center justify-center py-4">
              <Loader2 class="w-4 h-4 animate-spin text-text-muted" />
            </div>

            <template v-else-if="teams.length">
              <button
                v-for="team in teams"
                :key="team.teamId"
                class="w-full flex items-center gap-2.5 px-2 py-2 rounded-lg transition-colors cursor-pointer"
                :class="selectedTeam?.teamId === team.teamId ? 'bg-accent/10 text-accent' : 'hover:bg-white/5 text-text-primary'"
                @click="teamsStore.selectTeam(team); teamDropdownOpen = false"
              >
                <ArgonAvatar
                  :user-id="team.teamId"
                  :file-id="team.avatarFileId"
                  :fallback="team.name[0]"
                  :overrided-size="28"
                />
                <div class="flex-1 min-w-0 text-left">
                  <p class="text-sm truncate">{{ team.name }}</p>
                  <p class="text-[11px] text-text-muted">{{ team.appsCount }} app{{ team.appsCount !== 1 ? 's' : '' }}</p>
                </div>
                <Check v-if="selectedTeam?.teamId === team.teamId" class="w-4 h-4 text-accent shrink-0" />
              </button>
            </template>

            <p v-else class="text-sm text-text-muted text-center py-3">No teams yet</p>

            <div class="border-t border-glass-border mt-1 pt-1">
              <button
                class="w-full flex items-center gap-2 px-2 py-2 rounded-lg hover:bg-white/5 transition-colors text-accent cursor-pointer"
                @click="showCreateDialog = true; teamDropdownOpen = false"
              >
                <Plus class="w-4 h-4" />
                <span class="text-sm">Create new team</span>
              </button>
            </div>
          </div>
        </Transition>
      </div>

      <!-- Invites bell -->
      <div ref="invitesDropdownRef" class="relative">
        <button
          class="relative p-2 rounded-lg hover:bg-white/5 text-text-secondary hover:text-text-primary transition-colors cursor-pointer"
          @click="invitesDropdownOpen = !invitesDropdownOpen"
        >
          <Bell class="w-4.5 h-4.5" />
          <span
            v-if="myInvites.length"
            class="absolute -top-0.5 -right-0.5 w-4.5 h-4.5 bg-accent rounded-full text-[10px] font-bold text-white flex items-center justify-center ring-2 ring-surface-0"
          >
            {{ myInvites.length > 9 ? '9+' : myInvites.length }}
          </span>
        </button>

        <!-- Invites dropdown -->
        <Transition name="dropdown">
          <div
            v-if="invitesDropdownOpen"
            class="absolute z-50 right-0 mt-2 w-80 glass-card p-1.5 max-h-80 overflow-y-auto scrollbar-thin shadow-xl shadow-black/30"
          >
            <p class="text-[11px] uppercase tracking-wider text-text-muted font-medium px-2 py-1.5">
              Team invites
            </p>

            <div v-if="myInvitesLoading" class="flex items-center justify-center py-4">
              <Loader2 class="w-4 h-4 animate-spin text-text-muted" />
            </div>

            <template v-else-if="myInvites.length">
              <div
                v-for="inv in myInvites"
                :key="inv.team.teamId"
                class="flex items-center gap-2.5 p-2 rounded-lg hover:bg-white/[0.03] transition-colors"
              >
                <ArgonAvatar :file-id="inv.team.avatarFileId" :fallback="inv.team.name[0]" :overrided-size="32" />
                <div class="flex-1 min-w-0">
                  <p class="text-sm text-text-primary truncate leading-tight">{{ inv.team.name }}</p>
                  <p class="text-[11px] text-text-muted truncate">from @{{ inv.from.username }}</p>
                </div>
                <div class="flex gap-1 shrink-0">
                  <button
                    class="p-1.5 rounded-md bg-success/15 text-green-400 hover:bg-success/25 transition-colors cursor-pointer"
                    title="Accept"
                    @click="acceptInvite(inv.team.teamId)"
                  >
                    <Check class="w-3.5 h-3.5" />
                  </button>
                  <button
                    class="p-1.5 rounded-md bg-white/5 text-text-muted hover:bg-danger/15 hover:text-red-400 transition-colors cursor-pointer"
                    title="Decline"
                    @click="declineInvite(inv.team.teamId)"
                  >
                    <X class="w-3.5 h-3.5" />
                  </button>
                </div>
              </div>
            </template>

            <div v-else class="py-6 text-center">
              <Bell class="w-6 h-6 text-text-muted mx-auto mb-2 opacity-40" />
              <p class="text-sm text-text-muted">No pending invites</p>
            </div>
          </div>
        </Transition>
      </div>

      <div class="h-5 w-px bg-glass-border mx-0.5" />

      <!-- User -->
      <div v-if="isLoaded && user" class="flex items-center gap-2.5 pl-1">
        <span class="text-sm text-text-secondary hidden md:block">{{ user.displayName }}</span>
        <ArgonAvatar
          :fallback="user.displayName?.[0] ?? '?'"
          :user-id="user.userId"
          :file-id="user.avatarFileId"
          :overrided-size="32"
        />
      </div>
    </div>
  </header>

  <!-- Create team dialog -->
  <GlassDialog :open="showCreateDialog" @update:open="showCreateDialog = $event">
    <h3 class="text-lg font-semibold text-text-primary mb-1">Create new team</h3>
    <p class="text-sm text-text-muted mb-4">Enter a name for your new team.</p>

    <GlassInput
      v-model="newTeamName"
      placeholder="Team name"
      class="mb-4"
      @keydown.enter="handleCreateTeam"
    />

    <div class="flex justify-end gap-2">
      <GlassButton variant="ghost" @click="showCreateDialog = false">Cancel</GlassButton>
      <GlassButton variant="accent" @click="handleCreateTeam">Create</GlassButton>
    </div>
  </GlassDialog>
</template>

<style scoped>
.dropdown-enter-active,
.dropdown-leave-active {
  transition: all 0.15s ease;
}
.dropdown-enter-from,
.dropdown-leave-to {
  opacity: 0;
  transform: translateY(-4px);
}
</style>
