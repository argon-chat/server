import { defineStore } from "pinia"
import { ref, computed } from "vue"
import { useApi } from "./apiStore"
import { TeamShortDetails } from "@/lib/glue/accountConsole"

export const useTeamsStore = defineStore("teams", () => {
  const api = useApi()

  const teams = ref<TeamShortDetails[]>([])
  const selectedTeam = ref<TeamShortDetails | null>(null)
  const isLoading = ref(false)
  const errorMessage = ref<string | null>(null)

  async function fetchTeams() {
    try {
      isLoading.value = true
      errorMessage.value = null

      const data = await api.teamsManagement.GetMyTeams()
      teams.value = data ?? []

      const storedId = localStorage.getItem("selectedTeamId")
      if (storedId) {
        const found = teams.value.find(t => t.teamId === storedId)
        if (found) selectedTeam.value = found
      }
    } catch (err: any) {
      console.error("fetchTeams failed", err)
      errorMessage.value = err?.message ?? "Failed to fetch teams"
    } finally {
      isLoading.value = false
    }
  }

  function selectTeam(team: TeamShortDetails) {
    selectedTeam.value = team
    localStorage.setItem("selectedTeamId", team.teamId)
  }

  async function createTeam(name: string) {
    if (!name.trim()) throw new Error("Team name is required")

    try {
      const fullTeam = await api.teamsManagement.CreateTeam(name.trim());
      const team = { appsCount: fullTeam.apps.length, avatarFileId: fullTeam.avatarFileId, name: fullTeam.name, teamId: fullTeam.teamId };
      teams.value.push(team);
      selectTeam(team);
      return team
    } catch (err) {
      console.error("createTeam failed", err)
      throw err
    }
  }

  function clearTeams() {
    teams.value = []
    selectedTeam.value = null
    localStorage.removeItem("selectedTeamId")
  }

  const hasTeams = computed(() => teams.value.length > 0)

  return {
    teams,
    selectedTeam,
    isLoading,
    errorMessage,
    hasTeams,
    fetchTeams,
    selectTeam,
    createTeam,
    clearTeams,
  }
})
