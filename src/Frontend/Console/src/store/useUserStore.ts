import { defineStore } from "pinia";
import { ref } from "vue";
import { useApi } from "./apiStore";
import { MeDetails } from "@/lib/glue/accountConsole";
import { useTeamsStore } from "./useTeamsStore";
import { fetchUserInfo } from "@/composables/useOAuth";

export const useUserStore = defineStore("user", () => {
    const api = useApi();
    const teamsStore = useTeamsStore();
    const user = ref<MeDetails | null>(null);

    /**
     * The address of this user's avatar, as published by Aegis.
     *
     * Kept apart from `user` because it comes from somewhere else and is allowed to be missing: the
     * console's own `GetMe` carries a file id, which says where a file sits in this deployment's
     * storage, while userinfo carries the address that survives storage moving. Null covers every
     * way the second can be absent — no avatar set, the scope not granted, userinfo unreachable —
     * and each renders the same initials the console showed before there was an avatar.
     */
    const avatarUrl = ref<string | null>(null);
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

            // Deliberately outside this try's failure path: the console is perfectly usable without
            // an avatar, and letting userinfo failing take the profile load down with it would put
            // the page into its retry loop over a decoration.
            void loadAvatar();
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

    async function loadAvatar() {
        try {
            avatarUrl.value = (await fetchUserInfo())?.avatarUrl ?? null;
        } catch {
            avatarUrl.value = null;
        }
    }

    function retryFetch() {
        clearRetryTimer();
        retryCount.value = 0;
        errorMessage.value = null;
        fetchUser(true, false);
    }

    return { user, avatarUrl, isLoading, isLoaded, errorMessage, fetchUser, retryFetch };
});
