import { defineStore } from "pinia";
import { ref } from "vue";
import { useApi } from "./apiStore";
import { MeDetails } from "@/lib/glue/accountConsole";
import { useTeamsStore } from "./useTeamsStore";

export const useUserStore = defineStore("user", () => {
    const api = useApi();
    const teamsStore = useTeamsStore();
    const user = ref<MeDetails | null>(null);
    const isLoading = ref(false);
    const isLoaded = ref(false);
    const retryCount = ref(0);
    const errorMessage = ref<string | null>(null);

    const maxRetries = 3;
    const baseRetryDelay = 2000;
    const retryTimer = ref<number | null>(null);

    function clearRetryTimer() {
        if (retryTimer.value !== null) {
            clearTimeout(retryTimer.value);
            retryTimer.value = null;
        }
    }

    async function fetchUser(force = false, isRetry = false) {
        if (isLoaded.value && !force) return;
        if (isLoading.value && !isRetry) return;

        if (!isRetry) {
            isLoading.value = true;
            errorMessage.value = null;
        }

        clearRetryTimer();

        try {
            const me = await api.consoleInteraction.GetMe();
            user.value = me;
            await teamsStore.fetchTeams();
            isLoaded.value = true;
            retryCount.value = 0;
            errorMessage.value = null;

            clearRetryTimer();
        } catch (err) {
            retryCount.value++;
            user.value = null;
            console.error(`[UserStore] Failed to load user (attempt ${retryCount.value}):`, err);

            if (retryCount.value >= maxRetries) {
                errorMessage.value = "User data could not be uploaded. Try again later.";
                console.warn("[UserStore] Max retry attempts reached, giving up.");
            } else {
                const delay = Math.min(10000, baseRetryDelay * 2 ** (retryCount.value - 1));
                retryTimer.value = window.setTimeout(() => {
                    fetchUser(true, true);
                }, delay);
            }
        } finally {
            if (!isRetry) {
                isLoading.value = false;
            }
        }
    }

    function retryFetch() {
        clearRetryTimer();
        retryCount.value = 0;
        errorMessage.value = null;
        fetchUser(true, false);
    }

    return { user, isLoading, isLoaded, errorMessage, fetchUser, retryFetch };
});
