import { defineStore } from "pinia";
import { ref } from "vue";
import { useToast } from "@argon/ui/toast";
import { useLocale } from "@/store/localeStore";
import { registrationErrorKey } from "@/store/authMessages";

const { toast } = useToast();

const FETCH_TIMEOUT_MS = 15_000;

async function fetchWithTimeout(url: string, options: RequestInit, timeoutMs = FETCH_TIMEOUT_MS): Promise<Response> {
    const controller = new AbortController();
    const id = setTimeout(() => controller.abort(), timeoutMs);
    try {
        return await fetch(url, { ...options, signal: controller.signal });
    } finally {
        clearTimeout(id);
    }
}

export const useRegisterStore = defineStore("register", () => {
    const { t } = useLocale();
    const isLoading = ref(false);
    const inviteToken = ref("");
    const frozenEmail = ref("");
    const appName = ref("");
    const appAvatarFileId = ref<string | null>(null);
    const isTokenValid = ref(false);
    const isTokenChecked = ref(false);
    const errorMessage = ref<string | null>(null);
    const fieldErrors = ref<Record<string, string>>({});
    const isRegistered = ref(false);
    const redirectUrl = ref<string | null>(null);

    async function validateToken(token: string) {
        inviteToken.value = token;
        isLoading.value = true;
        isTokenChecked.value = false;
        try {
            const response = await fetchWithTimeout(`/api/auth/invite/info?token=${encodeURIComponent(token)}`, {
                method: "GET",
            });

            if (!response.ok) {
                const data = await response.json().catch(() => ({}));
                errorMessage.value = t("invite_invalid");
                isTokenValid.value = false;
                return;
            }

            const data = await response.json();
            frozenEmail.value = data.email;
            appName.value = data.appName;
            appAvatarFileId.value = data.appAvatarFileId;
            isTokenValid.value = true;
        } catch (error) {
            errorMessage.value = t("invite_check_failed");
            isTokenValid.value = false;
        } finally {
            isLoading.value = false;
            isTokenChecked.value = true;
        }
    }

    async function register(username: string, password: string, displayName: string, birthDate: string, tosAgreement: boolean) {
        isLoading.value = true;
        errorMessage.value = null;
        fieldErrors.value = {};

        try {
            const response = await fetchWithTimeout("/api/auth/invite/register", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                credentials: "include",
                body: JSON.stringify({
                    token: inviteToken.value,
                    username,
                    password,
                    displayName,
                    birthDate,
                    tosAgreement,
                }),
            });

            const data = await response.json();

            if (!response.ok) {
                const refusal = registrationErrorKey(data.error ?? "", data.field);
                if (refusal.field) fieldErrors.value[refusal.field] = t(refusal.key);
                errorMessage.value = t(refusal.key);
                return;
            }

            if (data.success && data.redirectUrl) {
                isRegistered.value = true;
                redirectUrl.value = data.redirectUrl;
                toast({
                    title: t("account_created"),
                    description: t("redirecting_to_app"),
                    duration: 3000,
                });

                // Build the POST form for OAuth redirect (same pattern as simpleAuthStore)
                const urlParams = new URLSearchParams(data.redirectUrl.split("?")[1] || "");
                const form = document.createElement("form");
                form.method = "POST";
                form.action = "/";
                for (const [key, value] of urlParams.entries()) {
                    const input = document.createElement("input");
                    input.type = "hidden";
                    input.name = key;
                    input.value = value;
                    form.appendChild(input);
                }
                document.body.appendChild(form);
                form.submit();
            }
        } catch (error) {
            const isTimeout = error instanceof DOMException && error.name === "AbortError";
            errorMessage.value = isTimeout
                ? t("request_timeout_desc")
                : t("register_failed_desc");
        } finally {
            isLoading.value = false;
        }
    }

    return {
        isLoading,
        inviteToken,
        frozenEmail,
        appName,
        appAvatarFileId,
        isTokenValid,
        isTokenChecked,
        errorMessage,
        fieldErrors,
        isRegistered,
        redirectUrl,
        validateToken,
        register,
    };
});
